using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Canonical;

[SupportedOSPlatform("windows")]
internal sealed class CanonicalCacheStore : ICanonicalCacheStore
{
    private readonly ApplicationPaths _paths;

    public CanonicalCacheStore(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<ICanonicalCacheEntry?> TryOpenAsync(
        CanonicalCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await CanonicalCacheReader.OpenAsync(
                    _paths,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CanonicalCacheReadException exception)
            when (exception.IsRebuildable)
        {
            if (exception.Kind != CanonicalCacheReadFailureKind.Missing)
            {
                await RemoveInvalidEntryAsync(
                        request,
                        exception.RebuildDataLeafName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return null;
        }
    }

    public async Task<ICanonicalCacheWriter> CreateWriterAsync(
        CanonicalCacheRequest request,
        CancellationToken cancellationToken = default) =>
        await CanonicalCacheWriter.CreateAsync(
                _paths,
                request,
                observer: null,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task RemoveInvalidEntryAsync(
        CanonicalCacheRequest request,
        string? dataLeafName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var directory = WindowsCanonicalCacheDirectory.Open(_paths);
        if (dataLeafName is not null)
        {
            await DeleteLeafIfPresentAsync(
                    directory,
                    dataLeafName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await DeleteLeafIfPresentAsync(
                directory,
                CanonicalCacheManifest.CreateManifestLeafName(request.Identity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteLeafIfPresentAsync(
        WindowsCanonicalCacheDirectory directory,
        string leafName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = directory.TryOpenExistingForDeletion(leafName);
        if (stream is null)
        {
            return;
        }

        Exception? primary = null;
        try
        {
            directory.DeleteOpenFile(stream.SafeFileHandle);
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        Exception? disposal = null;
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposal = exception;
        }

        if (primary is not null && disposal is not null)
        {
            throw new AggregateException(primary, disposal);
        }

        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        if (disposal is not null)
        {
            ExceptionDispatchInfo.Capture(disposal).Throw();
        }
    }
}
