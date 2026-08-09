namespace ApexLab.Application.Canonical;

public interface ICanonicalCacheStore
{
    Task<ICanonicalCacheEntry?> TryOpenAsync(
        CanonicalCacheRequest request,
        CancellationToken cancellationToken = default);

    Task<ICanonicalCacheWriter> CreateWriterAsync(
        CanonicalCacheRequest request,
        CancellationToken cancellationToken = default);
}
