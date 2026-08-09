using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapAuditEntry
{
    public BahrainLapAuditEntry(
        LapCandidateId candidateId,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        BahrainTelemetryContext? context,
        LapAuditDecision? decision = null,
        LapExclusionReason? exclusionReason = null,
        string? factualNote = null)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);
        CandidateId = candidateId;
        Boundary = boundary;
        EvidenceFlags = evidenceFlags;
        Context = context;
        Decision = decision;
        ExclusionReason = exclusionReason;
        FactualNote = factualNote;
    }

    public LapCandidateId CandidateId { get; init; }

    public LapBoundary Boundary { get; init; }

    public LapEvidenceFlags EvidenceFlags { get; init; }

    public BahrainTelemetryContext? Context { get; init; }

    public LapAuditDecision? Decision { get; init; }

    public LapExclusionReason? ExclusionReason { get; init; }

    public string? FactualNote { get; init; }

    public static BahrainLapAuditEntry FromCandidate(
        BahrainLapCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new(
            candidate.CandidateId,
            candidate.Boundary,
            candidate.EvidenceFlags,
            candidate.Context);
    }
}
