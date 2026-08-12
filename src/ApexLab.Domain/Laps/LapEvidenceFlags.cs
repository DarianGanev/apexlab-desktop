namespace ApexLab.Domain.Laps;

[Flags]
public enum LapEvidenceFlags
{
    None = 0,
    InvalidationObserved = 1 << 0,
    PitObserved = 1 << 1,
    FlashbackObserved = 1 << 2,
    MaterialGapObserved = 1 << 3,
    ContextChanged = 1 << 4,
    UnsupportedContext = 1 << 5,
    PlayerIndexChanged = 1 << 6,
    MissingContext = 1 << 7,
}

public static class LapEvidence
{
    private const LapEvidenceFlags All =
        LapEvidenceFlags.InvalidationObserved
        | LapEvidenceFlags.PitObserved
        | LapEvidenceFlags.FlashbackObserved
        | LapEvidenceFlags.MaterialGapObserved
        | LapEvidenceFlags.ContextChanged
        | LapEvidenceFlags.UnsupportedContext
        | LapEvidenceFlags.PlayerIndexChanged
        | LapEvidenceFlags.MissingContext;

    public static LapEvidenceFlags Validate(LapEvidenceFlags flags)
    {
        if ((flags & ~All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        return flags;
    }
}
