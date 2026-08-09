using ApexLab.Application.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class RawEvidenceCaptureIdTests
{
    [TestMethod]
    public void CreateProducesCanonicalRfcVersion4Identity()
    {
        var first = RawEvidenceCaptureId.Create();
        var second = RawEvidenceCaptureId.Create();

        Assert.AreEqual(32, first.Value.Length);
        Assert.IsTrue(first.Value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        Assert.AreEqual('4', first.Value[12]);
        Assert.Contains(first.Value[16], "89ab");
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(first.Value, first.ToString());
    }

    [TestMethod]
    public void ParseRoundTripsCanonicalIdentity()
    {
        const string value = "00112233445546778899aabbccddeeff";

        var parsed = RawEvidenceCaptureId.Parse(value);
        var success = RawEvidenceCaptureId.TryParse(value, out var tried);

        Assert.IsTrue(success);
        Assert.IsNotNull(tried);
        Assert.AreEqual(value, parsed.Value);
        Assert.AreEqual(parsed, tried);
    }

    [TestMethod]
    public void RejectsNoncanonicalOrNonVersion4Identities()
    {
        string?[] invalid =
        [
            null,
            "",
            "00112233445546778899aabbccddeef",
            "00112233445546778899aabbccddeeff0",
            "00112233445546778899AABBCCDDEEFF",
            "00112233-4455-4677-8899-aabbccddeeff",
            "00112233445536778899aabbccddeeff",
            "00112233445546777899aabbccddeeff",
            "gg112233445546778899aabbccddeeff",
        ];

        foreach (var value in invalid)
        {
            Assert.IsFalse(RawEvidenceCaptureId.TryParse(value, out var parsed));
            Assert.IsNull(parsed);
            Assert.ThrowsExactly<FormatException>(
                () => RawEvidenceCaptureId.Parse(value!));
        }
    }
}
