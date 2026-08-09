using ApexLab.Domain.Laps;

namespace ApexLab.Domain.Tests.Laps;

[TestClass]
public sealed class BahrainManualContextTests
{
    [TestMethod]
    public void CompleteConfirmedContextRetainsBoundedLocalValues()
    {
        var context = Context();

        Assert.AreEqual("F1 25 current PC build", context.GameBuild);
        Assert.AreEqual("F1 car", context.PlayerVehicle);
        Assert.AreEqual("Wheel profile", context.ControllerProfile);
        Assert.AreEqual("Fixed setup A", context.SetupDescriptor);
        Assert.AreEqual("Soft", context.TyreCompound);
        Assert.IsTrue(context.IsFullyConfirmed);
    }

    [TestMethod]
    public void FalseConfirmationIsRepresentableButNotFullyConfirmed()
    {
        var context = Context(setupUnchanged: false);

        Assert.IsFalse(context.SetupUnchanged);
        Assert.IsFalse(context.IsFullyConfirmed);
    }

    [TestMethod]
    public void ManualValuesMustBeTrimmedBoundedAndSingleLine()
    {
        foreach (var invalid in new[] { "", " ", " padded", "padded ", "line\nbreak" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => Context(gameBuild: invalid));
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            Context(gameBuild: new string('x', 81)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Context(playerVehicle: new string('x', 81)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Context(controllerProfile: new string('x', 81)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Context(setupDescriptor: new string('x', 121)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            Context(tyreCompound: new string('x', 41)));
    }

    internal static BahrainManualContext Context(
        string gameBuild = "F1 25 current PC build",
        string playerVehicle = "F1 car",
        string controllerProfile = "Wheel profile",
        string setupDescriptor = "Fixed setup A",
        string tyreCompound = "Soft",
        bool evidenceIntegrityPassed = true,
        bool trackAndModeVisuallyConfirmed = true,
        bool setupUnchanged = true,
        bool contextCrossCheckPassed = true) =>
        new(
            gameBuild,
            playerVehicle,
            controllerProfile,
            setupDescriptor,
            tyreCompound,
            evidenceIntegrityPassed,
            trackAndModeVisuallyConfirmed,
            setupUnchanged,
            contextCrossCheckPassed);
}
