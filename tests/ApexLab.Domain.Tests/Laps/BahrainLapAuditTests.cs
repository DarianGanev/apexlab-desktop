using ApexLab.Domain.Laps;

namespace ApexLab.Domain.Tests.Laps;

[TestClass]
public sealed class BahrainLapAuditTests
{
    [TestMethod]
    public void IncludedCandidateMustBeCompleteContextMatchedAndUncontradicted()
    {
        var included = AuditedLapCandidate.Include(Id('a'), Complete(1));

        Assert.AreEqual(LapAuditDecision.Included, included.Decision);
        Assert.IsTrue(included.ContextMatches);
        Assert.AreEqual(LapEvidenceFlags.None, included.EvidenceFlags);
        Assert.IsNull(included.ExclusionReason);
        Assert.IsNull(included.FactualNote);

        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Include(
                Id('b'),
                LapBoundary.Partial(
                    LapBoundaryCompleteness.TrailingPartial,
                    1,
                    2,
                    1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Include(
                Id('b'),
                Complete(1),
                LapEvidenceFlags.InvalidationObserved));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Include(Id('b'), Complete(1), contextMatches: false));
    }

    [TestMethod]
    public void ExclusionReasonMustAgreeWithObservedFact()
    {
        var invalid = AuditedLapCandidate.Exclude(
            Id('a'),
            Complete(1),
            LapEvidenceFlags.InvalidationObserved,
            contextMatches: true,
            LapExclusionReason.Invalidated);
        var incomplete = AuditedLapCandidate.Exclude(
            Id('b'),
            LapBoundary.Partial(
                LapBoundaryCompleteness.LeadingPartial,
                1,
                2,
                1),
            LapEvidenceFlags.None,
            contextMatches: true,
            LapExclusionReason.IncompleteLap);
        var other = AuditedLapCandidate.Exclude(
            Id('c'),
            Complete(2),
            LapEvidenceFlags.None,
            contextMatches: true,
            LapExclusionReason.OtherFactual,
            "Manual review found an unmodelled interruption.");

        Assert.AreEqual(LapExclusionReason.Invalidated, invalid.ExclusionReason);
        Assert.AreEqual(LapExclusionReason.IncompleteLap, incomplete.ExclusionReason);
        Assert.AreEqual(
            "Manual review found an unmodelled interruption.",
            other.FactualNote);

        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Exclude(
                Id('d'),
                Complete(3),
                LapEvidenceFlags.None,
                true,
                LapExclusionReason.Invalidated));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Exclude(
                Id('d'),
                Complete(3),
                LapEvidenceFlags.None,
                true,
                LapExclusionReason.OtherFactual));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Exclude(
                Id('d'),
                Complete(3),
                LapEvidenceFlags.InvalidationObserved,
                true,
                LapExclusionReason.Invalidated,
                "Notes are forbidden here."));
    }

    [TestMethod]
    public void FactualNoteIsTrimmedSingleLineAndBounded()
    {
        foreach (var invalid in new[] { "", " note", "note ", "line\nbreak" })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                AuditedLapCandidate.Exclude(
                    Id('a'),
                    Complete(1),
                    LapEvidenceFlags.None,
                    true,
                    LapExclusionReason.OtherFactual,
                    invalid));
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            AuditedLapCandidate.Exclude(
                Id('a'),
                Complete(1),
                LapEvidenceFlags.None,
                true,
                LapExclusionReason.OtherFactual,
                new string('x', 201)));
    }

    internal static LapCandidateId Id(char value) => new(new string(value, 64));

    internal static LapBoundary Complete(byte lapNumber, long start = 1) =>
        LapBoundary.Complete(start, start + 10, lapNumber, 90_000);
}
