using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Canonical;

public static class CanonicalReplayWorkflow
{
    public static async Task<CanonicalReplayResult> ExecuteAsync(
        CanonicalCacheRequest request,
        IDatagramSource source,
        ICanonicalPacketProjector projector,
        ICanonicalCacheStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(store);

        return await UseAsync(
                source,
                () => ExecuteWithOwnedSourceAsync(
                    request,
                    source,
                    projector,
                    store,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<CanonicalReplayResult> ExecuteWithOwnedSourceAsync(
        CanonicalCacheRequest request,
        IDatagramSource source,
        ICanonicalPacketProjector projector,
        ICanonicalCacheStore store,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(request.Identity, projector);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await store.TryOpenAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await UseAsync(
                    existing,
                    () => Task.FromResult(CreateReusedResult(request, existing)))
                .ConfigureAwait(false);
        }

        var writer = await store.CreateWriterAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await UseAsync(
                writer,
                () => BuildAsync(
                    request,
                    source,
                    projector,
                    writer,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    private static CanonicalReplayResult CreateReusedResult(
        CanonicalCacheRequest request,
        ICanonicalCacheEntry entry)
    {
        ValidateCompletion(request, entry.Completion);
        return new(CanonicalCacheDisposition.Reused, entry.Completion);
    }

    private static async Task<CanonicalReplayResult> BuildAsync(
        CanonicalCacheRequest request,
        IDatagramSource source,
        ICanonicalPacketProjector projector,
        ICanonicalCacheWriter writer,
        CancellationToken cancellationToken)
    {
        Exception? replayFailure = null;
        var startAttempted = false;

        try
        {
            startAttempted = true;
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            await ProjectAllAsync(source, projector, writer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            replayFailure = exception;
        }

        Exception? stopFailure = null;
        if (startAttempted)
        {
            try
            {
                await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
        }

        ThrowFailures(replayFailure, stopFailure);

        var completion = await writer.FinalizeAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateCompletion(request, completion);
        return new(CanonicalCacheDisposition.Built, completion);
    }

    private static async Task ProjectAllAsync(
        IDatagramSource source,
        ICanonicalPacketProjector projector,
        ICanonicalCacheWriter writer,
        CancellationToken cancellationToken)
    {
        long? previousSequence = null;
        await foreach (var envelope in source.Output
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (previousSequence.HasValue
                && envelope.Sequence <= previousSequence.Value)
            {
                throw new CanonicalReplayException(
                    CanonicalReplayFailureKind.DuplicateOrReorderedSequence,
                    "Canonical replay input must be strictly ordered.");
            }

            var firstExpected = previousSequence.HasValue
                ? checked(previousSequence.Value + 1)
                : 1;
            if (envelope.Sequence > firstExpected)
            {
                await writer.WriteAsync(
                        CanonicalRecord.Gap(
                            firstExpected,
                            envelope.Sequence - 1,
                            CanonicalGapReason.UnretainedOrMissingSourceRange),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var projection = projector.Project(envelope.Payload.Span);
            var record = ToRecord(
                envelope.Sequence,
                envelope.MonotonicTimestamp,
                projection);
            await writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            previousSequence = envelope.Sequence;
        }
    }

    private static CanonicalRecord ToRecord(
        long sourceSequence,
        long arrivalTimestamp,
        CanonicalProjectionResult projection) =>
        projection.Disposition switch
        {
            CanonicalProjectionDisposition.Projected when projection.Packet.HasValue =>
                CanonicalRecord.Observation(
                    sourceSequence,
                    arrivalTimestamp,
                    projection.Packet.Value),
            CanonicalProjectionDisposition.Excluded when projection.PacketId.HasValue =>
                CanonicalRecord.Exclusion(
                    sourceSequence,
                    projection.PacketId.Value,
                    MapExclusion(projection.Reason)),
            CanonicalProjectionDisposition.Rejected =>
                throw new CanonicalReplayException(
                    CanonicalReplayFailureKind.ProjectionRejected,
                    "A source datagram could not be projected safely.",
                    projection.Reason),
            _ => throw new CanonicalReplayException(
                CanonicalReplayFailureKind.UnexpectedProjection,
                "The projector returned an incomplete result."),
        };

    private static CanonicalExclusionReason MapExclusion(
        CanonicalProjectionReason reason) =>
        reason switch
        {
            CanonicalProjectionReason.CompatibleFamilyOutsideSlice =>
                CanonicalExclusionReason.CompatibleFamilyOutsideSlice,
            CanonicalProjectionReason.EventCodeOutsideSlice =>
                CanonicalExclusionReason.EventCodeOutsideSlice,
            _ => throw new CanonicalReplayException(
                CanonicalReplayFailureKind.UnexpectedProjection,
                "The projector returned an invalid exclusion reason."),
        };

    private static void ValidateIdentity(
        CanonicalReplayIdentity identity,
        ICanonicalPacketProjector projector)
    {
        if (!StringComparer.Ordinal.Equals(identity.ProtocolId, projector.ProtocolId)
            || !StringComparer.Ordinal.Equals(identity.ContractId, projector.ContractId)
            || !StringComparer.Ordinal.Equals(identity.DecoderId, projector.DecoderId)
            || !StringComparer.Ordinal.Equals(
                identity.CanonicalSchemaId,
                projector.CanonicalSchemaId))
        {
            throw new CanonicalReplayException(
                CanonicalReplayFailureKind.IdentityMismatch,
                "The replay identity does not match the selected projector.");
        }
    }

    private static void ValidateCompletion(
        CanonicalCacheRequest request,
        CanonicalCacheCompletion completion)
    {
        if (completion.Identity != request.Identity
            || completion.SourceStopwatchFrequency != request.SourceStopwatchFrequency)
        {
            throw new CanonicalReplayException(
                CanonicalReplayFailureKind.InvalidCacheCompletion,
                "The cache completion does not match the replay request.");
        }
    }

    private static void ThrowFailures(Exception? primary, Exception? secondary)
    {
        if (primary is not null && secondary is not null)
        {
            throw new AggregateException(primary, secondary);
        }

        if (primary is not null)
        {
            throw primary;
        }

        if (secondary is not null)
        {
            throw secondary;
        }
    }

    private static async Task<T> UseAsync<T>(
        IAsyncDisposable resource,
        Func<Task<T>> action)
    {
        Exception? primary = null;
        T? result = default;
        try
        {
            result = await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        Exception? disposal = null;
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposal = exception;
        }

        ThrowFailures(primary, disposal);
        return result!;
    }
}
