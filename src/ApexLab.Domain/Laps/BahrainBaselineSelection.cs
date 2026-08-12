namespace ApexLab.Domain.Laps;

public sealed record BahrainBaselineSelection
{
    internal BahrainBaselineSelection(
        BaselineDisposition disposition,
        IReadOnlyList<AuditedLapCandidate> includedLaps,
        IReadOnlyList<AuditedLapCandidate> excludedLaps,
        IReadOnlyList<BaselineAbstentionReason> abstentionReasons)
    {
        Disposition = disposition;
        IncludedLaps = includedLaps;
        ExcludedLaps = excludedLaps;
        AbstentionReasons = abstentionReasons;
    }

    public BaselineDisposition Disposition { get; }

    public IReadOnlyList<AuditedLapCandidate> IncludedLaps { get; }

    public IReadOnlyList<AuditedLapCandidate> ExcludedLaps { get; }

    public IReadOnlyList<BaselineAbstentionReason> AbstentionReasons { get; }
}
