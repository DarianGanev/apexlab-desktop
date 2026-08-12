namespace ApexLab.Domain.Laps;

public sealed record BahrainLapAudit
{
    private readonly IReadOnlyList<AuditedLapCandidate> _candidates;

    public BahrainLapAudit(
        BahrainManualContext manualContext,
        IEnumerable<AuditedLapCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(manualContext);
        ArgumentNullException.ThrowIfNull(candidates);
        var copied = candidates.ToArray();
        if (copied.Any(candidate => candidate is null))
        {
            throw new ArgumentException(
                "An audit cannot contain a null candidate.",
                nameof(candidates));
        }

        if (copied.Select(candidate => candidate.CandidateId).Distinct().Count()
            != copied.Length)
        {
            throw new ArgumentException(
                "An audit cannot contain duplicate candidate identities.",
                nameof(candidates));
        }

        ManualContext = manualContext;
        _candidates = Array.AsReadOnly(copied);
    }

    public BahrainManualContext ManualContext { get; }

    public IReadOnlyList<AuditedLapCandidate> Candidates => _candidates;
}
