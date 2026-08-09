namespace ApexLab.Application.Canonical;

public interface ICanonicalCacheEntry : IAsyncDisposable
{
    CanonicalCacheCompletion Completion { get; }

    IAsyncEnumerable<CanonicalRecord> ReadAllAsync(
        CancellationToken cancellationToken = default);
}
