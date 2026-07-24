using System.Diagnostics;
using System.Net.Sockets;
using ApexLab.Application.Capture;
using ApexLab.Protocols.F125;
using ApexLab.Telemetry.Udp;

namespace ApexLab.Replay.Probe;

internal static class ProbeCommand
{
    public static async Task<ProbeCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken interruptionToken)
    {
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
        await using var source = new UdpDatagramSource(options);
        var adapter = new F125TelemetryProtocolAdapter();
        var aggregator = new ProbeAggregator();
        var coordinator = new CaptureIngestionCoordinator(
            source,
            adapter,
            SenderPolicy.LoopbackOnly,
            aggregator);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var runTask = coordinator.RunAsync(interruptionToken);
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
                await source.StopAsync().ConfigureAwait(false);
                await runTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (interruptionToken.IsCancellationRequested)
        {
            return BuildResult(
                ProbeExitCode.Interrupted,
                "interrupted",
                adapter.ProtocolId,
                stopwatch.Elapsed,
                coordinator.Counters,
                aggregator);
        }
        catch (SocketException)
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

        var counters = coordinator.Counters;
        if (counters.Source.DatagramsObserved == 0)
        {
            return BuildResult(
                ProbeExitCode.NoTraffic,
                "noTraffic",
                adapter.ProtocolId,
                stopwatch.Elapsed,
                counters,
                aggregator);
        }

        if (counters.Classifier.Compatible == 0)
        {
            return BuildResult(
                ProbeExitCode.IncompatibleOnlyTraffic,
                "incompatibleOnlyTraffic",
                adapter.ProtocolId,
                stopwatch.Elapsed,
                counters,
                aggregator);
        }

        return BuildResult(
            ProbeExitCode.Success,
            "success",
            adapter.ProtocolId,
            stopwatch.Elapsed,
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
