using ApexLab.Domain.Laps;

namespace ApexLab.Domain.Tests.Laps;

[TestClass]
public sealed class LapBoundaryTests
{
    [TestMethod]
    public void CompleteBoundaryUsesHalfOpenRangeAndSeparateCompletionEvidence()
    {
        var boundary = LapBoundary.Complete(
            startSourceSequence: 10,
            completionEvidenceSourceSequence: 20,
            lapNumber: 7,
            officialLapTimeMilliseconds: 90_123);

        Assert.AreEqual(LapBoundaryCompleteness.Complete, boundary.Completeness);
        Assert.AreEqual(10L, boundary.StartSourceSequence);
        Assert.AreEqual(20L, boundary.EndSourceSequenceExclusive);
        Assert.AreEqual(20L, boundary.CompletionEvidenceSourceSequence);
        Assert.AreEqual((byte)7, boundary.LapNumber);
        Assert.AreEqual(90_123U, boundary.OfficialLapTimeMilliseconds);
    }

    [TestMethod]
    public void PartialBoundaryCannotClaimCompletionEvidenceOrOfficialTime()
    {
        foreach (var completeness in new[]
                 {
                     LapBoundaryCompleteness.LeadingPartial,
                     LapBoundaryCompleteness.TrailingPartial,
                     LapBoundaryCompleteness.Incoherent,
                 })
        {
            var boundary = LapBoundary.Partial(
                completeness,
                startSourceSequence: 1,
                endSourceSequenceExclusive: 8,
                lapNumber: 2);

            Assert.AreEqual(completeness, boundary.Completeness);
            Assert.IsNull(boundary.CompletionEvidenceSourceSequence);
            Assert.IsNull(boundary.OfficialLapTimeMilliseconds);
            Assert.AreEqual((byte)2, boundary.LapNumber);
        }
    }

    [TestMethod]
    public void BoundariesRejectImpossibleRangesKindsAndLapMetadata()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Complete(0, 2, 1, 1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            LapBoundary.Complete(2, 2, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Complete(1, 2, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Complete(1, 2, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Partial(
                LapBoundaryCompleteness.Complete,
                1,
                2,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Partial(
                (LapBoundaryCompleteness)99,
                1,
                2,
                1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            LapBoundary.Partial(
                LapBoundaryCompleteness.TrailingPartial,
                3,
                2,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapBoundary.Partial(
                LapBoundaryCompleteness.TrailingPartial,
                1,
                2,
                0));
    }
}
