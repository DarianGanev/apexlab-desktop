using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Canonical;

[SupportedOSPlatform("windows")]
internal sealed class CanonicalCacheReader : ICanonicalCacheEntry
{
    private readonly WindowsCanonicalCacheDirectory _directory;
    private FileStream? _dataStream;
    private int _enumerated;
    private int _disposed;

    private CanonicalCacheReader(
        WindowsCanonicalCacheDirectory directory,
        FileStream dataStream,
        CanonicalCacheCompletion completion)
    {
        _directory = directory;
        _dataStream = dataStream;
        Completion = completion;
    }

    public CanonicalCacheCompletion Completion { get; }

    public static async Task<CanonicalCacheReader> OpenAsync(
        ApplicationPaths paths,
        CanonicalCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsCanonicalCacheDirectory? directory = null;
        FileStream? manifestStream = null;
        FileStream? dataStream = null;
        try
        {
            directory = WindowsCanonicalCacheDirectory.Open(paths);
            var manifestLeaf = CanonicalCacheManifest.CreateManifestLeafName(
                request.Identity);
            try
            {
                manifestStream = directory.TryOpenExistingReadOnly(manifestLeaf);
            }
            catch (IOException exception)
            {
                throw Failure(
                    CanonicalCacheReadFailureKind.UnsafeStorage,
                    exception);
            }

            if (manifestStream is null)
            {
                throw Failure(CanonicalCacheReadFailureKind.Missing);
            }

            var manifest = await ReadManifestAsync(
                    manifestStream,
                    cancellationToken)
                .ConfigureAwait(false);
            await manifestStream.DisposeAsync().ConfigureAwait(false);
            manifestStream = null;
            if (manifest.Completion.Identity != request.Identity
                || manifest.Completion.SourceStopwatchFrequency
                != request.SourceStopwatchFrequency)
            {
                throw Failure(CanonicalCacheReadFailureKind.IdentityMismatch);
            }

            try
            {
                dataStream = directory.TryOpenExistingReadOnly(
                    manifest.DataLeafName);
            }
            catch (IOException exception)
            {
                throw Failure(
                    CanonicalCacheReadFailureKind.UnsafeStorage,
                    exception);
            }

            if (dataStream is null)
            {
                throw Failure(
                    CanonicalCacheReadFailureKind.MissingData,
                    dataLeafName: manifest.DataLeafName);
            }

            try
            {
                await VerifyDataAsync(
                        dataStream,
                        request,
                        manifest.Completion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CanonicalCacheReadException exception)
                when (exception.IsRebuildable)
            {
                throw exception.WithRebuildDataLeaf(manifest.DataLeafName);
            }
            dataStream.Position = 0;
            return new(directory, dataStream, manifest.Completion);
        }
        catch (Exception primary)
        {
            var cleanupFailures = new List<Exception>();
            await DisposeAndCollectAsync(dataStream, cleanupFailures)
                .ConfigureAwait(false);
            await DisposeAndCollectAsync(manifestStream, cleanupFailures)
                .ConfigureAwait(false);
            if (directory is not null)
            {
                try
                {
                    directory.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            if (cleanupFailures.Count != 0)
            {
                throw new AggregateException([primary, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(primary).Throw();
            throw new UnreachableException();
        }
    }

    public async IAsyncEnumerable<CanonicalRecord> ReadAllAsync(
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException(
                "A canonical cache entry can be enumerated only once.");
        }

        var stream = _dataStream!;
        stream.Position = 0;
        var header = new byte[CanonicalCacheFormat.HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            var prefix = new byte[sizeof(uint)];
            await stream.ReadExactlyAsync(prefix, cancellationToken)
                .ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (length == 0)
            {
                var footerRemainder = new byte[
                    CanonicalCacheFormat.FooterLength - sizeof(uint)];
                await stream.ReadExactlyAsync(footerRemainder, cancellationToken)
                    .ConfigureAwait(false);
                yield break;
            }

            if (length > CanonicalCacheFormat.MaximumRecordLength)
            {
                throw Failure(CanonicalCacheReadFailureKind.MalformedData);
            }

            var framed = new byte[checked((int)length + sizeof(uint))];
            prefix.CopyTo(framed, 0);
            await stream.ReadExactlyAsync(
                    framed.AsMemory(sizeof(uint)),
                    cancellationToken)
                .ConfigureAwait(false);
            yield return CanonicalCacheFormat.DeserializeRecord(framed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? streamFailure = null;
        try
        {
            if (_dataStream is not null)
            {
                await _dataStream.DisposeAsync().ConfigureAwait(false);
                _dataStream = null;
            }
        }
        catch (Exception exception)
        {
            streamFailure = exception;
        }

        Exception? directoryFailure = null;
        try
        {
            _directory.Dispose();
        }
        catch (Exception exception)
        {
            directoryFailure = exception;
        }

        if (streamFailure is not null && directoryFailure is not null)
        {
            throw new AggregateException(streamFailure, directoryFailure);
        }

        if (streamFailure is not null)
        {
            throw streamFailure;
        }

        if (directoryFailure is not null)
        {
            throw directoryFailure;
        }
    }

    private static async Task<CanonicalCacheManifest> ReadManifestAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length is < 2 or > CanonicalCacheManifest.MaximumLengthBytes)
        {
            throw Failure(CanonicalCacheReadFailureKind.MalformedManifest);
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return CanonicalCacheManifest.Deserialize(bytes);
        }
        catch (InvalidDataException exception)
        {
            throw Failure(
                CanonicalCacheReadFailureKind.MalformedManifest,
                exception);
        }
    }

    private static async Task VerifyDataAsync(
        FileStream stream,
        CanonicalCacheRequest request,
        CanonicalCacheCompletion completion,
        CancellationToken cancellationToken)
    {
        if (stream.Length != completion.DataLengthBytes
            || stream.Length
            is < CanonicalCacheFormat.HeaderLength + CanonicalCacheFormat.FooterLength
            or > CanonicalCacheFormat.MaximumDataLength)
        {
            throw Failure(CanonicalCacheReadFailureKind.MalformedData);
        }

        var accounting = await ScanDataAsync(
                stream,
                request.SourceStopwatchFrequency,
                cancellationToken)
            .ConfigureAwait(false);
        if (accounting != new CanonicalCacheFooter(
                completion.RecordCount,
                completion.ObservationCount,
                completion.ExclusionCount,
                completion.GapCount,
                completion.FirstSourceSequence,
                completion.LastSourceSequence))
        {
            throw Failure(CanonicalCacheReadFailureKind.MalformedData);
        }

        var hashes = await CanonicalCacheHash.CalculateAsync(
                stream,
                request.Identity,
                completion.DataLengthBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                hashes.DataSha256,
                completion.DataSha256)
            || !StringComparer.Ordinal.Equals(
                hashes.CanonicalSha256,
                completion.CanonicalSha256))
        {
            throw Failure(CanonicalCacheReadFailureKind.IntegrityMismatch);
        }
    }

    private static async Task<CanonicalCacheFooter> ScanDataAsync(
        FileStream stream,
        long expectedFrequency,
        CancellationToken cancellationToken)
    {
        try
        {
            stream.Position = 0;
            var headerBytes = new byte[CanonicalCacheFormat.HeaderLength];
            await stream.ReadExactlyAsync(headerBytes, cancellationToken)
                .ConfigureAwait(false);
            var header = CanonicalCacheFormat.DeserializeHeader(headerBytes);
            if (header.SourceStopwatchFrequency != expectedFrequency)
            {
                throw Failure(CanonicalCacheReadFailureKind.MalformedData);
            }

            long recordCount = 0;
            long observationCount = 0;
            long exclusionCount = 0;
            long gapCount = 0;
            long? firstSourceSequence = null;
            long? lastSourceSequence = null;
            long nextSourceSequence = 1;
            var sequenceExhausted = false;
            while (true)
            {
                var prefix = new byte[sizeof(uint)];
                await stream.ReadExactlyAsync(prefix, cancellationToken)
                    .ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                if (length == 0)
                {
                    var footerBytes = new byte[CanonicalCacheFormat.FooterLength];
                    prefix.CopyTo(footerBytes, 0);
                    await stream.ReadExactlyAsync(
                            footerBytes.AsMemory(sizeof(uint)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var footer = CanonicalCacheFormat.DeserializeFooter(footerBytes);
                    if (stream.ReadByte() != -1
                        || footer != new CanonicalCacheFooter(
                            recordCount,
                            observationCount,
                            exclusionCount,
                            gapCount,
                            firstSourceSequence,
                            lastSourceSequence))
                    {
                        throw Failure(CanonicalCacheReadFailureKind.MalformedData);
                    }

                    return footer;
                }

                if (length is < 1 or > CanonicalCacheFormat.MaximumRecordLength
                    || recordCount >= CanonicalCacheFormat.MaximumRecordCount)
                {
                    throw Failure(CanonicalCacheReadFailureKind.MalformedData);
                }

                var framed = new byte[checked((int)length + sizeof(uint))];
                prefix.CopyTo(framed, 0);
                await stream.ReadExactlyAsync(
                        framed.AsMemory(sizeof(uint)),
                        cancellationToken)
                    .ConfigureAwait(false);
                var record = CanonicalCacheFormat.DeserializeRecord(framed);
                if (sequenceExhausted)
                {
                    throw Failure(CanonicalCacheReadFailureKind.MalformedData);
                }

                switch (record.Kind)
                {
                    case CanonicalRecordKind.Gap
                        when record.FirstMissingSequence == nextSourceSequence
                             && record.LastMissingSequence is { } gapLast:
                        Advance(
                            gapLast,
                            ref nextSourceSequence,
                            ref sequenceExhausted);
                        gapCount++;
                        break;
                    case CanonicalRecordKind.Observation
                        when record.SourceSequence == nextSourceSequence:
                        Advance(
                            record.SourceSequence.Value,
                            ref nextSourceSequence,
                            ref sequenceExhausted);
                        observationCount++;
                        AccountSource(
                            record.SourceSequence.Value,
                            ref firstSourceSequence,
                            ref lastSourceSequence);
                        break;
                    case CanonicalRecordKind.Exclusion
                        when record.SourceSequence == nextSourceSequence:
                        Advance(
                            record.SourceSequence.Value,
                            ref nextSourceSequence,
                            ref sequenceExhausted);
                        exclusionCount++;
                        AccountSource(
                            record.SourceSequence.Value,
                            ref firstSourceSequence,
                            ref lastSourceSequence);
                        break;
                    default:
                        throw Failure(CanonicalCacheReadFailureKind.MalformedData);
                }

                recordCount++;
            }
        }
        catch (CanonicalCacheReadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                   EndOfStreamException
                   or InvalidDataException
                   or ArgumentException
                   or OverflowException)
        {
            throw Failure(
                CanonicalCacheReadFailureKind.MalformedData,
                exception);
        }
    }

    private static void Advance(
        long sequence,
        ref long next,
        ref bool exhausted)
    {
        if (sequence == long.MaxValue)
        {
            exhausted = true;
        }
        else
        {
            next = sequence + 1;
        }
    }

    private static void AccountSource(
        long sequence,
        ref long? first,
        ref long? last)
    {
        first ??= sequence;
        last = sequence;
    }

    private static async Task DisposeAndCollectAsync(
        IAsyncDisposable? resource,
        List<Exception> failures)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static CanonicalCacheReadException Failure(
        CanonicalCacheReadFailureKind kind,
        Exception? innerException = null,
        string? dataLeafName = null) =>
        new(
            kind,
            "The canonical cache entry could not be verified.",
            innerException,
            dataLeafName);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
