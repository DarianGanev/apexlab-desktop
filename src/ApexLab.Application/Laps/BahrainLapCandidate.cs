using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapCandidate
{
    public BahrainLapCandidate(
        LapCandidateId candidateId,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        BahrainTelemetryContext? context)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);
        CandidateId = candidateId;
        Boundary = boundary;
        EvidenceFlags = evidenceFlags;
        Context = context;
    }

    public LapCandidateId CandidateId { get; }

    public LapBoundary Boundary { get; }

    public LapEvidenceFlags EvidenceFlags { get; }

    public BahrainTelemetryContext? Context { get; }
}
