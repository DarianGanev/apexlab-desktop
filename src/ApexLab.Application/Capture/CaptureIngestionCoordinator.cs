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
    private readonly DatagramClassificationLedger _ledger = new();
    private int _runStarted;

    public CaptureIngestionCoordinator(
        IDatagramSource source,
        ITelemetryProtocolAdapter adapter,
        SenderPolicy senderPolicy,
        ICapturePacketObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(senderPolicy);

        _source = source;
        _adapter = adapter;
        _senderPolicy = senderPolicy;
        _observer = observer;
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
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new CaptureSourceStartupException(exception);
            }

            await foreach (var envelope in _source.Output
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Process(envelope);
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

    private void Process(DatagramEnvelope envelope)
    {
        var result = _senderPolicy.IsExpected(envelope.Sender)
            ? _adapter.Inspect(envelope.Payload.Span)
            : TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnexpectedSender);

        _ledger.Record(result.Classification);
        _observer?.Observe(
            new CapturePacketObservation(
                envelope.Sequence,
                envelope.MonotonicTimestamp,
                envelope.ReceivedAtUtc,
                envelope.Payload.Length,
                result));
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
