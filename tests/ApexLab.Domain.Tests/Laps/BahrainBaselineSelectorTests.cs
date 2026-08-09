using ApexLab.Domain.Laps;

namespace ApexLab.Domain.Tests.Laps;

[TestClass]
public sealed class BahrainBaselineSelectorTests
{
    [TestMethod]
    public void FourComparableLapsAbstainAndFiveBecomeReady()
    {
        var four = BahrainBaselineSelector.Select(Audit(includedCount: 4));
        var five = BahrainBaselineSelector.Select(Audit(includedCount: 5));

        Assert.AreEqual(BaselineDisposition.Abstained, four.Disposition);
        CollectionAssert.AreEqual(
            new[] { BaselineAbstentionReason.InsufficientComparableLaps },
            four.AbstentionReasons.ToArray());
        Assert.HasCount(4, four.IncludedLaps);
        Assert.AreEqual(BaselineDisposition.Ready, five.Disposition);
        Assert.IsEmpty(five.AbstentionReasons);
        Assert.HasCount(5, five.IncludedLaps);
    }

    [TestMethod]
    public void UnconfirmedManualContextForcesAbstentionEvenWithFiveLaps()
    {
        var result = BahrainBaselineSelector.Select(
            Audit(
                includedCount: 5,
                context: BahrainManualContextTests.Context(
                    contextCrossCheckPassed: false)));

        Assert.AreEqual(BaselineDisposition.Abstained, result.Disposition);
        CollectionAssert.AreEqual(
            new[] { BaselineAbstentionReason.ContextNotConfirmed },
            result.AbstentionReasons.ToArray());
    }

    [TestMethod]
    public void SelectionOrdersEvidenceDeterministicallyAndRejectsDuplicates()
    {
        AuditedLapCandidate[] unordered =
        [
            Included('b', lapNumber: 2, start: 20),
            Included('a', lapNumber: 1, start: 1),
            Included('e', lapNumber: 5, start: 80),
            Included('d', lapNumber: 4, start: 60),
            Included('c', lapNumber: 3, start: 40),
        ];
        var result = BahrainBaselineSelector.Select(
            new BahrainLapAudit(BahrainManualContextTests.Context(), unordered));

        CollectionAssert.AreEqual(
            new long[] { 1, 20, 40, 60, 80 },
            result.IncludedLaps
                .Select(lap => lap.Boundary.StartSourceSequence)
                .ToArray());

        Assert.ThrowsExactly<ArgumentException>(() =>
            new BahrainLapAudit(
                BahrainManualContextTests.Context(),
                [unordered[0], unordered[0]]));
    }

    [TestMethod]
    public void ExcludedLapsAreRetainedButDoNotMeetTheMinimum()
    {
        var candidates = new List<AuditedLapCandidate>
        {
            Included('a', 1, 1),
            Included('b', 2, 20),
            Included('c', 3, 40),
            Included('d', 4, 60),
            AuditedLapCandidate.Exclude(
                BahrainLapAuditTests.Id('e'),
                BahrainLapAuditTests.Complete(5, 80),
                LapEvidenceFlags.PitObserved,
                true,
                LapExclusionReason.PitEntryOrExit),
        };

        var result = BahrainBaselineSelector.Select(
            new BahrainLapAudit(BahrainManualContextTests.Context(), candidates));

        Assert.AreEqual(BaselineDisposition.Abstained, result.Disposition);
        Assert.HasCount(1, result.ExcludedLaps);
        Assert.AreEqual(
            LapExclusionReason.PitEntryOrExit,
            result.ExcludedLaps[0].ExclusionReason);
    }

    private static BahrainLapAudit Audit(
        int includedCount,
        BahrainManualContext? context = null)
    {
        var candidates = Enumerable.Range(0, includedCount)
            .Select(index => Included(
                (char)('a' + index),
                checked((byte)(index + 1)),
                1 + (index * 20L)))
            .ToArray();
        return new(context ?? BahrainManualContextTests.Context(), candidates);
    }

    private static AuditedLapCandidate Included(
        char id,
        byte lapNumber,
        long start) =>
        AuditedLapCandidate.Include(
            BahrainLapAuditTests.Id(id),
            BahrainLapAuditTests.Complete(lapNumber, start));
}
