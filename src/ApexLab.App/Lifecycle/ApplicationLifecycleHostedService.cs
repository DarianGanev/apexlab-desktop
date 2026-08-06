using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace ApexLab.App.Lifecycle;

public sealed class ApplicationLifecycleHostedService : IHostedService
{
    private readonly object _gate = new();
    private LifecycleStopResult? _lastStopResult;

    public ApplicationLifecycleHostedService(
        ApplicationLifecycleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        Coordinator = coordinator;
    }

    public ApplicationLifecycleCoordinator Coordinator { get; }

    public LifecycleStopResult? LastStopResult
    {
        get
        {
            lock (_gate)
            {
                return _lastStopResult;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var outcome = await Coordinator.StartAsync(cancellationToken)
            .ConfigureAwait(false);
        if (outcome == LifecycleStartOutcome.AlreadyRunning)
        {
            throw new InvalidOperationException(
                "Another ApexLab instance is already running for this Windows user.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stop = Coordinator.StopAsync();
        LifecycleStopResult result;
        try
        {
            result = await stop.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveLateStopResultAsync(stop);
            return;
        }

        SetLastStopResult(result);
        ThrowIfFailed(result);
    }

    private async Task ObserveLateStopResultAsync(
        Task<LifecycleStopResult> stop)
    {
        try
        {
            SetLastStopResult(await stop.ConfigureAwait(false));
        }
        catch
        {
            _ = stop.Exception;
        }
    }

    private void SetLastStopResult(LifecycleStopResult result)
    {
        lock (_gate)
        {
            _lastStopResult = result;
        }
    }

    private static void ThrowIfFailed(LifecycleStopResult result)
    {
        if (result.Outcome != LifecycleStopOutcome.Failed
            || result.PrimaryFailure is null)
        {
            return;
        }

        if (result.SubsequentFailures.Count != 0)
        {
            throw new AggregateException(
                "ApexLab host shutdown reported multiple lifecycle failures.",
                new[] { result.PrimaryFailure }
                    .Concat(result.SubsequentFailures));
        }

        ExceptionDispatchInfo.Capture(result.PrimaryFailure).Throw();
    }
}
