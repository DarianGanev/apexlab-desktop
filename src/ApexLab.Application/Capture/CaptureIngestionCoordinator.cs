using System.Runtime.ExceptionServices;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Capture;

public sealed class CaptureIngestionCoordinator
{
    private readonly IDatagramSource _source;
    private readonly ITelemetryProtocolAdapter _adapter;
    private readonly SenderPolicy _senderPolicy;
    private readonly ICapturePacketObserver? _observer;
    private readonly IRawEvidenceStore? _evidenceStore;
    private readonly DatagramClassificationLedger _ledger = new();
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _finalizeGate = new();
    private Task<RawEvidenceCompletion>? _finalizationTask;
    private int _runStarted;

    public CaptureIngestionCoordinator(
        IDatagramSource source,
        ITelemetryProtocolAdapter adapter,
        SenderPolicy senderPolicy,
        ICapturePacketObserver? observer = null,
        IRawEvidenceStore? evidenceStore = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(senderPolicy);

        _source = source;
        _adapter = adapter;
        _senderPolicy = senderPolicy;
        _observer = observer;
        _evidenceStore = evidenceStore;
    }

    public CaptureIngestionCounters Counters
    {
        get
        {
            // Classification can advance only after source admission. Reading the
            // ledger first therefore yields a compatible pair of point-in-time
            // snapshots even if admission advances before source counters are read.
            var classifier = _ledger.Snapshot();
            return new(_source.Counters, classifier);
        }
    }

    public CaptureCounters CaptureCounters
    {
        get
        {
            if (_evidenceStore is null)
            {
                throw new InvalidOperationException(
                    "This coordinator was created without an evidence store.");
            }

            var snapshot = _ledger.CaptureSnapshot();
            return new CaptureCounters(
                _source.Counters,
                snapshot.Classifier,
                snapshot.Evidence);
        }
    }

    public Task Started => _started.Task;

    public Task<RawEvidenceCompletion> FinalizeEvidenceAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_finalizeGate)
        {
            if (_evidenceStore is null)
            {
                return Task.FromException<RawEvidenceCompletion>(
                    new InvalidOperationException(
                        "This coordinator was created without an evidence store."));
            }

            return _finalizationTask ??=
                FinalizeEvidenceCoreAsync(
                    _evidenceStore,
                    cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A capture ingestion coordinator instance can run only once.");
        }

        Exception? processingFailure = null;
        Exception? stopFailure = null;
        try
        {
            try
            {
                await _source.StartAsync(cancellationToken).ConfigureAwait(false);
                _started.TrySetResult();
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _started.TrySetCanceled(cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                var startupFailure =
                    new CaptureSourceStartupException(exception);
                _started.TrySetException(startupFailure);
                throw startupFailure;
            }

            await foreach (var envelope in _source.Output
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessAsync(
                        envelope,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            processingFailure = exception;
        }

        try
        {
            await _source.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        Exception? accountingFailure = null;
        try
        {
            TransferUnreadBacklogToAbandonment();
        }
        catch (Exception exception)
        {
            accountingFailure = exception;
        }

        ThrowFailures(processingFailure, stopFailure, accountingFailure);
    }

    private async ValueTask ProcessAsync(
        DatagramEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var result = _senderPolicy.IsExpected(envelope.Sender)
            ? _adapter.Inspect(envelope.Payload.Span)
            : TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnexpectedSender);

        _ledger.Record(
            result.Classification,
            trackCompatibleEvidence: _evidenceStore is not null);
        _observer?.Observe(
            new CapturePacketObservation(
                envelope.Sequence,
                envelope.MonotonicTimestamp,
                envelope.ReceivedAtUtc,
                envelope.Payload.Length,
                result));
        if (result.Classification == TelemetryPacketClassification.Compatible
            && _evidenceStore is not null)
        {
            try
            {
                await _evidenceStore.WriteAsync(
                        envelope,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ledger.RecordEvidenceWritten();
            }
            catch
            {
                _ledger.RecordEvidenceWriteFailed();
                throw;
            }
        }
    }

    private async Task<RawEvidenceCompletion> FinalizeEvidenceCoreAsync(
        IRawEvidenceStore evidenceStore,
        CancellationToken cancellationToken)
    {
        var completion = await evidenceStore.FinalizeAsync(
                cancellationToken)
            .ConfigureAwait(false);
        _ledger.RecordEvidenceFinalized(completion.RecordCount);
        return completion;
    }

    private void TransferUnreadBacklogToAbandonment()
    {
        var sourceEnqueued = _source.Counters.SourceEnqueued;
        var classifier = _ledger.Snapshot();
        var alreadyAccounted = checked(
            classifier.SourceDequeued
            + classifier.ClassifierAbandonedOnTermination);
        if (alreadyAccounted > sourceEnqueued)
        {
            throw new InvalidOperationException(
                "Classifier accounting exceeded source admission.");
        }

        _ledger.RecordAbandoned(sourceEnqueued - alreadyAccounted);
    }

    private static void ThrowFailures(params Exception?[] failures)
    {
        var present = failures.Where(failure => failure is not null).ToArray();
        if (present.Length == 0)
        {
            return;
        }

        if (present.Length == 1)
        {
            ExceptionDispatchInfo.Capture(present[0]!).Throw();
        }

        throw new AggregateException(present!);
    }
}
