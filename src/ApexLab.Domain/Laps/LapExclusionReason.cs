namespace ApexLab.Domain.Laps;

public enum LapExclusionReason
{
    Invalidated = 1,
    PitEntryOrExit = 2,
    FlashbackObserved = 3,
    MaterialGap = 4,
    ContextMismatch = 5,
    IncompleteLap = 6,
    OtherFactual = 7,
}
