using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;

namespace ApexLab.Application.Tests.Laps;

[TestClass]
public sealed class BahrainLapAuditEvaluatorTests
{
    [TestMethod]
    public void ExactFiveLapAuditProducesReadySelection()
    {
        var inventory = Inventory(5);
        var document = CompletedDocument(inventory, includedCount: 5);

        var evaluation = BahrainLapAuditEvaluator.Evaluate(inventory, document);

        Assert.IsTrue(evaluation.ProvenanceComplete);
        Assert.IsTrue(evaluation.AllCandidatesAudited);
        Assert.IsTrue(evaluation.IncludedContextsMatch);
        Assert.AreEqual(5, evaluation.IncludedCount);
        Assert.AreEqual(0, evaluation.ExcludedCount);
        Assert.AreEqual(0, evaluation.PendingCount);
        Assert.IsNotNull(evaluation.Selection);
        Assert.AreEqual(BaselineDisposition.Ready, evaluation.Selection.Disposition);
    }

    [TestMethod]
    public void PendingTemplateIsAValidAuditIncompleteAbstention()
    {
        var inventory = Inventory(5);
        var template = BahrainLapAuditDocument.CreateTemplate(inventory);

        var evaluation = BahrainLapAuditEvaluator.Evaluate(inventory, template);

        Assert.IsFalse(evaluation.AllCandidatesAudited);
        Assert.AreEqual(5, evaluation.PendingCount);
        Assert.IsNull(evaluation.Selection);
        CollectionAssert.AreEqual(
            new[] { BaselineAbstentionReason.AuditIncomplete },
            evaluation.AbstentionReasons.ToArray());
        Assert.IsTrue(template.Entries.All(entry => entry.Decision is null));
        Assert.IsFalse(template.ManualInputs.IsComplete);
    }

    [TestMethod]
    public void FourIncludedAndOneFactuallyExcludedAbstainTruthfully()
    {
        var inventory = Inventory(
            count: 5,
            flaggedIndex: 4,
            flaggedValue: LapEvidenceFlags.PitObserved);
        var document = CompletedDocument(inventory, includedCount: 4);

        var evaluation = BahrainLapAuditEvaluator.Evaluate(inventory, document);

        Assert.AreEqual(4, evaluation.IncludedCount);
        Assert.AreEqual(1, evaluation.ExcludedCount);
        Assert.IsNotNull(evaluation.Selection);
        Assert.AreEqual(BaselineDisposition.Abstained, evaluation.Selection.Disposition);
        CollectionAssert.AreEqual(
            new[] { BaselineAbstentionReason.InsufficientComparableLaps },
            evaluation.AbstentionReasons.ToArray());
    }

    [TestMethod]
    public void ExactInventoryIdentityContextOrderAndCandidateFactsAreRequired()
    {
        var inventory = Inventory(5);
        var original = CompletedDocument(inventory, 5);
        BahrainLapAuditDocument[] mutations =
        [
            Document(
                inventory,
                original.Entries,
                canonicalIdentity: new string('c', 64)),
            Document(
                inventory,
                original.Entries,
                canonicalSha: new string('d', 64)),
            Document(
                inventory,
                original.Entries,
                referenceContext: BahrainTelemetryContextTests.Context(
                    BahrainTelemetryContextTests.Session(
                        trackTemperatureCelsius: 31))),
            Document(inventory, original.Entries.Reverse()),
            Document(inventory, original.Entries.Skip(1)),
            Document(
                inventory,
                [
                    original.Entries[0] with
                    {
                        EvidenceFlags = LapEvidenceFlags.PitObserved,
                    },
                    .. original.Entries.Skip(1),
                ]),
        ];

        foreach (var mutation in mutations)
        {
            Assert.ThrowsExactly<BahrainLapAuditException>(
                () => BahrainLapAuditEvaluator.Evaluate(inventory, mutation));
        }
    }

    [TestMethod]
    public void ContradictoryDecisionAndReasonAreInvalidInputNotWeakEvidence()
    {
        var inventory = Inventory(
            count: 5,
            flaggedIndex: 4,
            flaggedValue: LapEvidenceFlags.PitObserved);
        var includedContradiction = CompletedDocument(inventory, includedCount: 5);
        var wrongReasonEntries = includedContradiction.Entries.ToArray();
        wrongReasonEntries[4] = wrongReasonEntries[4] with
        {
            Decision = LapAuditDecision.Excluded,
            ExclusionReason = LapExclusionReason.Invalidated,
        };

        Assert.ThrowsExactly<BahrainLapAuditException>(() =>
            BahrainLapAuditEvaluator.Evaluate(inventory, includedContradiction));
        Assert.ThrowsExactly<BahrainLapAuditException>(() =>
            BahrainLapAuditEvaluator.Evaluate(
                inventory,
                Document(inventory, wrongReasonEntries)));
    }

