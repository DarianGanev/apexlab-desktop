using System.Windows.Input;
using ApexLab.App.Presentation;
using ApexLab.Application.Capture;

namespace ApexLab.App.Capture;

public sealed class CaptureViewModel :
    ObservableObject,
    IDisposable
{
    private readonly ICaptureWorkflow _workflow;
    private readonly SynchronizationContext? _synchronizationContext;
    private CaptureWorkflowSnapshot _snapshot;
    private string _statusText;
    private string _receivedText;
    private string _compatibleText;
    private string _pendingText;
    private string _writtenText;
    private string _finalizedText;
    private int _armRunning;
    private int _stopRunning;
    private int _disposed;

    public CaptureViewModel(ICaptureWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
        _synchronizationContext = SynchronizationContext.Current;
        _snapshot = workflow.Snapshot;
        _statusText = Status(_snapshot);
        _receivedText = Count(
            _snapshot.Counters.Source.DatagramsObserved);
        _compatibleText = Count(
            _snapshot.Counters.Classifier.Compatible);
        _pendingText = Count(
            _snapshot.Counters.Evidence.SinkPending
            + _snapshot.Counters.Evidence.SinkPendingDeferredCleanup);
        _writtenText = Count(
            _snapshot.Counters.Evidence.SinkWritten);
        _finalizedText = Count(
            _snapshot.Counters.Evidence.FinalizedRecords);
        ArmCommand = new RelayCommand(
            StartArm,
            () => _armRunning == 0 && _snapshot.CanArm);
        StopCommand = new RelayCommand(
            StartStop,
            () => _stopRunning == 0 && _snapshot.CanStop);
        workflow.SnapshotChanged += OnSnapshotChanged;
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

    public string PendingText
    {
        get => _pendingText;
        private set => SetProperty(ref _pendingText, value);
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

    public RelayCommand ArmCommand { get; }

    public RelayCommand StopCommand { get; }

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
        CaptureWorkflowSnapshot snapshot) =>
        Post(() => Apply(snapshot));

    private void Apply(CaptureWorkflowSnapshot snapshot)
    {
        _snapshot = snapshot;
        StatusText = Status(snapshot);
        ReceivedText = Count(
            snapshot.Counters.Source.DatagramsObserved);
        CompatibleText = Count(
            snapshot.Counters.Classifier.Compatible);
        PendingText = Count(
            snapshot.Counters.Evidence.SinkPending
            + snapshot.Counters.Evidence.SinkPendingDeferredCleanup);
        WrittenText = Count(
            snapshot.Counters.Evidence.SinkWritten);
        FinalizedText = Count(
            snapshot.Counters.Evidence.FinalizedRecords);
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        ArmCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
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

    private static string Status(CaptureWorkflowSnapshot snapshot) =>
        CaptureStatusText.For(
            snapshot.State,
            snapshot.FailureKind);

    private static string Count(long value) =>
        value.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
}
