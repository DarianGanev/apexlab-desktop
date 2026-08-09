using ApexLab.App;
using ApexLab.App.CommandLine;

namespace ApexLab.IntegrationTests.App;

[TestClass]
public sealed class AppStartupCoordinatorTests
{
    [TestMethod]
    public void SmokeMode_RunsSmokePathWithoutStartingInteractiveShell()
    {
        var interactiveStarts = 0;
        var smokeStarts = 0;
        var subject = new AppStartupCoordinator(
            _ =>
            {
                smokeStarts++;
                return (int)SmokeTestExitCode.Success;
            },
            () => interactiveStarts++);

        var exitCode = subject.Start(
        [
            "--smoke-test",
            "--data-root",
            Path.Combine(Path.GetTempPath(), "apexlab-startup-smoke"),
            "--result-file",
            Path.Combine(Path.GetTempPath(), "apexlab-startup-result.json"),
        ]);

        Assert.AreEqual((int)SmokeTestExitCode.Success, exitCode);
        Assert.AreEqual(1, smokeStarts);
        Assert.AreEqual(0, interactiveStarts);
    }

    [TestMethod]
    public void InteractiveMode_StartsShellWithoutRequestingApplicationExit()
    {
        var interactiveStarts = 0;
        var smokeStarts = 0;
        var subject = new AppStartupCoordinator(
            _ =>
            {
                smokeStarts++;
                return (int)SmokeTestExitCode.Success;
            },
            () => interactiveStarts++);

        var exitCode = subject.Start([]);

        Assert.IsNull(exitCode);
        Assert.AreEqual(0, smokeStarts);
        Assert.AreEqual(1, interactiveStarts);
    }

    [TestMethod]
    public void InvalidArguments_ExitWithoutStartingEitherPath()
    {
        var interactiveStarts = 0;
        var smokeStarts = 0;
        var subject = new AppStartupCoordinator(
            _ =>
            {
                smokeStarts++;
                return (int)SmokeTestExitCode.Success;
            },
            () => interactiveStarts++);

        var exitCode = subject.Start(["--unknown"]);

        Assert.AreEqual((int)SmokeTestExitCode.InvalidArguments, exitCode);
        Assert.AreEqual(0, smokeStarts);
        Assert.AreEqual(0, interactiveStarts);
    }
}
