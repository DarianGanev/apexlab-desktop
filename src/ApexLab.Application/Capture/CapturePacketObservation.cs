using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Capture;

public readonly record struct CapturePacketObservation
{
    public CapturePacketObservation(
        long sequence,
        long monotonicTimestamp,
        DateTimeOffset receivedAtUtc,
        int datagramLength,
        TelemetryPacketResult result)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                "A capture sequence must be positive.");
        }

        if (monotonicTimestamp < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monotonicTimestamp),
                "A monotonic timestamp cannot be negative.");
        }

        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An observation timestamp must use the UTC offset.",
                nameof(receivedAtUtc));
        }

        if (datagramLength is < 0 or > UdpDatagramLimits.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(datagramLength),
                "An observed UDP datagram length must be between 0 and 65507 bytes.");
        }

        if (result.Classification == TelemetryPacketClassification.Unspecified
            || !Enum.IsDefined(result.Classification))
        {
            throw new ArgumentException(
                "An observation requires a known packet classification.",
                nameof(result));
        }

        Sequence = sequence;
        MonotonicTimestamp = monotonicTimestamp;
        ReceivedAtUtc = receivedAtUtc;
        DatagramLength = datagramLength;
        Result = result;
    }

    public long Sequence { get; }

    public long MonotonicTimestamp { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public int DatagramLength { get; }

    public TelemetryPacketResult Result { get; }
}

public interface ICapturePacketObserver
{
    void Observe(CapturePacketObservation observation);
}
