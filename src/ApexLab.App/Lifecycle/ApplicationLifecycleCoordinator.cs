using System.Diagnostics;

namespace ApexLab.App.Lifecycle;

public interface IApplicationLifecycleOperations
{
    Task ValidateSettingsAsync(CancellationToken cancellationToken);
    Task PrepareDataRootAsync(CancellationToken cancellationToken);
    Task RollbackDataRootAsync(CancellationToken cancellationToken);
    Task StartProducersAsync(CancellationToken cancellationToken);
    Task StopProducersAsync(CancellationToken cancellationToken);
    Task DrainWorkAsync(CancellationToken cancellationToken);
    Task FinalizeStoresAsync(CancellationToken cancellationToken);
}

public enum LifecycleStartOutcome { Started, AlreadyRunning }
public enum LifecycleStopOutcome { Completed, Interrupted, Failed }

public sealed record LifecycleStopResult(
    LifecycleStopOutcome Outcome,
    bool TimedOut,
    Exception? PrimaryFailure,
    IReadOnlyList<Exception> SubsequentFailures,
    bool LeaseRetainedForDeferredCleanup = false);

internal sealed class ApplicationLifecycleTestHooks
{
    public Action? BeforeStartPublication { get; init; }
    public Action? DeferredCleanupComposed { get; init; }
    public Action? StopCoordinationStarted { get; init; }
}

public sealed class ApplicationLifecycleCoordinator
{
    private enum LifecycleState { Created, Starting, Started, Stopping, Stopped, Denied, Failed }

    private sealed record CleanupStage(
        Func<CancellationToken, Task> Operation,
        bool StopsProducers = false,
        bool ClearsDataRoot = false,
        bool ReleasesLease = false);

    private readonly object _gate = new();
    private readonly ISingleInstanceLease _lease;
    private readonly IApplicationLifecycleOperations _operations;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ApplicationLifecycleTestHooks? _testHooks;
    private readonly List<Exception> _lateFailures = [];
    private LifecycleState _state = LifecycleState.Created;
    private CancellationTokenSource? _startupCancellation;
    private int _startupCancellationUsers;
    private bool _startupFinished;
    private Task<LifecycleStartOutcome>? _startTask;
    private Task<LifecycleStopResult>? _stopTask;
    private Task _deferredCleanupCompletion = Task.CompletedTask;
    private bool _leaseAcquired;
    private bool _dataRootPrepared;
    private bool _producersStarted;
    private bool _producerOwnershipUncertain;
    private bool _leaseReleaseUncertain;
    private Exception? _retentionFailure;

    public ApplicationLifecycleCoordinator(
        ISingleInstanceLease lease,
        IApplicationLifecycleOperations operations,
        TimeSpan shutdownTimeout)
        : this(lease, operations, shutdownTimeout, null)
    {
    }

