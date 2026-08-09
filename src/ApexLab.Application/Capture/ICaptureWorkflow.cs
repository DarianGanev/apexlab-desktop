using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Capture;

public interface ICaptureWorkflow : IAsyncDisposable
{
    CaptureWorkflowSnapshot Snapshot { get; }

    Task DeferredCleanupCompletion { get; }

    event EventHandler<CaptureWorkflowSnapshot>? SnapshotChanged;

    Task<CaptureWorkflowSnapshot> ArmAsync(
        CancellationToken cancellationToken = default);

    Task<CaptureWorkflowSnapshot> StopAsync(
        CaptureStopReason reason,
        CancellationToken cancellationToken = default);

    void BeginStop(CaptureStopReason reason);

    Task StopProducersAsync(
        CancellationToken cancellationToken = default);

    Task DrainWorkAsync(
        CancellationToken cancellationToken = default);

    Task FinalizeStoresAsync(
        CancellationToken cancellationToken = default);

    void Reset();
}

public interface ICaptureSessionFactory
{
    Task<CaptureSessionComponents> CreateAsync(
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken);
}

public sealed class CaptureSessionComponents
{
    public CaptureSessionComponents(
        IDatagramSource source,
        ITelemetryProtocolAdapter adapter,
        SenderPolicy senderPolicy,
        IRawEvidenceStore evidenceStore)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(senderPolicy);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        if (!string.Equals(
                adapter.ProtocolId,
                evidenceStore.ProtocolId.Value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The capture adapter and evidence store must use the same protocol.",
                nameof(evidenceStore));
        }

        Source = source;
        Adapter = adapter;
        SenderPolicy = senderPolicy;
        EvidenceStore = evidenceStore;
    }

    public IDatagramSource Source { get; }

    public ITelemetryProtocolAdapter Adapter { get; }

    public SenderPolicy SenderPolicy { get; }

    public IRawEvidenceStore EvidenceStore { get; }
}
