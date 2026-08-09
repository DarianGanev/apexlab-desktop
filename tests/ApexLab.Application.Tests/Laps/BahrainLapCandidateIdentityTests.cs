using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;

namespace ApexLab.Application.Tests.Laps;

[TestClass]
public sealed class BahrainLapCandidateIdentityTests
{
    private const string CanonicalIdentity =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void FrozenLengthDelimitedCandidateIdentityHasGoldenDigest()
    {
        var id = BahrainLapCandidateIdentity.Calculate(
            CanonicalIdentity,
            LapBoundary.Complete(10, 20, 7, 90_123),
            LapEvidenceFlags.None,
            BahrainTelemetryContextTests.Context(
                BahrainTelemetryContextTests.Session()));

        Assert.AreEqual(
            "305dec329a4c87271990fbbd4f6d61d6fc9373ecec5faca44d07c677ffa5b0fb",
            id.Value);
    }

    [TestMethod]
    public void EveryEvidenceComponentChangesCandidateIdentity()
    {
        var context = BahrainTelemetryContextTests.Context(
            BahrainTelemetryContextTests.Session());
        var original = BahrainLapCandidateIdentity.Calculate(
            CanonicalIdentity,
            LapBoundary.Complete(10, 20, 7, 90_123),
            LapEvidenceFlags.None,
            context);
        LapCandidateId[] mutations =
        [
            BahrainLapCandidateIdentity.Calculate(
                new string('a', 64),
                LapBoundary.Complete(10, 20, 7, 90_123),
                LapEvidenceFlags.None,
                context),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(11, 20, 7, 90_123),
                LapEvidenceFlags.None,
                context),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(10, 20, 8, 90_123),
                LapEvidenceFlags.None,
                context),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(10, 20, 7, 90_124),
                LapEvidenceFlags.None,
                context),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(10, 20, 7, 90_123),
                LapEvidenceFlags.PitObserved,
                context),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(10, 20, 7, 90_123),
                LapEvidenceFlags.None,
                BahrainTelemetryContextTests.Context(
                    BahrainTelemetryContextTests.Session(
                        trackTemperatureCelsius: 31))),
            BahrainLapCandidateIdentity.Calculate(
                CanonicalIdentity,
                LapBoundary.Complete(10, 20, 7, 90_123),
                LapEvidenceFlags.None,
                context: null),
        ];

        foreach (var mutation in mutations)
        {
            Assert.AreNotEqual(original, mutation);
        }
    }

    [TestMethod]
    public void InventoryDefensivelyCopiesAndRequiresOrderedUniqueCandidates()
    {
        var context = BahrainTelemetryContextTests.Context(
            BahrainTelemetryContextTests.Session());
        var first = Candidate('a', 1, context);
        var second = Candidate('b', 20, context);
        var supplied = new[] { first, second };
        var inventory = new BahrainLapInventory(
            CanonicalIdentity,
            new string('b', 64),
            context,
            supplied);

        supplied[0] = second;
        Assert.AreSame(first, inventory.Candidates[0]);
        Assert.AreEqual(context, inventory.ReferenceContext);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BahrainLapInventory(
                CanonicalIdentity,
                new string('b', 64),
                context,
                [second, first]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BahrainLapInventory(
                CanonicalIdentity,
                new string('b', 64),
                context,
                [first, first]));
    }

    private static BahrainLapCandidate Candidate(
        char id,
        long start,
        BahrainTelemetryContext context) =>
        new(
            new LapCandidateId(new string(id, 64)),
            LapBoundary.Complete(
                start,
                start + 10,
                checked((byte)((start / 20) + 1)),
                90_000),
            LapEvidenceFlags.None,
            context);
}
