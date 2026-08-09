namespace ApexLab.Telemetry.Abstractions.Canonical;

public enum CanonicalProjectionReason
{
    None = 0,
    CompatibleFamilyOutsideSlice = 1,
    EventCodeOutsideSlice = 2,
    MalformedHeader = 3,
    UnsupportedFormat = 4,
    UnsupportedYear = 5,
    UnknownPacketId = 6,
    UnexpectedPacketFamily = 7,
    UnsupportedPacketVersion = 8,
    InvalidPacketLength = 9,
    PrivacyExcludedPacket = 10,
    InvalidPlayerCarIndex = 11,
    MalformedSelectedField = 12,
}
