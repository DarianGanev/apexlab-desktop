using ApexLab.App.Lifecycle;

namespace ApexLab.IntegrationTests.Lifecycle;

[TestClass]
public sealed class ApplicationLifecycleCoordinatorTests
{
    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Stop_before_start_is_terminal_and_prevents_all_startup_work()
    {
        var events = new List<string>();
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            new RecordingOperations(events),
            TimeSpan.FromSeconds(1));

        var stop = await subject.StopAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => subject.StartAsync());

        Assert.AreEqual(LifecycleStopOutcome.Completed, stop.Outcome);
        Assert.IsEmpty(events);
    }

    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    [DataRow("lease")]
    [DataRow("settings.validate")]
    [DataRow("data.prepare")]
    [DataRow("producers.start")]
    public async Task Stop_during_each_startup_boundary_cancels_and_unwinds_once(string boundary)
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new RecordingLease(events, available: true)
        {
            AcquireAction = boundary == "lease"
                ? () => { entered.TrySetResult(); proceed.Task.GetAwaiter().GetResult(); }
            : null,
        };
        var operations = new RecordingOperations(events)
        {
            ValidateAction = BoundaryAction("settings.validate", boundary, entered, proceed),
            PrepareAction = BoundaryAction("data.prepare", boundary, entered, proceed),
            StartAction = BoundaryAction("producers.start", boundary, entered, proceed),
        };
        var subject = new ApplicationLifecycleCoordinator(lease, operations, TimeSpan.FromSeconds(2));

        var start = subject.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = subject.StopAsync();
        proceed.TrySetResult();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await start);
        var stopResult = await stop;
        Assert.AreEqual(LifecycleStopOutcome.Completed, stopResult.Outcome);
        Assert.AreEqual(1, events.Count(item => item == "lease.release"));
        Assert.IsGreaterThan(events.IndexOf(boundary == "lease" ? "lease.acquire" : boundary), events.IndexOf("lease.release"));
        if (boundary == "producers.start")
        {
            Assert.AreEqual(1, events.Count(item => item == "producers.stop"));
        }
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Concurrent_start_calls_share_one_completion_task()
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingOperations(events)
        {
            ValidateAction = async _ => { entered.TrySetResult(); await proceed.Task; },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromSeconds(1));

        var first = subject.StartAsync();
        await entered.Task;
        var second = subject.StartAsync();

        Assert.AreSame(first, second);
        proceed.TrySetResult();
        Assert.AreEqual(LifecycleStartOutcome.Started, await first);
        await subject.StopAsync();
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Stop_at_final_start_publication_cannot_observe_started_with_incomplete_start_task()
    {
        var events = new List<string>();
        using var publicationReached = new ManualResetEventSlim(false);
        using var allowPublication = new ManualResetEventSlim(false);
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            new RecordingOperations(events),
            TimeSpan.FromSeconds(1),
            new ApplicationLifecycleTestHooks
            {
                BeforeStartPublication = () =>
                {
                    publicationReached.Set();
                    allowPublication.Wait();
                },
            });

        var start = subject.StartAsync();
        Assert.IsTrue(publicationReached.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(start.IsCompleted);
        var stop = subject.StopAsync();
        allowPublication.Set();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await start);
        Assert.AreEqual(LifecycleStopOutcome.Completed, (await stop).Outcome);
        CollectionAssert.Contains(events, "producers.stop");
        CollectionAssert.Contains(events, "data.rollback");
        Assert.AreEqual("lease.release", events[^1]);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Producer_stop_failure_continues_best_effort_cleanup_but_retains_lease()
    {
        var events = new List<string>();
        var stopFailure = new InvalidOperationException("producer ownership uncertain");
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            new RecordingOperations(events) { StopFailure = stopFailure },
            TimeSpan.FromSeconds(1));
        await subject.StartAsync();

        var result = await subject.StopAsync();

        Assert.AreEqual(LifecycleStopOutcome.Failed, result.Outcome);
        Assert.AreSame(stopFailure, result.PrimaryFailure);
        Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        CollectionAssert.Contains(events, "work.drain");
        CollectionAssert.Contains(events, "stores.finalize");
        Assert.DoesNotContain("lease.release", events);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Producer_stop_failure_during_startup_rollback_retains_lease()
    {
        var events = new List<string>();
        using var publicationReached = new ManualResetEventSlim(false);
        using var allowPublication = new ManualResetEventSlim(false);
        var stopFailure = new InvalidOperationException("producer ownership uncertain");
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            new RecordingOperations(events) { StopFailure = stopFailure },
            TimeSpan.FromSeconds(1),
            new ApplicationLifecycleTestHooks
            {
                BeforeStartPublication = () =>
                {
                    publicationReached.Set();
                    allowPublication.Wait();
                },
            });

        var start = subject.StartAsync();
        Assert.IsTrue(publicationReached.Wait(TimeSpan.FromSeconds(1)));
        var stop = subject.StopAsync();
        allowPublication.Set();

        var startFailure = await Assert.ThrowsExactlyAsync<AggregateException>(async () => await start);
        Assert.AreSame(stopFailure, startFailure.InnerExceptions[1]);
        var stopResult = await stop;
        Assert.AreEqual(LifecycleStopOutcome.Failed, stopResult.Outcome);
        Assert.IsTrue(stopResult.LeaseRetainedForDeferredCleanup);
        CollectionAssert.Contains(events, "data.rollback");
        Assert.DoesNotContain("lease.release", events);
    }

    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    public async Task Startup_observation_and_rollback_timeout_compose_owned_cleanup()
    {
        var events = new List<string>();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStartFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rollbackBlocker = new ManualResetEventSlim(false);
        var firstComposition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondComposition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compositionCount = 0;
        var primary = new InvalidOperationException("start failed late");
        var operations = new RecordingOperations(events)
        {
            StartAction = async _ =>
            {
                startEntered.TrySetResult();
                await allowStartFailure.Task;
                throw primary;
            },
            RollbackAction = _ => { rollbackBlocker.Wait(); return Task.CompletedTask; },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            operations,
            TimeSpan.FromMilliseconds(75),
            new ApplicationLifecycleTestHooks
            {
                DeferredCleanupComposed = () =>
                {
                    if (Interlocked.Increment(ref compositionCount) == 1) { firstComposition.TrySetResult(); }
                    else { secondComposition.TrySetResult(); }
                },
            });

        var start = subject.StartAsync();
        await startEntered.Task;
        var stopResult = await subject.StopAsync();
        Assert.AreEqual(LifecycleStopOutcome.Interrupted, stopResult.Outcome);
        await firstComposition.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var firstOwnedCompletion = subject.DeferredCleanupCompletion;

        allowStartFailure.TrySetResult();
        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await start);
        await secondComposition.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var composedCompletion = subject.DeferredCleanupCompletion;
        Assert.AreNotSame(firstOwnedCompletion, composedCompletion);
        Assert.DoesNotContain("lease.release", events);

        rollbackBlocker.Set();
        await composedCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("lease.release", events[^1]);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Blocking_startup_cancellation_callback_is_bounded_and_owned()
    {
        var events = new List<string>();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var allowCancellation = new ManualResetEventSlim(false);
        var operations = new RecordingOperations(events)
        {
            ValidateAction = async token =>
            {
                var pending = Task.Delay(Timeout.InfiniteTimeSpan, token);
                using var registration = token.Register(() =>
                {
                    cancellationEntered.Set();
                    allowCancellation.Wait();
                });
                startupEntered.TrySetResult();
                await pending;
            },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromMilliseconds(75));

        var start = subject.StartAsync();
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            var invocation = Task.Factory.StartNew(
                subject.StopAsync,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            Assert.IsTrue(cancellationEntered.Wait(TimeSpan.FromSeconds(1)));
            var stop = await invocation.WaitAsync(TimeSpan.FromMilliseconds(500));
            var result = await stop.WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
            Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
            Assert.DoesNotContain("lease.release", events);
        }
        finally
        {
            allowCancellation.Set();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await start);
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("lease.release", events[^1]);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Blocking_lease_release_is_bounded_and_owned_during_normal_stop()
    {
        var events = new List<string>();
        using var releaseEntered = new ManualResetEventSlim(false);
        using var allowRelease = new ManualResetEventSlim(false);
        var lease = new RecordingLease(events, available: true)
        {
            ReleaseAction = () =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
            },
        };
        var subject = new ApplicationLifecycleCoordinator(
            lease, new RecordingOperations(events), TimeSpan.FromMilliseconds(75));
        await subject.StartAsync();

        var stop = subject.StopAsync();
        try
        {
            Assert.IsTrue(releaseEntered.Wait(TimeSpan.FromSeconds(1)));
            var result = await stop.WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
            Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
        }
        finally
        {
            allowRelease.Set();
        }
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, events.Count(item => item == "lease.release"));
        Assert.IsEmpty(subject.LateFailures);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Blocking_lease_release_is_bounded_and_owned_during_startup_rollback()
    {
        var events = new List<string>();
        using var releaseEntered = new ManualResetEventSlim(false);
        using var allowRelease = new ManualResetEventSlim(false);
        var primary = new InvalidOperationException("settings failed");
        var lease = new RecordingLease(events, available: true)
        {
            ReleaseAction = () =>
            {
                releaseEntered.Set();
                allowRelease.Wait();
            },
        };
        var subject = new ApplicationLifecycleCoordinator(
            lease,
            new RecordingOperations(events) { FailureStage = "settings.validate", Failure = primary },
            TimeSpan.FromMilliseconds(75));

        var start = subject.StartAsync();
        try
        {
            Assert.IsTrue(releaseEntered.Wait(TimeSpan.FromSeconds(1)));
            var startFailure = await Assert.ThrowsExactlyAsync<AggregateException>(
                async () => await start.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.AreSame(primary, startFailure.InnerExceptions[0]);
            Assert.IsInstanceOfType<TimeoutException>(startFailure.InnerExceptions[1]);
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);

            var stopResult = await subject.StopAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(LifecycleStopOutcome.Interrupted, stopResult.Outcome);
            Assert.IsTrue(stopResult.LeaseRetainedForDeferredCleanup);
        }
        finally
        {
            allowRelease.Set();
        }
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, events.Count(item => item == "lease.release"));
        Assert.IsEmpty(subject.LateFailures);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Timed_out_shutdown_retains_cancellation_source_until_deferred_chain_finishes()
    {
        var events = new List<string>();
        var stageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var allowCancellation = new ManualResetEventSlim(false);
        CancellationToken capturedToken = default;
        CancellationTokenRegistration blockingRegistration = default;
        var operations = new RecordingOperations(events)
        {
            StopAction = token =>
            {
                capturedToken = token;
                blockingRegistration = token.Register(() =>
                {
                    cancellationEntered.Set();
                    allowCancellation.Wait();
                });
                return stageCompletion.Task;
            },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromMilliseconds(75));
        await subject.StartAsync();

        try
        {
            var result = await subject.StopAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
            Assert.IsTrue(cancellationEntered.Wait(TimeSpan.FromSeconds(1)));
            Assert.IsTrue(capturedToken.IsCancellationRequested);
            Assert.IsTrue(capturedToken.WaitHandle.WaitOne(0));
            var postResultCallback = false;
            using (capturedToken.Register(() => postResultCallback = true))
            {
                Assert.IsTrue(postResultCallback);
            }
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
            Assert.DoesNotContain("work.drain", events);

            stageCompletion.TrySetResult();
            Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
        }
        finally
        {
            stageCompletion.TrySetResult();
            allowCancellation.Set();
        }
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        blockingRegistration.Dispose();

        CollectionAssert.IsSubsetOf(
            new[] { "producers.stop", "work.drain", "stores.finalize", "lease.release" },
            events);
        Assert.IsGreaterThan(events.IndexOf("producers.stop"), events.IndexOf("work.drain"));
        Assert.IsGreaterThan(events.IndexOf("work.drain"), events.IndexOf("stores.finalize"));
        Assert.IsGreaterThan(events.IndexOf("stores.finalize"), events.IndexOf("lease.release"));
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Synchronously_blocking_stop_delegate_is_bounded_and_returns_interrupted()
    {
        var events = new List<string>();
        using var blocker = new ManualResetEventSlim(false);
        var operations = new RecordingOperations(events)
        {
            StopAction = _ => { blocker.Wait(); return Task.CompletedTask; },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromMilliseconds(75));
        await subject.StartAsync();

        try
        {
            var invocation = Task.Factory.StartNew(
                subject.StopAsync,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            var stopTask = await invocation.WaitAsync(TimeSpan.FromMilliseconds(500));
            var result = await stopTask.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
            Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        }
        finally
        {
            blocker.Set();
        }

        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Late_fault_is_observed_and_lease_is_held_until_deferred_cleanup_finishes()
    {
        var events = new List<string>();
        var lateStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingOperations(events) { StopAction = _ => lateStop.Task };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromMilliseconds(50));
        await subject.StartAsync();

        var result = await subject.StopAsync();
        Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
        Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        Assert.DoesNotContain("lease.release", events);

        var lateFailure = new IOException("late stop failure");
        lateStop.SetException(lateFailure);
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreSame(lateFailure, subject.LateFailures.Single());
        CollectionAssert.Contains(events, "work.drain");
        CollectionAssert.Contains(events, "stores.finalize");
        Assert.DoesNotContain("lease.release", events);
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Synchronously_blocking_start_rollback_is_bounded_and_owned()
    {
        var events = new List<string>();
        using var rollbackBlocker = new ManualResetEventSlim(false);
        var primary = new InvalidOperationException("producer start failed");
        var operations = new RecordingOperations(events)
        {
            FailureStage = "producers.start",
            Failure = primary,
            RollbackAction = _ => { rollbackBlocker.Wait(); return Task.CompletedTask; },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true), operations, TimeSpan.FromMilliseconds(75));

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            async () => await subject.StartAsync().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.AreSame(primary, thrown.InnerExceptions[0]);
        Assert.IsInstanceOfType<TimeoutException>(thrown.InnerExceptions[1]);
        Assert.DoesNotContain("lease.release", events);

        rollbackBlocker.Set();
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("lease.release", events[^1]);
    }

    [TestMethod]
    public async Task Start_runs_required_stages_in_order_and_stop_reverses_ownership_safely()
    {
        var events = new List<string>();
        var lease = new RecordingLease(events, available: true);
        var operations = new RecordingOperations(events);
        var subject = new ApplicationLifecycleCoordinator(lease, operations, TimeSpan.FromSeconds(2));

        var start = await subject.StartAsync();
        var stop = await subject.StopAsync();

        Assert.AreEqual(LifecycleStartOutcome.Started, start);
        Assert.AreEqual(LifecycleStopOutcome.Completed, stop.Outcome);
        CollectionAssert.AreEqual(
            new[] { "lease.acquire", "settings.validate", "data.prepare", "producers.start", "producers.stop", "work.drain", "stores.finalize", "lease.release" },
            events);
    }

    [TestMethod]
    public async Task Denied_lease_prevents_every_later_stage()
    {
        var events = new List<string>();
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: false),
            new RecordingOperations(events),
            TimeSpan.FromSeconds(2));

        var result = await subject.StartAsync();

        Assert.AreEqual(LifecycleStartOutcome.AlreadyRunning, result);
        CollectionAssert.AreEqual(new[] { "lease.acquire" }, events);
    }

    [TestMethod]
    [DataRow("settings.validate", "lease.acquire", "settings.validate", "lease.release")]
    [DataRow("data.prepare", "lease.acquire", "settings.validate", "data.prepare", "lease.release")]
    [DataRow("producers.start", "lease.acquire", "settings.validate", "data.prepare", "producers.start", "data.rollback", "lease.release")]
    public async Task Start_failure_unwinds_only_completed_stages_in_reverse_order(
        string failingStage,
        params string[] expectedEvents)
    {
        var events = new List<string>();
        var primary = new InvalidOperationException("primary");
        var operations = new RecordingOperations(events) { FailureStage = failingStage, Failure = primary };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            operations,
            TimeSpan.FromSeconds(2));

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => subject.StartAsync());

        Assert.AreSame(primary, thrown);
        CollectionAssert.AreEqual(expectedEvents, events);
    }

    [TestMethod]
    public async Task Start_cleanup_failures_follow_the_primary_in_deterministic_order()
    {
        var events = new List<string>();
        var primary = new InvalidOperationException("start");
        var rollback = new IOException("rollback");
        var release = new ApplicationException("release");
        var operations = new RecordingOperations(events)
        {
            FailureStage = "producers.start",
            Failure = primary,
            RollbackFailure = rollback,
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true) { ReleaseFailure = release },
            operations,
            TimeSpan.FromSeconds(2));

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(() => subject.StartAsync());

        CollectionAssert.AreEqual(new Exception[] { primary, rollback, release }, thrown.InnerExceptions.ToArray());
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Failed_start_with_uncertain_lease_release_cannot_report_completed_stop()
    {
        var events = new List<string>();
        var primary = new InvalidOperationException("settings failed");
        var release = new IOException("lease release failed");
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true) { ReleaseFailure = release },
            new RecordingOperations(events) { FailureStage = "settings.validate", Failure = primary },
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await subject.StartAsync());
        var result = await subject.StopAsync();

        Assert.AreEqual(LifecycleStopOutcome.Failed, result.Outcome);
        Assert.AreSame(release, result.PrimaryFailure);
        Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        Assert.AreEqual(1, events.Count(item => item == "lease.release"));
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Repeated_and_concurrent_stop_calls_share_one_task_and_one_result()
    {
        var events = new List<string>();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingOperations(events)
        {
            StopAction = async _ =>
            {
                stopEntered.SetResult();
                await allowStop.Task;
            },
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            operations,
            TimeSpan.FromSeconds(2));
        await subject.StartAsync();

        var first = subject.StopAsync();
        await stopEntered.Task;
        var second = subject.StopAsync();
        Assert.AreSame(first, second);
        allowStop.SetResult();

        var results = await Task.WhenAll(first, second, subject.StopAsync());
        Assert.IsTrue(results.All(result => ReferenceEquals(results[0], result)));
        Assert.AreEqual(1, events.Count(item => item == "producers.stop"));
        Assert.AreEqual(1, events.Count(item => item == "lease.release"));
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public async Task Shutdown_timeout_retains_lease_until_deferred_cleanup_releases_it()
    {
        var events = new List<string>();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingOperations(events) { StopAction = _ => never.Task };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            operations,
            TimeSpan.FromMilliseconds(50));
        await subject.StartAsync();

        var result = await subject.StopAsync();

        Assert.AreEqual(LifecycleStopOutcome.Interrupted, result.Outcome);
        Assert.IsTrue(result.TimedOut);
        Assert.IsNull(result.PrimaryFailure);
        Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        Assert.DoesNotContain("lease.release", events);
        never.SetResult();
        await subject.DeferredCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("lease.release", events[^1]);
    }

    [TestMethod]
    public async Task Stop_failures_do_not_prevent_later_cleanup_or_hide_the_primary()
    {
        var events = new List<string>();
        var stopFailure = new InvalidOperationException("stop");
        var drainFailure = new IOException("drain");
        var finalizeFailure = new ApplicationException("finalize");
        var operations = new RecordingOperations(events)
        {
            StopFailure = stopFailure,
            DrainFailure = drainFailure,
            FinalizeFailure = finalizeFailure,
        };
        var subject = new ApplicationLifecycleCoordinator(
            new RecordingLease(events, available: true),
            operations,
            TimeSpan.FromSeconds(2));
        await subject.StartAsync();

        var result = await subject.StopAsync();

        Assert.AreEqual(LifecycleStopOutcome.Failed, result.Outcome);
        Assert.AreSame(stopFailure, result.PrimaryFailure);
        Assert.IsTrue(result.LeaseRetainedForDeferredCleanup);
        CollectionAssert.AreEqual(
            new Exception[] { drainFailure, finalizeFailure },
            result.SubsequentFailures.ToArray());
        CollectionAssert.AreEqual(
            new[] { "lease.acquire", "settings.validate", "data.prepare", "producers.start", "producers.stop", "work.drain", "stores.finalize" },
            events);
    }

    private static Func<CancellationToken, Task>? BoundaryAction(
        string stage,
        string boundary,
        TaskCompletionSource entered,
        TaskCompletionSource proceed) =>
        stage == boundary
            ? async _ => { entered.TrySetResult(); await proceed.Task; }
    : null;

    private sealed class RecordingLease(List<string> events, bool available) : ISingleInstanceLease
    {
        private bool _released;

        public Exception? ReleaseFailure { get; init; }
        public Action? AcquireAction { get; init; }
        public Action? ReleaseAction { get; init; }

        public bool TryAcquire()
        {
            events.Add("lease.acquire");
            AcquireAction?.Invoke();
            return available;
        }

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            events.Add("lease.release");
            ReleaseAction?.Invoke();
            if (ReleaseFailure is not null)
            {
                throw ReleaseFailure;
            }
        }

        public void Dispose() => Release();
    }

    private sealed class RecordingOperations(List<string> events) : IApplicationLifecycleOperations
    {
        public string? FailureStage { get; init; }
        public Exception? Failure { get; init; }
        public Exception? RollbackFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public Exception? DrainFailure { get; init; }
        public Exception? FinalizeFailure { get; init; }
        public Func<CancellationToken, Task>? StopAction { get; init; }
        public Func<CancellationToken, Task>? ValidateAction { get; init; }
        public Func<CancellationToken, Task>? PrepareAction { get; init; }
        public Func<CancellationToken, Task>? StartAction { get; init; }
        public Func<CancellationToken, Task>? RollbackAction { get; init; }

        public Task ValidateSettingsAsync(CancellationToken cancellationToken) =>
            Stage("settings.validate", ValidateAction, cancellationToken);
        public Task PrepareDataRootAsync(CancellationToken cancellationToken) =>
            Stage("data.prepare", PrepareAction, cancellationToken);
        public Task StartProducersAsync(CancellationToken cancellationToken) =>
            Stage("producers.start", StartAction, cancellationToken);

        public Task RollbackDataRootAsync(CancellationToken cancellationToken)
        {
            events.Add("data.rollback");
            return RollbackFailure is not null
                ? Task.FromException(RollbackFailure)
                : RollbackAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public async Task StopProducersAsync(CancellationToken cancellationToken)
        {
            events.Add("producers.stop");
            if (StopAction is not null)
            {
                await StopAction(cancellationToken);
            }

            if (StopFailure is not null)
            {
                throw StopFailure;
            }
        }

        public Task DrainWorkAsync(CancellationToken cancellationToken)
        {
            events.Add("work.drain");
            return DrainFailure is null ? Task.CompletedTask : Task.FromException(DrainFailure);
        }

        public Task FinalizeStoresAsync(CancellationToken cancellationToken)
        {
            events.Add("stores.finalize");
            return FinalizeFailure is null ? Task.CompletedTask : Task.FromException(FinalizeFailure);
        }

        private Task Stage(string name, Func<CancellationToken, Task>? action, CancellationToken cancellationToken)
        {
            events.Add(name);
            return FailureStage == name
                ? Task.FromException(Failure!)
                : action?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
