namespace ApexLab.Telemetry.Abstractions.Protocol;

public readonly record struct TelemetryHeaderMetadata
{
    public TelemetryHeaderMetadata(
        ushort packetFormat,
        byte gameYear,
        byte gameMajorVersion,
        byte gameMinorVersion,
        byte packetVersion,
        byte packetId,
        ulong sessionUid,
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

        PacketFormat = packetFormat;
        GameYear = gameYear;
        GameMajorVersion = gameMajorVersion;
        GameMinorVersion = gameMinorVersion;
        PacketVersion = packetVersion;
        PacketId = packetId;
        SessionUid = sessionUid;
        SessionTimeSeconds = sessionTimeSeconds;
        FrameIdentifier = frameIdentifier;
        OverallFrameIdentifier = overallFrameIdentifier;
        PlayerCarIndex = playerCarIndex;
        SecondaryPlayerCarIndex = secondaryPlayerCarIndex;
    }

    public ushort PacketFormat { get; }

    public byte GameYear { get; }

    public byte GameMajorVersion { get; }

    public byte GameMinorVersion { get; }

    public byte PacketVersion { get; }

    public byte PacketId { get; }

    public ulong SessionUid { get; }

    public float SessionTimeSeconds { get; }

    public uint FrameIdentifier { get; }

    public uint OverallFrameIdentifier { get; }

    public byte PlayerCarIndex { get; }

    public byte SecondaryPlayerCarIndex { get; }
}
