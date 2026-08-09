namespace ApexLab.Application.Canonical;

public sealed record CanonicalCacheCompletion
{
    public CanonicalCacheCompletion(
        CanonicalReplayIdentity identity,
        long sourceStopwatchFrequency,
        long dataLengthBytes,
        string dataSha256,
        string canonicalSha256,
        long recordCount,
        long observationCount,
        long exclusionCount,
        long gapCount,
        long? firstSourceSequence,
        long? lastSourceSequence)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStopwatchFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            sourceStopwatchFrequency,
            10_000_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataLengthBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            dataLengthBytes,
            16L * 1_024 * 1_024 * 1_024);
        CanonicalIdentityValidation.RequireSha256(dataSha256, nameof(dataSha256));
        CanonicalIdentityValidation.RequireSha256(
            canonicalSha256,
            nameof(canonicalSha256));
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(exclusionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(gapCount);
        if (checked(observationCount + exclusionCount + gapCount) != recordCount)
        {
            throw new ArgumentException(
                "Canonical record accounting must be complete.",
                nameof(recordCount));
        }

        if (recordCount == 0)
        {
            if (firstSourceSequence.HasValue || lastSourceSequence.HasValue)
            {
                throw new ArgumentException(
                    "An empty canonical cache cannot declare source boundaries.",
                    nameof(recordCount));
            }
        }
        else if (firstSourceSequence is not >= 1
                 || lastSourceSequence < firstSourceSequence)
        {
            throw new ArgumentException(
                "A populated canonical cache requires ordered source boundaries.",
                nameof(firstSourceSequence));
        }

        Identity = identity;
        SourceStopwatchFrequency = sourceStopwatchFrequency;
        DataLengthBytes = dataLengthBytes;
        DataSha256 = dataSha256;
        CanonicalSha256 = canonicalSha256;
        RecordCount = recordCount;
        ObservationCount = observationCount;
        ExclusionCount = exclusionCount;
        GapCount = gapCount;
        FirstSourceSequence = firstSourceSequence;
        LastSourceSequence = lastSourceSequence;
    }

    public CanonicalReplayIdentity Identity { get; }
    public long SourceStopwatchFrequency { get; }
    public long DataLengthBytes { get; }
    public string DataSha256 { get; }
    public string CanonicalSha256 { get; }
    public long RecordCount { get; }
    public long ObservationCount { get; }
    public long ExclusionCount { get; }
    public long GapCount { get; }
    public long? FirstSourceSequence { get; }
    public long? LastSourceSequence { get; }
}
