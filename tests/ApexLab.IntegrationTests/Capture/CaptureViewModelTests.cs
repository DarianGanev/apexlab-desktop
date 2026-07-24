using ApexLab.App.Capture;
using ApexLab.Application.Capture;

namespace ApexLab.IntegrationTests.Capture;

[TestClass]
public sealed class CaptureViewModelTests
{
    [TestMethod]
    public void ReflectsWorkflowStateAndAggregateLedger()
    {
        var workflow = new StubWorkflow();
        using var subject = new CaptureViewModel(workflow);

        Assert.AreEqual("Ready to arm", subject.StatusText);
        Assert.IsTrue(subject.ArmCommand.CanExecute(null));
        Assert.IsFalse(subject.StopCommand.CanExecute(null));

        workflow.Publish(
            CaptureWorkflowSnapshot.Idle with
            {
                State = CaptureState.WaitingForTraffic,
                CaptureId = RawEvidenceCaptureId.Parse(
                    "00112233445546778899aabbccddeeff"),
            });

        Assert.AreEqual(
            "Waiting for F1 25 telemetry",
            subject.StatusText);
        Assert.IsFalse(subject.ArmCommand.CanExecute(null));
        Assert.IsTrue(subject.StopCommand.CanExecute(null));
        Assert.AreEqual("0", subject.ReceivedText);
        Assert.AreEqual("0", subject.FinalizedText);
    }

    private sealed class StubWorkflow : ICaptureWorkflow
    {
        public CaptureWorkflowSnapshot Snapshot { get; private set; } =
            CaptureWorkflowSnapshot.Idle;

        public Task DeferredCleanupCompletion => Task.CompletedTask;

        public event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged;

        public void Publish(CaptureWorkflowSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public Task<CaptureWorkflowSnapshot> ArmAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<CaptureWorkflowSnapshot> StopAsync(
            CaptureStopReason reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task StopProducersAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DrainWorkAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FinalizeStoresAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Reset()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
