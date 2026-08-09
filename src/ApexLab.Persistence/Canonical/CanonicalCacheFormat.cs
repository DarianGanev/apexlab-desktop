using System.Text;
using ApexLab.Application.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Canonical;

internal static class CanonicalCacheFormat
{
    private static ReadOnlySpan<byte> HeaderMagic => "APXCAN1\0"u8;
    private static ReadOnlySpan<byte> FooterMagic => "APXEND1\0"u8;

    public const ushort DataFormatVersion = 1;
    public const int HeaderLength = 20;
    public const int FooterLength = 65;
    public const int MaximumRecordLength = 1_024;
    public const long MaximumDataLength = 16L * 1_024 * 1_024 * 1_024;
    public const long MaximumRecordCount =
        (MaximumDataLength - HeaderLength - FooterLength) / 15;

    public static byte[] SerializeHeader(long sourceStopwatchFrequency)
    {
        _ = new CanonicalCacheHeader(sourceStopwatchFrequency);
        using var stream = new MemoryStream(HeaderLength);
        using var writer = NewWriter(stream);
        writer.Write(HeaderMagic);
        writer.Write(DataFormatVersion);
        writer.Write((ushort)HeaderLength);
        writer.Write(sourceStopwatchFrequency);
        return RequireLength(stream, HeaderLength);
    }

