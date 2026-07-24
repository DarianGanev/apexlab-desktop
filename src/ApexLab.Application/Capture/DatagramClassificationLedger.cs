using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Capture;

internal sealed class DatagramClassificationLedger
{
    private readonly object _gate = new();
    private long _sourceDequeued;
    private long _compatible;
    private long _malformedHeader;
    private long _unsupportedFormat;
    private long _unsupportedYear;
    private long _unknownPacketId;
    private long _unsupportedPacketVersion;
    private long _invalidPacketLength;
    private long _excludedPrivacyPacket;
    private long _unexpectedSender;
    private long _classifierAbandonedOnTermination;
    private long _sinkWritten;
    private long _sinkWriteFailed;
    private long _sinkPending;
    private long _sinkPendingDeferredCleanup;
    private long _finalizedRecords;
    private long _stagedRecords;

    public void Record(
        TelemetryPacketClassification classification,
        bool trackCompatibleEvidence = false)
    {
        lock (_gate)
        {
            ref var counter = ref CounterFor(classification);
            counter = checked(counter + 1);
            _sourceDequeued = checked(_sourceDequeued + 1);
            if (trackCompatibleEvidence
                && classification == TelemetryPacketClassification.Compatible)
            {
                _sinkPending = checked(_sinkPending + 1);
            }
        }
    }

    public void RecordEvidenceWritten()
    {
        lock (_gate)
        {
            RequirePendingEvidence();
            _sinkWritten = checked(_sinkWritten + 1);
            _stagedRecords = checked(_stagedRecords + 1);
        }
    }

    public void RecordEvidenceWriteFailed()
    {
        lock (_gate)
        {
            RequirePendingEvidence();
            _sinkWriteFailed = checked(_sinkWriteFailed + 1);
        }
    }

    public void RecordEvidenceFinalized(long recordCount)
    {
        CounterMath.RequireNonNegative(recordCount, nameof(recordCount));
        lock (_gate)
        {
            if (_sinkPending != 0
                || _sinkPendingDeferredCleanup != 0
                || _finalizedRecords != 0
                || _stagedRecords != recordCount
                || _sinkWritten != recordCount)
            {
                throw new InvalidOperationException(
                    "Evidence can be finalized only after every successful write is staged.");
            }

            _finalizedRecords = _stagedRecords;
            _stagedRecords = 0;
        }
    }

    public void TransferPendingEvidenceToDeferredCleanup()
    {
        lock (_gate)
        {
            _sinkPendingDeferredCleanup = checked(
                _sinkPendingDeferredCleanup + _sinkPending);
            _sinkPending = 0;
        }
    }

    public void RecordAbandoned(long count)
    {
        CounterMath.RequireNonNegative(count, nameof(count));
        if (count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _classifierAbandonedOnTermination = checked(
                _classifierAbandonedOnTermination + count);
        }
    }

    public DatagramClassificationCounters Snapshot()
    {
        lock (_gate)
        {
            return CreateClassificationSnapshot();
        }
    }

    public (
        DatagramClassificationCounters Classifier,
        EvidenceSinkCounters Evidence) CaptureSnapshot()
    {
        lock (_gate)
        {
            return (
                CreateClassificationSnapshot(),
                new EvidenceSinkCounters(
                    _sinkWritten,
                    _sinkWriteFailed,
                    _sinkPending,
                    _sinkPendingDeferredCleanup,
                    _finalizedRecords,
                    _stagedRecords));
        }
    }

    private ref long CounterFor(TelemetryPacketClassification classification)
    {
        switch (classification)
        {
            case TelemetryPacketClassification.Compatible:
                return ref _compatible;
            case TelemetryPacketClassification.MalformedHeader:
                return ref _malformedHeader;
            case TelemetryPacketClassification.UnsupportedFormat:
                return ref _unsupportedFormat;
            case TelemetryPacketClassification.UnsupportedYear:
                return ref _unsupportedYear;
            case TelemetryPacketClassification.UnknownPacketId:
                return ref _unknownPacketId;
            case TelemetryPacketClassification.UnsupportedPacketVersion:
                return ref _unsupportedPacketVersion;
            case TelemetryPacketClassification.InvalidPacketLength:
                return ref _invalidPacketLength;
            case TelemetryPacketClassification.ExcludedPrivacyPacket:
                return ref _excludedPrivacyPacket;
            case TelemetryPacketClassification.UnexpectedSender:
                return ref _unexpectedSender;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(classification),
                    classification,
                    "A known classifier outcome is required.");
        }
    }

    private DatagramClassificationCounters CreateClassificationSnapshot() =>
        new(
            _sourceDequeued,
            _compatible,
            _malformedHeader,
            _unsupportedFormat,
            _unsupportedYear,
            _unknownPacketId,
            _unsupportedPacketVersion,
            _invalidPacketLength,
            _excludedPrivacyPacket,
            _unexpectedSender,
            _classifierAbandonedOnTermination);

    private void RequirePendingEvidence()
    {
        if (_sinkPending > 0)
        {
            _sinkPending--;
            return;
        }

        if (_sinkPendingDeferredCleanup > 0)
        {
            _sinkPendingDeferredCleanup--;
            return;
        }

        throw new InvalidOperationException(
            "An evidence completion requires a pending compatible datagram.");
    }
}
