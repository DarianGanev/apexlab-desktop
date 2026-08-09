using ApexLab.Application.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class RawEvidenceProtocolIdTests
{
    [TestMethod]
    public void ParseAcceptsCanonicalProtocolIdentity()
    {
        var parsed = RawEvidenceProtocolId.Parse("ea-f1-25-v3");

        Assert.AreEqual("ea-f1-25-v3", parsed.Value);
        Assert.AreEqual(parsed.Value, parsed.ToString());
        Assert.IsTrue(
            RawEvidenceProtocolId.TryParse(parsed.Value, out var tried));
        Assert.AreEqual(parsed, tried);
    }

    [TestMethod]
    public void AcceptsEveryLengthBoundary()
    {
        Assert.AreEqual(
            "a",
            RawEvidenceProtocolId.Parse("a").Value);
        Assert.AreEqual(
            64,
            RawEvidenceProtocolId.Parse($"a{new string('1', 63)}").Value.Length);
    }

    [TestMethod]
    public void RejectsNoncanonicalProtocolIdentity()
    {
        string?[] invalid =
        [
            null,
            "",
            ".ea-f1",
            "-ea-f1",
            "EA-F1",
            "ea_f1",
            "ea f1",
            $"a{new string('1', 64)}",
        ];

        foreach (var value in invalid)
        {
            Assert.IsFalse(RawEvidenceProtocolId.TryParse(value, out var parsed));
            Assert.IsNull(parsed);
            Assert.ThrowsExactly<FormatException>(
                () => RawEvidenceProtocolId.Parse(value!));
        }
    }
}
