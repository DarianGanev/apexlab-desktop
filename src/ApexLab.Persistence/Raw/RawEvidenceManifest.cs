using ApexLab.Application.Capture;

namespace ApexLab.Persistence.Raw;

internal sealed class RawEvidenceManifest
{
    public RawEvidenceManifest(
        RawEvidenceCaptureId captureId,
        RawEvidenceProtocolId protocolId,
        long dataLengthBytes,
        long recordCount,
        long? firstSequence,
        long? lastSequence,
        long? firstArrivalTimestamp,
        long? lastArrivalTimestamp,
        long stopwatchFrequency,
        long createdUtcTicks,
        long finalizedUtcTicks,
        RawEvidenceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(protocolId);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            dataLengthBytes,
            RawEvidenceLimits.MinimumFileBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            dataLengthBytes,
            limits.MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            stopwatchFrequency,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            stopwatchFrequency,
            RawEvidenceFormat.MaximumStopwatchFrequency);
        ValidateUtcTicks(createdUtcTicks, nameof(createdUtcTicks));
        ValidateUtcTicks(finalizedUtcTicks, nameof(finalizedUtcTicks));

        if (recordCount == 0)
        {
            if (firstSequence.HasValue
                || lastSequence.HasValue
                || firstArrivalTimestamp.HasValue
                || lastArrivalTimestamp.HasValue)
            {
                throw new ArgumentException(
                    "An empty manifest must use null first/last values.",
                    nameof(recordCount));
            }
        }
        else
        {
            if (!firstSequence.HasValue
                || !lastSequence.HasValue
                || !firstArrivalTimestamp.HasValue
                || !lastArrivalTimestamp.HasValue
                || firstSequence.Value < 1
                || lastSequence.Value < firstSequence.Value
                || firstArrivalTimestamp.Value < 0
                || lastArrivalTimestamp.Value < firstArrivalTimestamp.Value)
            {
                throw new ArgumentException(
                    "A populated manifest requires ordered first/last values.",
                    nameof(recordCount));
            }

            var arrivalDelta = checked(
                (Int128)lastArrivalTimestamp.Value
                - firstArrivalTimestamp.Value);
            var elapsedMillisecondsNumerator = checked(arrivalDelta * 1_000);
            var maximumMilliseconds =
                limits.MaximumDuration.Ticks
                / TimeSpan.TicksPerMillisecond;
            var maximumDurationNumerator = checked(
                (Int128)maximumMilliseconds * stopwatchFrequency);
            if (elapsedMillisecondsNumerator > maximumDurationNumerator)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastArrivalTimestamp),
                    "Evidence duration exceeded the configured limit.");
            }
        }

        CaptureId = captureId;
        ProtocolId = protocolId;
        DataLengthBytes = dataLengthBytes;
        RecordCount = recordCount;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
        FirstArrivalTimestamp = firstArrivalTimestamp;
        LastArrivalTimestamp = lastArrivalTimestamp;
        StopwatchFrequency = stopwatchFrequency;
        CreatedUtcTicks = createdUtcTicks;
        FinalizedUtcTicks = finalizedUtcTicks;
        Limits = limits;
    }

    public RawEvidenceCaptureId CaptureId { get; }

    public RawEvidenceProtocolId ProtocolId { get; }

    public long DataLengthBytes { get; }

    public long RecordCount { get; }

    public long? FirstSequence { get; }

    public long? LastSequence { get; }

    public long? FirstArrivalTimestamp { get; }

    public long? LastArrivalTimestamp { get; }

    public long StopwatchFrequency { get; }

    public long CreatedUtcTicks { get; }

    public long FinalizedUtcTicks { get; }

    public RawEvidenceLimits Limits { get; }

    private static void ValidateUtcTicks(long value, string parameterName)
    {
        if (value < DateTimeOffset.MinValue.Ticks
            || value > DateTimeOffset.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A valid .NET UTC tick value is required.");
        }
    }
}
