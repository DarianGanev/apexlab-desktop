using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ApexLab.App.Lifecycle;
using Microsoft.Extensions.Hosting;

namespace ApexLab.App;

public sealed class AppHostLifetime : IDisposable
{
    private readonly IHost _host;
    private readonly Action<IServiceProvider> _showMainWindow;
    private readonly TimeSpan _shutdownTimeout;
    private bool _hostStarted;
    private bool _ownershipReleased;
    private Task _deferredHostDisposalCompletion = Task.CompletedTask;

    public AppHostLifetime(
        IHost host,
        Action<IServiceProvider> showMainWindow,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(showMainWindow);

        _host = host;
        _showMainWindow = showMainWindow;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (_shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownTimeout),
                "Shutdown timeout must be positive.");
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_ownershipReleased, this);
        if (_hostStarted)
        {
            return;
        }

        try
        {
            _host.StartAsync().GetAwaiter().GetResult();
            _hostStarted = true;
            _showMainWindow(_host.Services);
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = ReleaseOwnership();
            ThrowPreservingPrimary(primaryFailure, cleanupFailures);
        }
    }

    public void Dispose()
    {
        var cleanupFailures = ReleaseOwnership();
        ThrowCleanupFailures(cleanupFailures);
    }

    public Task DeferredHostDisposalCompletion =>
        Volatile.Read(ref _deferredHostDisposalCompletion);

    private List<Exception> ReleaseOwnership()
    {
        if (_ownershipReleased)
        {
            return [];
        }

        _ownershipReleased = true;
        var failures = new List<Exception>();
        if (_hostStarted)
        {
            try
            {
                _host.StopAsync(_shutdownTimeout).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var lifecycleOwnership = GetLifecycleOwnershipCompletion(failures);
        if (lifecycleOwnership is not null && !lifecycleOwnership.IsCompleted)
        {
            var deferredDisposal = DisposeHostAfterAsync(lifecycleOwnership);
            Volatile.Write(
                ref _deferredHostDisposalCompletion,
                deferredDisposal);
            ObserveFault(deferredDisposal);
            return failures;
        }

        try
        {
            _host.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures;
    }

    private Task? GetLifecycleOwnershipCompletion(
        ICollection<Exception> failures)
    {
        try
        {
            var coordinator = _host.Services.GetService(
                    typeof(ApplicationLifecycleCoordinator))
                as ApplicationLifecycleCoordinator;
            return coordinator is null
                ? null
                : AwaitLifecycleOwnershipReleaseAsync(coordinator);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return null;
        }
    }

    private static async Task AwaitLifecycleOwnershipReleaseAsync(
        ApplicationLifecycleCoordinator coordinator)
    {
        _ = await coordinator.StopAsync().ConfigureAwait(false);

        while (true)
        {
            var observed = coordinator.DeferredCleanupCompletion;
            await observed.ConfigureAwait(false);
            if (ReferenceEquals(
                    observed,
                    coordinator.DeferredCleanupCompletion))
            {
                return;
            }
        }
    }

    private async Task DisposeHostAfterAsync(Task deferredCleanup)
    {
        var failures = new List<Exception>();
        try
        {
            await deferredCleanup.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _host.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        ThrowCleanupFailures(failures);
    }

    private static void ThrowPreservingPrimary(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(
            "ApexLab startup failed and host cleanup also reported failures.",
            new[] { primaryFailure }.Concat(cleanupFailures));
    }

    private static void ThrowCleanupFailures(IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            return;
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(
            "ApexLab host shutdown reported multiple failures.",
            cleanupFailures);
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
