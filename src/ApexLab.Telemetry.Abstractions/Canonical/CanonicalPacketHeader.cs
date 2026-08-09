namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalPacketHeader
{
    public CanonicalPacketHeader(
        float sessionTimeSeconds,
        uint frameIdentifier,
        uint overallFrameIdentifier,
        byte playerCarIndex,
        byte secondaryPlayerCarIndex)
    {
        if (!float.IsFinite(sessionTimeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionTimeSeconds),
                "Session time must be finite.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            playerCarIndex,
            (byte)21);
        SessionTimeSeconds = sessionTimeSeconds;
        FrameIdentifier = frameIdentifier;
        OverallFrameIdentifier = overallFrameIdentifier;
        PlayerCarIndex = playerCarIndex;
        SecondaryPlayerCarIndex = secondaryPlayerCarIndex;
    }

    public float SessionTimeSeconds { get; }

    public uint FrameIdentifier { get; }

    public uint OverallFrameIdentifier { get; }

    public byte PlayerCarIndex { get; }

    public byte SecondaryPlayerCarIndex { get; }
}
