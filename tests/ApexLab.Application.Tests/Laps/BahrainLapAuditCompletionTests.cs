using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Tests.Laps;

[TestClass]
public sealed class BahrainLapAuditCompletionTests
{
    [TestMethod]
    public void OwnerConfirmationIncludesEveryEligibleLapAndExcludesPartials()
    {
        var template = Template(completeCount: 7, partialCount: 2);

        var completed = BahrainLapAuditCompletion.CompleteAllEligible(
            Inventory(template),
            template,
            ConfirmedInputs());

        Assert.AreEqual(ConfirmedInputs(), completed.ManualInputs);
        Assert.HasCount(7, completed.Entries.Where(entry =>
            entry.Decision == LapAuditDecision.Included));
        var excluded = completed.Entries.Where(entry =>
            entry.Decision == LapAuditDecision.Excluded).ToArray();
        Assert.HasCount(2, excluded);
        Assert.IsTrue(excluded.All(entry =>
            entry.ExclusionReason == LapExclusionReason.IncompleteLap));
        Assert.IsTrue(excluded.All(entry => entry.FactualNote is null));
    }

    [TestMethod]
    public void ConfirmedAuditBelowMinimumIsCompletedButAbstains()
    {
        var template = Template(completeCount: 4, partialCount: 1);

        var completed = BahrainLapAuditCompletion.CompleteAllEligible(
            Inventory(template),
            template,
            ConfirmedInputs());
        var evaluation = BahrainLapAuditEvaluator.Evaluate(
            Inventory(template),
            completed);

        Assert.AreEqual(4, evaluation.IncludedCount);
        Assert.IsTrue(evaluation.AllCandidatesAudited);
        Assert.AreEqual(
            BaselineDisposition.Abstained,
            evaluation.Selection?.Disposition);
        CollectionAssert.Contains(
            evaluation.AbstentionReasons.ToArray(),
            BaselineAbstentionReason.InsufficientComparableLaps);
    }

    [TestMethod]
    public void AllPartialInventoryWithoutReferenceContextCompletesAndAbstains()
    {
        var candidates = Enumerable.Range(0, 2).Select(index =>
        {
            var boundary = LapBoundary.Partial(
                index == 0
                    ? LapBoundaryCompleteness.LeadingPartial
                    : LapBoundaryCompleteness.TrailingPartial,
                1 + (index * 20L),
                11 + (index * 20L),
                checked((byte)(index + 1)));
            return Candidate(boundary, LapEvidenceFlags.None, context: null);
        }).ToArray();
        var inventory = new BahrainLapInventory(
            new string('a', 64),
            new string('b', 64),
            referenceContext: null,
            candidates);
        var template = BahrainLapAuditDocument.CreateTemplate(inventory);

        var completed = BahrainLapAuditCompletion.CompleteAllEligible(
            inventory,
            template,
            ConfirmedInputs());
        var evaluation = BahrainLapAuditEvaluator.Evaluate(inventory, completed);

        Assert.AreEqual(0, evaluation.IncludedCount);
        Assert.AreEqual(2, evaluation.ExcludedCount);
        Assert.IsTrue(evaluation.AllCandidatesAudited);
        Assert.AreEqual(
            BaselineDisposition.Abstained,
            evaluation.Selection?.Disposition);
    }

    [TestMethod]
    public void ContradictedCompleteLapRequiresIndividualReview()
    {
        var template = Template(
            completeCount: 7,
            partialCount: 2,
            flaggedCompleteIndex: 3);

        var exception = Assert.ThrowsExactly<BahrainLapAuditCompletionException>(() =>
            BahrainLapAuditCompletion.CompleteAllEligible(
                Inventory(template),
                template,
                ConfirmedInputs()));

        Assert.AreEqual(
            BahrainLapAuditCompletionFailureKind.RequiresIndividualReview,
            exception.Kind);
    }

