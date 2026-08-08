using System.Text.Json;
using System.Text.Json.Serialization;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Probe;

namespace ApexLab.Replay.Validation;

internal static class PrivateProbeEvidence
{
    private const int MaximumReportBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<PrivateProbeEvaluation> ReadAndEvaluateAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAndEvaluateCoreAsync(
                absolutePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PrivateProbeFailureException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException
                or InvalidOperationException
                or OverflowException)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        }
    }

    private static async Task<PrivateProbeEvaluation> ReadAndEvaluateCoreAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)
            || !Path.IsPathFullyQualified(absolutePath))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        }

        var reportFile = new FileInfo(absolutePath);
        if (!reportFile.Exists
            || reportFile.Length is < 1 or > MaximumReportBytes)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        }

        var json = await File.ReadAllTextAsync(
            reportFile.FullName,
            cancellationToken).ConfigureAwait(false);
        var report = JsonSerializer.Deserialize<ProbeReport>(json, JsonOptions)
            ?? throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        if (report.SchemaVersion != ProbeAggregator.SchemaVersion)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.UnsupportedSchema);
        }

        if (HasMalformedDomains(report))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        }

        if (!string.Equals(
                report.ProtocolId,
                F125Protocol.Id,
                StringComparison.Ordinal))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.ProtocolMismatch);
        }

        if (!string.Equals(
                report.Status,
                "success",
                StringComparison.Ordinal)
            || report.Classification.Compatible <= 0)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.NoCompatibleTraffic);
        }

        if (!HasValidAccounting(report))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.AccountingMismatch);
        }

        if (HasRejectedTraffic(report))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.RejectedTraffic);
        }

        if (HasRegression(report))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Regression);
        }

        if (!TryValidateDescriptors(report, out var descriptors))
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.DescriptorMismatch);
        }

        var peak = EvaluatePeak(report);
        return new PrivateProbeEvaluation(
            report.ProtocolId,
            peak,
            descriptors);
    }

    private static bool HasMalformedDomains(ProbeReport report)
    {
        if (string.IsNullOrWhiteSpace(report.Status)
            || string.IsNullOrWhiteSpace(report.ProtocolId)
            || report.Source is null
            || report.Classification is null
            || report.PacketShapes is null
            || report.Descriptors is null
            || report.RateBuckets is null
            || report.Sequence is null
            || report.Headers is null
            || report.PlayerIndices is null
            || report.DurationMilliseconds <= 0)
        {
            return true;
        }

        long[] values =
        [
            report.Source.DatagramsObserved,
            report.Source.SourceEnqueued,
            report.Source.SourceDroppedFull,
            report.Source.SourceRejectedOversized,
            report.Source.SocketErrors,
            report.Classification.SourceDequeued,
            report.Classification.Compatible,
            report.Classification.MalformedHeader,
            report.Classification.UnsupportedFormat,
            report.Classification.UnsupportedYear,
            report.Classification.UnknownPacketId,
            report.Classification.UnsupportedPacketVersion,
            report.Classification.InvalidPacketLength,
            report.Classification.ExcludedPrivacyPacket,
            report.Classification.UnexpectedSender,
            report.Classification.ClassifierAbandonedOnTermination,
            report.Sequence.Gaps,
            report.Sequence.Regressions,
            report.Sequence.MonotonicTimestampRegressions,
            report.Sequence.UtcTimestampRegressions,
            report.Headers.SessionUidCardinality,
            report.Headers.SessionTimeRegressions,
            report.Headers.FrameSkippedIdentifierValues,
            report.Headers.FrameRegressions,
            report.Headers.OverallFrameSkippedIdentifierValues,
            report.Headers.OverallFrameRegressions,
            report.PlayerIndices.SecondaryAbsentCount,
        ];
        return values.Any(value => value < 0)
            || HasInvalidRange(
                report.PlayerIndices.PlayerMinimum,
                report.PlayerIndices.PlayerMaximum)
            || HasInvalidRange(
                report.PlayerIndices.SecondaryMinimum,
                report.PlayerIndices.SecondaryMaximum);
    }

    private static bool HasInvalidRange(byte? minimum, byte? maximum) =>
        minimum.HasValue != maximum.HasValue
        || minimum.HasValue && minimum.Value > maximum!.Value;

    private static int EvaluatePeak(ProbeReport report)
    {
        if (report.RateBuckets is null || report.RateBuckets.Count == 0)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.NoCompatibleTraffic);
        }

        try
        {
            var offsets = new HashSet<long>();
            long total = 0;
            long peak = 0;
            foreach (var bucket in report.RateBuckets)
            {
                if (bucket.OffsetSeconds < 0
                    || bucket.Count < 0
                    || !offsets.Add(bucket.OffsetSeconds))
                {
                    throw new PrivateProbeFailureException(
                        PrivateProbeFailureKind.Malformed);
                }

                total = checked(total + bucket.Count);
                peak = Math.Max(peak, bucket.Count);
            }

            if (peak <= 0)
            {
                throw new PrivateProbeFailureException(
                    PrivateProbeFailureKind.NoCompatibleTraffic);
            }

            if (total != report.Classification.SourceDequeued
                || peak > int.MaxValue)
            {
                throw new PrivateProbeFailureException(
                    PrivateProbeFailureKind.Malformed);
            }

            return checked((int)peak);
        }
        catch (OverflowException)
        {
            throw new PrivateProbeFailureException(
                PrivateProbeFailureKind.Malformed);
        }
    }

    private static bool HasValidAccounting(ProbeReport report)
    {
        try
        {
            var source = report.Source;
            var classification = report.Classification;
            var sourceAccounted = checked(
                source.SourceEnqueued
                + source.SourceDroppedFull
                + source.SourceRejectedOversized);
            var classified = checked(
                classification.Compatible
                + classification.MalformedHeader
                + classification.UnsupportedFormat
                + classification.UnsupportedYear
                + classification.UnknownPacketId
                + classification.UnsupportedPacketVersion
                + classification.InvalidPacketLength
                + classification.ExcludedPrivacyPacket
                + classification.UnexpectedSender);
            var enqueuedAccounted = checked(
                classification.SourceDequeued
                + classification.ClassifierAbandonedOnTermination);
            return source.DatagramsObserved == sourceAccounted
                && classification.SourceDequeued == classified
                && source.SourceEnqueued == enqueuedAccounted;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool HasRejectedTraffic(ProbeReport report)
    {
        var source = report.Source;
        var classification = report.Classification;
        return source.SourceDroppedFull != 0
            || source.SourceRejectedOversized != 0
            || source.SocketErrors != 0
            || classification.MalformedHeader != 0
            || classification.UnsupportedFormat != 0
            || classification.UnsupportedYear != 0
            || classification.UnknownPacketId != 0
            || classification.UnsupportedPacketVersion != 0
            || classification.InvalidPacketLength != 0
            || classification.UnexpectedSender != 0
            || classification.ClassifierAbandonedOnTermination != 0;
    }

    private static bool HasRegression(ProbeReport report) =>
        report.Sequence.Regressions != 0
        || report.Sequence.MonotonicTimestampRegressions != 0
        || report.Sequence.UtcTimestampRegressions != 0
        || report.Headers.SessionTimeRegressions != 0
        || report.Headers.FrameRegressions != 0
        || report.Headers.OverallFrameRegressions != 0;

    private static bool TryValidateDescriptors(
        ProbeReport report,
        out IReadOnlySet<PrivateDescriptorShape> observed)
    {
        observed = new HashSet<PrivateDescriptorShape>();
        if (report.PacketShapes is null || report.Descriptors is null)
        {
            return false;
        }

        try
        {
            var shapeCounts = new Dictionary<PrivateDescriptorShape, long>();
            foreach (var shape in report.PacketShapes)
            {
                var key = new PrivateDescriptorShape(
                    shape.PacketId,
                    shape.PacketVersion,
                    shape.DatagramLength);
                if (shape.Count <= 0
                    || !MatchesCatalog(key)
                    || !shapeCounts.TryAdd(key, shape.Count))
                {
                    return false;
                }
            }

            var descriptorCounts =
                new Dictionary<PrivateDescriptorShape, long>();
            long descriptorTotal = 0;
            foreach (var descriptor in report.Descriptors)
            {
                var key = new PrivateDescriptorShape(
                    descriptor.PacketId,
                    descriptor.PacketVersion,
                    descriptor.DatagramLength);
                if (descriptor.Count <= 0
                    || !MatchesCatalog(key)
                    || !descriptorCounts.TryAdd(key, descriptor.Count))
                {
                    return false;
                }

                descriptorTotal = checked(
                    descriptorTotal + descriptor.Count);
            }

            if (descriptorTotal != report.Classification.SourceDequeued
                || shapeCounts.Count != descriptorCounts.Count)
            {
                return false;
            }

            foreach (var pair in descriptorCounts)
            {
                if (!shapeCounts.TryGetValue(pair.Key, out var shapeCount)
                    || shapeCount != pair.Value)
                {
                    return false;
                }
            }

            observed = descriptorCounts.Keys.ToHashSet();
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool MatchesCatalog(PrivateDescriptorShape shape) =>
        F125PacketDescriptorCatalog.TryGet(
            shape.PacketId,
            out var descriptor)
        && descriptor.PacketVersion == shape.PacketVersion
        && descriptor.DatagramLength == shape.DatagramLength;
}

internal sealed record PrivateProbeEvaluation(
    string ProtocolId,
    int MeasuredPeakDatagramsPerSecond,
    IReadOnlySet<PrivateDescriptorShape> ObservedDescriptors);

internal readonly record struct PrivateDescriptorShape(
    byte PacketId,
    byte PacketVersion,
    int DatagramLength);

internal enum PrivateProbeFailureKind
{
    Malformed,
    UnsupportedSchema,
    ProtocolMismatch,
    AccountingMismatch,
    RejectedTraffic,
    Regression,
    DescriptorMismatch,
    NoCompatibleTraffic,
}

internal sealed class PrivateProbeFailureException : Exception
{
    public PrivateProbeFailureException(PrivateProbeFailureKind kind)
        : base("Private probe evidence is invalid.")
    {
        Kind = kind;
    }

    public PrivateProbeFailureKind Kind { get; }
}
