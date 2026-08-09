using ApexLab.Domain.Laps;

namespace ApexLab.Domain.Tests.Laps;

[TestClass]
public sealed class LapAuditPrimitiveTests
{
    [TestMethod]
    public void ContractAndEnumsFreezeTheProvisionalPublicVocabulary()
    {
        Assert.AreEqual(
            "bahrain-tt-lap-audit-v1",
            typeof(BahrainLapAuditContract).GetField(
                nameof(BahrainLapAuditContract.AuditId))!.GetRawConstantValue());
        Assert.AreEqual(
            5,
            typeof(BahrainLapAuditContract).GetField(
                nameof(BahrainLapAuditContract.MinimumComparableBaselineLaps))!
                .GetRawConstantValue());
        CollectionAssert.AreEqual(
            new[]
            {
                "Complete", "LeadingPartial", "TrailingPartial", "Incoherent",
            },
            Enum.GetNames<LapBoundaryCompleteness>());
        CollectionAssert.AreEqual(
            new[]
            {
                "None", "InvalidationObserved", "PitObserved",
                "FlashbackObserved", "MaterialGapObserved", "ContextChanged",
                "UnsupportedContext", "PlayerIndexChanged", "MissingContext",
            },
            Enum.GetNames<LapEvidenceFlags>());
        CollectionAssert.AreEqual(
            new[] { "Included", "Excluded" },
            Enum.GetNames<LapAuditDecision>());
        CollectionAssert.AreEqual(
            new[]
            {
                "Invalidated", "PitEntryOrExit", "FlashbackObserved",
                "MaterialGap", "ContextMismatch", "IncompleteLap",
                "OtherFactual",
            },
            Enum.GetNames<LapExclusionReason>());
    }

    [TestMethod]
    public void CandidateIdRequiresCanonicalLowercaseSha256()
    {
        var value = new string('a', 64);
        var id = new LapCandidateId(value);

        Assert.AreEqual(value, id.Value);
        foreach (var invalid in new[]
                 {
                     "", new string('a', 63), new string('a', 65),
                     new string('A', 64), new string('g', 64),
                 })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new LapCandidateId(invalid),
                invalid);
        }
    }

    [TestMethod]
    public void EvidenceFlagsAreClosedToTheFrozenMask()
    {
        var combined = LapEvidenceFlags.InvalidationObserved
            | LapEvidenceFlags.MaterialGapObserved;

        Assert.AreEqual(combined, LapEvidence.Validate(combined));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LapEvidence.Validate((LapEvidenceFlags)(1 << 8)));
    }
}
