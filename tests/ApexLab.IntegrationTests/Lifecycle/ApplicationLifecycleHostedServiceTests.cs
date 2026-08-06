using System.Diagnostics;
using ApexLab.App.Capture;
using ApexLab.App.Lifecycle;
using ApexLab.Application.Capture;

namespace ApexLab.IntegrationTests.Lifecycle;

[TestClass]
public sealed class ApplicationLifecycleHostedServiceTests
{
    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Bounded_host_stop_retains_lease_until_blocked_capture_stage_resolves()
    {
        var releaseStage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new RecordingCaptureWorkflow
        {
            StopProducers = _ => releaseStage.Task,
        };
        var lease = new RecordingLease();
        var coordinator = new ApplicationLifecycleCoordinator(
            lease,
            new CaptureLifecycleOperations(workflow),
            TimeSpan.FromMilliseconds(80));
        var subject = new ApplicationLifecycleHostedService(coordinator);
        await subject.StartAsync(TestContext.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await subject.StopAsync(TestContext.CancellationToken);
        stopwatch.Stop();

        Assert.IsLessThan(TimeSpan.FromMilliseconds(750), stopwatch.Elapsed);
        Assert.AreEqual(LifecycleStopOutcome.Interrupted, subject.LastStopResult?.Outcome);
        Assert.IsTrue(subject.LastStopResult?.LeaseRetainedForDeferredCleanup);
        Assert.AreEqual(0, lease.ReleaseCount);
        Assert.IsFalse(coordinator.DeferredCleanupCompletion.IsCompleted);

        releaseStage.TrySetResult();
        await coordinator.DeferredCleanupCompletion.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, lease.ReleaseCount);
        CollectionAssert.AreEqual(
            new[] { "begin:HostShutdown", "stop", "drain", "finalize" },
            workflow.Events);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Deferred_capture_failure_remains_observable_after_bounded_host_stop()
    {
        var releaseStage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("private detail must not enter UI text");
        var workflow = new RecordingCaptureWorkflow
        {
            FinalizeStores = async _ =>
            {
                await releaseStage.Task;
                throw cleanupFailure;
            },
        };
        var lease = new RecordingLease();
        var coordinator = new ApplicationLifecycleCoordinator(
            lease,
            new CaptureLifecycleOperations(workflow),
            TimeSpan.FromMilliseconds(80));
        var subject = new ApplicationLifecycleHostedService(coordinator);
        await subject.StartAsync(TestContext.CancellationToken);

        await subject.StopAsync(TestContext.CancellationToken);

        Assert.AreEqual(LifecycleStopOutcome.Interrupted, subject.LastStopResult?.Outcome);
        Assert.AreEqual(0, lease.ReleaseCount);
        releaseStage.TrySetResult();
        await coordinator.DeferredCleanupCompletion.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, lease.ReleaseCount);
        Assert.HasCount(1, coordinator.LateFailures);
        Assert.AreSame(cleanupFailure, coordinator.LateFailures[0]);
    }

    [TestMethod]
    public async Task Immediate_capture_failure_is_preserved_by_host_stop()
    {
        var failure = new IOException("disk failed");
        var coordinator = new ApplicationLifecycleCoordinator(
            new RecordingLease(),
            new CaptureLifecycleOperations(
                new RecordingCaptureWorkflow
                {
                    FinalizeStores = _ => Task.FromException(failure),
                }),
            TimeSpan.FromSeconds(1));
        var subject = new ApplicationLifecycleHostedService(coordinator);
        await subject.StartAsync(TestContext.CancellationToken);

        var thrown = await Assert.ThrowsExactlyAsync<IOException>(
            () => subject.StopAsync(TestContext.CancellationToken));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(LifecycleStopOutcome.Failed, subject.LastStopResult?.Outcome);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class RecordingLease : ISingleInstanceLease
    {
        public int ReleaseCount { get; private set; }

        public bool TryAcquire() => true;

        public void Release() => ReleaseCount++;

        public void Dispose() => Release();
    }

    private sealed class RecordingCaptureWorkflow : ICaptureWorkflow
    {
        public List<string> Events { get; } = [];

        public Func<CancellationToken, Task>? StopProducers { get; init; }

        public Func<CancellationToken, Task>? DrainWork { get; init; }

        public Func<CancellationToken, Task>? FinalizeStores { get; init; }

        public CaptureWorkflowSnapshot Snapshot => CaptureWorkflowSnapshot.Idle;

        public Task DeferredCleanupCompletion => Task.CompletedTask;

        public event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task<CaptureWorkflowSnapshot> ArmAsync(
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Host startup must not arm capture.");

        public Task<CaptureWorkflowSnapshot> StopAsync(
            CaptureStopReason reason,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Staged host shutdown must not run the monolithic stop path.");

        public void BeginStop(CaptureStopReason reason) =>
            Events.Add($"begin:{reason}");

        public Task StopProducersAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("stop");
            return StopProducers?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task DrainWorkAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("drain");
            return DrainWork?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task FinalizeStoresAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("finalize");
            return FinalizeStores?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void Reset()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
