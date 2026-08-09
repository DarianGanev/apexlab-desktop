using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public static class BahrainLapAuditEvaluator
{
    public static BahrainLapAuditEvaluation Evaluate(
        BahrainLapInventory inventory,
        BahrainLapAuditDocument document)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(document);
        ValidateProvenance(inventory, document);
        ValidateInventory(inventory, document);

        var audited = new List<AuditedLapCandidate>(document.Entries.Count);
        var includedCount = 0;
        var excludedCount = 0;
        var pendingCount = 0;
        var includedContextsMatch = true;
        for (var index = 0; index < document.Entries.Count; index++)
        {
            var candidate = inventory.Candidates[index];
            var entry = document.Entries[index];
            var contextMatches = inventory.ReferenceContext is not null
                && candidate.Context == inventory.ReferenceContext;
            switch (entry.Decision)
            {
                case null:
                    if (entry.ExclusionReason.HasValue || entry.FactualNote is not null)
                    {
                        throw InvalidDecision();
                    }

                    pendingCount++;
                    break;
                case LapAuditDecision.Included:
                    includedCount++;
                    includedContextsMatch &= contextMatches;
                    audited.Add(CreateIncluded(candidate, contextMatches));
                    break;
                case LapAuditDecision.Excluded when entry.ExclusionReason.HasValue:
                    excludedCount++;
                    audited.Add(CreateExcluded(
                        candidate,
                        contextMatches,
                        entry.ExclusionReason.Value,
                        entry.FactualNote));
                    break;
                default:
                    throw InvalidDecision();
            }
        }

        if (pendingCount != 0 || !document.ManualInputs.IsComplete)
        {
            return new(
                includedCount,
                excludedCount,
                pendingCount,
                includedContextsMatch,
                selection: null,
                Array.AsReadOnly(
                    new[] { BaselineAbstentionReason.AuditIncomplete }));
        }

        BahrainManualContext manualContext;
        try
        {
            manualContext = document.ManualInputs.ToDomain();
        }
        catch (Exception exception) when (exception is
                   ArgumentException or InvalidOperationException)
        {
            throw new BahrainLapAuditException(
                BahrainLapAuditFailureKind.InvalidManualContext,
                "The manual comparison context is invalid.",
                exception);
        }

        var selection = BahrainBaselineSelector.Select(
            new BahrainLapAudit(manualContext, audited));
        return new(
            includedCount,
            excludedCount,
            pendingCount,
            includedContextsMatch,
            selection,
            selection.AbstentionReasons);
    }

    private static void ValidateProvenance(
        BahrainLapInventory inventory,
        BahrainLapAuditDocument document)
    {
        if (!StringComparer.Ordinal.Equals(
                inventory.CanonicalIdentitySha256,
                document.CanonicalIdentitySha256)
            || !StringComparer.Ordinal.Equals(
                inventory.CanonicalSha256,
                document.CanonicalSha256)
            || inventory.ReferenceContext != document.ReferenceContext)
        {
            throw new BahrainLapAuditException(
                BahrainLapAuditFailureKind.ProvenanceMismatch,
                "The lap audit provenance does not match the verified inventory.");
        }
    }

    private static void ValidateInventory(
        BahrainLapInventory inventory,
        BahrainLapAuditDocument document)
    {
        if (inventory.Candidates.Count != document.Entries.Count)
        {
            throw InventoryMismatch();
        }

        for (var index = 0; index < inventory.Candidates.Count; index++)
        {
            var candidate = inventory.Candidates[index];
            var entry = document.Entries[index];
            if (candidate.CandidateId != entry.CandidateId
                || candidate.Boundary != entry.Boundary
                || candidate.EvidenceFlags != entry.EvidenceFlags
                || candidate.Context != entry.Context)
            {
                throw InventoryMismatch();
            }
        }
    }

    private static AuditedLapCandidate CreateIncluded(
        BahrainLapCandidate candidate,
        bool contextMatches)
    {
        try
        {
            return AuditedLapCandidate.Include(
                candidate.CandidateId,
                candidate.Boundary,
                candidate.EvidenceFlags,
                contextMatches);
        }
        catch (ArgumentException exception)
        {
            throw new BahrainLapAuditException(
                BahrainLapAuditFailureKind.InvalidDecision,
                "An included lap contradicts its recorded evidence.",
                exception);
        }
    }

    private static AuditedLapCandidate CreateExcluded(
        BahrainLapCandidate candidate,
        bool contextMatches,
        LapExclusionReason exclusionReason,
        string? factualNote)
    {
        try
        {
            return AuditedLapCandidate.Exclude(
                candidate.CandidateId,
                candidate.Boundary,
                candidate.EvidenceFlags,
                contextMatches,
                exclusionReason,
                factualNote);
        }
        catch (ArgumentException exception)
        {
            throw new BahrainLapAuditException(
                BahrainLapAuditFailureKind.InvalidDecision,
                "An excluded lap reason contradicts its recorded evidence.",
                exception);
        }
    }

    private static BahrainLapAuditException InventoryMismatch() =>
        new(
            BahrainLapAuditFailureKind.InventoryMismatch,
            "The lap audit entries do not exactly match the verified inventory.");

    private static BahrainLapAuditException InvalidDecision() =>
        new(
            BahrainLapAuditFailureKind.InvalidDecision,
            "Every lap decision must use the complete conditional audit shape.");
}
