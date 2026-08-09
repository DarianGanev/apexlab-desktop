using System.Text.RegularExpressions;
using ApexLab.Application.Identity;

namespace ApexLab.Application.Tests.Identity;

[TestClass]
public sealed class ApplicationIdentityTests
{
    [TestMethod]
    public void ProductName_IsApexLab()
    {
        Assert.AreEqual("ApexLab", ApplicationIdentity.ProductName);
    }

    [TestMethod]
    public void InformationalVersion_IsSemanticVersion()
    {
        const string semanticVersionPattern =
            @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$";

        Assert.IsTrue(
            Regex.IsMatch(ApplicationIdentity.InformationalVersion, semanticVersionPattern),
            $"'{ApplicationIdentity.InformationalVersion}' is not a semantic version.");
    }
}
