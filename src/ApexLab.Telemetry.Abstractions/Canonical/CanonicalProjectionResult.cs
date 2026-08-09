namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalProjectionResult
{
    private CanonicalProjectionResult(
        CanonicalProjectionDisposition disposition,
        CanonicalProjectionReason reason,
        byte? packetId,
        CanonicalPacket? packet)
    {
        Disposition = disposition;
        Reason = reason;
        PacketId = packetId;
        Packet = packet;
    }

    public CanonicalProjectionDisposition Disposition { get; }
    public CanonicalProjectionReason Reason { get; }
    public byte? PacketId { get; }
    public CanonicalPacket? Packet { get; }

    public static CanonicalProjectionResult Projected(CanonicalPacket packet)
    {
        if (packet.Family == CanonicalPacketFamily.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(packet));
        }

        return new(
            CanonicalProjectionDisposition.Projected,
            CanonicalProjectionReason.None,
            packetId: null,
            packet);
    }

    public static CanonicalProjectionResult Excluded(
        byte packetId,
        CanonicalProjectionReason reason)
    {
        if (reason is not (
            CanonicalProjectionReason.CompatibleFamilyOutsideSlice
            or CanonicalProjectionReason.EventCodeOutsideSlice))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(
            CanonicalProjectionDisposition.Excluded,
            reason,
            packetId,
            packet: null);
    }

    public static CanonicalProjectionResult Rejected(
        CanonicalProjectionReason reason,
        byte? packetId = null)
    {
        if (!Enum.IsDefined(reason)
            || reason is CanonicalProjectionReason.None
                or CanonicalProjectionReason.CompatibleFamilyOutsideSlice
                or CanonicalProjectionReason.EventCodeOutsideSlice)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(
            CanonicalProjectionDisposition.Rejected,
            reason,
            packetId,
            packet: null);
    }
}
