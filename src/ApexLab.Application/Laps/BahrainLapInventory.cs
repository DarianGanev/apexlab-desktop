using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapInventory
{
    private readonly IReadOnlyList<BahrainLapCandidate> _candidates;

    public BahrainLapInventory(
        string canonicalIdentitySha256,
        string canonicalSha256,
        BahrainTelemetryContext? referenceContext,
        IEnumerable<BahrainLapCandidate> candidates)
    {
        LapIdentityValidation.RequireSha256(
            canonicalIdentitySha256,
            nameof(canonicalIdentitySha256));
        LapIdentityValidation.RequireSha256(
            canonicalSha256,
            nameof(canonicalSha256));
        ArgumentNullException.ThrowIfNull(candidates);
        var copied = candidates.ToArray();
        if (copied.Any(candidate => candidate is null))
        {
            throw new ArgumentException(
                "A lap inventory cannot contain a null candidate.",
                nameof(candidates));
        }

        for (var index = 1; index < copied.Length; index++)
        {
            var previous = copied[index - 1];
            var current = copied[index];
            if (current.Boundary.StartSourceSequence
                    < previous.Boundary.StartSourceSequence
                || (current.Boundary.StartSourceSequence
                        == previous.Boundary.StartSourceSequence
                    && StringComparer.Ordinal.Compare(
                        current.CandidateId.Value,
                        previous.CandidateId.Value) <= 0))
            {
                throw new ArgumentException(
                    "Lap inventory candidates must be uniquely and deterministically ordered.",
                    nameof(candidates));
            }
        }

        if (copied.Select(candidate => candidate.CandidateId).Distinct().Count()
            != copied.Length)
        {
            throw new ArgumentException(
                "A lap inventory cannot contain duplicate candidate identities.",
                nameof(candidates));
        }

        CanonicalIdentitySha256 = canonicalIdentitySha256;
        CanonicalSha256 = canonicalSha256;
        ReferenceContext = referenceContext;
        _candidates = Array.AsReadOnly(copied);
    }

    public string CanonicalIdentitySha256 { get; }

    public string CanonicalSha256 { get; }

    public BahrainTelemetryContext? ReferenceContext { get; }

    public IReadOnlyList<BahrainLapCandidate> Candidates => _candidates;
}