    internal ApplicationLifecycleCoordinator(
        ISingleInstanceLease lease,
        IApplicationLifecycleOperations operations,
        TimeSpan shutdownTimeout,
        ApplicationLifecycleTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operations);
        if (shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout), "Shutdown timeout must be positive.");
        }

        _lease = lease;
        _operations = operations;
        _shutdownTimeout = shutdownTimeout;
        _testHooks = testHooks;
    }

    public Task DeferredCleanupCompletion
    {
        get { lock (_gate) { return _deferredCleanupCompletion; } }
    }

    public IReadOnlyList<Exception> LateFailures
    {
        get { lock (_gate) { return _lateFailures.ToArray(); } }
    }

    public Task<LifecycleStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<LifecycleStartOutcome>? completion = null;
        CancellationToken startupToken = default;
        lock (_gate)
        {
            if (_startTask is not null)
            {
                return _startTask;
            }

            if (_state != LifecycleState.Created)
            {
                return _startTask = Task.FromException<LifecycleStartOutcome>(
                    new InvalidOperationException("A stopped application lifecycle cannot be started."));
            }

            _state = LifecycleState.Starting;
            _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupToken = _startupCancellation.Token;
            completion = new TaskCompletionSource<LifecycleStartOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _startTask = completion.Task;
            ObserveFault(_startTask);
        }

        _ = Task.Run(() => RunStartAndPublishAsync(completion!, startupToken));
        return completion!.Task;
    }

    public Task<LifecycleStopResult> StopAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        CancellationTokenSource? startupCancellation = null;
        Task<LifecycleStartOutcome>? startTask = null;
        TaskCompletionSource<LifecycleStopResult>? completion = null;
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            if (_state == LifecycleState.Created)
            {
                _state = LifecycleState.Stopped;
                return _stopTask = Task.FromResult(CompletedStop());
            }

            var ownsResources = _leaseAcquired
                || !_deferredCleanupCompletion.IsCompleted
                || _producerOwnershipUncertain
                || _leaseReleaseUncertain;
            if (_state is LifecycleState.Stopped or LifecycleState.Denied or LifecycleState.Failed
                && !ownsResources)
            {
                return _stopTask = Task.FromResult(CompletedStop());
            }

            if (_state == LifecycleState.Starting)
            {
                startupCancellation = _startupCancellation;
                _startupCancellationUsers++;
            }

            _state = LifecycleState.Stopping;
            startTask = _startTask!;
            completion = new TaskCompletionSource<LifecycleStopResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        var startupCancellationRequest = startupCancellation is null
            ? Task.FromResult<Exception?>(null)
            : RequestCancellationAsync(startupCancellation, ReleaseStartupCancellationUser);
        _ = CompleteStopAsync(completion!, startTask!, startupCancellationRequest, stopwatch);
        return completion!.Task;
    }

    private async Task RunStartAndPublishAsync(
        TaskCompletionSource<LifecycleStartOutcome> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await RunStartupStagesAsync(cancellationToken).ConfigureAwait(false);
            if (outcome == LifecycleStartOutcome.Started)
            {
                _testHooks?.BeforeStartPublication?.Invoke();
            }

            var stopRequested = false;
            lock (_gate)
            {
                stopRequested = _state == LifecycleState.Stopping;
                if (!stopRequested)
                {
                    _state = outcome == LifecycleStartOutcome.Started
                        ? LifecycleState.Started
                        : LifecycleState.Denied;
                    completion.TrySetResult(outcome);
                }
            }

            if (stopRequested)
            {
                var primary = new OperationCanceledException(cancellationToken);
                var cleanupFailures = await RollbackStartAsync().ConfigureAwait(false);
                CompleteStartFailure(completion, CombinePrimaryAndCleanup(primary, cleanupFailures));
            }
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = await RollbackStartAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_state != LifecycleState.Stopping) { _state = LifecycleState.Failed; }
                completion.TrySetException(CombinePrimaryAndCleanup(primaryFailure, cleanupFailures));
            }
        }
        finally
        {
            MarkStartupFinished();
        }
    }

    private async Task<LifecycleStartOutcome> RunStartupStagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await InvokeOperationAsync(_ => Task.FromResult(_lease.TryAcquire()), cancellationToken)
                .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LifecycleStartOutcome.AlreadyRunning;
        }

        _leaseAcquired = true;
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeOperationAsync(_operations.ValidateSettingsAsync, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeOperationAsync(_operations.PrepareDataRootAsync, cancellationToken).ConfigureAwait(false);
        _dataRootPrepared = true;
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeOperationAsync(_operations.StartProducersAsync, cancellationToken).ConfigureAwait(false);
        _producersStarted = true;
        cancellationToken.ThrowIfCancellationRequested();
        return LifecycleStartOutcome.Started;
    }

    private void CompleteStartFailure(
        TaskCompletionSource<LifecycleStartOutcome> completion,
        Exception failure)
    {
        lock (_gate)
        {
            completion.TrySetException(failure);
        }
    }

    private async Task CompleteStopAsync(
        TaskCompletionSource<LifecycleStopResult> completion,
        Task<LifecycleStartOutcome> startTask,
        Task<Exception?> startupCancellationRequest,
        Stopwatch stopwatch)
    {
        try
        {
            var result = await Task.Factory.StartNew(
                    () => StopCoreAsync(startTask, startupCancellationRequest, stopwatch),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap()
                .ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<LifecycleStopResult> StopCoreAsync(
        Task<LifecycleStartOutcome> startTask,
        Task<Exception?> startupCancellationRequest,
        Stopwatch stopwatch)
    {
        _testHooks?.StopCoordinationStarted?.Invoke();
        var failures = new List<Exception>();
        try
        {
            var cancellationFailure = await startupCancellationRequest
                .WaitAsync(Remaining(stopwatch))
                .ConfigureAwait(false);
            if (cancellationFailure is not null) { failures.Add(cancellationFailure); }
        }
        catch (TimeoutException)
        {
            BeginDeferredStartupCompletion(startTask, startupCancellationRequest);
            return InterruptedStop(failures);
        }

        var started = false;
        try
        {
            started = await startTask.WaitAsync(Remaining(stopwatch)).ConfigureAwait(false)
                == LifecycleStartOutcome.Started;
        }
        catch (TimeoutException)
        {
            BeginDeferredStartupCompletion(startTask, null);
            return InterruptedStop(failures);
        }
        catch (Exception)
        {
            // Startup owns rollback and publishes its failure only after synchronous rollback work is done.
        }

        if (!await AwaitDeferredCleanupWithinBudgetAsync(stopwatch).ConfigureAwait(false))
        {
            return InterruptedStop(failures);
        }

        if (!started)
        {
            AddRetentionFailureIfNeeded(failures);
            SetStopped();
            return FailedOrCompletedStop(failures, _leaseAcquired);
        }

        if (!_leaseAcquired)
        {
            SetStopped();
            return FailedOrCompletedStop(failures, retainedLease: false);
        }

        var stages = CreateNormalStopStages();
        CancellationTokenSource? shutdownCancellation = new();
        try
        {
            for (var index = 0; index < stages.Count; index++)
            {
                var stage = stages[index];
                if (ShouldSkipStage(stage)) { continue; }

                var remaining = Remaining(stopwatch);
                if (remaining <= TimeSpan.Zero)
                {
                    BeginDeferredCleanup(null, stages.Skip(index).ToArray());
                    return InterruptedStop(failures);
                }

                var operation = InvokeOperationAsync(stage.Operation, shutdownCancellation.Token);
                try
                {
                    await operation.WaitAsync(remaining).ConfigureAwait(false);
                    MarkStageCompleted(stage);
                }
                catch (TimeoutException)
                {
                    var cancellationRequest = RequestCancellationAsync(shutdownCancellation);
                    BeginDeferredCleanup(
                        operation,
                        stages.Skip(index + 1).ToArray(),
                        stage,
                        shutdownCancellation,
                        cancellationRequest);
                    shutdownCancellation = null;
                    return InterruptedStop(failures);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    MarkStageFailure(stage, exception);
                }
            }
        }
        finally
        {
            shutdownCancellation?.Dispose();
        }

        AddRetentionFailureIfNeeded(failures);
        _dataRootPrepared = false;
        SetStopped();
        return FailedOrCompletedStop(failures, _leaseAcquired);
    }

    private async Task<IReadOnlyList<Exception>> RollbackStartAsync()
    {
        var failures = new List<Exception>();
        var stages = CreateStartupRollbackStages();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < stages.Count; index++)
        {
            var remaining = Remaining(stopwatch);
            if (remaining <= TimeSpan.Zero)
            {
                BeginDeferredCleanup(null, stages.Skip(index).ToArray());
                failures.Add(new TimeoutException("Lifecycle rollback exceeded the shutdown timeout."));
                return failures;
            }

            var stage = stages[index];
            if (ShouldSkipStage(stage)) { continue; }
            var task = InvokeOperationAsync(stage.Operation, CancellationToken.None);
            try
            {
                await task.WaitAsync(remaining).ConfigureAwait(false);
                MarkStageCompleted(stage);
            }
            catch (TimeoutException)
            {
                BeginDeferredCleanup(task, stages.Skip(index + 1).ToArray(), stage);
                failures.Add(new TimeoutException("Lifecycle rollback exceeded the shutdown timeout."));
                return failures;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                MarkStageFailure(stage, exception);
            }
        }

        return failures;
    }

    private IReadOnlyList<CleanupStage> CreateNormalStopStages()
    {
        var stages = new List<CleanupStage>();
        if (_producersStarted)
        {
            stages.Add(new CleanupStage(_operations.StopProducersAsync, StopsProducers: true));
        }
        stages.Add(new CleanupStage(_operations.DrainWorkAsync));
        stages.Add(new CleanupStage(_operations.FinalizeStoresAsync));
        AddLeaseReleaseStage(stages);
        return stages;
    }

    private IReadOnlyList<CleanupStage> CreateStartupRollbackStages()
    {
        var stages = new List<CleanupStage>();
        if (_producersStarted)
        {
            stages.Add(new CleanupStage(_operations.StopProducersAsync, StopsProducers: true));
        }
        if (_dataRootPrepared)
        {
            stages.Add(new CleanupStage(_operations.RollbackDataRootAsync, ClearsDataRoot: true));
        }
        AddLeaseReleaseStage(stages);
        return stages;
    }

    private void AddLeaseReleaseStage(ICollection<CleanupStage> stages)
    {
        if (_leaseAcquired)
        {
            stages.Add(new CleanupStage(
                _ =>
                {
                    _lease.Release();
                    return Task.CompletedTask;
                },
                ReleasesLease: true));
        }
    }

    private void BeginDeferredStartupCompletion(
        Task<LifecycleStartOutcome> startTask,
        Task<Exception?>? cancellationRequest)
    {
        ComposeDeferredCleanup(async () =>
        {
            if (cancellationRequest is not null)
            {
                var cancellationFailure = await cancellationRequest.ConfigureAwait(false);
                if (cancellationFailure is not null) { AddLateFailure(cancellationFailure); }
            }

            try
            {
                if (await startTask.ConfigureAwait(false) == LifecycleStartOutcome.Started)
                {
                    await RunDeferredStagesAsync(CreateNormalStopStages()).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested by StopAsync is an expected startup outcome.
            }
            catch (Exception exception)
            {
                AddLateFailure(exception);
            }
            if (!_leaseAcquired) { SetStopped(); }
        });
    }

    private void BeginDeferredCleanup(
        Task? current,
        IReadOnlyList<CleanupStage> remaining,
        CleanupStage? currentStage = null,
        CancellationTokenSource? ownedCancellation = null,
        Task<Exception?>? cancellationRequest = null)
    {
        ComposeDeferredCleanup(async () =>
        {
            try
            {
                if (cancellationRequest is not null)
                {
                    var cancellationFailure = await cancellationRequest.ConfigureAwait(false);
                    if (cancellationFailure is not null) { AddLateFailure(cancellationFailure); }
                }

                if (current is not null && currentStage is not null)
                {
                    await ObserveDeferredStageAsync(current, currentStage).ConfigureAwait(false);
                }
                await RunDeferredStagesAsync(remaining).ConfigureAwait(false);
            }
            finally
            {
                ownedCancellation?.Dispose();
            }
        });
    }

    private async Task RunDeferredStagesAsync(IReadOnlyList<CleanupStage> stages)
    {
        foreach (var stage in stages)
        {
            if (ShouldSkipStage(stage)) { continue; }
            await ObserveDeferredStageAsync(
                InvokeOperationAsync(stage.Operation, CancellationToken.None),
                stage).ConfigureAwait(false);
        }
        _dataRootPrepared = false;
        SetStopped();
    }

    private async Task ObserveDeferredStageAsync(Task task, CleanupStage stage)
    {
        try
        {
            await task.ConfigureAwait(false);
            MarkStageCompleted(stage);
        }
        catch (Exception exception)
        {
            AddLateFailure(exception);
            MarkStageFailure(stage, exception);
        }
    }

    private void ComposeDeferredCleanup(Func<Task> cleanup)
    {
        Task composed;
        lock (_gate)
        {
            var prior = _deferredCleanupCompletion;
            composed = Task.Run(async () =>
            {
                await prior.ConfigureAwait(false);
                await cleanup().ConfigureAwait(false);
            });
            ObserveFault(composed);
            _deferredCleanupCompletion = composed;
        }
        _testHooks?.DeferredCleanupComposed?.Invoke();
    }

    private async Task<bool> AwaitDeferredCleanupWithinBudgetAsync(Stopwatch stopwatch)
    {
        while (true)
        {
            var observed = DeferredCleanupCompletion;
            try
            {
                await observed.WaitAsync(Remaining(stopwatch)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }

            lock (_gate)
            {
                if (ReferenceEquals(observed, _deferredCleanupCompletion)) { return true; }
            }
        }
    }

    private void MarkStageCompleted(CleanupStage stage)
    {
        if (stage.StopsProducers) { _producersStarted = false; }
        if (stage.ClearsDataRoot) { _dataRootPrepared = false; }
        if (stage.ReleasesLease)
        {
            _leaseAcquired = false;
            _leaseReleaseUncertain = false;
        }
    }

    private void MarkStageFailure(CleanupStage stage, Exception exception)
    {
        if (stage.StopsProducers)
        {
            _producerOwnershipUncertain = true;
            _retentionFailure ??= exception;
        }
        if (stage.ReleasesLease)
        {
            _leaseReleaseUncertain = true;
            _retentionFailure ??= exception;
        }
    }

    private bool ShouldSkipStage(CleanupStage stage) =>
        stage.ReleasesLease && _producerOwnershipUncertain;

    private void AddRetentionFailureIfNeeded(ICollection<Exception> failures)
    {
        if (_leaseAcquired && _retentionFailure is not null && !failures.Contains(_retentionFailure))
        {
            failures.Add(_retentionFailure);
        }
    }

    private void MarkStartupFinished()
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            _startupFinished = true;
            if (_startupCancellationUsers == 0)
            {
                dispose = _startupCancellation;
                _startupCancellation = null;
            }
        }
        dispose?.Dispose();
    }

    private void ReleaseStartupCancellationUser()
    {
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            _startupCancellationUsers--;
            if (_startupFinished && _startupCancellationUsers == 0)
            {
                dispose = _startupCancellation;
                _startupCancellation = null;
            }
        }
        dispose?.Dispose();
    }

    private static Task InvokeOperationAsync(Func<CancellationToken, Task> operation, CancellationToken token) =>
        Task.Factory.StartNew(
                () => operation(token),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    private static Task<T> InvokeOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        Task.Factory.StartNew(
                () => operation(token),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    private static Task<Exception?> RequestCancellationAsync(
        CancellationTokenSource cancellation,
        Action? completed = null) =>
        Task.Factory.StartNew(
            () =>
            {
                try
                {
                    cancellation.Cancel();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
                finally
                {
                    completed?.Invoke();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private TimeSpan Remaining(Stopwatch stopwatch)
    {
        var remaining = _shutdownTimeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void AddLateFailure(Exception exception) { lock (_gate) { _lateFailures.Add(exception); } }
    private void SetStopped() { lock (_gate) { _state = LifecycleState.Stopped; } }

    private static LifecycleStopResult CompletedStop() =>
        new(LifecycleStopOutcome.Completed, false, null, []);

    private static LifecycleStopResult InterruptedStop(IReadOnlyList<Exception>? failures = null)
    {
        failures ??= [];
        return new(
            LifecycleStopOutcome.Interrupted,
            true,
            failures.Count == 0 ? null : failures[0],
            failures.Count <= 1 ? [] : failures.Skip(1).ToArray(),
            LeaseRetainedForDeferredCleanup: true);
    }

    private static LifecycleStopResult FailedOrCompletedStop(
        IReadOnlyList<Exception> failures,
        bool retainedLease) =>
        new(
            failures.Count == 0 && !retainedLease ? LifecycleStopOutcome.Completed : LifecycleStopOutcome.Failed,
            false,
            failures.Count == 0 ? null : failures[0],
            failures.Count <= 1 ? [] : failures.Skip(1).ToArray(),
            retainedLease);

    private static Exception CombinePrimaryAndCleanup(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures) =>
        cleanupFailures.Count == 0
            ? primaryFailure
            : new AggregateException(
                "ApexLab startup failed and lifecycle cleanup also reported failures.",
                new[] { primaryFailure }.Concat(cleanupFailures));

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
