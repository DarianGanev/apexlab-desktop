namespace ApexLab.Application.Canonical;

public enum CanonicalCacheDisposition
{
    Unspecified = 0,
    Built = 1,
    Reused = 2,
}

public sealed record CanonicalReplayResult
{
    public CanonicalReplayResult(
        CanonicalCacheDisposition disposition,
        CanonicalCacheCompletion completion)
    {
        if (!Enum.IsDefined(disposition)
            || disposition == CanonicalCacheDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        ArgumentNullException.ThrowIfNull(completion);
        Disposition = disposition;
        Completion = completion;
    }

    public CanonicalCacheDisposition Disposition { get; }

    public CanonicalCacheCompletion Completion { get; }
}