    [TestMethod]
    public void CompleteButUnconfirmedManualContextUsesDomainAbstention()
    {
        var inventory = Inventory(5);
        var document = CompletedDocument(
            inventory,
            includedCount: 5,
            inputs: Inputs(contextCrossCheckPassed: false));

        var evaluation = BahrainLapAuditEvaluator.Evaluate(inventory, document);

        Assert.IsNotNull(evaluation.Selection);
        Assert.AreEqual(BaselineDisposition.Abstained, evaluation.Selection.Disposition);
        CollectionAssert.AreEqual(
            new[] { BaselineAbstentionReason.ContextNotConfirmed },
            evaluation.AbstentionReasons.ToArray());
    }

    [TestMethod]
    public void OtherFactualRequiresTheBoundedNoteContract()
    {
        var inventory = Inventory(1);
        var entry = BahrainLapAuditEntry.FromCandidate(inventory.Candidates[0]) with
        {
            Decision = LapAuditDecision.Excluded,
            ExclusionReason = LapExclusionReason.OtherFactual,
            FactualNote = null,
        };

        Assert.ThrowsExactly<BahrainLapAuditException>(() =>
            BahrainLapAuditEvaluator.Evaluate(
                inventory,
                Document(inventory, [entry])));
    }

    private static BahrainLapAuditDocument CompletedDocument(
        BahrainLapInventory inventory,
        int includedCount,
        BahrainLapAuditManualInputs? inputs = null)
    {
        var entries = inventory.Candidates.Select((candidate, index) =>
        {
            var entry = BahrainLapAuditEntry.FromCandidate(candidate);
            return index < includedCount
                ? entry with { Decision = LapAuditDecision.Included }
                : entry with
                {
                    Decision = LapAuditDecision.Excluded,
                    ExclusionReason = LapExclusionReason.PitEntryOrExit,
                };
        });
        return Document(inventory, entries, inputs: inputs ?? Inputs());
    }

    private static BahrainLapAuditDocument Document(
        BahrainLapInventory inventory,
        IEnumerable<BahrainLapAuditEntry> entries,
        string? canonicalIdentity = null,
        string? canonicalSha = null,
        BahrainTelemetryContext? referenceContext = null,
        BahrainLapAuditManualInputs? inputs = null) =>
        new(
            schemaVersion: 1,
            BahrainLapAuditContract.AuditId,
            canonicalIdentity ?? inventory.CanonicalIdentitySha256,
            canonicalSha ?? inventory.CanonicalSha256,
            referenceContext ?? inventory.ReferenceContext,
            inputs ?? Inputs(),
            entries);

    private static BahrainLapAuditManualInputs Inputs(
        bool contextCrossCheckPassed = true) =>
        new(
            gameBuild: "F1 25 current PC build",
            playerVehicle: "F1 car",
            controllerProfile: "Wheel profile",
            setupDescriptor: "Fixed setup A",
            tyreCompound: "Soft",
            evidenceIntegrityPassed: true,
            trackAndModeVisuallyConfirmed: true,
            setupUnchanged: true,
            contextCrossCheckPassed);

    private static BahrainLapInventory Inventory(
        int count,
        int flaggedIndex = -1,
        LapEvidenceFlags flaggedValue = LapEvidenceFlags.None)
    {
        var context = BahrainTelemetryContextTests.Context(
            BahrainTelemetryContextTests.Session());
        var candidates = Enumerable.Range(0, count)
            .Select(index =>
            {
                var boundary = LapBoundary.Complete(
                    startSourceSequence: 1 + (index * 20L),
                    completionEvidenceSourceSequence: 11 + (index * 20L),
                    lapNumber: checked((byte)(index + 1)),
                    officialLapTimeMilliseconds: checked((uint)(90_000 + index)));
                var flags = index == flaggedIndex
                    ? flaggedValue
                    : LapEvidenceFlags.None;
                return new BahrainLapCandidate(
                    BahrainLapCandidateIdentity.Calculate(
                        new string('a', 64),
                        boundary,
                        flags,
                        context),
                    boundary,
                    flags,
                    context);
            });
        return new(
            new string('a', 64),
            new string('b', 64),
            context,
            candidates);
    }
}
