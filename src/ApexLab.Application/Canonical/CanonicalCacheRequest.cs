namespace ApexLab.Application.Canonical;

public sealed record CanonicalCacheRequest
{
    public CanonicalCacheRequest(
        CanonicalReplayIdentity identity,
        long sourceStopwatchFrequency)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStopwatchFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            sourceStopwatchFrequency,
            10_000_000_000);
        Identity = identity;
        SourceStopwatchFrequency = sourceStopwatchFrequency;
    }

    public CanonicalReplayIdentity Identity { get; }

    public long SourceStopwatchFrequency { get; }
}
