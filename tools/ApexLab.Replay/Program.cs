using ApexLab.Replay.Probe;

using var interruption = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    interruption.Cancel();
};

Console.CancelKeyPress += cancelHandler;
try
{
    var result = await ProbeCommand.ExecuteAsync(args, interruption.Token);
    await Console.Out.WriteLineAsync(result.Json);
    return (int)result.ExitCode;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
