using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Raw;

[SupportedOSPlatform("windows")]
public static class RawEvidenceReader
{
    public static async Task<RawEvidenceCapture> OpenAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        RawEvidenceProtocolId expectedProtocolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedProtocolId);
        var capture = await OpenAsync(
            paths,
            captureId,
            cancellationToken).ConfigureAwait(false);
        if (capture.Completion.ProtocolId == expectedProtocolId)
        {
            return capture;
        }

        var mismatch = new RawEvidenceReadException(
            RawEvidenceReadFailureKind.UnsupportedProtocol);
        try
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(
                "Raw evidence protocol validation and cleanup failed.",
                mismatch,
                cleanupFailure);
        }

        throw mismatch;
    }

    public static async Task<RawEvidenceCapture> OpenAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(captureId);
        cancellationToken.ThrowIfCancellationRequested();

        WindowsRawEvidenceDirectory? directory = null;
        FileStream? manifestStream = null;
        FileStream? dataStream = null;
        try
        {
            directory = WindowsRawEvidenceDirectory.Open(paths);
            await RejectStagingFileAsync(
                directory,
                $"{captureId.Value}.apxraw.partial")
                .ConfigureAwait(false);
            await RejectStagingFileAsync(
                directory,
                $"{captureId.Value}.apxraw.json.partial")
                .ConfigureAwait(false);

            manifestStream = OpenRequired(
                directory,
                $"{captureId.Value}.apxraw.json");
            if (manifestStream.Length is < 1
                or > RawEvidenceFormat.MaximumManifestBytes)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.DeclaredLimitViolation);
            }

            var manifestBytes = new byte[
                checked((int)manifestStream.Length)];
            await ReadExactlyAsync(
                manifestStream,
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            await manifestStream.DisposeAsync().ConfigureAwait(false);
            manifestStream = null;

            var document = RawEvidenceManifestParser.Parse(
                manifestBytes);
            var manifest = document.Manifest;
            if (manifest.CaptureId != captureId)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.MalformedStructure);
            }

            dataStream = OpenRequired(
                directory,
                $"{captureId.Value}.apxraw");
            if (dataStream.Length < RawEvidenceLimits.MinimumFileBytes
                || dataStream.Length > manifest.Limits.MaximumFileBytes
                || dataStream.Length
                    > RawEvidenceLimits.AbsoluteMaximumFileBytes)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.DeclaredLimitViolation);
            }

            if (dataStream.Length < manifest.DataLengthBytes)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.TruncatedData);
            }

            if (dataStream.Length > manifest.DataLengthBytes)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.TrailingData);
            }

            await ValidateDataAsync(
                dataStream,
                document,
                cancellationToken).ConfigureAwait(false);
            dataStream.Position = RawEvidenceFormat.DataHeaderLength;

            var completion = new RawEvidenceCompletion(
                manifest.CaptureId,
                manifest.ProtocolId,
                manifest.RecordCount,
                manifest.DataLengthBytes,
                Convert.ToHexStringLower(document.Digest),
                new DateTimeOffset(
                    manifest.FinalizedUtcTicks,
                    TimeSpan.Zero));
            return new RawEvidenceCapture(
                directory,
                dataStream,
                completion,
                manifest.Limits,
                manifest.StopwatchFrequency,
                new DateTimeOffset(
                    manifest.CreatedUtcTicks,
                    TimeSpan.Zero));
        }
        catch (Exception exception)
        {
            var primary = ClassifyUnexpected(exception);
            var cleanupFailures =
                await RawEvidenceReadResourceCleanup.ReleaseAsync(
                    manifestStream,
                    dataStream,
                    directory).ConfigureAwait(false);
            if (cleanupFailures.Count != 0)
            {
                throw new AggregateException(
                    "Raw evidence validation and cleanup failed.",
                    [primary, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(primary).Throw();
            throw new UnreachableException();
        }
    }

    private static FileStream OpenRequired(
        WindowsRawEvidenceDirectory directory,
        string leafName)
    {
        try
        {
            return directory.OpenExistingReadOnly(leafName);
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode
                is WindowsRawEvidenceNative.ErrorFileNotFound
                or WindowsRawEvidenceNative.ErrorPathNotFound)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MissingOrIncomplete);
        }
    }

    private static Exception ClassifyUnexpected(Exception exception)
    {
        if (exception is RawEvidenceReadException
            or OperationCanceledException)
        {
            return exception;
        }

        if (exception is Win32Exception
            or UnauthorizedAccessException
            or IOException)
        {
            return Failure(
                RawEvidenceReadFailureKind.UnsafePath);
        }

        if (exception is OverflowException)
        {
            return Failure(
                RawEvidenceReadFailureKind.MalformedStructure,
                exception);
        }

        return exception;
    }

    private static RawEvidenceReadException Failure(
        RawEvidenceReadFailureKind kind,
        Exception? innerException = null) =>
        new(kind, innerException);

    private static async Task RejectStagingFileAsync(
        WindowsRawEvidenceDirectory directory,
        string leafName)
    {
        var staging = directory.TryOpenExistingReadOnly(leafName);
        if (staging is null)
        {
            return;
        }

        await staging.DisposeAsync().ConfigureAwait(false);
        throw Failure(
            RawEvidenceReadFailureKind.MissingOrIncomplete);
    }

    private static async Task ValidateDataAsync(
        FileStream stream,
        RawEvidenceManifestDocument document,
        CancellationToken cancellationToken)
    {
        var manifest = document.Manifest;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var header = new byte[RawEvidenceFormat.DataHeaderLength];
        await ReadAndHashExactlyAsync(
            stream,
            header,
            hash,
            cancellationToken).ConfigureAwait(false);
        ValidateDataHeader(header, manifest);

        var footerOffset = checked(
            manifest.DataLengthBytes - RawEvidenceFormat.FooterLength);
        var recordHeader =
            new byte[RawEvidenceFormat.RecordHeaderLength];
        var payloadBuffer = ArrayPool<byte>.Shared.Rent(
            manifest.Limits.MaximumPayloadBytes);
        long recordCount = 0;
        long recordsByteLength = 0;
        long? firstSequence = null;
        long? lastSequence = null;
        long? firstArrival = null;
        long? lastArrival = null;
        try
        {
            while (stream.Position < footerOffset)
            {
                if (footerOffset - stream.Position
                    < RawEvidenceFormat.RecordHeaderLength)
                {
                    throw Failure(
                        RawEvidenceReadFailureKind.TruncatedData);
                }

                await ReadAndHashExactlyAsync(
                    stream,
                    recordHeader,
                    hash,
                    cancellationToken).ConfigureAwait(false);
                var record = ParseRecordHeader(
                    recordHeader,
                    manifest.Limits.MaximumPayloadBytes);
                if (stream.Position + record.PayloadLength
                    > footerOffset)
                {
                    throw Failure(
                        RawEvidenceReadFailureKind.MalformedStructure);
                }

                ValidateOrdering(
                    record,
                    lastSequence,
                    lastArrival);
                await ReadAndHashExactlyAsync(
                    stream,
                    payloadBuffer.AsMemory(
                        0,
                        record.PayloadLength),
                    hash,
                    cancellationToken).ConfigureAwait(false);
                firstSequence ??= record.Sequence;
                firstArrival ??= record.ArrivalTimestamp;
                lastSequence = record.Sequence;
                lastArrival = record.ArrivalTimestamp;
                recordCount = checked(recordCount + 1);
                recordsByteLength = checked(
                    recordsByteLength
                    + RawEvidenceFormat.RecordHeaderLength
                    + record.PayloadLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                payloadBuffer,
                clearArray: true);
        }

        var footer = new byte[RawEvidenceFormat.FooterLength];
        await ReadAndHashExactlyAsync(
            stream,
            footer,
            hash,
            cancellationToken).ConfigureAwait(false);
        ValidateFooter(
            footer,
            recordCount,
            recordsByteLength,
            firstSequence,
            lastSequence,
            firstArrival,
            lastArrival);
        RequireManifestAgreement(
            manifest,
            recordCount,
            firstSequence,
            lastSequence,
            firstArrival,
            lastArrival);
        if (stream.Position != manifest.DataLengthBytes
            || stream.Length != manifest.DataLengthBytes)
        {
            throw Failure(
                stream.Position < manifest.DataLengthBytes
                    ? RawEvidenceReadFailureKind.TruncatedData
                    : RawEvidenceReadFailureKind.TrailingData);
        }

        hash.AppendData(document.Preimage);
        var actualDigest = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(
                actualDigest,
                document.Digest))
        {
            throw Failure(
                RawEvidenceReadFailureKind.HashMismatch);
        }
    }

    internal static RawEvidenceRecordHeader ParseRecordHeader(
        ReadOnlySpan<byte> bytes,
        int maximumPayloadBytes)
    {
        if (bytes.Length != RawEvidenceFormat.RecordHeaderLength
            || !bytes[..4].SequenceEqual("REC1"u8)
            || bytes[33] != 0)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        var encodedPayloadLength =
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]);
        if (encodedPayloadLength > int.MaxValue)
        {
            throw Failure(
                RawEvidenceReadFailureKind.DeclaredLimitViolation);
        }

        var payloadLength = (int)encodedPayloadLength;
        var recordLength =
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (payloadLength is < 1
            || payloadLength > maximumPayloadBytes
            || recordLength
                != RawEvidenceFormat.RecordHeaderLength
                + payloadLength)
        {
            throw Failure(
                payloadLength > maximumPayloadBytes
                    ? RawEvidenceReadFailureKind.DeclaredLimitViolation
                    : RawEvidenceReadFailureKind.MalformedStructure);
        }

        var sequence =
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var arrival =
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        var receivedUtcTicks =
            BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        if (sequence < 1
            || arrival < 0
            || !IsValidUtcTicks(receivedUtcTicks))
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        var addressFamily = bytes[32];
        var port =
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]);
        var scopeId =
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]);
        IPAddress address;
        if (addressFamily == 4)
        {
            if (!bytes[40..52].SequenceEqual(new byte[12])
                || scopeId != 0)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.MalformedStructure);
            }

            address = new IPAddress(bytes[36..40]);
        }
        else if (addressFamily == 6)
        {
            address = new IPAddress(bytes[36..52], scopeId);
        }
        else
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        return new RawEvidenceRecordHeader(
            sequence,
            arrival,
            new DateTimeOffset(
                receivedUtcTicks,
                TimeSpan.Zero),
            new DatagramSender(address, port),
            payloadLength);
    }

    private static void ValidateDataHeader(
        ReadOnlySpan<byte> bytes,
        RawEvidenceManifest manifest)
    {
        if (bytes.Length != RawEvidenceFormat.DataHeaderLength
            || !bytes[..8].SequenceEqual("APXRAW1\0"u8))
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) != 1)
        {
            throw Failure(
                RawEvidenceReadFailureKind.UnsupportedVersion);
        }

        if (
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..])
                != RawEvidenceFormat.DataHeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0
            || Encoding.ASCII.GetString(bytes[16..48])
                != manifest.CaptureId.Value
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[48..])
                != manifest.StopwatchFrequency
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[56..])
                != manifest.CreatedUtcTicks
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[64..])
                != manifest.Limits.MaximumPayloadBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..]) != 0)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }
    }

    private static void ValidateOrdering(
        RawEvidenceRecordHeader record,
        long? lastSequence,
        long? lastArrival)
    {
        if (lastSequence.HasValue
            && record.Sequence <= lastSequence.Value)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        if (lastArrival.HasValue
            && record.ArrivalTimestamp < lastArrival.Value)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }
    }

    private static void ValidateFooter(
        ReadOnlySpan<byte> bytes,
        long recordCount,
        long recordsByteLength,
        long? firstSequence,
        long? lastSequence,
        long? firstArrival,
        long? lastArrival)
    {
        if (bytes.Length != RawEvidenceFormat.FooterLength
            || !bytes[..8].SequenceEqual("APXFTR1\0"u8))
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) != 1)
        {
            throw Failure(
                RawEvidenceReadFailureKind.UnsupportedVersion);
        }

        if (
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..])
                != RawEvidenceFormat.FooterLength
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..])
                != checked((ulong)recordCount)
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[24..])
                != (firstSequence ?? -1)
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[32..])
                != (lastSequence ?? -1)
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..])
                != checked((ulong)recordsByteLength)
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[48..])
                != (firstArrival ?? -1)
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[56..])
                != (lastArrival ?? -1))
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }
    }

    private static void RequireManifestAgreement(
        RawEvidenceManifest manifest,
        long recordCount,
        long? firstSequence,
        long? lastSequence,
        long? firstArrival,
        long? lastArrival)
    {
        if (manifest.RecordCount != recordCount
            || manifest.FirstSequence != firstSequence
            || manifest.LastSequence != lastSequence
            || manifest.FirstArrivalTimestamp != firstArrival
            || manifest.LastArrivalTimestamp != lastArrival)
        {
            throw Failure(
                RawEvidenceReadFailureKind.MalformedStructure);
        }
    }

    private static bool IsValidUtcTicks(long ticks) =>
        ticks >= DateTimeOffset.MinValue.Ticks
        && ticks <= DateTimeOffset.MaxValue.Ticks;

    internal static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var consumed = 0;
        while (consumed < destination.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(
                    destination[consumed..],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.UnsafePath);
            }

            if (read == 0)
            {
                throw Failure(
                    RawEvidenceReadFailureKind.TruncatedData);
            }

            consumed = checked(consumed + read);
        }
    }

    private static async Task ReadAndHashExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(
            stream,
            destination,
            cancellationToken).ConfigureAwait(false);
        hash.AppendData(destination.Span);
    }
}

