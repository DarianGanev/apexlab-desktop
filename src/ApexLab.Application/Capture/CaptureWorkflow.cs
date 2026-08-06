using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace ApexLab.Application.Capture;

public sealed class CaptureWorkflow : ICaptureWorkflow
{
    private sealed record ActiveSession(
        CaptureSessionComponents Components,
        CaptureIngestionCoordinator Coordinator,
        CancellationTokenSource Lifetime,
        Task RunTask);

    private sealed record SessionRelease(
        CaptureCounters Counters,
        Exception? Failure);

    private readonly object _gate = new();
    private readonly ICaptureSessionFactory _factory;
    private readonly TimeSpan _stopTimeout;
    private CaptureWorkflowSnapshot _snapshot =
        CaptureWorkflowSnapshot.Idle;
    private Task<CaptureWorkflowSnapshot>? _armTask;
    private Task<CaptureWorkflowSnapshot>? _stopTask;
    private Task? _storeFinalizationTask;
    private Task _deferredCleanupCompletion = Task.CompletedTask;
    private ActiveSession? _session;
    private CancellationTokenSource? _armCancellation;
    private bool _disposeRequested;

    public CaptureWorkflow(
        ICaptureSessionFactory factory,
        TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopTimeout),
                "The capture stop timeout must be positive.");
        }

        _factory = factory;
        _stopTimeout = stopTimeout;
    }

    public CaptureWorkflowSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CurrentSnapshot();
            }
        }
    }

    public Task DeferredCleanupCompletion
    {
        get
        {
            lock (_gate)
            {
                return _deferredCleanupCompletion;
            }
        }
    }

    public event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged;

    public Task<CaptureWorkflowSnapshot> ArmAsync(
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<CaptureWorkflowSnapshot> completion;
        CaptureWorkflowSnapshot binding;
        CancellationTokenSource armCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_snapshot.State == CaptureState.Binding
                && _armTask is not null)
            {
                return _armTask;
            }

            if (!_snapshot.CanArm)
            {
                return Task.FromException<CaptureWorkflowSnapshot>(
                    new InvalidOperationException(
                        "Capture can be armed only while idle or stopped."));
            }

            var captureId = RawEvidenceCaptureId.Create();
            binding = _snapshot = new CaptureWorkflowSnapshot(
                CaptureState.Binding,
                captureId,
                new CaptureCounters(default, default, default),
                StopReason: null,
                CaptureFailureKind.None,
                Failure: null,
                Completion: null);
            _stopTask = null;
            _storeFinalizationTask = null;
            completion = new TaskCompletionSource<CaptureWorkflowSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _armTask = completion.Task;
            armCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            _armCancellation = armCancellation;
        }

        SnapshotChanged?.Invoke(this, binding);
        _ = Task.Run(
            () => ArmCoreAsync(
                binding.CaptureId!,
                completion,
                armCancellation),
            CancellationToken.None);
        return completion.Task;
    }

    public Task<CaptureWorkflowSnapshot> StopAsync(
        CaptureStopReason reason,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<CaptureWorkflowSnapshot> completion;
        CaptureWorkflowSnapshot stopping;
        lock (_gate)
        {
            if (_snapshot.State is CaptureState.Idle
                or CaptureState.Stopped
                or CaptureState.Faulted
                or CaptureState.Disposed)
            {
                return Task.FromResult(_snapshot);
            }

            if (_stopTask is not null)
            {
                return _stopTask;
            }

            stopping = _snapshot = _snapshot with
            {
                State = CaptureState.Stopping,
                StopReason = reason,
            };
            completion = new TaskCompletionSource<CaptureWorkflowSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        SnapshotChanged?.Invoke(this, stopping);
        _ = Task.Run(
            () => StopCoreAsync(completion, cancellationToken),
            CancellationToken.None);
        return completion.Task;
    }

    public void Reset()
    {
        CaptureWorkflowSnapshot stopped;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_snapshot.State != CaptureState.Faulted
                || _session is not null
                || !_deferredCleanupCompletion.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Only a fault with resolved ownership can be reset.");
            }

            stopped = _snapshot = _snapshot with
            {
                State = CaptureState.Stopped,
                FailureKind = CaptureFailureKind.None,
                Failure = null,
            };
            _armTask = null;
            _stopTask = null;
        }

        SnapshotChanged?.Invoke(this, stopped);
    }

    public async Task StopProducersAsync(
        CancellationToken cancellationToken = default)
    {
        Task<CaptureWorkflowSnapshot>? armTask;
        CancellationTokenSource? armCancellation;
        ActiveSession? session;
        lock (_gate)
        {
            armTask = _armTask;
            armCancellation = _armCancellation;
            session = _session;
        }

        if (armTask is not null && !armTask.IsCompleted)
        {
            armCancellation?.Cancel();
            try
            {
                await armTask.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (armCancellation?.IsCancellationRequested == true)
            {
                return;
            }

            lock (_gate)
            {
                session = _session;
            }
        }

        if (session is not null)
        {
            await session.Coordinator.StopSourceAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task DrainWorkAsync(
        CancellationToken cancellationToken = default)
    {
        ActiveSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is not null)
        {
            await session.RunTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task FinalizeStoresAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _storeFinalizationTask ??=
                FinalizeStoresCoreAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
        }

        await StopAsync(
                CaptureStopReason.HostShutdown,
                CancellationToken.None)
            .ConfigureAwait(false);
        await DeferredCleanupCompletion.ConfigureAwait(false);

        CaptureWorkflowSnapshot disposed;
        lock (_gate)
        {
            disposed = _snapshot = _snapshot with
            {
                State = CaptureState.Disposed,
            };
        }

        SnapshotChanged?.Invoke(this, disposed);
    }

    private async Task ArmCoreAsync(
        RawEvidenceCaptureId captureId,
        TaskCompletionSource<CaptureWorkflowSnapshot> completion,
        CancellationTokenSource armCancellation)
    {
        CaptureSessionComponents? components = null;
        CancellationTokenSource? lifetime = null;
        ActiveSession? session = null;
        var cancellationToken = armCancellation.Token;
        try
        {
            components = await _factory.CreateAsync(
                    captureId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (components.EvidenceStore.CaptureId != captureId)
            {
                throw new InvalidOperationException(
                    "The evidence store returned a different capture identity.");
            }

            lifetime = new CancellationTokenSource();
            var coordinator = new CaptureIngestionCoordinator(
                components.Source,
                components.Adapter,
                components.SenderPolicy,
                new SessionObserver(this),
                components.EvidenceStore);
            var runTask = coordinator.RunAsync(lifetime.Token);
            session = new ActiveSession(
                components,
                coordinator,
                lifetime,
                runTask);
            lock (_gate)
            {
                _session = session;
            }

            await coordinator.Started
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var waiting = PublishState(
                CaptureState.WaitingForTraffic);
            completion.TrySetResult(waiting);
            _ = ObserveUnexpectedCompletionAsync(session);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            var cleanupFailure = session is null
                ? await DisposeComponentsAsync(components)
                    .ConfigureAwait(false)
                : (await ReleaseSessionAsync(
                        session,
                        transferPendingEvidence: true)
                    .ConfigureAwait(false)).Failure;
            if (session is null)
            {
                lifetime?.Dispose();
            }

            if (cleanupFailure is null)
            {
                PublishState(CaptureState.Stopped);
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                var failed = PublishFailure(
                    CaptureState.Faulted,
                    Classify(cleanupFailure),
                    cleanupFailure);
                completion.TrySetResult(failed);
            }
        }
        catch (Exception exception)
        {
            var cleanupFailure = session is null
                ? await DisposeComponentsAsync(components)
                    .ConfigureAwait(false)
                : (await ReleaseSessionAsync(
                        session,
                        transferPendingEvidence: true)
                    .ConfigureAwait(false)).Failure;
            if (session is null)
            {
                lifetime?.Dispose();
            }

            var failure = Combine(exception, cleanupFailure);
            var recoverable = exception is CaptureSourceStartupException;
            var failed = PublishFailure(
                recoverable
                    ? CaptureState.Stopped
                    : CaptureState.Faulted,
                Classify(exception),
                failure);
            completion.TrySetResult(failed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(
                        _armCancellation,
                        armCancellation))
                {
                    _armCancellation = null;
                }
            }

            armCancellation.Dispose();
        }
    }

    private async Task StopCoreAsync(
        TaskCompletionSource<CaptureWorkflowSnapshot> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var armTask = _armTask;
            if (armTask is not null && !armTask.IsCompleted)
            {
                CancellationTokenSource? armCancellation;
                lock (_gate)
                {
                    armCancellation = _armCancellation;
                }

                armCancellation?.Cancel();
                try
                {
                    await armTask.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (armCancellation?.IsCancellationRequested == true)
                {
                    // Stop owns cancellation of an in-flight bind.
                }
            }

            ActiveSession? session;
            lock (_gate)
            {
                session = _session;
            }

            if (session is null)
            {
                completion.TrySetResult(
                    PublishState(CaptureState.Stopped));
                return;
            }

            await StopProducersAsync(cancellationToken)
                .ConfigureAwait(false);
            await DrainWorkAsync(cancellationToken)
                .WaitAsync(_stopTimeout, cancellationToken)
                .ConfigureAwait(false);
            await FinalizeStoresAsync(cancellationToken)
                .ConfigureAwait(false);
            completion.TrySetResult(Snapshot);
        }
        catch (TimeoutException exception)
        {
            completion.TrySetResult(BeginInterruptedCleanup(exception));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetResult(BeginInterruptedCleanup(exception));
        }
        catch (Exception exception)
        {
            completion.TrySetResult(
                await ResolveStopFailureAsync(exception)
                    .ConfigureAwait(false));
        }
    }

    private async Task FinalizeStoresCoreAsync(
        CancellationToken cancellationToken)
    {
        ActiveSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return;
        }

        RawEvidenceCompletion? evidenceCompletion = null;
        Exception? finalizationFailure = null;
        try
        {
            evidenceCompletion =
                await session.Coordinator.FinalizeEvidenceAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            finalizationFailure = exception;
        }

        var release = await ReleaseSessionAsync(
                session,
                transferPendingEvidence: false)
            .ConfigureAwait(false);
        var failure = CombineFailures(
            "Capture finalization and cleanup reported multiple failures.",
            finalizationFailure,
            release.Failure);
        if (failure is not null)
        {
            PublishFailure(
                CaptureState.Faulted,
                Classify(finalizationFailure ?? failure),
                failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        PublishState(
            CaptureState.Stopped,
            release.Counters,
            evidenceCompletion);
    }

    private async Task<CaptureWorkflowSnapshot> ResolveStopFailureAsync(
        Exception primaryFailure)
    {
        ActiveSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return PublishFailure(
                CaptureState.Faulted,
                Classify(primaryFailure),
                primaryFailure);
        }

        var release = await ReleaseSessionAsync(
                session,
                transferPendingEvidence: true)
            .ConfigureAwait(false);
        var failure = CombineFailures(
                "Capture failed and cleanup also reported a failure.",
                primaryFailure,
                release.Failure)
            ?? primaryFailure;
        return PublishFailure(
            CaptureState.Faulted,
            Classify(primaryFailure),
            failure);
    }

    private async Task<SessionRelease> ReleaseSessionAsync(
        ActiveSession session,
        bool transferPendingEvidence)
    {
        var failures = new List<Exception>();
        if (transferPendingEvidence)
        {
            try
            {
                session.Coordinator.TransferEvidenceToDeferredCleanup();
            }
            catch (Exception exception)
            {
                AddDistinct(failures, exception);
            }
        }

        session.Lifetime.Cancel();
        try
        {
            await session.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (session.Lifetime.IsCancellationRequested)
        {
            // Expected when cleanup cancels source startup or ingestion.
        }
        catch (Exception exception)
        {
            AddDistinct(failures, exception);
        }

        CaptureCounters counters;
        try
        {
            counters = session.Coordinator.CaptureCounters;
        }
        catch (Exception exception)
        {
            AddDistinct(failures, exception);
            counters = Snapshot.Counters;
        }

        var cleanupFailure = await DisposeComponentsAsync(
                session.Components)
            .ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            AddDistinct(failures, cleanupFailure);
        }

        session.Lifetime.Dispose();
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        return new SessionRelease(
            counters,
            CombineFailures(
                "Capture cleanup reported multiple failures.",
                failures.ToArray()));
    }

    private CaptureWorkflowSnapshot BeginInterruptedCleanup(
        Exception interruption)
    {
        ActiveSession? session;
        var cleanupStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task cleanup;
        lock (_gate)
        {
            session = _session;
            if (session is null)
            {
                return _snapshot;
            }

            session.Coordinator.TransferEvidenceToDeferredCleanup();
            session.Lifetime.Cancel();
            cleanup = Task.Run(
                async () =>
                {
                    await cleanupStart.Task.ConfigureAwait(false);
                    await CompleteDeferredCleanupAsync(session)
                        .ConfigureAwait(false);
                },
                CancellationToken.None);
            _deferredCleanupCompletion = cleanup;
        }

        ObserveFault(cleanup);
        var snapshot = PublishFailure(
            CaptureState.Interrupted,
            CaptureFailureKind.Interrupted,
            interruption);
        cleanupStart.TrySetResult();
        return snapshot;
    }

    private async Task CompleteDeferredCleanupAsync(
        ActiveSession session)
    {
        var failures = new List<Exception>();
        RawEvidenceCompletion? completion = null;
        try
        {
            await session.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (session.Lifetime.IsCancellationRequested)
        {
            // Cancellation transfers any unread source backlog to abandonment.
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 0)
        {
            try
            {
                completion = await session.Coordinator
                    .FinalizeEvidenceAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var cleanupFailure = await DisposeComponentsAsync(
                session.Components)
            .ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            failures.Add(cleanupFailure);
        }

        session.Lifetime.Dispose();
        CaptureCounters counters;
        try
        {
            counters = session.Coordinator.CaptureCounters;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            counters = Snapshot.Counters;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        if (failures.Count == 0)
        {
            PublishState(
                CaptureState.Stopped,
                counters,
                completion);
            return;
        }

        PublishFailure(
            CaptureState.Faulted,
            Classify(failures[0]),
            failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "Deferred capture cleanup reported multiple failures.",
                    failures));
    }

    private async Task ObserveUnexpectedCompletionAsync(
        ActiveSession session)
    {
        try
        {
            await session.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (session.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_session != session
                    || _snapshot.State == CaptureState.Stopping)
                {
                    return;
                }
            }

            var counters = session.Coordinator.CaptureCounters;
            var cleanupFailure = await DisposeComponentsAsync(
                    session.Components)
                .ConfigureAwait(false);
            session.Lifetime.Dispose();
            var failure = Combine(exception, cleanupFailure);
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }

                _snapshot = _snapshot with
                {
                    Counters = counters,
                };
            }

            PublishFailure(
                CaptureState.Faulted,
                Classify(exception),
                failure);
        }
    }

    private void Observe(CapturePacketObservation observation)
    {
        var next = observation.Result.Classification
            == Telemetry.Abstractions.Protocol.TelemetryPacketClassification.Compatible
            ? CaptureState.ReceivingCompatibleTraffic
            : CaptureState.IncompatibleTraffic;
        lock (_gate)
        {
            if (_snapshot.State is not (
                CaptureState.WaitingForTraffic
                or CaptureState.ReceivingCompatibleTraffic
                or CaptureState.IncompatibleTraffic))
            {
                return;
            }
        }

        PublishState(next);
    }

    private CaptureWorkflowSnapshot PublishState(
        CaptureState state,
        CaptureCounters? counters = null,
        RawEvidenceCompletion? completion = null)
    {
        CaptureWorkflowSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot = _snapshot with
            {
                State = state,
                Counters = counters ?? CurrentCounters(),
                Completion = completion ?? _snapshot.Completion,
            };
        }

        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private CaptureWorkflowSnapshot PublishFailure(
        CaptureState state,
        CaptureFailureKind kind,
        Exception failure)
    {
        CaptureWorkflowSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot = _snapshot with
            {
                State = state,
                Counters = CurrentCounters(),
                FailureKind = kind,
                Failure = failure,
            };
        }

        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private CaptureWorkflowSnapshot CurrentSnapshot() =>
        _snapshot with
        {
            Counters = CurrentCounters(),
        };

    private CaptureCounters CurrentCounters()
    {
        if (_session is null)
        {
            return _snapshot.Counters;
        }

        try
        {
            return _session.Coordinator.CaptureCounters;
        }
        catch (ArgumentException)
        {
            return _snapshot.Counters;
        }
    }

    private static CaptureFailureKind Classify(Exception exception)
    {
        if (exception is CaptureSourceStartupException
            {
                InnerException: SocketException
                {
                    SocketErrorCode: SocketError.AddressAlreadyInUse,
                },
            })
        {
            return CaptureFailureKind.PortConflict;
        }

        if (exception is ArgumentException)
        {
            return CaptureFailureKind.Configuration;
        }

        return exception is IOException
            ? CaptureFailureKind.EvidenceWrite
            : CaptureFailureKind.Unexpected;
    }

    private static async Task<Exception?> DisposeComponentsAsync(
        CaptureSessionComponents? components)
    {
        if (components is null)
        {
            return null;
        }

        var failures = new List<Exception>();
        try
        {
            await components.Source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await components.EvidenceStore.DisposeAsync()
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
    }

    private static Exception Combine(
        Exception primary,
        Exception? cleanup)
    {
        if (cleanup is null)
        {
            return primary;
        }

        return new AggregateException(
            "Capture failed and cleanup also reported a failure.",
            primary,
            cleanup);
    }

    private static Exception? CombineFailures(
        string message,
        params Exception?[] failures)
    {
        var distinct = new List<Exception>();
        foreach (var failure in failures)
        {
            if (failure is not null)
            {
                AddDistinct(distinct, failure);
            }
        }

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => new AggregateException(message, distinct),
        };
    }

    private static void AddDistinct(
        ICollection<Exception> failures,
        Exception failure)
    {
        if (!failures.Any(existing => ReferenceEquals(existing, failure)))
        {
            failures.Add(failure);
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class SessionObserver(
        CaptureWorkflow owner) : ICapturePacketObserver
    {
        public void Observe(CapturePacketObservation observation) =>
            owner.Observe(observation);
    }
}
