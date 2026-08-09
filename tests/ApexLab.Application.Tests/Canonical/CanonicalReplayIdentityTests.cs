using ApexLab.Application.Canonical;

namespace ApexLab.Application.Tests.Canonical;

[TestClass]
public sealed class CanonicalReplayIdentityTests
{
    private const string SourceSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void LengthFramedFrozenIdentityHasAnIndependentGoldenDigest()
    {
        var identity = Identity();

        Assert.AreEqual(SourceSha256, identity.SourceEvidenceSha256);
        Assert.AreEqual("ea-f1-25-v3", identity.ProtocolId);
        Assert.AreEqual("apexlab-bahrain-tt-slice-v1", identity.ContractId);
        Assert.AreEqual("f125-v3-minimal-decoder-v1", identity.DecoderId);
        Assert.AreEqual("apexlab-canonical-sample-v1", identity.CanonicalSchemaId);
        Assert.AreEqual(
            "75a1f0f8f2993b77daf1b67fcc7144a2fdec751ad765bc199f7a9a1123e65154",
            identity.IdentitySha256);
    }

    [TestMethod]
    public void EveryIdentityComponentChangesTheDigest()
    {
        var original = Identity();
        CanonicalReplayIdentity[] mutations =
        [
            Identity(sourceSha256: new string('a', 64)),
            Identity(protocolId: "ea-f1-25-v4"),
            Identity(contractId: "apexlab-bahrain-tt-slice-v2"),
            Identity(decoderId: "f125-v3-minimal-decoder-v2"),
            Identity(canonicalSchemaId: "apexlab-canonical-sample-v2"),
        ];

        foreach (var mutation in mutations)
        {
            Assert.AreNotEqual(original.IdentitySha256, mutation.IdentitySha256);
        }
    }

    [TestMethod]
    public void RejectsMalformedDigestAndVersionIdentities()
    {
        string[] invalidDigests =
        [
            "", new string('a', 63), new string('a', 65), new string('A', 64),
            new string('g', 64),
        ];
        foreach (var digest in invalidDigests)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => Identity(sourceSha256: digest),
                digest);
        }

        string[] invalidIds =
        [
            "", "Uppercase", " leading", "trailing ", "has/slash",
            "has_underscore", new string('a', 65),
        ];
        foreach (var id in invalidIds)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => Identity(protocolId: id),
                id);
        }
    }

    [TestMethod]
    public void CacheRequestRequiresBoundedSourceClockFrequency()
    {
        var request = new CanonicalCacheRequest(
            Identity(),
            sourceStopwatchFrequency: 10_000_000);

        Assert.AreEqual(10_000_000L, request.SourceStopwatchFrequency);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CanonicalCacheRequest(Identity(), 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CanonicalCacheRequest(Identity(), 10_000_000_001));
    }

    internal static CanonicalReplayIdentity Identity(
        string sourceSha256 = SourceSha256,
        string protocolId = "ea-f1-25-v3",
        string contractId = "apexlab-bahrain-tt-slice-v1",
        string decoderId = "f125-v3-minimal-decoder-v1",
        string canonicalSchemaId = "apexlab-canonical-sample-v1") =>
        new(
            sourceSha256,
            protocolId,
            contractId,
            decoderId,
            canonicalSchemaId);
}
