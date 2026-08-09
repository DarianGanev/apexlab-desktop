namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalEventPacket
{
    private CanonicalEventPacket(
        CanonicalEventKind kind,
        uint? flashbackFrameIdentifier,
        float? flashbackSessionTimeSeconds)
    {
        Kind = kind;
        FlashbackFrameIdentifier = flashbackFrameIdentifier;
        FlashbackSessionTimeSeconds = flashbackSessionTimeSeconds;
    }

    public CanonicalEventKind Kind { get; }

    public uint? FlashbackFrameIdentifier { get; }

    public float? FlashbackSessionTimeSeconds { get; }

    public static CanonicalEventPacket SessionStarted() =>
        new(CanonicalEventKind.SessionStarted, null, null);

    public static CanonicalEventPacket SessionEnded() =>
        new(CanonicalEventKind.SessionEnded, null, null);

    public static CanonicalEventPacket Flashback(
        uint frameIdentifier,
        float sessionTimeSeconds)
    {
        CanonicalValueValidation.RequireFinite(
            sessionTimeSeconds,
            nameof(sessionTimeSeconds));
        return new(
            CanonicalEventKind.Flashback,
            frameIdentifier,
            sessionTimeSeconds);
    }
}
