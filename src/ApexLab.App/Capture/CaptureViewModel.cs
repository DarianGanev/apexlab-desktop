using System.Windows.Input;
using ApexLab.App.Presentation;
using ApexLab.Application.Capture;

namespace ApexLab.App.Capture;

public sealed class CaptureViewModel :
    ObservableObject,
    IDisposable
{
    private sealed record CaptureViewProjection(
        CaptureWorkflowSnapshot Snapshot,
        CaptureStatusPresentation Presentation,
        string ReceivedText,
        string CompatibleText,
        string ActivePendingText,
        string DeferredPendingText,
        string WrittenText,
        string FinalizedText,
        bool IsProvisional,
        string ProvisionalText);

    private readonly ICaptureWorkflow _workflow;
    private readonly Func<CaptureWorkflowSnapshot, CaptureStatusPresentation>
        _projectStatus;
    private readonly SynchronizationContext? _synchronizationContext;
    private CaptureWorkflowSnapshot _snapshot;
    private string _statusText;
    private string _receivedText;
    private string _compatibleText;
    private string _activePendingText;
    private string _deferredPendingText;
    private string _writtenText;
    private string _finalizedText;
    private string _diagnosticText;
    private string _nextStepText;
    private string _durabilityText;
    private string _provisionalText;
    private bool _isProvisional;
    private Task? _observedDeferredCleanup;
    private int _armRunning;
    private int _stopRunning;
    private int _disposed;

    public CaptureViewModel(ICaptureWorkflow workflow)
        : this(workflow, CaptureStatusText.For)
    {
    }

    internal CaptureViewModel(
        ICaptureWorkflow workflow,
        Func<CaptureWorkflowSnapshot, CaptureStatusPresentation>
            projectStatus)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(projectStatus);
        _workflow = workflow;
        _projectStatus = projectStatus;
        _synchronizationContext = SynchronizationContext.Current;
        _snapshot = workflow.Snapshot;
        var projection = Project(_snapshot);
        _statusText = projection.Presentation.StatusText;
        _receivedText = projection.ReceivedText;
        _compatibleText = projection.CompatibleText;
        _activePendingText = projection.ActivePendingText;
        _deferredPendingText = projection.DeferredPendingText;
        _writtenText = projection.WrittenText;
        _finalizedText = projection.FinalizedText;
        _diagnosticText = projection.Presentation.DiagnosticText;
        _nextStepText = projection.Presentation.NextStepText;
        _durabilityText = projection.Presentation.DurabilityText;
        _isProvisional = projection.IsProvisional;
        _provisionalText = projection.ProvisionalText;
        ArmCommand = new RelayCommand(
            StartArm,
            () => _armRunning == 0 && _snapshot.CanArm);
        StopCommand = new RelayCommand(
            StartStop,
            () => _stopRunning == 0 && _snapshot.CanStop);
        ResetCommand = new RelayCommand(
            Reset,
            CanReset);
        workflow.SnapshotChanged += OnSnapshotChanged;
        ObserveDeferredCleanup(_snapshot);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ReceivedText
    {
        get => _receivedText;
        private set => SetProperty(ref _receivedText, value);
    }

    public string CompatibleText
    {
        get => _compatibleText;
        private set => SetProperty(ref _compatibleText, value);
    }

    public string ActivePendingText
    {
        get => _activePendingText;
        private set => SetProperty(ref _activePendingText, value);
    }

    public string DeferredPendingText
    {
        get => _deferredPendingText;
        private set => SetProperty(ref _deferredPendingText, value);
    }

    public string WrittenText
    {
        get => _writtenText;
        private set => SetProperty(ref _writtenText, value);
    }

    public string FinalizedText
    {
        get => _finalizedText;
        private set => SetProperty(ref _finalizedText, value);
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public string NextStepText
    {
        get => _nextStepText;
        private set => SetProperty(ref _nextStepText, value);
    }

    public string DurabilityText
    {
        get => _durabilityText;
        private set => SetProperty(ref _durabilityText, value);
    }

    public string ProvisionalText
    {
        get => _provisionalText;
        private set => SetProperty(ref _provisionalText, value);
    }

    public bool IsProvisional
    {
        get => _isProvisional;
        private set => SetProperty(ref _isProvisional, value);
    }

    public RelayCommand ArmCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ResetCommand { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workflow.SnapshotChanged -= OnSnapshotChanged;
    }

    private void StartArm()
    {
        if (Interlocked.Exchange(ref _armRunning, 1) != 0)
        {
            return;
        }

        RaiseCommandState();
        _ = CompleteOperationAsync(
            () => _workflow.ArmAsync(),
            () => Interlocked.Exchange(ref _armRunning, 0));
    }

    private void StartStop()
    {
        if (Interlocked.Exchange(ref _stopRunning, 1) != 0)
        {
            return;
        }

        RaiseCommandState();
        _ = CompleteOperationAsync(
            () => _workflow.StopAsync(CaptureStopReason.User),
            () => Interlocked.Exchange(ref _stopRunning, 0));
    }

    private void Reset()
    {
        if (!CanReset())
        {
            return;
        }

        try
        {
            _workflow.Reset();
        }
        catch (InvalidOperationException)
        {
            // Ownership changed after the command check; the workflow remains authoritative.
        }
        finally
        {
            RaiseCommandState();
        }
    }

    private async Task CompleteOperationAsync(
        Func<Task<CaptureWorkflowSnapshot>> operation,
        Action release)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The workflow publishes privacy-safe failure state.
        }
        finally
        {
            release();
            Post(RaiseCommandState);
        }
    }

    private void OnSnapshotChanged(
        object? sender,
        CaptureWorkflowSnapshot snapshot)
    {
        var projection = Project(snapshot);
        Post(() => Apply(projection));
    }

    private void Apply(CaptureViewProjection projection)
    {
        var snapshot = projection.Snapshot;
        _snapshot = snapshot;
        StatusText = projection.Presentation.StatusText;
        ReceivedText = projection.ReceivedText;
        CompatibleText = projection.CompatibleText;
        ActivePendingText = projection.ActivePendingText;
        DeferredPendingText = projection.DeferredPendingText;
        WrittenText = projection.WrittenText;
        FinalizedText = projection.FinalizedText;
        DiagnosticText = projection.Presentation.DiagnosticText;
        NextStepText = projection.Presentation.NextStepText;
        DurabilityText = projection.Presentation.DurabilityText;
        IsProvisional = projection.IsProvisional;
        ProvisionalText = projection.ProvisionalText;
        ObserveDeferredCleanup(snapshot);
        RaiseCommandState();
    }

    private CaptureViewProjection Project(
        CaptureWorkflowSnapshot snapshot) =>
        new(
            snapshot,
            _projectStatus(snapshot),
            Count(snapshot.Counters.Source.DatagramsObserved),
            Count(snapshot.Counters.Classifier.Compatible),
            Count(snapshot.Counters.Evidence.SinkPending),
            Count(
                snapshot.Counters.Evidence
                    .SinkPendingDeferredCleanup),
            Count(snapshot.Counters.Evidence.SinkWritten),
            Count(snapshot.Counters.Evidence.FinalizedRecords),
            snapshot.IsProvisional,
            Provisional(snapshot));

    private void RaiseCommandState()
    {
        ArmCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
    }

    private bool CanReset() =>
        _snapshot.CanReset
        && _workflow.DeferredCleanupCompletion.IsCompleted;

    private void ObserveDeferredCleanup(
        CaptureWorkflowSnapshot snapshot)
    {
        if (snapshot.State != CaptureState.Faulted)
        {
            return;
        }

        var deferred = _workflow.DeferredCleanupCompletion;
        if (ReferenceEquals(deferred, _observedDeferredCleanup))
        {
            return;
        }

        _observedDeferredCleanup = deferred;
        _ = RefreshResetAfterCleanupAsync(deferred);
    }

    private async Task RefreshResetAfterCleanupAsync(Task deferred)
    {
        try
        {
            await deferred.ConfigureAwait(false);
        }
        catch
        {
            // A cleanup failure is already represented by the workflow fault state.
        }

        Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0
                && ReferenceEquals(
                    deferred,
                    _observedDeferredCleanup))
            {
                RaiseCommandState();
            }
        });
    }

    private void Post(Action action)
    {
        if (_synchronizationContext is null
            || ReferenceEquals(
                SynchronizationContext.Current,
                _synchronizationContext))
        {
            action();
            return;
        }

        _synchronizationContext.Post(
            static state => ((Action)state!).Invoke(),
            action);
    }

    private static string Provisional(
        CaptureWorkflowSnapshot snapshot) =>
        snapshot.IsProvisional
            ? "Provisional: deferred cleanup is still resolving and counts may still change."
            : "Counters are resolved for the current capture state.";

    private static string Count(long value) =>
        value.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
}
