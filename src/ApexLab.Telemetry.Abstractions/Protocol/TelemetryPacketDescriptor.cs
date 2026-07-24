namespace ApexLab.Telemetry.Abstractions.Protocol;

public sealed record TelemetryPacketDescriptor
{
    public const int MaximumUdpPayloadLength = 65_507;

    public TelemetryPacketDescriptor(
        string familyName,
        byte packetId,
        byte packetVersion,
        int datagramLength,
        TelemetryPacketPrivacyDisposition privacyDisposition)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            throw new ArgumentException("A packet family name is required.", nameof(familyName));
        }

        if (datagramLength is < 1 or > MaximumUdpPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(datagramLength),
                "A packet datagram length must be between 1 and 65507 bytes.");
        }

        if (privacyDisposition == TelemetryPacketPrivacyDisposition.Unspecified
            || !Enum.IsDefined(privacyDisposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(privacyDisposition),
                "An explicit, known privacy disposition is required.");
        }

        FamilyName = familyName;
        PacketId = packetId;
        PacketVersion = packetVersion;
        DatagramLength = datagramLength;
        PrivacyDisposition = privacyDisposition;
    }

    public string FamilyName { get; }

    public byte PacketId { get; }

    public byte PacketVersion { get; }

    public int DatagramLength { get; }

    public TelemetryPacketPrivacyDisposition PrivacyDisposition { get; }
}
