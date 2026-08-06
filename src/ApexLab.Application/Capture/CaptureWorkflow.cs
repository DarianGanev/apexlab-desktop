using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace ApexLab.Application.Capture;

public sealed class CaptureWorkflow : ICaptureWorkflow
{
    private sealed class ActiveSession(
        CaptureSessionComponents components,
        CaptureIngestionCoordinator coordinator,
        CancellationTokenSource lifetime,
        Task runTask)
    {
        private readonly object _releaseGate = new();
        private Task<SessionRelease>? _releaseTask;
        private bool _transferPendingEvidence;

        public CaptureSessionComponents Components { get; } = components;

        public CaptureIngestionCoordinator Coordinator { get; } = coordinator;

        public CancellationTokenSource Lifetime { get; } = lifetime;

        public Task RunTask { get; } = runTask;

        public bool TransferPendingEvidenceRequested
        {
            get
            {
                lock (_releaseGate)
                {
                    return _transferPendingEvidence;
                }
            }
        }

        public void RequestPendingEvidenceTransfer()
        {
            lock (_releaseGate)
            {
                _transferPendingEvidence = true;
            }
        }

        public Task<SessionRelease> GetOrCreateRelease(
            bool transferPendingEvidence,
            Func<Task<SessionRelease>> create)
        {
            lock (_releaseGate)
            {
                _transferPendingEvidence |= transferPendingEvidence;
                return _releaseTask ??= create();
            }
        }
    }

    private sealed record SessionRelease(
        CaptureCounters Counters,
        Exception? Failure);

    private readonly object _gate = new();
    private readonly ICaptureSessionFactory _factory;
    private readonly TimeSpan _stopTimeout;
    private CaptureWorkflowSnapshot _snapshot =
        CaptureWorkflowSnapshot.Idle;
    private Task<CaptureWorkflowSnapshot>? _armTask;
    private Task? _armOwnershipTask;
    private Task<CaptureWorkflowSnapshot>? _stopTask;
    private Task? _storeFinalizationTask;
    private Task _deferredCleanupCompletion = Task.CompletedTask;
    private Task? _disposeTask;
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

        NotifySnapshotChanged(binding);
        var ownership = Task.Run(
            () => ArmCoreAsync(
                binding.CaptureId!,
                completion,
                armCancellation),
            CancellationToken.None);
        lock (_gate)
        {
            if (ReferenceEquals(_armCancellation, armCancellation))
            {
                _armOwnershipTask = ownership;
            }
        }

        ObserveFault(ownership);
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

        NotifySnapshotChanged(stopping);
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

        NotifySnapshotChanged(stopped);
    }

    public async Task StopProducersAsync(
        CancellationToken cancellationToken = default)
    {
        Task? armOwnershipTask;
        CancellationTokenSource? armCancellation;
        ActiveSession? session;
        lock (_gate)
        {
            armOwnershipTask = _armOwnershipTask;
            armCancellation = _armCancellation;
            session = _session;
        }

        if (armOwnershipTask is not null && !armOwnershipTask.IsCompleted)
        {
            armCancellation?.Cancel();
            await armOwnershipTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

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

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(
                _disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
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

        NotifySnapshotChanged(disposed);
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
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
        var pipeline = StopPipelineAsync();
        try
        {
            completion.TrySetResult(
                await pipeline.WaitAsync(
                        _stopTimeout,
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (TimeoutException exception)
        {
            completion.TrySetResult(
                BeginInterruptedCleanup(pipeline, exception));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetResult(
                BeginInterruptedCleanup(pipeline, exception));
        }
        catch (Exception exception)
        {
            completion.TrySetResult(
                await ResolveStopFailureAsync(exception)
                    .ConfigureAwait(false));
        }
    }

    private async Task<CaptureWorkflowSnapshot> StopPipelineAsync()
    {
        Task? armOwnershipTask;
        CancellationTokenSource? armCancellation;
        lock (_gate)
        {
            armOwnershipTask = _armOwnershipTask;
            armCancellation = _armCancellation;
        }

        if (armOwnershipTask is not null
            && !armOwnershipTask.IsCompleted)
        {
            armCancellation?.Cancel();
            await armOwnershipTask.ConfigureAwait(false);
        }

        ActiveSession? session;
        CaptureWorkflowSnapshot snapshot;
        lock (_gate)
        {
            session = _session;
            snapshot = CurrentSnapshot();
        }

        if (session is null)
        {
            if (snapshot.State == CaptureState.Faulted
                && snapshot.Failure is not null)
            {
                ExceptionDispatchInfo.Capture(snapshot.Failure).Throw();
            }

            return PublishState(CaptureState.Stopped);
        }

        await StopProducersAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            await DrainWorkAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (session.Lifetime.IsCancellationRequested)
        {
            // A timed-out pipeline owns cancellation and still finalizes salvageable writes.
        }
        await FinalizeStoresAsync(CancellationToken.None)
            .ConfigureAwait(false);
        return Snapshot;
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

    private Task<SessionRelease> ReleaseSessionAsync(
        ActiveSession session,
        bool transferPendingEvidence)
    {
        return session.GetOrCreateRelease(
            transferPendingEvidence,
            () => ReleaseSessionCoreAsync(session));
    }

    private async Task<SessionRelease> ReleaseSessionCoreAsync(
        ActiveSession session)
    {
        var failures = new List<Exception>();
        if (session.TransferPendingEvidenceRequested)
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

        if (session.TransferPendingEvidenceRequested)
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
        Task<CaptureWorkflowSnapshot> pipeline,
        Exception interruption)
    {
        ActiveSession? session;
        Task cleanup;
        lock (_gate)
        {
            if (pipeline.IsCompleted)
            {
                return CurrentSnapshot();
            }

            session = _session;
            if (session is not null)
            {
                session.RequestPendingEvidenceTransfer();
                session.Coordinator.TransferEvidenceToDeferredCleanup();
                session.Lifetime.Cancel();
            }

            cleanup = Task.Run(
                () => CompleteDeferredStopAsync(pipeline),
                CancellationToken.None);
            _deferredCleanupCompletion = cleanup;
        }

        ObserveFault(cleanup);
        var snapshot = PublishFailure(
            CaptureState.Interrupted,
            CaptureFailureKind.Interrupted,
            interruption);
        return snapshot;
    }

    private async Task CompleteDeferredStopAsync(
        Task<CaptureWorkflowSnapshot> pipeline)
    {
        try
        {
            await pipeline.ConfigureAwait(false);
            return;
        }
        catch (Exception primaryFailure)
        {
            var resolved = await ResolveStopFailureAsync(primaryFailure)
                .ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(
                resolved.Failure ?? primaryFailure).Throw();
        }
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

            var release = await ReleaseSessionAsync(
                    session,
                    transferPendingEvidence: true)
                .ConfigureAwait(false);
            var failure = CombineFailures(
                    "Capture failed and cleanup also reported a failure.",
                    exception,
                    release.Failure)
                ?? exception;
            lock (_gate)
            {
                _snapshot = _snapshot with
                {
                    Counters = release.Counters,
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

        NotifySnapshotChanged(snapshot);
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

        NotifySnapshotChanged(snapshot);
        return snapshot;
    }

    private void NotifySnapshotChanged(CaptureWorkflowSnapshot snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<CaptureWorkflowSnapshot> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // Presentation observers cannot own or corrupt capture work.
            }
        }
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
