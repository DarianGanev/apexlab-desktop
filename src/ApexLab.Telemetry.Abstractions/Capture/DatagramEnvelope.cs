namespace ApexLab.Telemetry.Abstractions.Capture;

public sealed class DatagramEnvelope
{
    private readonly byte[] _payload;

    private DatagramEnvelope(
        long sequence,
        long monotonicTimestamp,
        DateTimeOffset receivedAtUtc,
        DatagramSender sender,
        byte[] payloadSnapshot)
    {
        Sequence = sequence;
        MonotonicTimestamp = monotonicTimestamp;
        ReceivedAtUtc = receivedAtUtc;
        Sender = sender;
        _payload = payloadSnapshot;
    }

    public long Sequence { get; }

    public long MonotonicTimestamp { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public DatagramSender Sender { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public static DatagramEnvelope CopyFrom(
        long sequence,
        long monotonicTimestamp,
        DateTimeOffset receivedAtUtc,
        DatagramSender sender,
        ReadOnlySpan<byte> payload)
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
                "A receive timestamp must use the UTC offset.",
                nameof(receivedAtUtc));
        }

        if (!sender.IsSpecified)
        {
            throw new ArgumentException(
                "A UDP sender must be specified.",
                nameof(sender));
        }

        if (payload.Length > UdpDatagramLimits.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A UDP payload cannot exceed {UdpDatagramLimits.MaximumPayloadLength} bytes.");
        }

        return new DatagramEnvelope(
            sequence,
            monotonicTimestamp,
            receivedAtUtc,
            sender,
            payload.ToArray());
    }
}