    [TestMethod]
    public void ExistingDecisionsAndUnconfirmedContextAreNeverOverwritten()
    {
        var template = Template(completeCount: 7, partialCount: 2);
        var decided = Document(
            template,
            template.Entries.Select((entry, index) => index == 0
                ? entry with { Decision = LapAuditDecision.Included }
                : entry),
            BahrainLapAuditManualInputs.Empty());
        var unconfirmed = new BahrainLapAuditManualInputs(
            "synthetic-game-build",
            "synthetic-player-vehicle",
            "synthetic-controller-profile",
            "synthetic-setup-descriptor",
            "synthetic-tyre-compound",
            true,
            true,
            true,
            false);

        var existing = Assert.ThrowsExactly<BahrainLapAuditCompletionException>(() =>
            BahrainLapAuditCompletion.CompleteAllEligible(
                Inventory(decided),
                decided,
                ConfirmedInputs()));
        var invalidContext = Assert.ThrowsExactly<BahrainLapAuditCompletionException>(() =>
            BahrainLapAuditCompletion.CompleteAllEligible(
                Inventory(template),
                template,
                unconfirmed));

        Assert.AreEqual(
            BahrainLapAuditCompletionFailureKind.NotPendingTemplate,
            existing.Kind);
        Assert.AreEqual(
            BahrainLapAuditCompletionFailureKind.ManualContextNotConfirmed,
            invalidContext.Kind);
    }

    private static BahrainLapAuditDocument Template(
        int completeCount,
        int partialCount,
        int flaggedCompleteIndex = -1)
    {
        var context = Context();
        var candidates = new List<BahrainLapCandidate>();
        for (var index = 0; index < completeCount; index++)
        {
            var boundary = LapBoundary.Complete(
                1 + (index * 20L),
                11 + (index * 20L),
                checked((byte)(index + 1)),
                checked((uint)(90_000 + index)));
            var flags = index == flaggedCompleteIndex
                ? LapEvidenceFlags.PitObserved
                : LapEvidenceFlags.None;
            candidates.Add(Candidate(boundary, flags, context));
        }

        for (var index = 0; index < partialCount; index++)
        {
            var start = 1 + ((completeCount + index) * 20L);
            candidates.Add(Candidate(
                LapBoundary.Partial(
                    LapBoundaryCompleteness.TrailingPartial,
                    start,
                    start + 10,
                    checked((byte)(completeCount + index + 1))),
                LapEvidenceFlags.None,
                context));
        }

        return BahrainLapAuditDocument.CreateTemplate(new BahrainLapInventory(
            new string('a', 64),
            new string('b', 64),
            context,
            candidates));
    }

    private static BahrainLapInventory Inventory(
        BahrainLapAuditDocument document) =>
        new(
            document.CanonicalIdentitySha256,
            document.CanonicalSha256,
            document.ReferenceContext,
            document.Entries.Select(entry => new BahrainLapCandidate(
                entry.CandidateId,
                entry.Boundary,
                entry.EvidenceFlags,
                entry.Context)));

    private static BahrainLapCandidate Candidate(
        LapBoundary boundary,
        LapEvidenceFlags flags,
        BahrainTelemetryContext? context) =>
        new(
            BahrainLapCandidateIdentity.Calculate(
                new string('a', 64),
                boundary,
                flags,
                context),
            boundary,
            flags,
            context);

    private static BahrainLapAuditDocument Document(
        BahrainLapAuditDocument source,
        IEnumerable<BahrainLapAuditEntry> entries,
        BahrainLapAuditManualInputs inputs) =>
        new(
            source.SchemaVersion,
            source.LapAuditId,
            source.CanonicalIdentitySha256,
            source.CanonicalSha256,
            source.ReferenceContext,
            inputs,
            entries);

    private static BahrainLapAuditManualInputs ConfirmedInputs() =>
        new(
            "synthetic-game-build",
            "synthetic-player-vehicle",
            "synthetic-controller-profile",
            "synthetic-setup-descriptor",
            "synthetic-tyre-compound",
            true,
            true,
            true,
            true);

    private static BahrainTelemetryContext Context() =>
        new(
            new CanonicalSessionPacket(
                weather: 0,
                trackTemperatureCelsius: 30,
                airTemperatureCelsius: 24,
                trackLengthMetres: 5_412,
                sessionType: 18,
                trackId: 3,
                formula: 0,
                isSpectating: false,
                isNetworkGame: false,
                steeringAssist: 0,
                brakingAssist: 0,
                gearboxAssist: 1,
                pitAssist: 0,
                pitReleaseAssist: 0,
                ersAssist: 0,
                drsAssist: 0,
                dynamicRacingLine: 0,
                dynamicRacingLineType: 0,
                gameMode: 5,
                ruleSet: 2,
                timeOfDayMinutesSinceMidnight: 900,
                equalCarPerformance: true,
                recoveryMode: 0),
            playerCarIndex: 7,
            secondaryPlayerCarIndex: byte.MaxValue);
}
