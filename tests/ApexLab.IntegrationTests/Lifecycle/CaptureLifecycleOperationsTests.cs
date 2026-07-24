using ApexLab.App.Capture;
using ApexLab.Application.Capture;

namespace ApexLab.IntegrationTests.Lifecycle;

[TestClass]
public sealed class CaptureLifecycleOperationsTests
{
    [TestMethod]
    public async Task ShutdownStagesDelegateInProducerDrainFinalizeOrder()
    {
        var workflow = new RecordingCaptureWorkflow();
        var subject = new CaptureLifecycleOperations(workflow);

        await subject.ValidateSettingsAsync(TestContext.CancellationToken);
        await subject.PrepareDataRootAsync(TestContext.CancellationToken);
        await subject.StartProducersAsync(TestContext.CancellationToken);
        await subject.StopProducersAsync(TestContext.CancellationToken);
        await subject.DrainWorkAsync(TestContext.CancellationToken);
        await subject.FinalizeStoresAsync(TestContext.CancellationToken);
        await subject.RollbackDataRootAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[] { "stop", "drain", "finalize" },
            workflow.Events);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class RecordingCaptureWorkflow : ICaptureWorkflow
    {
        public List<string> Events { get; } = [];

        public CaptureWorkflowSnapshot Snapshot =>
            CaptureWorkflowSnapshot.Idle;

        public Task DeferredCleanupCompletion => Task.CompletedTask;

        public event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task<CaptureWorkflowSnapshot> ArmAsync(
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "Host startup must not arm capture.");

        public Task<CaptureWorkflowSnapshot> StopAsync(
            CaptureStopReason reason,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "Lifecycle phases must remain staged.");

        public Task StopProducersAsync(
            CancellationToken cancellationToken = default)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }

        public Task DrainWorkAsync(
            CancellationToken cancellationToken = default)
        {
            Events.Add("drain");
            return Task.CompletedTask;
        }

        public Task FinalizeStoresAsync(
            CancellationToken cancellationToken = default)
        {
            Events.Add("finalize");
            return Task.CompletedTask;
        }

        public void Reset()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
