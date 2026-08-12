namespace ApexLab.Domain.Laps;

public static class BahrainBaselineSelector
{
    public static BahrainBaselineSelection Select(BahrainLapAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var ordered = audit.Candidates
            .OrderBy(candidate => candidate.Boundary.StartSourceSequence)
            .ThenBy(candidate => candidate.CandidateId.Value, StringComparer.Ordinal)
            .ToArray();
        var included = Array.AsReadOnly(
            ordered.Where(candidate =>
                    candidate.Decision == LapAuditDecision.Included)
                .ToArray());
        var excluded = Array.AsReadOnly(
            ordered.Where(candidate =>
                    candidate.Decision == LapAuditDecision.Excluded)
                .ToArray());
        var reasons = new List<BaselineAbstentionReason>();
        if (!audit.ManualContext.IsFullyConfirmed)
        {
            reasons.Add(BaselineAbstentionReason.ContextNotConfirmed);
        }

        if (included.Count < BahrainLapAuditContract.MinimumComparableBaselineLaps)
        {
            reasons.Add(BaselineAbstentionReason.InsufficientComparableLaps);
        }

        return new(
            reasons.Count == 0
                ? BaselineDisposition.Ready
                : BaselineDisposition.Abstained,
            included,
            excluded,
            Array.AsReadOnly(reasons.ToArray()));
    }
}
