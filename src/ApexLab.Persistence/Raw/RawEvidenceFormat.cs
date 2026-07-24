using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Raw;

internal static class RawEvidenceFormat
{
    public const int DataHeaderLength = 72;
    public const int RecordHeaderLength = 60;
    public const int FooterLength = 64;
    public const int MaximumManifestBytes = 4_096;
    public const long MaximumStopwatchFrequency = 10_000_000_000;
    private const string DigestPlaceholder =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static void WriteDataHeader(
        Span<byte> destination,
        RawEvidenceCaptureId captureId,
        long stopwatchFrequency,
        long createdUtcTicks,
        int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        RequireLength(destination, DataHeaderLength, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfLessThan(stopwatchFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            stopwatchFrequency,
            MaximumStopwatchFrequency);
        ValidateUtcTicks(createdUtcTicks, nameof(createdUtcTicks));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumPayloadBytes,
            UdpDatagramLimits.MaximumPayloadLength);

        destination.Clear();
        "APXRAW1\0"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[10..],
            DataHeaderLength);
        Encoding.ASCII.GetBytes(captureId.Value, destination[16..48]);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[48..],
            stopwatchFrequency);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[56..],
            createdUtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[64..],
            checked((uint)maximumPayloadBytes));
    }

    public static void WriteRecordHeader(
        Span<byte> destination,
        DatagramEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireLength(destination, RecordHeaderLength, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            envelope.Payload.Length,
            1);

        var address = envelope.Sender.Address!;
        var addressBytes = address.GetAddressBytes();
        var addressFamily = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => (byte)4,
            AddressFamily.InterNetworkV6 => (byte)6,
            _ => throw new ArgumentException(
                "Raw evidence supports only IPv4 or IPv6 senders.",
                nameof(envelope)),
        };
        var scopeId = addressFamily == 6
            ? address.ScopeId
            : 0;
        if (scopeId is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(envelope),
                "An IPv6 scope ID must fit in UInt32.");
        }

        destination.Clear();
        "REC1"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..],
            checked((uint)(RecordHeaderLength + envelope.Payload.Length)));
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[8..],
            envelope.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[16..],
            envelope.MonotonicTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[24..],
            envelope.ReceivedAtUtc.UtcTicks);
        destination[32] = addressFamily;
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[34..],
            checked((ushort)envelope.Sender.Port));
        addressBytes.CopyTo(destination[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[52..],
            addressFamily == 6
                ? checked((uint)scopeId)
                : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[56..],
            checked((uint)envelope.Payload.Length));
    }

    public static void WriteFooter(
        Span<byte> destination,
        long recordCount,
        long? firstSequence,
        long? lastSequence,
        long recordsByteLength,
        long? firstArrivalTimestamp,
        long? lastArrivalTimestamp)
    {
        RequireLength(destination, FooterLength, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(recordsByteLength);

        if (recordCount == 0)
        {
            if (recordsByteLength != 0
                || firstSequence.HasValue
                || lastSequence.HasValue
                || firstArrivalTimestamp.HasValue
                || lastArrivalTimestamp.HasValue)
            {
                throw new ArgumentException(
                    "An empty footer requires zero bytes and null first/last values.",
                    nameof(recordCount));
            }
        }
        else if (firstSequence is not >= 1
                 || lastSequence < firstSequence
                 || firstArrivalTimestamp is not >= 0
                 || lastArrivalTimestamp < firstArrivalTimestamp)
        {
            throw new ArgumentException(
                "A populated footer requires ordered first/last values.",
                nameof(recordCount));
        }

        destination.Clear();
        "APXFTR1\0"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[10..],
            FooterLength);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[16..],
            checked((ulong)recordCount));
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[24..],
            firstSequence ?? -1);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[32..],
            lastSequence ?? -1);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[40..],
            checked((ulong)recordsByteLength));
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[48..],
            firstArrivalTimestamp ?? -1);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[56..],
            lastArrivalTimestamp ?? -1);
    }

    public static byte[] SerializeManifestPreimage(
        RawEvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("dataFormatVersion", 1);
            writer.WriteString("captureId", manifest.CaptureId.Value);
            writer.WriteString("protocolId", manifest.ProtocolId.Value);
            writer.WriteString(
                "dataFileName",
                $"{manifest.CaptureId.Value}.apxraw");
            writer.WriteNumber("dataLengthBytes", manifest.DataLengthBytes);
            writer.WriteString("sha256", DigestPlaceholder);
            writer.WriteNumber("recordCount", manifest.RecordCount);
            WriteNullableNumber(
                writer,
                "firstSequence",
                manifest.FirstSequence);
            WriteNullableNumber(
                writer,
                "lastSequence",
                manifest.LastSequence);
            WriteNullableNumber(
                writer,
                "firstArrivalTimestamp",
                manifest.FirstArrivalTimestamp);
            WriteNullableNumber(
                writer,
                "lastArrivalTimestamp",
                manifest.LastArrivalTimestamp);
            writer.WriteNumber(
                "stopwatchFrequency",
                manifest.StopwatchFrequency);
            writer.WriteNumber(
                "createdUtcTicks",
                manifest.CreatedUtcTicks);
            writer.WriteNumber(
                "finalizedUtcTicks",
                manifest.FinalizedUtcTicks);
            writer.WriteStartObject("limits");
            writer.WriteNumber(
                "maximumDurationMilliseconds",
                manifest.Limits.MaximumDuration.Ticks
                / TimeSpan.TicksPerMillisecond);
            writer.WriteNumber(
                "maximumFileBytes",
                manifest.Limits.MaximumFileBytes);
            writer.WriteNumber(
                "minimumFreeSpaceBytes",
                manifest.Limits.MinimumFreeSpaceBytes);
            writer.WriteNumber(
                "maximumPayloadBytes",
                manifest.Limits.MaximumPayloadBytes);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaximumManifestBytes)
        {
            throw new InvalidOperationException(
                "The canonical raw evidence manifest exceeded its v1 bound.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] ApplyDigest(
        ReadOnlySpan<byte> manifestPreimage,
        ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32)
        {
            throw new ArgumentException(
                "A SHA-256 digest must contain exactly 32 bytes.",
                nameof(digest));
        }

        ReadOnlySpan<byte> propertyPrefix = "\"sha256\":\""u8;
        var prefixIndex = manifestPreimage.IndexOf(propertyPrefix);
        if (prefixIndex < 0)
        {
            throw new ArgumentException(
                "The canonical manifest digest property is missing.",
                nameof(manifestPreimage));
        }

        var digestOffset = checked(prefixIndex + propertyPrefix.Length);
        var placeholder = manifestPreimage.Slice(
            digestOffset,
            DigestPlaceholder.Length);
        if (!placeholder.SequenceEqual(DigestPlaceholderu8)
            || manifestPreimage[digestOffset + DigestPlaceholder.Length] != '"'
            || manifestPreimage[(digestOffset + DigestPlaceholder.Length + 1)..]
                .IndexOf(propertyPrefix) >= 0)
        {
            throw new ArgumentException(
                "The canonical manifest digest placeholder is invalid.",
                nameof(manifestPreimage));
        }

        var result = manifestPreimage.ToArray();
        const string lowercaseHex = "0123456789abcdef";
        for (var index = 0; index < digest.Length; index++)
        {
            result[digestOffset + (index * 2)] =
                (byte)lowercaseHex[digest[index] >> 4];
            result[digestOffset + (index * 2) + 1] =
                (byte)lowercaseHex[digest[index] & 0x0F];
        }

        return result;
    }

    private static ReadOnlySpan<byte> DigestPlaceholderu8 =>
        "0000000000000000000000000000000000000000000000000000000000000000"u8;

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void RequireLength(
        ReadOnlySpan<byte> destination,
        int required,
        string parameterName)
    {
        if (destination.Length != required)
        {
            throw new ArgumentException(
                $"The destination must contain exactly {required} bytes.",
                parameterName);
        }
    }

    private static void ValidateUtcTicks(long value, string parameterName)
    {
        if (value < DateTimeOffset.MinValue.Ticks
            || value > DateTimeOffset.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A valid .NET UTC tick value is required.");
        }
    }
}
