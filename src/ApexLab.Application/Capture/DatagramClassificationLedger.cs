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
    private long _classifierAbandonedOnInterrupt;

    public void Record(TelemetryPacketClassification classification)
    {
        lock (_gate)
        {
            ref var counter = ref CounterFor(classification);
            counter = checked(counter + 1);
            _sourceDequeued = checked(_sourceDequeued + 1);
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
            _classifierAbandonedOnInterrupt = checked(
                _classifierAbandonedOnInterrupt + count);
        }
    }

    public DatagramClassificationCounters Snapshot()
    {
        lock (_gate)
        {
            return new(
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
                _classifierAbandonedOnInterrupt);
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
}
