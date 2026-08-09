namespace ApexLab.Protocols.F125.Decoding;

public enum F125DecodeReason
{
    None = 0,
    MalformedHeader = 1,
    UnsupportedFormat = 2,
    UnsupportedYear = 3,
    UnknownPacketId = 4,
    UnexpectedPacketFamily = 5,
    UnsupportedPacketVersion = 6,
    InvalidPacketLength = 7,
    PrivacyExcludedPacket = 8,
    InvalidPlayerCarIndex = 9,
    MalformedSelectedField = 10,
    EventCodeOutsideSlice = 11,
}
