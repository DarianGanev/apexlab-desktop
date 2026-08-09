using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;

namespace ApexLab.Persistence.Tests.Laps;

[TestClass]
public sealed class BahrainLapAuditJsonTests
{
    [TestMethod]
    public void TemplateSerializationIsDeterministicReadableAndRoundTripsExactly()
    {
        var document = BahrainLapAuditTestData.Document();

        var first = BahrainLapAuditJson.Serialize(document);
        var second = BahrainLapAuditJson.Serialize(document);
        var roundTrip = BahrainLapAuditJson.Deserialize(first);

        CollectionAssert.AreEqual(first, second);
        Assert.AreEqual((byte)'{', first[0]);
        Assert.AreEqual((byte)'\n', first[^1]);
        Assert.IsFalse(first.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.AreEqual(
            "359c1e19bc0040ca8471912ca63065177c28590d873654cf5866c48b26049f0b",
            Convert.ToHexStringLower(SHA256.HashData(first)));
        Assert.AreEqual(document.SchemaVersion, roundTrip.SchemaVersion);
        Assert.AreEqual(document.LapAuditId, roundTrip.LapAuditId);
        Assert.AreEqual(document.CanonicalIdentitySha256, roundTrip.CanonicalIdentitySha256);
        Assert.AreEqual(document.CanonicalSha256, roundTrip.CanonicalSha256);
        Assert.AreEqual(document.ReferenceContext, roundTrip.ReferenceContext);
        Assert.AreEqual(document.ManualInputs, roundTrip.ManualInputs);
        CollectionAssert.AreEqual(document.Entries.ToArray(), roundTrip.Entries.ToArray());

        using var json = JsonDocument.Parse(first);
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion", "lapAuditId", "canonicalIdentitySha256",
                "canonicalSha256", "referenceContext", "manualInputs", "entries",
            },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void CompletedDecisionAndConditionalReasonRoundTrip()
    {
        var included = BahrainLapAuditJson.Deserialize(
            BahrainLapAuditJson.Serialize(
                BahrainLapAuditTestData.Document(LapAuditDecision.Included)));
        var excluded = BahrainLapAuditJson.Deserialize(
            BahrainLapAuditJson.Serialize(
                BahrainLapAuditTestData.Document(
                    LapAuditDecision.Excluded,
                    LapExclusionReason.PitEntryOrExit)));

        Assert.AreEqual(LapAuditDecision.Included, included.Entries[0].Decision);
        Assert.IsNull(included.Entries[0].ExclusionReason);
        Assert.AreEqual(LapAuditDecision.Excluded, excluded.Entries[0].Decision);
        Assert.AreEqual(
            LapExclusionReason.PitEntryOrExit,
            excluded.Entries[0].ExclusionReason);
    }

    [TestMethod]
    public void ParserRejectsMalformedNonCanonicalAndOpenDocuments()
    {
        var canonical = Encoding.UTF8.GetString(
            BahrainLapAuditJson.Serialize(BahrainLapAuditTestData.Document()));
        string[] invalid =
        [
            canonical[..^1],
            "\uFEFF" + canonical,
            canonical.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"unknown\": true,",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"lapAuditId\": \"bahrain-tt-lap-audit-v1\"",
                "\"lapAuditId\": \"bahrain-tt-lap-audit-v2\"",
                StringComparison.Ordinal),
            canonical.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            canonical.Replace("\"complete\"", "\"unknown\"", StringComparison.Ordinal),
            canonical.Replace("\"evidenceFlags\": []", "\"evidenceFlags\": [\"unknown\"]", StringComparison.Ordinal),
            canonical + "{}\n",
            "{not-json}\n",
        ];

        foreach (var json in invalid)
        {
            Assert.ThrowsExactly<InvalidDataException>(
                () => BahrainLapAuditJson.Deserialize(Encoding.UTF8.GetBytes(json)),
                json[..Math.Min(json.Length, 40)]);
        }
    }

    [TestMethod]
    public void CodecEnforcesDocumentAndManualTextBounds()
    {
        var oversized = new byte[BahrainLapAuditJson.MaximumLengthBytes + 1];
        oversized[^1] = (byte)'\n';
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BahrainLapAuditJson.Deserialize(oversized));

        var canonical = Encoding.UTF8.GetString(BahrainLapAuditJson.Serialize(
            BahrainLapAuditTestData.Document(LapAuditDecision.Included)));
        var invalidText = canonical.Replace(
            "F1 25 current PC build",
            new string('x', 81),
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BahrainLapAuditJson.Deserialize(Encoding.UTF8.GetBytes(invalidText)));

        var document = BahrainLapAuditTestData.Document();
        var invalidManualInputs = new BahrainLapAuditManualInputs(
            " F1 25 current PC build",
            "Ferrari",
            "Wheel",
            "Baseline setup",
            "Soft",
            true,
            true,
            true,
            true);
        var invalidDocument = new BahrainLapAuditDocument(
            document.SchemaVersion,
            document.LapAuditId,
            document.CanonicalIdentitySha256,
            document.CanonicalSha256,
            document.ReferenceContext,
            invalidManualInputs,
            document.Entries);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BahrainLapAuditJson.Serialize(invalidDocument));
    }
}
