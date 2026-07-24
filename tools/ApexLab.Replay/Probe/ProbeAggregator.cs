using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Replay.Probe;

internal sealed class ProbeAggregator : ICapturePacketObserver
{
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<PacketShapeKey, long> _packetShapes = [];
    private readonly Dictionary<DescriptorKey, long> _descriptors = [];
    private readonly SortedDictionary<long, long> _rateBuckets = [];
    private readonly HashSet<ulong> _sessionUids = [];
    private readonly Dictionary<HeaderStreamKey, HeaderStreamState> _headerStreams = [];
    private DateTimeOffset? _firstReceivedAtUtc;
    private DateTimeOffset? _lastReceivedAtUtc;
    private long? _lastSequence;
    private long? _lastMonotonicTimestamp;
    private long _sequenceGaps;
    private long _sequenceRegressions;
    private long _monotonicTimestampRegressions;
    private long _utcTimestampRegressions;
    private long _sessionTimeRegressions;
    private long _frameSkippedIdentifierValues;
    private long _frameRegressions;
    private long _overallFrameSkippedIdentifierValues;
    private long _overallFrameRegressions;
    private byte? _playerMinimum;
    private byte? _playerMaximum;
    private byte? _secondaryMinimum;
    private byte? _secondaryMaximum;
    private long _secondaryAbsentCount;

    public void Observe(CapturePacketObservation observation)
    {
        lock (_gate)
        {
            RecordSequence(observation);
            RecordRate(observation.ReceivedAtUtc);
            RecordDescriptor(observation.Result.Descriptor);
            if (observation.Result.Header is { } header)
            {
                RecordHeader(header, observation.DatagramLength);
            }
        }
    }

    public ProbeReport BuildReport(
        string status,
        string protocolId,
        TimeSpan duration,
        CaptureIngestionCounters counters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolId);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(counters);

