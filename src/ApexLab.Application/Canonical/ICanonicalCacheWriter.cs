namespace ApexLab.Application.Canonical;

public interface ICanonicalCacheWriter : IAsyncDisposable
{
    ValueTask WriteAsync(
        CanonicalRecord record,
        CancellationToken cancellationToken = default);

    Task<CanonicalCacheCompletion> FinalizeAsync(
        CancellationToken cancellationToken = default);
}
