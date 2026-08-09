using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Laps;

public enum RawEvidenceBahrainLapAuditFailureKind
{
    CanonicalCacheUnavailable = 1,
}

public sealed class RawEvidenceBahrainLapAuditException : Exception
{
    public RawEvidenceBahrainLapAuditException(
        RawEvidenceBahrainLapAuditFailureKind kind,
        string message)
        : base(message) => Kind = kind;

    public RawEvidenceBahrainLapAuditFailureKind Kind { get; }
}

[SupportedOSPlatform("windows")]
public static class RawEvidenceBahrainLapAudit
{
    public static async Task<BahrainLapAuditDocument> PrepareAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        ICanonicalPacketProjector projector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inventory = await BuildInventoryAsync(
                paths,
                paths,
                captureId,
                projector,
                cancellationToken)
            .ConfigureAwait(false);
        var document = BahrainLapAuditDocument.CreateTemplate(inventory);
        await BahrainLapAuditStore.PrepareAsync(
                paths,
                captureId,
                document,
                cancellationToken)
            .ConfigureAwait(false);
        return document;
    }

    public static async Task<BahrainLapAuditEvaluation> EvaluateAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        ICanonicalPacketProjector projector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inventory = await BuildInventoryAsync(
                paths,
                paths,
                captureId,
                projector,
                cancellationToken)
            .ConfigureAwait(false);
        var document = await BahrainLapAuditStore.OpenAsync(
                paths,
                captureId,
                cancellationToken)
            .ConfigureAwait(false);
        return BahrainLapAuditEvaluator.Evaluate(inventory, document);
    }

    public static async Task<BahrainLapInventory> BuildInventoryAsync(
        ApplicationPaths evidencePaths,
        ApplicationPaths cachePaths,
        RawEvidenceCaptureId captureId,
        ICanonicalPacketProjector projector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidencePaths);
        ArgumentNullException.ThrowIfNull(cachePaths);
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(projector);
        cancellationToken.ThrowIfCancellationRequested();
        var replay = await RawEvidenceCanonicalReplay.ExecuteAsync(
                evidencePaths,
                cachePaths,
                captureId,
                projector,
                cancellationToken)
            .ConfigureAwait(false);
        var request = new CanonicalCacheRequest(
            replay.Completion.Identity,
            replay.Completion.SourceStopwatchFrequency);
        var store = new CanonicalCacheStore(cachePaths);
        var entry = await store.TryOpenAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            throw new RawEvidenceBahrainLapAuditException(
                RawEvidenceBahrainLapAuditFailureKind.CanonicalCacheUnavailable,
                "The verified canonical lap input became unavailable.");
        }

        BahrainLapInventory? inventory = null;
        Exception? primary = null;
        try
        {
            inventory = await BahrainLapCandidateAssembler.AssembleAsync(
                    entry.Completion,
                    entry.ReadAllAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        Exception? disposal = null;
        try
        {
            await entry.DisposeAsync().ConfigureAwait(false);
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

        return inventory!;
    }
}
