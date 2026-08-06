using System.Net;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class CaptureWorkflowTests
{
    [TestMethod]
    public async Task ConcurrentArmCallersShareOneBindingOperation()
    {
        var factory = new ControlledSessionFactory();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));

        var first = subject.ArmAsync(TestContext.CancellationToken);
        var second = subject.ArmAsync(TestContext.CancellationToken);

        await factory.CreationStarted.WaitAsync(
            TestContext.CancellationToken);
        Assert.AreSame(first, second);
        Assert.AreEqual(CaptureState.Binding, subject.Snapshot.State);
        Assert.IsNotNull(subject.Snapshot.CaptureId);
        Assert.AreEqual(1, factory.CreateCalls);

        factory.CompleteCreation();
        var armed = await first.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            CaptureState.WaitingForTraffic,
            armed.State);
        Assert.AreEqual(armed, subject.Snapshot);
    }

    [TestMethod]
    public async Task StopWhileBindingCancelsCreationAndSettlesStopped()
    {
        var factory = new ControlledSessionFactory();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var armTask = subject.ArmAsync(TestContext.CancellationToken);
        await factory.CreationStarted.WaitAsync(
            TestContext.CancellationToken);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            timeout.Token);

        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => armTask);
        Assert.AreEqual(0, factory.Source.DisposeCalls);
        Assert.AreEqual(0, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task StopFromBindingNotificationCannotRunBeforeArmOwnershipIsPublished()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        CaptureWorkflowSnapshot? stopped = null;
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Binding)
            {
                stopped = subject.StopAsync(
                        CaptureStopReason.User,
                        TestContext.CancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
        };

        var arm = subject.ArmAsync(TestContext.CancellationToken);

        Assert.IsNotNull(stopped);
        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        await Assert.ThrowsAsync<OperationCanceledException>(() => arm);
        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(CaptureStopReason.User, subject.Snapshot.StopReason);
        Assert.IsNull(subject.Snapshot.Failure);
    }

    [TestMethod]
    public async Task StopDuringCancellationInsensitiveBindingRetainsOwnershipUntilCreationResolves()
    {
        var factory = new ControlledSessionFactory
        {
            IgnoreCreationCancellation = true,
        };
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        var arm = subject.ArmAsync(TestContext.CancellationToken);
        Task<CaptureWorkflowSnapshot>? prematureRearm = null;
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Stopped
                && !subject.DeferredCleanupCompletion.IsCompleted)
            {
                prematureRearm ??= subject.ArmAsync(
                    TestContext.CancellationToken);
            }
        };
        await factory.CreationStarted.WaitAsync(
            TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        try
        {
            var interrupted = await stop.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.CancellationToken);

            Assert.AreEqual(CaptureState.Interrupted, interrupted.State);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
            Assert.AreEqual(0, factory.Source.DisposeCalls);
            Assert.AreEqual(0, factory.Evidence.DisposeCalls);
        }
        finally
        {
            factory.CompleteCreation();
        }

        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => arm);
        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
        Assert.IsNotNull(prematureRearm);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => prematureRearm!);
    }

    [TestMethod]
    public async Task PipelineCompletionAtInterruptionBoundaryCannotRegressTerminalState()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockFinalizationUntilReleased();
        var stopped = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new CaptureWorkflowTestHooks
        {
            BeforeInterruptedClaim = () =>
            {
                factory.Evidence.ReleaseFinalization();
                stopped.Task.Wait(TimeSpan.FromSeconds(2));
            },
        };
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50),
            hooks);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Stopped)
            {
                stopped.TrySetResult(snapshot);
            }
        };
        await subject.ArmAsync(TestContext.CancellationToken);

        var result = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, result.State);
        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.IsTrue(subject.DeferredCleanupCompletion.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task PipelineFaultAtInterruptionBoundaryStillResolvesAndReleasesOwnership()
    {
        var failure = new IOException("synthetic boundary stop failure");
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Source.StopFailure = failure;
        factory.Source.BlockStopUntilReleased();
        var hooks = new CaptureWorkflowTestHooks
        {
            InterruptedClaimStarting = pipeline =>
            {
                factory.Source.ReleaseStop();
                try
                {
                    pipeline.GetAwaiter().GetResult();
                }
                catch (IOException exception)
                    when (ReferenceEquals(exception, failure))
                {
                    // The fault must already exist when interruption is claimed.
                }
            },
        };
        var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50),
            hooks);
        try
        {
            await subject.ArmAsync(TestContext.CancellationToken);

            var result = await subject.StopAsync(
                CaptureStopReason.User,
                TestContext.CancellationToken);

            Assert.AreEqual(CaptureState.Interrupted, result.State);
            var thrown = await Assert.ThrowsExactlyAsync<IOException>(
                () => subject.DeferredCleanupCompletion);
            Assert.AreSame(failure, thrown);
            Assert.AreEqual(CaptureState.Faulted, subject.Snapshot.State);
            Assert.AreSame(failure, subject.Snapshot.Failure);
            Assert.AreEqual(1, factory.Source.DisposeCalls);
            Assert.AreEqual(1, factory.Evidence.DisposeCalls);
        }
        finally
        {
            try
            {
                await subject.DisposeAsync();
            }
            catch (IOException exception)
                when (ReferenceEquals(exception, failure))
            {
                // Every disposer observes the same terminal cleanup failure.
            }
        }
    }

    [TestMethod]
    public async Task CleanStopDrainsFinalizesAndDisposesTheSession()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        Assert.AreEqual(
            CaptureStopReason.User,
            stopped.StopReason);
        Assert.IsNotNull(stopped.Completion);
        Assert.AreEqual(1, factory.Source.StopCalls);
        Assert.AreEqual(1, factory.Evidence.FinalizeCalls);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
        Assert.IsTrue(subject.DeferredCleanupCompletion.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task TimedOutWriteRetainsOwnershipUntilDeferredCleanupFinishes()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Evidence.WriteStarted.WaitAsync(
            TestContext.CancellationToken);

        var interrupted = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Interrupted, interrupted.State);
        Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
        Assert.AreEqual(0, factory.Source.DisposeCalls);
        Assert.AreEqual(0, factory.Evidence.DisposeCalls);

        factory.Evidence.ReleaseWrites();
        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(1L, subject.Snapshot.Counters.Evidence.FinalizedRecords);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task StopDeadlineBoundsCancellationInsensitiveProducerStop()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Source.BlockStopUntilReleased();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        try
        {
            await factory.Source.StopStarted.WaitAsync(
                TestContext.CancellationToken);
            var interrupted = await stop.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.CancellationToken);

            Assert.AreEqual(CaptureState.Interrupted, interrupted.State);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
            Assert.AreEqual(0, factory.Source.DisposeCalls);
            Assert.AreEqual(0, factory.Evidence.DisposeCalls);
        }
        finally
        {
            factory.Source.ReleaseStop();
        }

        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);
        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task StopDeadlineBoundsCancellationInsensitiveFinalization()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockFinalizationUntilReleased();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        try
        {
            await factory.Evidence.FinalizationStarted.WaitAsync(
                TestContext.CancellationToken);
            var interrupted = await stop.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.CancellationToken);

            Assert.AreEqual(CaptureState.Interrupted, interrupted.State);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
            Assert.AreEqual(0, factory.Source.DisposeCalls);
            Assert.AreEqual(0, factory.Evidence.DisposeCalls);
        }
        finally
        {
            factory.Evidence.ReleaseFinalization();
        }

        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);
        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task StopAndUnexpectedFailureShareExactlyOneSessionRelease()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.FailWrites = true;
        factory.Source.BlockDisposalUntilReleased();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Source.DisposeStarted.WaitAsync(
            TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        var secondReleaseStarted = false;
        try
        {
            await factory.Source.SecondDisposeStarted.WaitAsync(
                TimeSpan.FromMilliseconds(250),
                TestContext.CancellationToken);
            secondReleaseStarted = true;
        }
        catch (TimeoutException)
        {
            // One release operation remains blocked at the first disposal.
        }
        finally
        {
            factory.Source.ReleaseDisposal();
        }

        await stop.WaitAsync(TestContext.CancellationToken);
        Assert.IsFalse(secondReleaseStarted);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task ConcurrentDisposeCallersShareCompleteDeferredCleanup()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Evidence.WriteStarted.WaitAsync(
            TestContext.CancellationToken);
        var interrupted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Interrupted)
            {
                interrupted.TrySetResult();
            }
        };

        var first = subject.DisposeAsync().AsTask();
        await interrupted.Task.WaitAsync(TestContext.CancellationToken);
        var second = subject.DisposeAsync().AsTask();
        try
        {
            Assert.AreSame(first, second);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            factory.Evidence.ReleaseWrites();
        }

        await Task.WhenAll(first, second).WaitAsync(
            TestContext.CancellationToken);
        Assert.AreEqual(CaptureState.Disposed, subject.Snapshot.State);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task DeferredCleanupFailureFaultsCompletionAndPreservesFaultedSnapshot()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        factory.Evidence.FinalizeFailure =
            new IOException("synthetic deferred finalization failure");
        var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        try
        {
            await subject.ArmAsync(TestContext.CancellationToken);
            factory.Source.Publish(marker: 1);
            await factory.Evidence.WriteStarted.WaitAsync(
                TestContext.CancellationToken);
            var interrupted = await subject.StopAsync(
                CaptureStopReason.User,
                TestContext.CancellationToken);
            Assert.AreEqual(CaptureState.Interrupted, interrupted.State);

            factory.Evidence.ReleaseWrites();
            var thrown = await Assert.ThrowsExactlyAsync<IOException>(
                () => subject.DeferredCleanupCompletion);

            Assert.AreSame(factory.Evidence.FinalizeFailure, thrown);
            Assert.AreEqual(CaptureState.Faulted, subject.Snapshot.State);
            Assert.AreSame(
                factory.Evidence.FinalizeFailure,
                subject.Snapshot.Failure);
        }
        finally
        {
            factory.Evidence.ReleaseWrites();
            try
            {
                await subject.DisposeAsync();
            }
            catch (IOException exception)
                when (ReferenceEquals(
                    exception,
                    factory.Evidence.FinalizeFailure))
            {
                // Every disposer observes the same terminal cleanup failure.
            }
        }
    }

    [TestMethod]
    public async Task ThrowingSnapshotSubscriberCannotFaultIngestionOrStrandEvidence()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);
        EventHandler<CaptureWorkflowSnapshot> throwing = (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.ReceivingCompatibleTraffic)
            {
                throw new InvalidOperationException(
                    "synthetic subscriber failure");
            }
        };
        subject.SnapshotChanged += throwing;
        try
        {
            factory.Source.Publish(marker: 1);
            await factory.Evidence.WriteStarted.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.CancellationToken);
        }
        finally
        {
            subject.SnapshotChanged -= throwing;
        }

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        Assert.AreEqual(1L, stopped.Counters.Evidence.FinalizedRecords);
        Assert.AreEqual(0L, stopped.Counters.Evidence.SinkPending);
        Assert.AreEqual(
            0L,
            stopped.Counters.Evidence.SinkPendingDeferredCleanup);
    }

    [TestMethod]
    [DataRow(RawEvidenceLimitKind.FileSize)]
    [DataRow(RawEvidenceLimitKind.FreeSpace)]
    public async Task EvidenceLimitFinalizesSalvageAndPublishesDistinctStop(
        RawEvidenceLimitKind kind)
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var terminal = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Stopped
                && snapshot.StopReason == CaptureStopReason.LimitReached)
            {
                terminal.TrySetResult(snapshot);
            }
        };
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Evidence.FirstWriteCompleted.WaitAsync(
            TestContext.CancellationToken);
        var limit = new RawEvidenceLimitReachedException(kind);
        factory.Evidence.WriteFailure = limit;

        factory.Source.Publish(marker: 1);
        var stopped = await terminal.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        Assert.AreEqual(
            CaptureStopReason.LimitReached,
            stopped.StopReason);
        Assert.AreEqual(
            CaptureFailureKind.EvidenceLimit,
            stopped.FailureKind);
        Assert.AreSame(limit, stopped.Failure);
        Assert.IsNotNull(stopped.Completion);
        Assert.AreEqual(1L, stopped.Completion.RecordCount);
        Assert.AreEqual(2L, stopped.Counters.Classifier.Compatible);
        Assert.AreEqual(1L, stopped.Counters.Evidence.SinkWritten);
        Assert.AreEqual(1L, stopped.Counters.Evidence.SinkWriteFailed);
        Assert.AreEqual(1L, stopped.Counters.Evidence.FinalizedRecords);
        Assert.AreEqual(0L, stopped.Counters.Evidence.StagedRecords);
        Assert.AreEqual(1, factory.Evidence.FinalizeCalls);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task DurationLimitStopsAndFinalizesWithoutWaitingForAnotherPacket()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.Limits = new RawEvidenceLimits(
            maximumDuration: TimeSpan.FromMilliseconds(50),
            minimumFreeSpaceBytes: 0);
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var terminal = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Stopped
                && snapshot.StopReason == CaptureStopReason.LimitReached)
            {
                terminal.TrySetResult(snapshot);
            }
        };

        await subject.ArmAsync(TestContext.CancellationToken);
        var stopped = await terminal.Task.WaitAsync(
            TimeSpan.FromMilliseconds(750),
            TestContext.CancellationToken);

        Assert.AreEqual(
            CaptureFailureKind.EvidenceLimit,
            stopped.FailureKind);
        var limit = Assert.IsInstanceOfType<RawEvidenceLimitReachedException>(
            stopped.Failure);
        Assert.AreEqual(RawEvidenceLimitKind.Duration, limit.Kind);
        Assert.AreEqual(0L, stopped.Counters.Classifier.Compatible);
        Assert.AreEqual(0L, stopped.Completion?.RecordCount);
        Assert.AreEqual(1, factory.Evidence.FinalizeCalls);
    }

    [TestMethod]
    public async Task DurationLimitCannotReplaceAnExistingUserStop()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.Limits = new RawEvidenceLimits(
            maximumDuration: TimeSpan.FromMilliseconds(75),
            minimumFreeSpaceBytes: 0);
        factory.Evidence.BlockFinalizationUntilReleased();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        await factory.Evidence.FinalizationStarted.WaitAsync(
            TestContext.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(150),
            TestContext.CancellationToken);
        try
        {
            Assert.AreEqual(
                CaptureStopReason.User,
                subject.Snapshot.StopReason);
            Assert.IsNull(subject.Snapshot.EvidenceLimitKind);
        }
        finally
        {
            factory.Evidence.ReleaseFinalization();
        }

        var stopped = await stop.WaitAsync(TestContext.CancellationToken);
        Assert.AreEqual(CaptureStopReason.User, stopped.StopReason);
    }

    [TestMethod]
    public async Task CreationTimeEvidenceLimitIsARecoverableLimitStop()
    {
        var limit = new RawEvidenceLimitReachedException(
            RawEvidenceLimitKind.FreeSpace);
        var factory = new ControlledSessionFactory
        {
            CreationFailure = limit,
        };
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));

        var result = await subject.ArmAsync(TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, result.State);
        Assert.AreEqual(CaptureStopReason.LimitReached, result.StopReason);
        Assert.AreEqual(CaptureFailureKind.EvidenceLimit, result.FailureKind);
        Assert.AreEqual(RawEvidenceLimitKind.FreeSpace, result.EvidenceLimitKind);
        Assert.AreSame(limit, result.Failure);
        Assert.IsTrue(result.CanArm);
    }

    [TestMethod]
    public async Task InterruptedLimitStopRetainsTypedLimitThroughDeferredFinalization()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockFinalizationUntilReleased();
        var limit = new RawEvidenceLimitReachedException(
            RawEvidenceLimitKind.FileSize);
        factory.Evidence.WriteFailure = limit;
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        var interrupted = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Interrupted)
            {
                interrupted.TrySetResult(snapshot);
            }
        };
        await subject.ArmAsync(TestContext.CancellationToken);

        factory.Source.Publish(marker: 1);
        var provisional = await interrupted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureStopReason.LimitReached, provisional.StopReason);
        Assert.AreEqual(CaptureFailureKind.Interrupted, provisional.FailureKind);
        Assert.AreEqual(
            RawEvidenceLimitKind.FileSize,
            provisional.EvidenceLimitKind);

        factory.Evidence.ReleaseFinalization();
        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(
            RawEvidenceLimitKind.FileSize,
            subject.Snapshot.EvidenceLimitKind);
        Assert.IsNotNull(subject.Snapshot.Completion);
    }

    [TestMethod]
    public async Task PacketBurstPublishesAggregateSnapshotsAtBoundedRate()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);
        var publications = 0;
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.IncompatibleTraffic
                && snapshot.Counters.Classifier.MalformedHeader != 0)
            {
                Interlocked.Increment(ref publications);
            }
        };

        for (var index = 0; index < 100; index++)
        {
            factory.Source.Publish(marker: 0);
        }

        await WaitUntilAsync(
            () => subject.Snapshot.Counters.Classifier.MalformedHeader == 100,
            TimeSpan.FromSeconds(2));
        await Task.Delay(
            TimeSpan.FromMilliseconds(300),
            TestContext.CancellationToken);

        var observedPublications = Volatile.Read(ref publications);
        Assert.IsTrue(
            observedPublications is >= 1 and <= 4,
            $"Expected 1-4 coalesced publications, but observed {observedPublications}.");
        await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task CompletedEvidenceWritePublishesWithoutAnotherPacket()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);
        var written = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Counters.Evidence.SinkWritten == 1)
            {
                written.TrySetResult(snapshot);
            }
        };

        factory.Source.Publish(marker: 1);
        await factory.Evidence.WriteStarted.WaitAsync(
            TestContext.CancellationToken);
        factory.Evidence.ReleaseWrites();
        var observed = await written.Task.WaitAsync(
            TimeSpan.FromMilliseconds(750),
            TestContext.CancellationToken);

        Assert.AreEqual(0L, observed.Counters.Evidence.SinkPending);
        Assert.AreEqual(1L, observed.Counters.Evidence.StagedRecords);
        await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ObservationValidatedBeforeStopCannotOverwriteStoppingState()
    {
        using var observationValidated = new ManualResetEventSlim(false);
        using var allowObservation = new ManualResetEventSlim(false);
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Source.BlockStopUntilReleased();
        var hooks = new CaptureWorkflowTestHooks
        {
            ObservationValidated = () =>
            {
                observationValidated.Set();
                Assert.IsTrue(
                    allowObservation.Wait(TimeSpan.FromSeconds(5)));
            },
        };
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2),
            hooks);
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        Assert.IsTrue(
            observationValidated.Wait(TimeSpan.FromSeconds(5)));

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        await factory.Source.StopStarted.WaitAsync(
            TestContext.CancellationToken);
        Assert.AreEqual(CaptureState.Stopping, subject.Snapshot.State);
        allowObservation.Set();
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                TestContext.CancellationToken);
            Assert.AreEqual(CaptureState.Stopping, subject.Snapshot.State);
        }
        finally
        {
            factory.Source.ReleaseStop();
        }

        await stop.WaitAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WriteFailureFaultsAndReleasesUnfinalizedOwnership()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.FailWrites = true;
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var faulted = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Faulted)
            {
                faulted.TrySetResult(snapshot);
            }
        };
        await subject.ArmAsync(TestContext.CancellationToken);

        factory.Source.Publish(marker: 1);
        var snapshot = await faulted.Task.WaitAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(
            CaptureFailureKind.EvidenceWrite,
            snapshot.FailureKind);
        Assert.IsInstanceOfType<IOException>(snapshot.Failure);
        Assert.IsNull(snapshot.Completion);
        Assert.AreEqual(0, factory.Evidence.FinalizeCalls);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task SourceStopFailureStillReleasesEveryOwnedResource()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Source.StopFailure =
            new IOException("synthetic stop failure");
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Faulted, stopped.State);
        Assert.AreSame(factory.Source.StopFailure, stopped.Failure);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task FinalizeFailureStillReleasesEveryOwnedResource()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.FinalizeFailure =
            new IOException("synthetic finalize failure");
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Faulted, stopped.State);
        Assert.AreSame(factory.Evidence.FinalizeFailure, stopped.Failure);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task WriteFailureDuringStopStillReleasesEveryOwnedResource()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        factory.Evidence.FailWrites = true;
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Evidence.WriteStarted.WaitAsync(
            TestContext.CancellationToken);

        var stop = subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);
        factory.Evidence.ReleaseWrites();
        var stopped = await stop.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Faulted, stopped.State);
        Assert.IsInstanceOfType<IOException>(stopped.Failure);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task CancelledBindingKeepsEvidenceOwnedUntilIngestionStops()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Source.BlockStartUntilReleased();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var arm = subject.ArmAsync(cancellation.Token);
        await factory.Source.StartEntered.WaitAsync(
            TestContext.CancellationToken);

        cancellation.Cancel();
        await factory.Source.StartCancellationObserved.WaitAsync(
            TestContext.CancellationToken);
        try
        {
            Assert.AreEqual(0, factory.Evidence.DisposeCalls);
        }
        finally
        {
            factory.Source.ReleaseStart();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => arm);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
        Assert.IsFalse(factory.Source.DisposedWhileStartActive);
        Assert.IsFalse(factory.Evidence.DisposedWhileSourceStartActive);
    }

    public TestContext TestContext { get; set; } = null!;

    private async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The capture condition did not become true before its deadline.");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.CancellationToken);
        }
    }

    private sealed class ControlledSessionFactory : ICaptureSessionFactory
    {
        private readonly TaskCompletionSource _creation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _creationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledSessionFactory()
        {
            Evidence.IsSourceStartActive = () => Source.IsStartActive;
        }

        public ControlledDatagramSource Source { get; } = new();

        public RecordingEvidenceStore Evidence { get; } = new();

        public int CreateCalls { get; private set; }

        public bool IgnoreCreationCancellation { get; init; }

        public Exception? CreationFailure { get; init; }

        public Task CreationStarted => _creationStarted.Task;

        public async Task<CaptureSessionComponents> CreateAsync(
            RawEvidenceCaptureId captureId,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            _creationStarted.TrySetResult();
            if (IgnoreCreationCancellation)
            {
                await _creation.Task;
            }
            else
            {
                await _creation.Task.WaitAsync(cancellationToken);
            }
            if (CreationFailure is not null)
            {
                throw CreationFailure;
            }

            Evidence.SetCaptureId(captureId);
            return new CaptureSessionComponents(
                Source,
                new CompatibleProtocolAdapter(),
                SenderPolicy.LoopbackOnly,
                Evidence);
        }

        public void CompleteCreation() => _creation.TrySetResult();
    }

    private sealed class ControlledDatagramSource : IDatagramSource
    {
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();
        private long _enqueued;
        private TaskCompletionSource? _startEntered;
        private TaskCompletionSource? _startCancellationObserved;
        private TaskCompletionSource? _startRelease;
        private TaskCompletionSource? _stopStarted;
        private TaskCompletionSource? _stopRelease;
        private TaskCompletionSource? _disposeStarted;
        private TaskCompletionSource? _secondDisposeStarted;
        private TaskCompletionSource? _disposeRelease;
        private int _startActive;

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters
        {
            get
            {
                var enqueued = Interlocked.Read(ref _enqueued);
                return new DatagramSourceCounters(
                    enqueued,
                    enqueued,
                    sourceDroppedFull: 0,
                    sourceRejectedOversized: 0,
                    socketErrors: 0);
            }
        }

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Exception? StopFailure { get; set; }

        public Task StartEntered =>
            _startEntered?.Task ?? Task.CompletedTask;

        public Task StartCancellationObserved =>
            _startCancellationObserved?.Task ?? Task.CompletedTask;

        public Task StopStarted =>
            _stopStarted?.Task ?? Task.CompletedTask;

        public Task DisposeStarted =>
            _disposeStarted?.Task ?? Task.CompletedTask;

        public Task SecondDisposeStarted =>
            _secondDisposeStarted?.Task ?? Task.CompletedTask;

        public bool IsStartActive =>
            Volatile.Read(ref _startActive) != 0;

        public bool DisposedWhileStartActive { get; private set; }

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _startEntered?.TrySetResult();
            if (_startRelease is null)
            {
                return;
            }

            Volatile.Write(ref _startActive, 1);
            try
            {
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _startCancellationObserved?.TrySetResult();
                await _startRelease.Task.ConfigureAwait(false);
                throw;
            }
            finally
            {
                Volatile.Write(ref _startActive, 0);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            _channel.Writer.TryComplete();
            _stopStarted?.TrySetResult();
            if (_stopRelease is not null)
            {
                await _stopRelease.Task;
            }

            if (StopFailure is not null)
            {
                throw StopFailure;
            }
        }

        public void Publish(byte marker)
        {
            var sequence = Interlocked.Increment(ref _enqueued);
            Assert.IsTrue(
                _channel.Writer.TryWrite(
                    DatagramEnvelope.CopyFrom(
                        sequence,
                        sequence,
                        DateTimeOffset.UnixEpoch,
                        new DatagramSender(
                            IPAddress.Loopback,
                            20_777),
                        [marker])));
        }

        public void BlockStartUntilReleased()
        {
            _startEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _startCancellationObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _startRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseStart() => _startRelease?.TrySetResult();

        public void BlockStopUntilReleased()
        {
            _stopStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseStop() => _stopRelease?.TrySetResult();

        public void BlockDisposalUntilReleased()
        {
            _disposeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _secondDisposeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseDisposal() =>
            _disposeRelease?.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposedWhileStartActive = IsStartActive;
            _disposeStarted?.TrySetResult();
            if (DisposeCalls > 1)
            {
                _secondDisposeStarted?.TrySetResult();
            }
            if (_disposeRelease is not null)
            {
                await _disposeRelease.Task;
            }
            _channel.Writer.TryComplete();
        }
    }

    private sealed class RecordingEvidenceStore : IRawEvidenceStore
    {
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finalizationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWriteCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _writeRelease;
        private TaskCompletionSource? _finalizationRelease;
        private long _written;

        public RawEvidenceCaptureId CaptureId { get; private set; } =
            RawEvidenceCaptureId.Parse(
                "00112233445546778899aabbccddeeff");

        public RawEvidenceProtocolId ProtocolId { get; } =
            RawEvidenceProtocolId.Parse("synthetic-v1");

        public RawEvidenceLimits Limits { get; set; } =
            new(minimumFreeSpaceBytes: 0);

        public int FinalizeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool FailWrites { get; set; }

        public Exception? WriteFailure { get; set; }

        public Exception? FinalizeFailure { get; set; }

        public Func<bool>? IsSourceStartActive { get; set; }

        public bool DisposedWhileSourceStartActive { get; private set; }

        public Task WriteStarted => _writeStarted.Task;

        public Task FinalizationStarted => _finalizationStarted.Task;

        public Task FirstWriteCompleted => _firstWriteCompleted.Task;

        public void SetCaptureId(RawEvidenceCaptureId captureId) =>
            CaptureId = captureId;

        public async ValueTask WriteAsync(
            DatagramEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            var release = _writeRelease;
            if (release is not null)
            {
                await release.Task.ConfigureAwait(false);
            }

            if (FailWrites)
            {
                throw new IOException("synthetic evidence write failed");
            }

            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Interlocked.Increment(ref _written);
            _firstWriteCompleted.TrySetResult();
        }

        public void BlockWrites() =>
            _writeRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites() => _writeRelease?.TrySetResult();

        public void BlockFinalizationUntilReleased() =>
            _finalizationRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFinalization() =>
            _finalizationRelease?.TrySetResult();

        public async Task<RawEvidenceCompletion> FinalizeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCalls++;
            _finalizationStarted.TrySetResult();
            if (_finalizationRelease is not null)
            {
                await _finalizationRelease.Task;
            }

            if (FinalizeFailure is not null)
            {
                throw FinalizeFailure;
            }

            return new RawEvidenceCompletion(
                CaptureId,
                ProtocolId,
                Interlocked.Read(ref _written),
                RawEvidenceLimits.MinimumFileBytes,
                new string('0', 64),
                DateTimeOffset.UnixEpoch);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposedWhileSourceStartActive =
                IsSourceStartActive?.Invoke() == true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibleProtocolAdapter : ITelemetryProtocolAdapter
    {
        private static readonly TelemetryPacketDescriptor Descriptor =
            new(
                "Synthetic",
                packetId: 1,
                packetVersion: 1,
                datagramLength: 1,
                TelemetryPacketPrivacyDisposition.EvidenceAllowed);

        public string ProtocolId => "synthetic-v1";

        public int HeaderLength => 1;

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
        {
            if (datagram.Length == 1 && datagram[0] == 1)
            {
                return TelemetryPacketResult.Compatible(
                    new TelemetryHeaderMetadata(
                        packetFormat: 2025,
                        gameYear: 25,
                        gameMajorVersion: 1,
                        gameMinorVersion: 0,
                        packetVersion: 1,
                        packetId: 1,
                        sessionUid: 0,
                        sessionTimeSeconds: 0,
                        frameIdentifier: 0,
                        overallFrameIdentifier: 0,
                        playerCarIndex: 0,
                        secondaryPlayerCarIndex: byte.MaxValue),
                    Descriptor);
            }

            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.MalformedHeader);
        }
    }
}
