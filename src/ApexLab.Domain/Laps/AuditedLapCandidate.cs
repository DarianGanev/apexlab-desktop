namespace ApexLab.Domain.Laps;

public sealed record AuditedLapCandidate
{
    private const int MaximumFactualNoteLength = 200;

    private AuditedLapCandidate(
        LapCandidateId candidateId,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        bool contextMatches,
        LapAuditDecision decision,
        LapExclusionReason? exclusionReason,
        string? factualNote)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        CandidateId = candidateId;
        Boundary = boundary;
        EvidenceFlags = evidenceFlags;
        ContextMatches = contextMatches;
        Decision = decision;
        ExclusionReason = exclusionReason;
        FactualNote = factualNote;
    }

    public LapCandidateId CandidateId { get; }

    public LapBoundary Boundary { get; }

    public LapEvidenceFlags EvidenceFlags { get; }

    public bool ContextMatches { get; }

    public LapAuditDecision Decision { get; }

    public LapExclusionReason? ExclusionReason { get; }

    public string? FactualNote { get; }

    public static AuditedLapCandidate Include(
        LapCandidateId candidateId,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags = LapEvidenceFlags.None,
        bool contextMatches = true)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);
        if (boundary.Completeness != LapBoundaryCompleteness.Complete
            || evidenceFlags != LapEvidenceFlags.None
            || !contextMatches)
        {
            throw new ArgumentException(
                "An included lap must be complete, context-matched, and free of direct contradictions.",
                nameof(boundary));
        }

        return new(
            candidateId,
            boundary,
            evidenceFlags,
            contextMatches,
            LapAuditDecision.Included,
            exclusionReason: null,
            factualNote: null);
    }

    public static AuditedLapCandidate Exclude(
        LapCandidateId candidateId,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        bool contextMatches,
        LapExclusionReason exclusionReason,
        string? factualNote = null)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);
        if (!Enum.IsDefined(exclusionReason))
        {
            throw new ArgumentOutOfRangeException(nameof(exclusionReason));
        }

        ValidateReason(
            boundary,
            evidenceFlags,
            contextMatches,
            exclusionReason,
            factualNote);
        return new(
            candidateId,
            boundary,
            evidenceFlags,
            contextMatches,
            LapAuditDecision.Excluded,
            exclusionReason,
            factualNote);
    }

    private static void ValidateReason(
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        bool contextMatches,
        LapExclusionReason exclusionReason,
        string? factualNote)
    {
        var reasonMatches = exclusionReason switch
        {
            LapExclusionReason.Invalidated =>
                evidenceFlags.HasFlag(LapEvidenceFlags.InvalidationObserved),
            LapExclusionReason.PitEntryOrExit =>
                evidenceFlags.HasFlag(LapEvidenceFlags.PitObserved),
            LapExclusionReason.FlashbackObserved =>
                evidenceFlags.HasFlag(LapEvidenceFlags.FlashbackObserved),
            LapExclusionReason.MaterialGap =>
                evidenceFlags.HasFlag(LapEvidenceFlags.MaterialGapObserved),
            LapExclusionReason.ContextMismatch =>
                !contextMatches
                || (evidenceFlags & ContextEvidenceFlags) != 0,
            LapExclusionReason.IncompleteLap =>
                boundary.Completeness != LapBoundaryCompleteness.Complete,
            LapExclusionReason.OtherFactual => factualNote is not null,
            _ => false,
        };
        if (!reasonMatches)
        {
            throw new ArgumentException(
                "The exclusion reason is not supported by the recorded facts.",
                nameof(exclusionReason));
        }

        if (exclusionReason == LapExclusionReason.OtherFactual)
        {
            _ = LapAuditText.Require(
                factualNote!,
                MaximumFactualNoteLength,
                nameof(factualNote));
        }
        else if (factualNote is not null)
        {
            throw new ArgumentException(
                "A factual note is permitted only for other-factual exclusions.",
                nameof(factualNote));
        }
    }

    private const LapEvidenceFlags ContextEvidenceFlags =
        LapEvidenceFlags.ContextChanged
        | LapEvidenceFlags.UnsupportedContext
        | LapEvidenceFlags.PlayerIndexChanged
        | LapEvidenceFlags.MissingContext;
}
