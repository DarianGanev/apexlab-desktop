using ApexLab.App.CommandLine;

namespace ApexLab.App;

internal sealed class AppStartupCoordinator
{
    private readonly Func<IReadOnlyList<string>, int> _runSmokeTest;
    private readonly Action _startInteractiveShell;

    internal AppStartupCoordinator(
        Func<IReadOnlyList<string>, int> runSmokeTest,
        Action startInteractiveShell)
    {
        ArgumentNullException.ThrowIfNull(runSmokeTest);
        ArgumentNullException.ThrowIfNull(startInteractiveShell);
        _runSmokeTest = runSmokeTest;
        _startInteractiveShell = startInteractiveShell;
    }

    internal int? Start(IReadOnlyList<string> arguments)
    {
        var parsed = StartupArguments.Parse(arguments);
        if (!parsed.IsSuccess)
        {
            return (int)SmokeTestExitCode.InvalidArguments;
        }

        if (parsed.Value!.Mode == StartupMode.SmokeTest)
        {
            return _runSmokeTest(arguments);
        }

        _startInteractiveShell();
        return null;
    }
}
