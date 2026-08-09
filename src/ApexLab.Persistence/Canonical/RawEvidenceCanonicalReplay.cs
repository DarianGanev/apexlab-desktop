using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Canonical;

[SupportedOSPlatform("windows")]
public static class RawEvidenceCanonicalReplay
{
    public static Task<CanonicalReplayResult> ExecuteAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        ICanonicalPacketProjector projector,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            paths,
            paths,
            captureId,
            projector,
            cancellationToken);

    public static async Task<CanonicalReplayResult> ExecuteAsync(
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

        RawEvidenceCapture? unownedCapture = null;
        Exception? primaryFailure = null;
        try
        {
            unownedCapture = await RawEvidenceReader.OpenAsync(
                    evidencePaths,
                    captureId,
                    RawEvidenceProtocolId.Parse(projector.ProtocolId),
                    cancellationToken)
                .ConfigureAwait(false);
            var request = new CanonicalCacheRequest(
                new CanonicalReplayIdentity(
                    unownedCapture.Completion.Sha256,
                    projector.ProtocolId,
                    projector.ContractId,
                    projector.DecoderId,
                    projector.CanonicalSchemaId),
                unownedCapture.StopwatchFrequency);
            var store = new CanonicalCacheStore(cachePaths);
            var source = new RawReplayDatagramSource(
                unownedCapture,
                new RawReplayOptions(RawReplayTimingMode.Immediate));
            unownedCapture = null;
            return await CanonicalReplayWorkflow.ExecuteAsync(
                    request,
                    source,
                    projector,
                    store,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (unownedCapture is not null)
            {
                try
                {
                    await unownedCapture.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (primaryFailure is not null)
                {
                    throw new AggregateException(primaryFailure, cleanupFailure);
                }
            }
        }
    }
}