    public static CanonicalCacheHeader DeserializeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != HeaderLength)
        {
            throw FormatFailure();
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = NewReader(stream);
            RequireMagic(reader, HeaderMagic);
            RequireEqual(reader.ReadUInt16(), DataFormatVersion);
            RequireEqual(reader.ReadUInt16(), (ushort)HeaderLength);
            return new(reader.ReadInt64());
        }
        catch (Exception exception) when (IsParseFailure(exception))
        {
            throw FormatFailure(exception);
        }
    }

    public static byte[] SerializeRecord(CanonicalRecord record)
    {
        using var body = new MemoryStream(MaximumRecordLength);
        using (var writer = NewWriter(body))
        {
            writer.Write((byte)record.Kind);
            switch (record.Kind)
            {
                case CanonicalRecordKind.Observation:
                    WriteObservation(writer, record);
                    break;
                case CanonicalRecordKind.Exclusion:
                    WriteExclusion(writer, record);
                    break;
                case CanonicalRecordKind.Gap:
                    WriteGap(writer, record);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(record));
            }
        }

        if (body.Length is < 1 or > MaximumRecordLength)
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        using var framed = new MemoryStream(checked((int)body.Length + sizeof(uint)));
        using (var writer = NewWriter(framed))
        {
            writer.Write(checked((uint)body.Length));
            writer.Write(body.GetBuffer(), 0, checked((int)body.Length));
        }

        return framed.ToArray();
    }

    public static CanonicalRecord DeserializeRecord(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < sizeof(uint) + 1)
        {
            throw FormatFailure();
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = NewReader(stream);
            var length = reader.ReadUInt32();
            if (length is < 1 or > MaximumRecordLength
                || length != bytes.Length - sizeof(uint))
            {
                throw FormatFailure();
            }

            var kind = (CanonicalRecordKind)reader.ReadByte();
            var record = kind switch
            {
                CanonicalRecordKind.Observation => ReadObservation(reader),
                CanonicalRecordKind.Exclusion => ReadExclusion(reader),
                CanonicalRecordKind.Gap => ReadGap(reader),
                _ => throw FormatFailure(),
            };
            if (stream.Position != stream.Length)
            {
                throw FormatFailure();
            }

            return record;
        }
        catch (Exception exception) when (IsParseFailure(exception))
        {
            throw FormatFailure(exception);
        }
    }

    public static byte[] SerializeFooter(CanonicalCacheFooter footer)
    {
        using var stream = new MemoryStream(FooterLength);
        using var writer = NewWriter(stream);
        writer.Write(0U);
        writer.Write(FooterMagic);
        writer.Write(DataFormatVersion);
        writer.Write((ushort)(FooterLength - sizeof(uint)));
        writer.Write(footer.RecordCount);
        writer.Write(footer.ObservationCount);
        writer.Write(footer.ExclusionCount);
        writer.Write(footer.GapCount);
        writer.Write((byte)(footer.FirstSourceSequence.HasValue ? 1 : 0));
        writer.Write(footer.FirstSourceSequence ?? 0);
        writer.Write(footer.LastSourceSequence ?? 0);
        return RequireLength(stream, FooterLength);
    }

    public static CanonicalCacheFooter DeserializeFooter(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FooterLength)
        {
            throw FormatFailure();
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = NewReader(stream);
            RequireEqual(reader.ReadUInt32(), 0U);
            RequireMagic(reader, FooterMagic);
            RequireEqual(reader.ReadUInt16(), DataFormatVersion);
            RequireEqual(
                reader.ReadUInt16(),
                (ushort)(FooterLength - sizeof(uint)));
            var recordCount = reader.ReadInt64();
            var observationCount = reader.ReadInt64();
            var exclusionCount = reader.ReadInt64();
            var gapCount = reader.ReadInt64();
            var hasBoundary = ReadBooleanByte(reader);
            var first = reader.ReadInt64();
            var last = reader.ReadInt64();
            if (!hasBoundary && (first != 0 || last != 0))
            {
                throw FormatFailure();
            }

            return new(
                recordCount,
                observationCount,
                exclusionCount,
                gapCount,
                hasBoundary ? first : null,
                hasBoundary ? last : null);
        }
        catch (Exception exception) when (IsParseFailure(exception))
        {
            throw FormatFailure(exception);
        }
    }

    private static void WriteObservation(
        BinaryWriter writer,
        CanonicalRecord record)
    {
        if (record.SourceSequence is not { } sequence
            || record.ArrivalTimestamp is not { } arrival
            || record.Packet is not { } packet)
        {
            throw new ArgumentException(
                "An observation record is incomplete.",
                nameof(record));
        }

        writer.Write(sequence);
        writer.Write(arrival);
        writer.Write((byte)packet.Family);
        WritePacketHeader(writer, packet.Header);
        switch (packet.Family)
        {
            case CanonicalPacketFamily.Motion when packet.Motion is { } motion:
                WriteSingle(writer, motion.WorldPositionXMetres);
                WriteSingle(writer, motion.WorldPositionYMetres);
                WriteSingle(writer, motion.WorldPositionZMetres);
                break;
            case CanonicalPacketFamily.Session when packet.Session is { } session:
                WriteSession(writer, session);
                break;
            case CanonicalPacketFamily.LapData when packet.Lap is { } lap:
                WriteLap(writer, lap);
                break;
            case CanonicalPacketFamily.Event when packet.Event is { } @event:
                WriteEvent(writer, @event);
                break;
            case CanonicalPacketFamily.CarTelemetry
                when packet.CarTelemetry is { } telemetry:
                writer.Write(telemetry.SpeedKilometresPerHour);
                WriteSingle(writer, telemetry.ThrottleRatio);
                WriteSingle(writer, telemetry.BrakeRatio);
                writer.Write(telemetry.Gear);
                break;
            default:
                throw new ArgumentException(
                    "The canonical packet union is incomplete.",
                    nameof(record));
        }
    }

    private static void WriteExclusion(BinaryWriter writer, CanonicalRecord record)
    {
        if (record.SourceSequence is not { } sequence
            || record.PacketId is not { } packetId
            || record.ExclusionReason is not { } reason)
        {
            throw new ArgumentException(
                "An exclusion record is incomplete.",
                nameof(record));
        }

        writer.Write(sequence);
        writer.Write(packetId);
        writer.Write((byte)reason);
    }

    private static void WriteGap(BinaryWriter writer, CanonicalRecord record)
    {
        if (record.FirstMissingSequence is not { } first
            || record.LastMissingSequence is not { } last
            || record.GapReason is not { } reason)
        {
            throw new ArgumentException(
                "A gap record is incomplete.",
                nameof(record));
        }

        writer.Write(first);
        writer.Write(last);
        writer.Write((byte)reason);
    }

    private static void WritePacketHeader(
        BinaryWriter writer,
        CanonicalPacketHeader header)
    {
        WriteSingle(writer, header.SessionTimeSeconds);
        writer.Write(header.FrameIdentifier);
        writer.Write(header.OverallFrameIdentifier);
        writer.Write(header.PlayerCarIndex);
        writer.Write(header.SecondaryPlayerCarIndex);
    }

    private static void WriteSession(
        BinaryWriter writer,
        CanonicalSessionPacket value)
    {
        writer.Write(value.Weather);
        writer.Write(value.TrackTemperatureCelsius);
        writer.Write(value.AirTemperatureCelsius);
        writer.Write(value.TrackLengthMetres);
        writer.Write(value.SessionType);
        writer.Write(value.TrackId);
        writer.Write(value.Formula);
        WriteBooleanByte(writer, value.IsSpectating);
        WriteBooleanByte(writer, value.IsNetworkGame);
        writer.Write(value.SteeringAssist);
        writer.Write(value.BrakingAssist);
        writer.Write(value.GearboxAssist);
        writer.Write(value.PitAssist);
        writer.Write(value.PitReleaseAssist);
        writer.Write(value.ErsAssist);
        writer.Write(value.DrsAssist);
        writer.Write(value.DynamicRacingLine);
        writer.Write(value.DynamicRacingLineType);
        writer.Write(value.GameMode);
        writer.Write(value.RuleSet);
        writer.Write(value.TimeOfDayMinutesSinceMidnight);
        WriteBooleanByte(writer, value.EqualCarPerformance);
        writer.Write(value.RecoveryMode);
    }

    private static void WriteLap(BinaryWriter writer, CanonicalLapPacket value)
    {
        writer.Write(value.LastLapTimeMilliseconds);
        writer.Write(value.CurrentLapTimeMilliseconds);
        WriteSingle(writer, value.LapDistanceMetres);
        WriteSingle(writer, value.TotalDistanceMetres);
        writer.Write(value.CurrentLapNumber);
        writer.Write(value.PitStatus);
        writer.Write(value.Sector);
        WriteBooleanByte(writer, value.CurrentLapInvalid);
        writer.Write(value.DriverStatus);
        writer.Write(value.ResultStatus);
    }

    private static void WriteEvent(BinaryWriter writer, CanonicalEventPacket value)
    {
        writer.Write((byte)value.Kind);
        var hasFlashback = value.FlashbackFrameIdentifier.HasValue
            && value.FlashbackSessionTimeSeconds.HasValue;
        WriteBooleanByte(writer, hasFlashback);
        if (hasFlashback)
        {
            writer.Write(value.FlashbackFrameIdentifier!.Value);
            WriteSingle(writer, value.FlashbackSessionTimeSeconds!.Value);
        }
    }

    private static CanonicalRecord ReadObservation(BinaryReader reader)
    {
        var sequence = reader.ReadInt64();
        var arrival = reader.ReadInt64();
        var family = (CanonicalPacketFamily)reader.ReadByte();
        var header = ReadPacketHeader(reader);
        var packet = family switch
        {
            CanonicalPacketFamily.Motion => CanonicalPacket.CreateMotion(
                header,
                new CanonicalMotionPacket(
                    ReadSingle(reader),
                    ReadSingle(reader),
                    ReadSingle(reader))),
            CanonicalPacketFamily.Session => CanonicalPacket.CreateSession(
                header,
                ReadSession(reader)),
            CanonicalPacketFamily.LapData => CanonicalPacket.CreateLapData(
                header,
                ReadLap(reader)),
            CanonicalPacketFamily.Event => CanonicalPacket.CreateEvent(
                header,
                ReadEvent(reader)),
            CanonicalPacketFamily.CarTelemetry =>
                CanonicalPacket.CreateCarTelemetry(
                    header,
                    new CanonicalCarTelemetryPacket(
                        reader.ReadUInt16(),
                        ReadSingle(reader),
                        ReadSingle(reader),
                        reader.ReadSByte())),
            _ => throw FormatFailure(),
        };
        return CanonicalRecord.Observation(sequence, arrival, packet);
    }

    private static CanonicalRecord ReadExclusion(BinaryReader reader) =>
        CanonicalRecord.Exclusion(
            reader.ReadInt64(),
            reader.ReadByte(),
            (CanonicalExclusionReason)reader.ReadByte());

    private static CanonicalRecord ReadGap(BinaryReader reader) =>
        CanonicalRecord.Gap(
            reader.ReadInt64(),
            reader.ReadInt64(),
            (CanonicalGapReason)reader.ReadByte());

    private static CanonicalPacketHeader ReadPacketHeader(BinaryReader reader) =>
        new(
            ReadSingle(reader),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadByte(),
            reader.ReadByte());

    private static CanonicalSessionPacket ReadSession(BinaryReader reader) =>
        new(
            reader.ReadByte(),
            reader.ReadSByte(),
            reader.ReadSByte(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadSByte(),
            reader.ReadByte(),
            ReadBooleanByte(reader),
            ReadBooleanByte(reader),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadUInt32(),
            ReadBooleanByte(reader),
            reader.ReadByte());

    private static CanonicalLapPacket ReadLap(BinaryReader reader) =>
        new(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            ReadSingle(reader),
            ReadSingle(reader),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            ReadBooleanByte(reader),
            reader.ReadByte(),
            reader.ReadByte());

    private static CanonicalEventPacket ReadEvent(BinaryReader reader)
    {
        var kind = (CanonicalEventKind)reader.ReadByte();
        var hasFlashback = ReadBooleanByte(reader);
        if (kind == CanonicalEventKind.Flashback && hasFlashback)
        {
            return CanonicalEventPacket.Flashback(
                reader.ReadUInt32(),
                ReadSingle(reader));
        }

        if (hasFlashback)
        {
            throw FormatFailure();
        }

        return kind switch
        {
            CanonicalEventKind.SessionStarted =>
                CanonicalEventPacket.SessionStarted(),
            CanonicalEventKind.SessionEnded =>
                CanonicalEventPacket.SessionEnded(),
            _ => throw FormatFailure(),
        };
    }

    private static BinaryWriter NewWriter(Stream stream) =>
        new(stream, Encoding.UTF8, leaveOpen: true);

    private static BinaryReader NewReader(Stream stream) =>
        new(stream, Encoding.UTF8, leaveOpen: true);

    private static void WriteSingle(BinaryWriter writer, float value) =>
        writer.Write(BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(BinaryReader reader)
    {
        var value = BitConverter.Int32BitsToSingle(reader.ReadInt32());
        if (!float.IsFinite(value))
        {
            throw FormatFailure();
        }

        return value;
    }

    private static void WriteBooleanByte(BinaryWriter writer, bool value) =>
        writer.Write((byte)(value ? 1 : 0));

    private static bool ReadBooleanByte(BinaryReader reader) =>
        reader.ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw FormatFailure(),
        };

    private static void RequireMagic(
        BinaryReader reader,
        ReadOnlySpan<byte> expected)
    {
        if (!reader.ReadBytes(expected.Length).AsSpan().SequenceEqual(expected))
        {
            throw FormatFailure();
        }
    }

    private static void RequireEqual<T>(T actual, T expected)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw FormatFailure();
        }
    }

    private static byte[] RequireLength(MemoryStream stream, int expected)
    {
        if (stream.Length != expected)
        {
            throw new InvalidOperationException(
                "The canonical format length contract was violated.");
        }

        return stream.ToArray();
    }

    private static bool IsParseFailure(Exception exception) =>
        exception is EndOfStreamException
            or ArgumentException
            or OverflowException;

    private static InvalidDataException FormatFailure(
        Exception? innerException = null) =>
        new("The canonical cache data is malformed.", innerException);
}

