using ApexLab.Replay.BahrainValidation;
using ApexLab.Replay.Probe;
using ApexLab.Replay.Replay;
using ApexLab.Replay.Validation;

using var interruption = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    interruption.Cancel();
};

Console.CancelKeyPress += cancelHandler;
try
{
    if (args.Length != 0 && args[0] == "validate-bahrain-decoder")
    {
        var validation = await BahrainDecoderValidationCommand.ExecuteAsync(
            args,
            interruption.Token);
        await Console.Out.WriteLineAsync(validation.Json);
        return (int)validation.ExitCode;
    }

    if (args.Length != 0 && args[0] == "validate")
    {
        var validation = await PrivateValidationCommand.ExecuteAsync(
            args,
            interruption.Token);
        await Console.Out.WriteLineAsync(validation.Json);
        return (int)validation.ExitCode;
    }

    if (args.Length != 0 && args[0] == "replay")
    {
        var replay = await ReplayCommand.ExecuteAsync(
            args,
            interruption.Token);
        await Console.Out.WriteLineAsync(replay.Json);
        return (int)replay.ExitCode;
    }

    var probe = await ProbeCommand.ExecuteAsync(
        args,
        interruption.Token);
    await Console.Out.WriteLineAsync(probe.Json);
    return (int)probe.ExitCode;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
