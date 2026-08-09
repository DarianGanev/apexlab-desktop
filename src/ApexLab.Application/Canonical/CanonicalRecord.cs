using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Canonical;

public readonly record struct CanonicalRecord
{
    private CanonicalRecord(
        CanonicalRecordKind kind,
        long? sourceSequence = null,
        long? arrivalTimestamp = null,
        CanonicalPacket? packet = null,
        byte? packetId = null,
        CanonicalExclusionReason? exclusionReason = null,
        long? firstMissingSequence = null,
        long? lastMissingSequence = null,
        CanonicalGapReason? gapReason = null)
    {
        Kind = kind;
        SourceSequence = sourceSequence;
        ArrivalTimestamp = arrivalTimestamp;
        Packet = packet;
        PacketId = packetId;
        ExclusionReason = exclusionReason;
        FirstMissingSequence = firstMissingSequence;
        LastMissingSequence = lastMissingSequence;
        GapReason = gapReason;
    }

    public CanonicalRecordKind Kind { get; }
    public long? SourceSequence { get; }
    public long? ArrivalTimestamp { get; }
    public CanonicalPacket? Packet { get; }
    public byte? PacketId { get; }
    public CanonicalExclusionReason? ExclusionReason { get; }
    public long? FirstMissingSequence { get; }
    public long? LastMissingSequence { get; }
    public CanonicalGapReason? GapReason { get; }

    public static CanonicalRecord Observation(
        long sourceSequence,
        long arrivalTimestamp,
        CanonicalPacket packet)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(arrivalTimestamp);
        if (packet.Family == CanonicalPacketFamily.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        return new(
            CanonicalRecordKind.Observation,
            sourceSequence,
            arrivalTimestamp,
            packet);
    }

    public static CanonicalRecord Exclusion(
        long sourceSequence,
        byte packetId,
        CanonicalExclusionReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSequence, 1);
        if (!Enum.IsDefined(reason)
            || reason == CanonicalExclusionReason.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(
            CanonicalRecordKind.Exclusion,
            sourceSequence,
            packetId: packetId,
            exclusionReason: reason);
    }

    public static CanonicalRecord Gap(
        long firstMissingSequence,
        long lastMissingSequence,
        CanonicalGapReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstMissingSequence, 1);
        if (lastMissingSequence < firstMissingSequence)
        {
            throw new ArgumentException(
                "A canonical gap range must be ordered.",
                nameof(lastMissingSequence));
        }

        if (!Enum.IsDefined(reason)
            || reason == CanonicalGapReason.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(
            CanonicalRecordKind.Gap,
            firstMissingSequence: firstMissingSequence,
            lastMissingSequence: lastMissingSequence,
            gapReason: reason);
    }
}
