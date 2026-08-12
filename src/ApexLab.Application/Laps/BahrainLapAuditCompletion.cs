using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public static class BahrainLapAuditCompletion
{
    public static BahrainLapAuditDocument CompleteAllEligible(
        BahrainLapInventory inventory,
        BahrainLapAuditDocument template,
        BahrainLapAuditManualInputs confirmedInputs)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(confirmedInputs);
        _ = BahrainLapAuditEvaluator.Evaluate(inventory, template);

        if (!IsPending(template))
        {
            throw Failure(
                BahrainLapAuditCompletionFailureKind.NotPendingTemplate,
                "Only an untouched pending lap-audit template can be completed.");
        }

        ValidateManualContext(confirmedInputs);
        var entries = new BahrainLapAuditEntry[template.Entries.Count];
        for (var index = 0; index < template.Entries.Count; index++)
        {
            var entry = template.Entries[index];
            if (entry.Boundary.Completeness != LapBoundaryCompleteness.Complete)
            {
                entries[index] = entry with
                {
                    Decision = LapAuditDecision.Excluded,
                    ExclusionReason = LapExclusionReason.IncompleteLap,
                };
                continue;
            }

            if (template.ReferenceContext is null
                || entry.EvidenceFlags != LapEvidenceFlags.None
                || entry.Context != template.ReferenceContext)
            {
                throw IndividualReviewRequired();
            }

            entries[index] = entry with
            {
                Decision = LapAuditDecision.Included,
            };
        }

        return new(
            template.SchemaVersion,
            template.LapAuditId,
            template.CanonicalIdentitySha256,
            template.CanonicalSha256,
            template.ReferenceContext,
            confirmedInputs,
            entries);
    }

    private static bool IsPending(BahrainLapAuditDocument template) =>
        template.ManualInputs == BahrainLapAuditManualInputs.Empty()
        && template.Entries.All(entry =>
            entry.Decision is null
            && entry.ExclusionReason is null
            && entry.FactualNote is null);

    private static void ValidateManualContext(
        BahrainLapAuditManualInputs confirmedInputs)
    {
        try
        {
            if (!confirmedInputs.IsComplete
                || !confirmedInputs.ToDomain().IsFullyConfirmed)
            {
                throw Failure(
                    BahrainLapAuditCompletionFailureKind.ManualContextNotConfirmed,
                    "Every manual context fact must be present and confirmed.");
            }
        }
        catch (Exception exception) when (exception is
                   ArgumentException or InvalidOperationException)
        {
            throw Failure(
                BahrainLapAuditCompletionFailureKind.ManualContextNotConfirmed,
                "Every manual context fact must be valid and confirmed.",
                exception);
        }
    }

    private static BahrainLapAuditCompletionException IndividualReviewRequired() =>
        Failure(
            BahrainLapAuditCompletionFailureKind.RequiresIndividualReview,
            "A complete lap contradicts the all-eligible confirmation and requires individual review.");

    private static BahrainLapAuditCompletionException Failure(
        BahrainLapAuditCompletionFailureKind kind,
        string message,
        Exception? innerException = null) =>
        new(kind, message, innerException);
}