        lock (_gate)
        {
            var source = counters.Source;
            var classifier = counters.Classifier;
            return new ProbeReport(
                SchemaVersion,
                status,
                protocolId,
                checked((long)duration.TotalMilliseconds),
                new ProbeSourceReport(
                    source.DatagramsObserved,
                    source.SourceEnqueued,
                    source.SourceDroppedFull,
                    source.SourceRejectedOversized,
                    source.SocketErrors),
                new ProbeClassificationReport(
                    classifier.SourceDequeued,
                    classifier.Compatible,
                    classifier.MalformedHeader,
                    classifier.UnsupportedFormat,
                    classifier.UnsupportedYear,
                    classifier.UnknownPacketId,
                    classifier.UnsupportedPacketVersion,
                    classifier.InvalidPacketLength,
                    classifier.ExcludedPrivacyPacket,
                    classifier.UnexpectedSender,
                    classifier.ClassifierAbandonedOnInterrupt),
                _packetShapes
                    .OrderBy(pair => pair.Key.PacketId)
                    .ThenBy(pair => pair.Key.PacketVersion)
                    .ThenBy(pair => pair.Key.DatagramLength)
                    .Select(pair => new ProbePacketShapeReport(
                        pair.Key.PacketId,
                        pair.Key.PacketVersion,
                        pair.Key.DatagramLength,
                        pair.Value))
                    .ToArray(),
                _descriptors
                    .OrderBy(pair => pair.Key.PacketId)
                    .ThenBy(pair => pair.Key.PacketVersion)
                    .ThenBy(pair => pair.Key.DatagramLength)
                    .Select(pair => new ProbeDescriptorReport(
                        pair.Key.PacketId,
                        pair.Key.PacketVersion,
                        pair.Key.DatagramLength,
                        pair.Value))
                    .ToArray(),
                _rateBuckets
                    .Select(pair => new ProbeRateBucketReport(pair.Key, pair.Value))
                    .ToArray(),
                new ProbeSequenceReport(
                    _sequenceGaps,
                    _sequenceRegressions,
                    _monotonicTimestampRegressions,
                    _utcTimestampRegressions),
                new ProbeHeaderReport(
                    _sessionUids.Count,
                    _sessionTimeRegressions,
                    _frameSkippedIdentifierValues,
                    _frameRegressions,
                    _overallFrameSkippedIdentifierValues,
                    _overallFrameRegressions),
                new ProbePlayerIndexReport(
                    _playerMinimum,
                    _playerMaximum,
                    _secondaryMinimum,
                    _secondaryMaximum,
                    _secondaryAbsentCount));
        }
    }

    private void RecordSequence(CapturePacketObservation observation)
    {
        if (_lastSequence.HasValue)
        {
            if (observation.Sequence <= _lastSequence.Value)
            {
                _sequenceRegressions = checked(_sequenceRegressions + 1);
            }
            else if (observation.Sequence > _lastSequence.Value + 1)
            {
                _sequenceGaps = checked(
                    _sequenceGaps + observation.Sequence - _lastSequence.Value - 1);
            }
        }

        if (_lastMonotonicTimestamp.HasValue
            && observation.MonotonicTimestamp < _lastMonotonicTimestamp.Value)
        {
            _monotonicTimestampRegressions = checked(
                _monotonicTimestampRegressions + 1);
        }

        if (_lastReceivedAtUtc.HasValue
            && observation.ReceivedAtUtc < _lastReceivedAtUtc.Value)
        {
            _utcTimestampRegressions = checked(_utcTimestampRegressions + 1);
        }

        _lastSequence = observation.Sequence;
        _lastMonotonicTimestamp = observation.MonotonicTimestamp;
        _lastReceivedAtUtc = observation.ReceivedAtUtc;
    }

    private void RecordRate(DateTimeOffset receivedAtUtc)
    {
        _firstReceivedAtUtc ??= receivedAtUtc;
        var elapsed = receivedAtUtc - _firstReceivedAtUtc.Value;
        var bucket = Math.Max(0, (long)Math.Floor(elapsed.TotalSeconds));
        _rateBuckets.TryGetValue(bucket, out var count);
        _rateBuckets[bucket] = checked(count + 1);
    }

    private void RecordDescriptor(TelemetryPacketDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return;
        }

        var key = new DescriptorKey(
            descriptor.PacketId,
            descriptor.PacketVersion,
            descriptor.DatagramLength);
        _descriptors.TryGetValue(key, out var count);
        _descriptors[key] = checked(count + 1);
    }

    private void RecordHeader(TelemetryHeaderMetadata header, int datagramLength)
    {
        _sessionUids.Add(header.SessionUid);
        RecordRange(header.PlayerCarIndex, ref _playerMinimum, ref _playerMaximum);
        if (header.SecondaryPlayerCarIndex == byte.MaxValue)
        {
            _secondaryAbsentCount = checked(_secondaryAbsentCount + 1);
        }
        else
        {
            RecordRange(
                header.SecondaryPlayerCarIndex,
                ref _secondaryMinimum,
                ref _secondaryMaximum);
        }

        var shape = new PacketShapeKey(
            header.PacketId,
            header.PacketVersion,
            datagramLength);
        _packetShapes.TryGetValue(shape, out var shapeCount);
        _packetShapes[shape] = checked(shapeCount + 1);

        var streamKey = new HeaderStreamKey(header.SessionUid, header.PacketId);
        if (_headerStreams.TryGetValue(streamKey, out var prior))
        {
            if (header.SessionTimeSeconds < prior.SessionTimeSeconds)
            {
                _sessionTimeRegressions = checked(_sessionTimeRegressions + 1);
            }

            RecordFrameDelta(
                prior.FrameIdentifier,
                header.FrameIdentifier,
                ref _frameSkippedIdentifierValues,
                ref _frameRegressions);
            RecordFrameDelta(
                prior.OverallFrameIdentifier,
                header.OverallFrameIdentifier,
                ref _overallFrameSkippedIdentifierValues,
                ref _overallFrameRegressions);
        }

        _headerStreams[streamKey] = new HeaderStreamState(
            header.SessionTimeSeconds,
            header.FrameIdentifier,
            header.OverallFrameIdentifier);
    }

    private static void RecordRange(
        byte value,
        ref byte? minimum,
        ref byte? maximum)
    {
        minimum = !minimum.HasValue || value < minimum.Value
            ? value
            : minimum;
        maximum = !maximum.HasValue || value > maximum.Value
            ? value
            : maximum;
    }

    private static void RecordFrameDelta(
        uint previous,
        uint current,
        ref long skippedIdentifierValues,
        ref long regressions)
    {
        if (current < previous)
        {
            regressions = checked(regressions + 1);
        }
        else if ((ulong)current > (ulong)previous + 1)
        {
            skippedIdentifierValues = checked(
                skippedIdentifierValues + current - previous - 1);
        }
    }

    private readonly record struct PacketShapeKey(
        byte PacketId,
        byte PacketVersion,
        int DatagramLength);

    private readonly record struct DescriptorKey(
        byte PacketId,
        byte PacketVersion,
        int DatagramLength);

    private readonly record struct HeaderStreamKey(
        ulong SessionUid,
        byte PacketId);

    private readonly record struct HeaderStreamState(
        float SessionTimeSeconds,
        uint FrameIdentifier,
        uint OverallFrameIdentifier);
}
