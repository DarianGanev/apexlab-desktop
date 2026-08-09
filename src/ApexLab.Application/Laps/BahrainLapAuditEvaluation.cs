using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapAuditEvaluation
{
    internal BahrainLapAuditEvaluation(
        int includedCount,
        int excludedCount,
        int pendingCount,
        bool includedContextsMatch,
        BahrainBaselineSelection? selection,
        IReadOnlyList<BaselineAbstentionReason> abstentionReasons)
    {
        IncludedCount = includedCount;
        ExcludedCount = excludedCount;
        PendingCount = pendingCount;
        IncludedContextsMatch = includedContextsMatch;
        Selection = selection;
        AbstentionReasons = abstentionReasons;
    }

    public int IncludedCount { get; }
    public int ExcludedCount { get; }
    public int PendingCount { get; }
    public bool AllCandidatesAudited => PendingCount == 0;
    public bool IncludedContextsMatch { get; }
    public bool ProvenanceComplete => true;
    public BahrainBaselineSelection? Selection { get; }
    public IReadOnlyList<BaselineAbstentionReason> AbstentionReasons { get; }
}
