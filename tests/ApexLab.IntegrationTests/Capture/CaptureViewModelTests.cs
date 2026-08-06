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

    [TestMethod]
    public void StopRemainsAvailableWhileArmIsStillBinding()
    {
        var workflow = new StubWorkflow
        {
            ArmCompletion = new TaskCompletionSource<CaptureWorkflowSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var subject = new CaptureViewModel(workflow);

        subject.ArmCommand.Execute(null);
        workflow.Publish(
            CaptureWorkflowSnapshot.Idle with
            {
                State = CaptureState.Binding,
                CaptureId = RawEvidenceCaptureId.Parse(
                    "00112233445546778899aabbccddeeff"),
            });

        Assert.IsTrue(subject.StopCommand.CanExecute(null));
    }

    [TestMethod]
    public void ProjectsEveryAggregateDiagnosticWithoutHidingHistory()
    {
        var workflow = new StubWorkflow();
        using var subject = new CaptureViewModel(workflow);
        var source = new DatagramSourceCounters(
            datagramsObserved: 56,
            sourceEnqueued: 37,
            sourceDroppedFull: 9,
            sourceRejectedOversized: 10,
            socketErrors: 11);
        var classifier = new DatagramClassificationCounters(
            sourceDequeued: 37,
            compatible: 1,
            malformedHeader: 1,
            unsupportedFormat: 2,
            unsupportedYear: 3,
            unknownPacketId: 4,
            unsupportedPacketVersion: 5,
            invalidPacketLength: 6,
            excludedPrivacyPacket: 7,
            unexpectedSender: 8,
            classifierAbandonedOnTermination: 0);
        var evidence = new EvidenceSinkCounters(
            sinkWritten: 1,
            sinkWriteFailed: 0,
            sinkPending: 0,
            sinkPendingDeferredCleanup: 0,
            finalizedRecords: 0,
            stagedRecords: 1);

        workflow.Publish(
            CaptureWorkflowSnapshot.Idle with
            {
                State = CaptureState.ReceivingCompatibleTraffic,
                CaptureId = CaptureId(),
                Counters = new CaptureCounters(source, classifier, evidence),
            });

        Assert.AreEqual("56", subject.ReceivedText);
        Assert.AreEqual("1", subject.CompatibleText);
        Assert.AreEqual("0", subject.ActivePendingText);
        Assert.AreEqual("0", subject.DeferredPendingText);
        StringAssert.Contains(subject.DiagnosticText, "Malformed header: 1");
        StringAssert.Contains(subject.DiagnosticText, "Wrong packet format: 2");
        StringAssert.Contains(subject.DiagnosticText, "Wrong game year: 3");
        StringAssert.Contains(subject.DiagnosticText, "Unknown packet ID: 4");
        StringAssert.Contains(subject.DiagnosticText, "Unsupported packet version: 5");
        StringAssert.Contains(subject.DiagnosticText, "Invalid packet length: 6");
        StringAssert.Contains(subject.DiagnosticText, "Privacy-excluded packets: 7");
        StringAssert.Contains(subject.DiagnosticText, "Unexpected same-PC sender: 8");
        StringAssert.Contains(subject.DiagnosticText, "Source queue drops: 9");
        StringAssert.Contains(subject.DiagnosticText, "Oversized datagrams: 10");
        StringAssert.Contains(subject.DiagnosticText, "Socket errors: 11");
        StringAssert.Contains(subject.NextStepText, "Select F1 25 UDP mode");
        StringAssert.Contains(subject.NextStepText, "base-v3 source");
        StringAssert.Contains(subject.NextStepText, "intentionally not retained");
        StringAssert.Contains(subject.NextStepText, "Stop other same-PC telemetry senders");
        StringAssert.Contains(subject.NextStepText, "reduce the UDP send rate");
        StringAssert.Contains(subject.DurabilityText, "staged, not final evidence");
    }

    [TestMethod]
    public void SeparatesActiveAndDeferredPendingAndProjectsProvisionalResolution()
    {
        var workflow = new StubWorkflow();
        using var subject = new CaptureViewModel(workflow);

        workflow.Publish(SnapshotWithEvidence(
            CaptureState.ReceivingCompatibleTraffic,
            new EvidenceSinkCounters(
                sinkWritten: 0,
                sinkWriteFailed: 0,
                sinkPending: 2,
                sinkPendingDeferredCleanup: 0,
                finalizedRecords: 0,
                stagedRecords: 0)));

        Assert.AreEqual("2", subject.ActivePendingText);
        Assert.AreEqual("0", subject.DeferredPendingText);
        Assert.IsFalse(subject.IsProvisional);

        workflow.Publish(SnapshotWithEvidence(
            CaptureState.Interrupted,
            new EvidenceSinkCounters(
                sinkWritten: 0,
                sinkWriteFailed: 0,
                sinkPending: 0,
                sinkPendingDeferredCleanup: 2,
                finalizedRecords: 0,
                stagedRecords: 0)) with
            {
                StopReason = CaptureStopReason.User,
                FailureKind = CaptureFailureKind.Interrupted,
            });

        Assert.AreEqual("0", subject.ActivePendingText);
        Assert.AreEqual("2", subject.DeferredPendingText);
        Assert.IsTrue(subject.IsProvisional);
        StringAssert.Contains(subject.ProvisionalText, "counts may still change");
        StringAssert.Contains(subject.DurabilityText, "Deferred writes");

        workflow.Publish(SnapshotWithEvidence(
            CaptureState.Stopped,
            new EvidenceSinkCounters(
                sinkWritten: 2,
                sinkWriteFailed: 0,
                sinkPending: 0,
                sinkPendingDeferredCleanup: 0,
                finalizedRecords: 2,
                stagedRecords: 0)) with
            {
                Completion = Completion(recordCount: 2),
            });

        Assert.AreEqual("0", subject.DeferredPendingText);
        Assert.IsFalse(subject.IsProvisional);
        StringAssert.Contains(subject.DurabilityText, "completion manifest was committed");
        StringAssert.Contains(subject.DurabilityText, "directory-entry crash durability is not claimed");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResetIsEnabledOnlyAfterFaultOwnershipResolves(
        bool cleanupFaults)
    {
        var deferred = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new StubWorkflow { Deferred = deferred };
        using var subject = new CaptureViewModel(workflow);
        var commandChanged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.ResetCommand.CanExecuteChanged += (_, _) =>
        {
            if (subject.ResetCommand.CanExecute(null))
            {
                commandChanged.TrySetResult();
            }
        };
        workflow.Publish(
            CaptureWorkflowSnapshot.Idle with
            {
                State = CaptureState.Faulted,
                FailureKind = CaptureFailureKind.EvidenceWrite,
                Failure = new IOException("private failure"),
            });

        Assert.IsFalse(subject.ResetCommand.CanExecute(null));
        if (cleanupFaults)
        {
            deferred.TrySetException(new IOException("cleanup detail"));
        }
        else
        {
            deferred.TrySetResult();
        }

        await commandChanged.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsTrue(subject.ResetCommand.CanExecute(null));

        subject.ResetCommand.Execute(null);

        Assert.AreEqual(1, workflow.ResetCount);
        workflow.Publish(CaptureWorkflowSnapshot.Idle with
        {
            State = CaptureState.Interrupted,
            FailureKind = CaptureFailureKind.Interrupted,
        });
        Assert.IsFalse(subject.ResetCommand.CanExecute(null));
        workflow.Publish(CaptureWorkflowSnapshot.Idle with
        {
            State = CaptureState.Stopped,
        });
        Assert.IsFalse(subject.ResetCommand.CanExecute(null));
    }

    public TestContext TestContext { get; set; } = null!;

    private static CaptureWorkflowSnapshot SnapshotWithEvidence(
        CaptureState state,
        EvidenceSinkCounters evidence)
    {
        var compatible = evidence.AccountedCompatible;
        return CaptureWorkflowSnapshot.Idle with
        {
            State = state,
            CaptureId = CaptureId(),
            Counters = new CaptureCounters(
                new DatagramSourceCounters(
                    compatible,
                    compatible,
                    sourceDroppedFull: 0,
                    sourceRejectedOversized: 0,
                    socketErrors: 0),
                new DatagramClassificationCounters(
                    compatible,
                    compatible,
                    malformedHeader: 0,
                    unsupportedFormat: 0,
                    unsupportedYear: 0,
                    unknownPacketId: 0,
                    unsupportedPacketVersion: 0,
                    invalidPacketLength: 0,
                    excludedPrivacyPacket: 0,
                    unexpectedSender: 0,
                    classifierAbandonedOnTermination: 0),
                evidence),
        };
    }

    private static RawEvidenceCaptureId CaptureId() =>
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");

    private static RawEvidenceCompletion Completion(long recordCount) =>
        new(
            CaptureId(),
            RawEvidenceProtocolId.Parse("f1-25-v3"),
            recordCount,
            RawEvidenceLimits.MinimumFileBytes,
            new string('a', 64),
            DateTimeOffset.UnixEpoch);

    private sealed class StubWorkflow : ICaptureWorkflow
    {
        public CaptureWorkflowSnapshot Snapshot { get; private set; } =
            CaptureWorkflowSnapshot.Idle;

        public TaskCompletionSource<CaptureWorkflowSnapshot>? ArmCompletion
        {
            get;
            init;
        }

        public TaskCompletionSource? Deferred { get; init; }

        public int ResetCount { get; private set; }

        public Task DeferredCleanupCompletion =>
            Deferred?.Task ?? Task.CompletedTask;

        public event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged;

        public void Publish(CaptureWorkflowSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public Task<CaptureWorkflowSnapshot> ArmAsync(
            CancellationToken cancellationToken = default) =>
            ArmCompletion?.Task ?? Task.FromResult(Snapshot);

        public Task<CaptureWorkflowSnapshot> StopAsync(
            CaptureStopReason reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public void BeginStop(CaptureStopReason reason)
        {
        }

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
            ResetCount++;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
