using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Raw;

[SupportedOSPlatform("windows")]
public sealed class RawEvidenceWriter : IRawEvidenceStore
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<long> _availableFreeSpace;
    private readonly long _stopwatchFrequency;
    private readonly long _createdUtcTicks;
    private readonly WindowsRawEvidenceDirectory _directory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _finalizationGate = new();
    private FileStream? _dataStream;
    private FileStream? _manifestStream;
    private Task<RawEvidenceCompletion>? _finalizationTask;
    private WriterState _state = WriterState.Writing;
    private long _recordCount;
    private long _recordsByteLength;
    private long? _firstSequence;
    private long? _lastSequence;
    private long? _firstArrivalTimestamp;
    private long? _lastArrivalTimestamp;
    private int _disposeStarted;

    private RawEvidenceWriter(
        RawEvidenceCaptureId captureId,
        RawEvidenceProtocolId protocolId,
        RawEvidenceLimits limits,
        long stopwatchFrequency,
        TimeProvider timeProvider,
        Func<long> availableFreeSpace,
        long createdUtcTicks,
        WindowsRawEvidenceDirectory directory,
        FileStream dataStream)
    {
        CaptureId = captureId;
        ProtocolId = protocolId;
        Limits = limits;
        _stopwatchFrequency = stopwatchFrequency;
        _timeProvider = timeProvider;
        _availableFreeSpace = availableFreeSpace;
        _createdUtcTicks = createdUtcTicks;
        _directory = directory;
        _dataStream = dataStream;
    }

    public RawEvidenceCaptureId CaptureId { get; }

    public RawEvidenceProtocolId ProtocolId { get; }

    public RawEvidenceLimits Limits { get; }

    public static Task<RawEvidenceWriter> CreateAsync(
        ApplicationPaths paths,
        RawEvidenceProtocolId protocolId,
        RawEvidenceLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protocolId);
        return CreateForTestingAsync(
            paths,
            RawEvidenceCaptureId.Create(),
            protocolId,
            limits ?? new RawEvidenceLimits(),
            Stopwatch.Frequency,
            TimeProvider.System,
            () => new DriveInfo(
                Path.GetPathRoot(paths.RootDirectory)!)
                .AvailableFreeSpace,
            cancellationToken);
    }

    internal static async Task<RawEvidenceWriter> CreateForTestingAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        RawEvidenceProtocolId protocolId,
        RawEvidenceLimits limits,
        long stopwatchFrequency,
        TimeProvider timeProvider,
        Func<long> availableFreeSpace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(protocolId);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(availableFreeSpace);
        ArgumentOutOfRangeException.ThrowIfLessThan(stopwatchFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            stopwatchFrequency,
            RawEvidenceFormat.MaximumStopwatchFrequency);
        cancellationToken.ThrowIfCancellationRequested();

        WindowsRawEvidenceDirectory? directory = null;
        FileStream? dataStream = null;
        var stagingName = DataStagingName(captureId);
        try
        {
            directory = WindowsRawEvidenceDirectory.Open(paths);
            RequireFreeSpace(
                availableFreeSpace(),
                limits.MinimumFreeSpaceBytes,
                RawEvidenceFormat.DataHeaderLength);
            dataStream = directory.CreateNewFile(stagingName);
            var createdUtcTicks = RequireUtc(
                timeProvider.GetUtcNow(),
                "Evidence creation time").UtcTicks;
            var header = new byte[RawEvidenceFormat.DataHeaderLength];
            RawEvidenceFormat.WriteDataHeader(
                header,
                captureId,
                stopwatchFrequency,
                createdUtcTicks,
                limits.MaximumPayloadBytes);
            await dataStream.WriteAsync(
                header,
                cancellationToken).ConfigureAwait(false);

            return new RawEvidenceWriter(
                captureId,
                protocolId,
                limits,
                stopwatchFrequency,
                timeProvider,
                availableFreeSpace,
                createdUtcTicks,
                directory,
                dataStream);
        }
        catch (Exception primary)
        {
            Exception? cleanupFailure = null;
            if (dataStream is not null)
            {
                try
                {
                    directory!.DeleteOpenFile(
                        dataStream.SafeFileHandle);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                await dataStream.DisposeAsync().ConfigureAwait(false);
            }

            directory?.Dispose();
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Raw evidence creation and cleanup failed.",
                    primary,
                    cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask WriteAsync(
        DatagramEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWriting();
            ValidateEnvelope(envelope);
            var recordLength = checked(
                RawEvidenceFormat.RecordHeaderLength
                + envelope.Payload.Length);
            var projectedDataLength = checked(
                (Int128)RawEvidenceFormat.DataHeaderLength
                + _recordsByteLength
                + recordLength
                + RawEvidenceFormat.FooterLength);
            if (projectedDataLength > Limits.MaximumFileBytes)
            {
                throw new InvalidOperationException(
                    "The raw evidence file limit would be exceeded.");
            }

            RequireFreeSpace(
                _availableFreeSpace(),
                Limits.MinimumFreeSpaceBytes,
                recordLength);

            var header = new byte[RawEvidenceFormat.RecordHeaderLength];
            RawEvidenceFormat.WriteRecordHeader(header, envelope);
            try
            {
                await _dataStream!.WriteAsync(
                    header,
                    cancellationToken).ConfigureAwait(false);
                await _dataStream.WriteAsync(
                    envelope.Payload,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _state = WriterState.Faulted;
                throw;
            }

            _firstSequence ??= envelope.Sequence;
            _firstArrivalTimestamp ??= envelope.MonotonicTimestamp;
            _lastSequence = envelope.Sequence;
            _lastArrivalTimestamp = envelope.MonotonicTimestamp;
            _recordCount = checked(_recordCount + 1);
            _recordsByteLength = checked(
                _recordsByteLength + recordLength);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<RawEvidenceCompletion> FinalizeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_finalizationGate)
        {
            if (_finalizationTask is not null)
            {
                return _finalizationTask;
            }

            if (_state != WriterState.Writing)
            {
                throw new InvalidOperationException(
                    "The raw evidence writer cannot be finalized in its current state.");
            }

            _state = WriterState.Finalizing;
            _finalizationTask = FinalizeCoreAsync();
            return _finalizationTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Task<RawEvidenceCompletion>? finalization;
        lock (_finalizationGate)
        {
            finalization = _finalizationTask;
            if (finalization is null)
            {
                _state = WriterState.Disposed;
            }
        }

        if (finalization is not null)
        {
            try
            {
                await finalization.ConfigureAwait(false);
            }
            catch
            {
                // Finalization already performed and reported its cleanup.
            }

            _state = WriterState.Disposed;
            _writeGate.Dispose();
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cleanupErrors = MarkOpenFilesForDeletion();
            await CloseStreamsAsync().ConfigureAwait(false);
            _directory.Dispose();
            if (cleanupErrors.Count != 0)
            {
                throw new AggregateException(
                    "Incomplete raw evidence could not be fully removed.",
                    cleanupErrors);
            }
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private async Task<RawEvidenceCompletion> FinalizeCoreAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_state != WriterState.Finalizing)
                {
                    throw new InvalidOperationException(
                        "The raw evidence writer faulted before finalization acquired ownership.");
                }

                var finalizedAt = RequireUtc(
                    _timeProvider.GetUtcNow(),
                    "Evidence finalization time");
                var dataLength = checked(
                    RawEvidenceFormat.DataHeaderLength
                    + _recordsByteLength
                    + RawEvidenceFormat.FooterLength);
                var manifest = new RawEvidenceManifest(
                    CaptureId,
                    ProtocolId,
                    dataLength,
                    _recordCount,
                    _firstSequence,
                    _lastSequence,
                    _firstArrivalTimestamp,
                    _lastArrivalTimestamp,
                    _stopwatchFrequency,
                    _createdUtcTicks,
                    finalizedAt.UtcTicks,
                    Limits);
                var manifestPreimage =
                    RawEvidenceFormat.SerializeManifestPreimage(manifest);
                RequireFreeSpace(
                    _availableFreeSpace(),
                    Limits.MinimumFreeSpaceBytes,
                    checked(
                        RawEvidenceFormat.FooterLength
                        + manifestPreimage.Length));

                var footer = new byte[RawEvidenceFormat.FooterLength];
                RawEvidenceFormat.WriteFooter(
                    footer,
                    _recordCount,
                    _firstSequence,
                    _lastSequence,
                    _recordsByteLength,
                    _firstArrivalTimestamp,
                    _lastArrivalTimestamp);
                await _dataStream!.WriteAsync(footer).ConfigureAwait(false);
                _dataStream.Flush(flushToDisk: true);

                var identity = _directory.GetIdentity(
                    _dataStream.SafeFileHandle);
                var digest = await HashEvidenceAsync(
                    _dataStream,
                    dataLength,
                    manifestPreimage).ConfigureAwait(false);
                RequireIdentity(identity, _dataStream.SafeFileHandle);
                var manifestBytes = RawEvidenceFormat.ApplyDigest(
                    manifestPreimage,
                    digest);

                _manifestStream = _directory.CreateNewFile(
                    ManifestStagingName(CaptureId));
                await _manifestStream.WriteAsync(
                    manifestBytes).ConfigureAwait(false);
                _manifestStream.Flush(flushToDisk: true);

                RequireIdentity(identity, _dataStream.SafeFileHandle);
                _directory.RenameOpenFile(
                    _dataStream.SafeFileHandle,
                    DataFinalName(CaptureId));
                _directory.RenameOpenFile(
                    _manifestStream.SafeFileHandle,
                    ManifestFinalName(CaptureId));

                await CloseStreamsAsync().ConfigureAwait(false);
                _directory.Dispose();
                _state = WriterState.Finalized;
                return new RawEvidenceCompletion(
                    CaptureId,
                    ProtocolId,
                    _recordCount,
                    dataLength,
                    Convert.ToHexStringLower(digest),
                    finalizedAt);
            }
            catch (Exception primary)
            {
                _state = WriterState.Faulted;
                var cleanupErrors = MarkOpenFilesForDeletion();
                await CloseStreamsAsync().ConfigureAwait(false);
                _directory.Dispose();
                if (cleanupErrors.Count == 0)
                {
                    throw;
                }

                throw new AggregateException(
                    "Raw evidence finalization and cleanup failed.",
                    [primary, .. cleanupErrors]);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void ValidateEnvelope(DatagramEnvelope envelope)
    {
        if (envelope.Payload.Length is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(envelope),
                "Raw evidence does not store empty UDP payloads.");
        }

        if (envelope.Payload.Length > Limits.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(envelope),
                "The raw evidence payload limit would be exceeded.");
        }

        if (_lastSequence.HasValue
            && envelope.Sequence <= _lastSequence.Value)
        {
            throw new InvalidOperationException(
                "Raw evidence sequences must be strictly increasing.");
        }

        if (_lastArrivalTimestamp.HasValue
            && envelope.MonotonicTimestamp
            < _lastArrivalTimestamp.Value)
        {
            throw new InvalidOperationException(
                "Raw evidence arrival timestamps cannot regress.");
        }

        if (_firstArrivalTimestamp.HasValue)
        {
            var delta = checked(
                (Int128)envelope.MonotonicTimestamp
                - _firstArrivalTimestamp.Value);
            var elapsedMillisecondsNumerator = checked(delta * 1_000);
            var maximumMilliseconds =
                Limits.MaximumDuration.Ticks
                / TimeSpan.TicksPerMillisecond;
            var maximumDurationNumerator = checked(
                (Int128)maximumMilliseconds * _stopwatchFrequency);
            if (elapsedMillisecondsNumerator > maximumDurationNumerator)
            {
                throw new InvalidOperationException(
                    "The raw evidence duration limit would be exceeded.");
            }
        }
    }

    private async Task<byte[]> HashEvidenceAsync(
        FileStream stream,
        long expectedLength,
        byte[] manifestPreimage)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1_024);
        try
        {
            long consumed = 0;
            while (consumed < expectedLength)
            {
                var count = checked((int)Math.Min(
                    buffer.Length,
                    expectedLength - consumed));
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, count)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Raw evidence ended before its expected length.");
                }

                hash.AppendData(buffer, 0, read);
                consumed = checked(consumed + read);
            }

            if (stream.Length != expectedLength)
            {
                throw new IOException(
                    "Raw evidence length changed during finalization.");
            }

            hash.AppendData(manifestPreimage);
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                buffer,
                clearArray: true);
        }
    }

    private void RequireIdentity(
        RawEvidenceFileIdentity expected,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (_directory.GetIdentity(handle) != expected)
        {
            throw new IOException(
                "The raw evidence file identity changed during finalization.");
        }
    }

    private async Task CloseStreamsAsync()
    {
        if (_manifestStream is not null)
        {
            await _manifestStream.DisposeAsync().ConfigureAwait(false);
            _manifestStream = null;
        }

        if (_dataStream is not null)
        {
            await _dataStream.DisposeAsync().ConfigureAwait(false);
            _dataStream = null;
        }
    }

    private List<Exception> MarkOpenFilesForDeletion()
    {
        var failures = new List<Exception>();
        TryMarkForDeletion(_manifestStream, failures);
        TryMarkForDeletion(_dataStream, failures);
        return failures;
    }

    private void TryMarkForDeletion(
        FileStream? stream,
        List<Exception> failures)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            _directory.DeleteOpenFile(stream.SafeFileHandle);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void RequireFreeSpace(
        long available,
        long minimum,
        long pendingGrowth)
    {
        if (available < 0
            || (Int128)available < (Int128)minimum + pendingGrowth)
        {
            throw new IOException(
                "Insufficient free space remains for raw evidence.");
        }
    }

    private static DateTimeOffset RequireUtc(
        DateTimeOffset value,
        string operation)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{operation} must use the UTC offset.");
        }

        return value;
    }

    private void RequireWriting()
    {
        if (_state != WriterState.Writing)
        {
            throw new InvalidOperationException(
                "The raw evidence writer no longer accepts records.");
        }
    }

    private static string DataStagingName(RawEvidenceCaptureId id) =>
        $"{id.Value}.apxraw.partial";

    private static string DataFinalName(RawEvidenceCaptureId id) =>
        $"{id.Value}.apxraw";

    private static string ManifestStagingName(RawEvidenceCaptureId id) =>
        $"{id.Value}.apxraw.json.partial";

    private static string ManifestFinalName(RawEvidenceCaptureId id) =>
        $"{id.Value}.apxraw.json";

    private enum WriterState
    {
        Writing,
        Finalizing,
        Finalized,
        Faulted,
        Disposed,
    }
}
