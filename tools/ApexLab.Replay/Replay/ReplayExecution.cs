using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;

namespace ApexLab.Replay.Replay;

internal static class ReplayExecution
{
    public static async Task<ReplayExecutionResult> ExecuteAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        RawReplayOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(options);

        RawEvidenceCapture? unownedCapture = null;
        try
        {
            unownedCapture = await RawEvidenceReader.OpenAsync(
                paths,
                captureId,
                cancellationToken).ConfigureAwait(false);
            if (!ReplayProtocolRegistry.TryResolve(
                    unownedCapture.Completion.ProtocolId.Value,
                    out var adapter))
            {
                throw new ReplayUnsupportedProtocolException();
            }

            var completion = unownedCapture.Completion;
            var observer = new ReplayObservationAggregator();
            await using var source = new RawReplayDatagramSource(
                unownedCapture,
                options);
            unownedCapture = null;
            var coordinator = new CaptureIngestionCoordinator(
                source,
                adapter,
                SenderPolicy.LoopbackOnly,
                observer);
            try
            {
                await coordinator.RunAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new ReplayExecutionInterruptedException(
                    adapter.ProtocolId,
                    options,
                    coordinator.Counters);
            }

            return new ReplayExecutionResult(
                adapter.ProtocolId,
                options.TimingMode,
                options.SpeedPermille,
                completion.RecordCount,
                coordinator.Counters,
                observer.SequenceGapCount,
                observer.BuildDescriptors());
        }
        finally
        {
            if (unownedCapture is not null)
            {
                try
                {
                    await unownedCapture.DisposeAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Reader disposal already attempts every retained handle.
                }
            }
        }
    }
}

internal sealed record ReplayExecutionResult(
    string ProtocolId,
    RawReplayTimingMode TimingMode,
    int SpeedPermille,
    long RecordCount,
    CaptureIngestionCounters Counters,
    long SequenceGapCount,
    IReadOnlyList<ReplayDescriptorObservation> Descriptors);

internal sealed class ReplayUnsupportedProtocolException : Exception;

internal sealed class ReplayExecutionInterruptedException : Exception
{
    public ReplayExecutionInterruptedException(
        string protocolId,
        RawReplayOptions options,
        CaptureIngestionCounters counters)
    {
        ProtocolId = protocolId;
        Options = options;
        Counters = counters;
    }

    public string ProtocolId { get; }

    public RawReplayOptions Options { get; }

    public CaptureIngestionCounters Counters { get; }
}
