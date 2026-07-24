namespace ApexLab.Replay.Probe;

internal sealed record ProbeReport(
    int SchemaVersion,
    string Status,
    string ProtocolId,
    long DurationMilliseconds,
    ProbeSourceReport Source,
    ProbeClassificationReport Classification,
    IReadOnlyList<ProbePacketShapeReport> PacketShapes,
    IReadOnlyList<ProbeDescriptorReport> Descriptors,
    IReadOnlyList<ProbeRateBucketReport> RateBuckets,
    ProbeSequenceReport Sequence,
    ProbeHeaderReport Headers,
    ProbePlayerIndexReport PlayerIndices);

internal sealed record ProbeSourceReport(
    long DatagramsObserved,
    long SourceEnqueued,
    long SourceDroppedFull,
    long SourceRejectedOversized,
    long SocketErrors);

internal sealed record ProbeClassificationReport(
    long SourceDequeued,
    long Compatible,
    long MalformedHeader,
    long UnsupportedFormat,
    long UnsupportedYear,
    long UnknownPacketId,
    long UnsupportedPacketVersion,
    long InvalidPacketLength,
    long ExcludedPrivacyPacket,
    long UnexpectedSender,
    long ClassifierAbandonedOnInterrupt);

internal sealed record ProbePacketShapeReport(
    byte PacketId,
    byte PacketVersion,
    int DatagramLength,
    long Count);

internal sealed record ProbeDescriptorReport(
    byte PacketId,
    byte PacketVersion,
    int DatagramLength,
    long Count);

internal sealed record ProbeRateBucketReport(
    long OffsetSeconds,
    long Count);

internal sealed record ProbeSequenceReport(
    long Gaps,
    long Regressions,
    long MonotonicTimestampRegressions,
    long UtcTimestampRegressions);

internal sealed record ProbeHeaderReport(
    int SessionUidCardinality,
    long SessionTimeRegressions,
    long FrameSkippedIdentifierValues,
    long FrameRegressions,
    long OverallFrameSkippedIdentifierValues,
    long OverallFrameRegressions);

internal sealed record ProbePlayerIndexReport(
    byte? PlayerMinimum,
    byte? PlayerMaximum,
    byte? SecondaryMinimum,
    byte? SecondaryMaximum,
    long SecondaryAbsentCount);

internal sealed record ProbeStatusReport(
    int SchemaVersion,
    string Status);
