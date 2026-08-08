using ApexLab.Application.Capture;

namespace ApexLab.Replay.Replay;

internal sealed class ReplayObservationAggregator : ICapturePacketObserver
{
    private readonly Dictionary<ReplayDescriptorKey, long> _descriptors = [];
    private long? _lastSequence;

    public long SequenceGapCount { get; private set; }

    public void Observe(CapturePacketObservation observation)
    {
        if (_lastSequence is { } previous)
        {
            if (observation.Sequence <= previous)
            {
                throw new InvalidDataException(
                    "Replay evidence sequence order is invalid.");
            }

            SequenceGapCount = checked(
                SequenceGapCount
                + observation.Sequence
                - previous
                - 1);
        }

        _lastSequence = observation.Sequence;
        if (observation.Result.Descriptor is not { } descriptor)
        {
            return;
        }

        var key = new ReplayDescriptorKey(
            descriptor.PacketId,
            descriptor.PacketVersion,
            observation.DatagramLength);
        _descriptors.TryGetValue(key, out var count);
        _descriptors[key] = checked(count + 1);
    }

    public IReadOnlyList<ReplayDescriptorObservation> BuildDescriptors() =>
        _descriptors
            .OrderBy(pair => pair.Key.PacketId)
            .ThenBy(pair => pair.Key.PacketVersion)
            .ThenBy(pair => pair.Key.DatagramLength)
            .Select(pair => new ReplayDescriptorObservation(
                pair.Key.PacketId,
                pair.Key.PacketVersion,
                pair.Key.DatagramLength,
                pair.Value))
            .ToArray();

    private readonly record struct ReplayDescriptorKey(
        byte PacketId,
        byte PacketVersion,
        int DatagramLength);
}

internal sealed record ReplayDescriptorObservation(
    byte PacketId,
    byte PacketVersion,
    int DatagramLength,
    long Count);