internal readonly record struct CanonicalCacheHeader
{
    public CanonicalCacheHeader(long sourceStopwatchFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStopwatchFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            sourceStopwatchFrequency,
            10_000_000_000);
        SourceStopwatchFrequency = sourceStopwatchFrequency;
    }

    public long SourceStopwatchFrequency { get; }
}

internal readonly record struct CanonicalCacheFooter
{
    public CanonicalCacheFooter(
        long recordCount,
        long observationCount,
        long exclusionCount,
        long gapCount,
        long? firstSourceSequence,
        long? lastSourceSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(exclusionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(gapCount);
        if (checked(observationCount + exclusionCount + gapCount) != recordCount)
        {
            throw new ArgumentException("Canonical footer accounting must balance.");
        }

        if (recordCount == 0)
        {
            if (firstSourceSequence.HasValue || lastSourceSequence.HasValue)
            {
                throw new ArgumentException(
                    "An empty canonical footer cannot have source boundaries.");
            }
        }
        else if (firstSourceSequence is not >= 1
                 || lastSourceSequence < firstSourceSequence)
        {
            throw new ArgumentException(
                "A populated canonical footer requires ordered source boundaries.");
        }

        RecordCount = recordCount;
        ObservationCount = observationCount;
        ExclusionCount = exclusionCount;
        GapCount = gapCount;
        FirstSourceSequence = firstSourceSequence;
        LastSourceSequence = lastSourceSequence;
    }

    public long RecordCount { get; }
    public long ObservationCount { get; }
    public long ExclusionCount { get; }
    public long GapCount { get; }
    public long? FirstSourceSequence { get; }
    public long? LastSourceSequence { get; }
}
