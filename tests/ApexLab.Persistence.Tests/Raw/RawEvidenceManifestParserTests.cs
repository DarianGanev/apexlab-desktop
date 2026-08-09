using System.Text;
using ApexLab.Application.Capture;
using ApexLab.Persistence.Raw;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
public sealed class RawEvidenceManifestParserTests
{
    private static readonly RawEvidenceCaptureId CaptureId =
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");

    [TestMethod]
    public void ParsesOnlyTheExactCanonicalManifest()
    {
        var bytes = CreateCanonicalManifest();

        var parsed = RawEvidenceManifestParser.Parse(bytes);

        Assert.AreEqual(CaptureId, parsed.Manifest.CaptureId);
        Assert.AreEqual(
            "ea-f1-25-v3",
            parsed.Manifest.ProtocolId.Value);
        Assert.AreEqual(136L, parsed.Manifest.DataLengthBytes);
        Assert.HasCount(32, parsed.Digest);
        Assert.IsTrue(parsed.Digest.All(value => value == 0x5a));
    }

    [TestMethod]
    [DataRow("leading whitespace")]
    [DataRow("reordered properties")]
    [DataRow("uppercase digest")]
    [DataRow("unknown property")]
    [DataRow("duplicate property")]
    [DataRow("exponent number")]
    public void RejectsAnyNoncanonicalJsonRepresentation(string mutation)
    {
        var text = Encoding.UTF8.GetString(
            CreateCanonicalManifest());
        text = mutation switch
        {
            "leading whitespace" => $" {text}",
            "reordered properties" => text.Replace(
                "{\"schemaVersion\":1,\"dataFormatVersion\":1",
                "{\"dataFormatVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal),
            "uppercase digest" => text.Replace(
                string.Concat(Enumerable.Repeat("5a", 32)),
                string.Concat(Enumerable.Repeat("5A", 32)),
                StringComparison.Ordinal),
            "unknown property" => text.Replace(
                "{\"schemaVersion\":1",
                "{\"unknown\":0,\"schemaVersion\":1",
                StringComparison.Ordinal),
            "duplicate property" => text.Replace(
                "{\"schemaVersion\":1",
                "{\"schemaVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal),
            "exponent number" => text.Replace(
                "\"dataLengthBytes\":136",
                "\"dataLengthBytes\":1.36e2",
                StringComparison.Ordinal),
            _ => throw new AssertFailedException("Unknown mutation."),
        };

        var exception = Assert.ThrowsExactly<RawEvidenceReadException>(
            () => RawEvidenceManifestParser.Parse(
                Encoding.UTF8.GetBytes(text)),
            mutation);
        Assert.AreEqual(
            RawEvidenceReadFailureKind.MalformedStructure,
            exception.Kind);
    }

    [TestMethod]
    public void RejectsOversizedInputBeforeJsonParsing()
    {
        var exception = Assert.ThrowsExactly<RawEvidenceReadException>(
            () => RawEvidenceManifestParser.Parse(
                new byte[RawEvidenceFormat.MaximumManifestBytes + 1]));
        Assert.AreEqual(
            RawEvidenceReadFailureKind.DeclaredLimitViolation,
            exception.Kind);
    }

    private static byte[] CreateCanonicalManifest()
    {
        var manifest = new RawEvidenceManifest(
            CaptureId,
            RawEvidenceProtocolId.Parse("ea-f1-25-v3"),
            dataLengthBytes: 136,
            recordCount: 0,
            firstSequence: null,
            lastSequence: null,
            firstArrivalTimestamp: null,
            lastArrivalTimestamp: null,
            stopwatchFrequency: 1_000,
            createdUtcTicks: 621_355_968_000_000_000,
            finalizedUtcTicks: 621_355_968_000_000_001,
            new RawEvidenceLimits(
                minimumFreeSpaceBytes: 0));
        return RawEvidenceFormat.ApplyDigest(
            RawEvidenceFormat.SerializeManifestPreimage(manifest),
            Enumerable.Repeat((byte)0x5a, 32).ToArray());
    }
}
