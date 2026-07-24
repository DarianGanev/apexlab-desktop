namespace ApexLab.Telemetry.Abstractions.Protocol;

public enum TelemetryPacketClassification
{
    Unspecified,
    Compatible,
    MalformedHeader,
    UnsupportedFormat,
    UnsupportedYear,
    UnknownPacketId,
    UnsupportedPacketVersion,
    InvalidPacketLength,
    ExcludedPrivacyPacket,
}
