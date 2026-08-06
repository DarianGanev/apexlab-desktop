using ApexLab.App;
using ApexLab.App.Lifecycle;
using Microsoft.Extensions.Hosting;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
public sealed class AppHostLifetimeTests
{
    [TestMethod]
    public void Normal_start_and_repeated_exit_start_and_show_once_then_release_once()
    {
        var host = new RecordingHost();
        var showCount = 0;
        var subject = new AppHostLifetime(host, _ => showCount++);

        subject.Start();
        subject.Start();
        subject.Dispose();
        subject.Dispose();

        Assert.AreEqual(1, host.StartCount);
        Assert.AreEqual(1, showCount);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Start_failure_disposes_ownership_without_showing_or_stopping()
    {
        var failure = new InvalidOperationException("start failed");
        var host = new RecordingHost { StartFailure = failure };
        var showCount = 0;
        var subject = new AppHostLifetime(host, _ => showCount++);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Start);

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(0, showCount);
        Assert.AreEqual(0, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Resolve_or_show_failure_stops_started_host_and_disposes_ownership()
    {
        var failure = new InvalidOperationException("show failed");
        var host = new RecordingHost();
        var subject = new AppHostLifetime(host, _ => throw failure);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Start);

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Startup_failure_is_first_when_cleanup_also_fails()
    {
        var primary = new InvalidOperationException("show failed");
        var stopFailure = new InvalidOperationException("stop failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost
        {
            StopFailure = stopFailure,
            DisposeFailure = disposeFailure,
        };
        var subject = new AppHostLifetime(host, _ => throw primary);

        var thrown = Assert.ThrowsExactly<AggregateException>(subject.Start);

        CollectionAssert.AreEqual(
            new Exception[] { primary, stopFailure, disposeFailure },
            thrown.InnerExceptions.ToArray());
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Start_failure_remains_primary_when_dispose_also_fails()
    {
        var primary = new InvalidOperationException("start failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost
        {
            StartFailure = primary,
            DisposeFailure = disposeFailure,
        };
        var subject = new AppHostLifetime(host, _ => Assert.Fail("Show must not run."));

        var thrown = Assert.ThrowsExactly<AggregateException>(subject.Start);

        CollectionAssert.AreEqual(
            new Exception[] { primary, disposeFailure },
            thrown.InnerExceptions.ToArray());
        Assert.AreEqual(0, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Stop_failure_still_disposes_and_repeated_exit_does_not_retry_cleanup()
    {
        var stopFailure = new InvalidOperationException("stop failed");
        var host = new RecordingHost { StopFailure = stopFailure };
        var subject = new AppHostLifetime(host, _ => { });
        subject.Start();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Dispose);
        subject.Dispose();

        Assert.AreSame(stopFailure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Dispose_failure_is_reported_once_after_ownership_is_released()
    {
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost { DisposeFailure = disposeFailure };
        var subject = new AppHostLifetime(host, _ => { });
        subject.Start();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Dispose);
        subject.Dispose();

        Assert.AreSame(disposeFailure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Window_exit_defers_host_disposal_while_lifecycle_owns_cleanup()
    {
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new RecordingLease();
        var coordinator = new ApplicationLifecycleCoordinator(
            lease,
            new BlockingStopOperations(releaseCleanup.Task),
            TimeSpan.FromMilliseconds(80));
        Assert.AreEqual(
            LifecycleStartOutcome.Started,
            await coordinator.StartAsync(TestContext.CancellationToken));
        var host = new DeferredDisposalHost(coordinator, lease);
        var subject = new AppHostLifetime(
            host,
            _ => { },
            TimeSpan.FromMilliseconds(250));
        subject.Start();

        var exit = Task.Run(subject.Dispose);
        try
        {
            await exit.WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.AreEqual(0, host.DisposeCount);
            Assert.AreEqual(0, host.LeaseReleaseCount);
            var completionProperty = typeof(AppHostLifetime).GetProperty(
                "DeferredHostDisposalCompletion");
            Assert.IsNotNull(completionProperty);
            var completion = (Task)completionProperty.GetValue(subject)!;
            Assert.IsFalse(completion.IsCompleted);

            releaseCleanup.TrySetResult();
            await completion.WaitAsync(TestContext.CancellationToken);

            Assert.AreEqual(1, host.DisposeCount);
            Assert.AreEqual(1, host.LeaseReleaseCount);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await exit.WaitAsync(TestContext.CancellationToken);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class RecordingHost : IHost
    {
        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Exception? StartFailure { get; init; }

        public Exception? StopFailure { get; init; }

        public Exception? DisposeFailure { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartFailure is null
                ? Task.CompletedTask
                : Task.FromException(StartFailure);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingLease : ISingleInstanceLease
    {
        public int ReleaseCount { get; private set; }

        public bool TryAcquire() => true;

        public void Release() => ReleaseCount++;

        public void Dispose() => Release();
    }

    private sealed class BlockingStopOperations(Task release) :
        IApplicationLifecycleOperations
    {
        public Task ValidateSettingsAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PrepareDataRootAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RollbackDataRootAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StartProducersAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopProducersAsync(CancellationToken cancellationToken) =>
            release;

        public Task DrainWorkAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FinalizeStoresAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class DeferredDisposalHost : IHost
    {
        private readonly ApplicationLifecycleCoordinator _coordinator;
        private readonly RecordingLease _lease;

        public DeferredDisposalHost(
            ApplicationLifecycleCoordinator coordinator,
            RecordingLease lease)
        {
            _coordinator = coordinator;
            _lease = lease;
            Services = new SingleServiceProvider(coordinator);
        }

        public IServiceProvider Services { get; }

        public int DisposeCount { get; private set; }

        public int LeaseReleaseCount => _lease.ReleaseCount;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken = default) =>
            _ = await _coordinator.StopAsync();

        public void Dispose()
        {
            _coordinator.DeferredCleanupCompletion.GetAwaiter().GetResult();
            DisposeCount++;
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType.IsInstanceOfType(service) ? service : null;
    }
}
