namespace ApexLab.Domain.Laps;

public enum BaselineAbstentionReason
{
    AuditIncomplete = 1,
    ContextNotConfirmed = 2,
    ContextMismatch = 3,
    ContradictoryIncludedLap = 4,
    InsufficientComparableLaps = 5,
}