[SupportedOSPlatform("windows")]
public sealed class RawEvidenceCapture : IAsyncDisposable
{
    private readonly WindowsRawEvidenceDirectory _directory;
    private FileStream? _dataStream;
    private int _enumerationStarted;
    private int _disposed;

    internal RawEvidenceCapture(
        WindowsRawEvidenceDirectory directory,
        FileStream dataStream,
        RawEvidenceCompletion completion,
        RawEvidenceLimits limits,
        long stopwatchFrequency,
        DateTimeOffset createdAtUtc)
    {
        _directory = directory;
        _dataStream = dataStream;
        Completion = completion;
        Limits = limits;
        StopwatchFrequency = stopwatchFrequency;
        CreatedAtUtc = createdAtUtc;
    }

    public RawEvidenceCompletion Completion { get; }

    public RawEvidenceLimits Limits { get; }

    public long StopwatchFrequency { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public async IAsyncEnumerable<DatagramEnvelope> ReadAllAsync(
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Interlocked.Exchange(ref _enumerationStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A verified raw evidence capture can be enumerated only once.");
        }

        var stream = _dataStream!;
        try
        {
            stream.Position = RawEvidenceFormat.DataHeaderLength;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException)
        {
            throw new RawEvidenceReadException(
                RawEvidenceReadFailureKind.UnsafePath);
        }

        var recordHeader =
            new byte[RawEvidenceFormat.RecordHeaderLength];
        for (long index = 0;
             index < Completion.RecordCount;
             index++)
        {
            await RawEvidenceReader.ReadExactlyAsync(
                stream,
                recordHeader,
                cancellationToken).ConfigureAwait(false);
            var record = RawEvidenceReader.ParseRecordHeader(
                recordHeader,
                Limits.MaximumPayloadBytes);
            var payload = new byte[record.PayloadLength];
            await RawEvidenceReader.ReadExactlyAsync(
                stream,
                payload,
                cancellationToken).ConfigureAwait(false);
            yield return DatagramEnvelope.CopyFrom(
                record.Sequence,
                record.ArrivalTimestamp,
                record.ReceivedAtUtc,
                record.Sender,
                payload);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var dataStream = _dataStream;
        _dataStream = null;
        var failures =
            await RawEvidenceReadResourceCleanup.ReleaseAsync(
                manifestStream: null,
                dataStream: dataStream,
                directory: _directory).ConfigureAwait(false);
        if (failures.Count != 0)
        {
            throw new AggregateException(
                "Verified raw evidence handles could not be fully released.",
                failures);
        }
    }
}

internal readonly record struct RawEvidenceRecordHeader(
    long Sequence,
    long ArrivalTimestamp,
    DateTimeOffset ReceivedAtUtc,
    DatagramSender Sender,
    int PayloadLength);

[SupportedOSPlatform("windows")]
internal static class RawEvidenceReadResourceCleanup
{
    public static async Task<List<Exception>> ReleaseAsync(
        FileStream? manifestStream,
        FileStream? dataStream,
        WindowsRawEvidenceDirectory? directory)
    {
        var failures = new List<Exception>();
        await TryDisposeStreamAsync(
            manifestStream,
            failures).ConfigureAwait(false);
        await TryDisposeStreamAsync(
            dataStream,
            failures).ConfigureAwait(false);
        if (directory is not null)
        {
            try
            {
                directory.Dispose();
            }
            catch (Exception)
            {
                failures.Add(SanitizeCleanupFailure());
            }
        }

        return failures;
    }

    private static async Task TryDisposeStreamAsync(
        FileStream? stream,
        List<Exception> failures)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            failures.Add(SanitizeCleanupFailure());
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                failures.Add(SanitizeCleanupFailure());
            }
        }
    }

    private static IOException SanitizeCleanupFailure() =>
        new("A raw evidence read handle could not be released.");
}
