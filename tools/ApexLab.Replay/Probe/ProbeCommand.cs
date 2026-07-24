using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using ApexLab.Application.Capture;
using ApexLab.Protocols.F125;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Udp;

namespace ApexLab.Replay.Probe;

internal static class ProbeCommand
{
    public static async Task<ProbeCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken interruptionToken)
    {
        return await ExecuteAsync(
                arguments,
                interruptionToken,
                static options => new UdpDatagramSource(options))
            .ConfigureAwait(false);
    }

    internal static async Task<ProbeCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken interruptionToken,
        Func<UdpDatagramSourceOptions, IDatagramSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);

        if (!ProbeArguments.TryParse(arguments, out var parsed))
        {
            return new(
                ProbeExitCode.InvalidArguments,
                ProbeJson.SerializeStatus("invalidArguments"));
        }

        var options = new UdpDatagramSourceOptions(
            parsed.Port,
            parsed.ChannelCapacity,
            parsed.MaximumDatagramBytes);
        var adapter = new F125TelemetryProtocolAdapter();
        var aggregator = new ProbeAggregator();
        CaptureIngestionCoordinator? coordinator = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using (var source = sourceFactory(options))
            {
                using var runCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        interruptionToken);
                coordinator = new CaptureIngestionCoordinator(
                    source,
                    adapter,
                    SenderPolicy.LoopbackOnly,
                    aggregator);
                var runTask = coordinator.RunAsync(runCancellation.Token);
                var durationTask = Task.Delay(parsed.Duration, interruptionToken);
                var firstCompleted = await Task.WhenAny(runTask, durationTask)
                    .ConfigureAwait(false);

                if (ReferenceEquals(firstCompleted, runTask))
                {
                    await runTask.ConfigureAwait(false);
                }
                else if (interruptionToken.IsCancellationRequested)
                {
                    await AwaitInterruptedAsync(runTask).ConfigureAwait(false);
                    return BuildResult(
                        ProbeExitCode.Interrupted,
                        "interrupted",
                        adapter.ProtocolId,
                        stopwatch.Elapsed,
                        coordinator.Counters,
                        aggregator);
                }
                else
                {
                    runCancellation.Cancel();
                    await StopAndObserveRunAsync(
                            () => source.StopAsync(),
                            runTask,
                            runCancellation.Token)
                        .ConfigureAwait(false);
                }

                return BuildCaptureOutcome(
                    adapter.ProtocolId,
                    stopwatch.Elapsed,
                    coordinator.Counters,
                    aggregator);
            }
        }
        catch (OperationCanceledException)
            when (interruptionToken.IsCancellationRequested
                  && coordinator is not null)
        {
            return BuildResult(
                ProbeExitCode.Interrupted,
                "interrupted",
                adapter.ProtocolId,
                stopwatch.Elapsed,
                coordinator.Counters,
                aggregator);
        }
        catch (CaptureSourceStartupException exception)
            when (exception.InnerException is SocketException)
        {
            return new(
                ProbeExitCode.BindFailure,
                ProbeJson.SerializeStatus("bindFailure"));
        }
        catch (Exception)
        {
            return new(
                ProbeExitCode.UnexpectedFailure,
                ProbeJson.SerializeStatus("unexpectedFailure"));
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    internal static async Task StopAndObserveRunAsync(
        Func<Task> stopOperation,
        Task runTask,
        CancellationToken expectedRunCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(stopOperation);
        ArgumentNullException.ThrowIfNull(runTask);

        Exception? stopFailure = null;
        Exception? runFailure = null;
        try
        {
            await stopOperation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            runFailure = exception;
        }

        if (expectedRunCancellation.IsCancellationRequested
            && runFailure is OperationCanceledException)
        {
            runFailure = null;
        }

        ThrowFailures(stopFailure, runFailure);
    }

    private static ProbeCommandResult BuildCaptureOutcome(
        string protocolId,
        TimeSpan elapsed,
        CaptureIngestionCounters counters,
        ProbeAggregator aggregator)
    {
        if (counters.Source.DatagramsObserved == 0)
        {
            return BuildResult(
                ProbeExitCode.NoTraffic,
                "noTraffic",
                protocolId,
                elapsed,
                counters,
                aggregator);
        }

        if (counters.Classifier.Compatible == 0)
        {
            return BuildResult(
                ProbeExitCode.IncompatibleOnlyTraffic,
                "incompatibleOnlyTraffic",
                protocolId,
                elapsed,
                counters,
                aggregator);
        }

        return BuildResult(
            ProbeExitCode.Success,
            "success",
            protocolId,
            elapsed,
            counters,
            aggregator);
    }

    private static async Task AwaitInterruptedAsync(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ThrowFailures(Exception? stopFailure, Exception? runFailure)
    {
        if (stopFailure is null && runFailure is null)
        {
            return;
        }

        if (stopFailure is null)
        {
            ExceptionDispatchInfo.Capture(runFailure!).Throw();
        }

        if (runFailure is null || ReferenceEquals(stopFailure, runFailure))
        {
            ExceptionDispatchInfo.Capture(stopFailure!).Throw();
        }

        throw new AggregateException(stopFailure!, runFailure!);
    }

    private static ProbeCommandResult BuildResult(
        ProbeExitCode exitCode,
        string status,
        string protocolId,
        TimeSpan elapsed,
        CaptureIngestionCounters counters,
        ProbeAggregator aggregator)
    {
        var report = aggregator.BuildReport(
            status,
            protocolId,
            elapsed,
            counters);
        return new(exitCode, ProbeJson.Serialize(report));
    }
}
