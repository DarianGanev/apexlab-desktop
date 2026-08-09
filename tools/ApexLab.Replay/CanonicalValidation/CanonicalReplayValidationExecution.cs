using System.Runtime.ExceptionServices;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;
using ApexLab.Protocols.F125.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Replay.CanonicalValidation;

internal static class CanonicalReplayValidationExecution
{
    private const string TemporaryPrefix = "apexlab-canonical-validation-";

    public static Task<CanonicalReplayValidationExecutionResult> ExecuteAsync(
        ApplicationPaths evidencePaths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            evidencePaths,
            captureId,
            CreateTemporaryCachePaths,
            cancellationToken);

    internal static async Task<CanonicalReplayValidationExecutionResult> ExecuteAsync(
        ApplicationPaths evidencePaths,
        RawEvidenceCaptureId captureId,
        Func<ApplicationPaths> cachePathsFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidencePaths);
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(cachePathsFactory);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedRoots = new List<string>();
        Exception? primaryFailure = null;
        try
        {
            var firstPaths = RequireFreshTemporaryPaths(cachePathsFactory());
            ownedRoots.Add(firstPaths.RootDirectory);
            var secondPaths = RequireFreshTemporaryPaths(cachePathsFactory());
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    firstPaths.RootDirectory,
                    secondPaths.RootDirectory))
            {
                throw new InvalidOperationException(
                    "Canonical validation cache roots must be distinct.");
            }

            ownedRoots.Add(secondPaths.RootDirectory);
            var projector = new F125BahrainCanonicalProjector();
            var first = await RawEvidenceCanonicalReplay.ExecuteAsync(
                    evidencePaths,
                    firstPaths,
                    captureId,
                    projector,
                    cancellationToken)
                .ConfigureAwait(false);
            var firstManifest = await ReadOnlyManifestAsync(
                    firstPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            var second = await RawEvidenceCanonicalReplay.ExecuteAsync(
                    evidencePaths,
                    secondPaths,
                    captureId,
                    projector,
                    cancellationToken)
                .ConfigureAwait(false);
            var secondManifest = await ReadOnlyManifestAsync(
                    secondPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            var mutated = await RawEvidenceCanonicalReplay.ExecuteAsync(
                    evidencePaths,
                    firstPaths,
                    captureId,
                    new VersionMutatedProjector(projector),
                    cancellationToken)
                .ConfigureAwait(false);

            var deterministic =
                first.Disposition == CanonicalCacheDisposition.Built
                && second.Disposition == CanonicalCacheDisposition.Built
                && first.Completion == second.Completion
                && firstManifest.AsSpan().SequenceEqual(secondManifest);
            var explicitGapPolicy = first.Completion.GapCount > 0
                && first.Completion.GapCount == second.Completion.GapCount;
            var staleRejected =
                mutated.Disposition == CanonicalCacheDisposition.Built
                && mutated.Completion.Identity != first.Completion.Identity
                && StringComparer.Ordinal.Equals(
                    mutated.Completion.DataSha256,
                    first.Completion.DataSha256)
                && !StringComparer.Ordinal.Equals(
                    mutated.Completion.CanonicalSha256,
                    first.Completion.CanonicalSha256);
            return new(
                projector.ProtocolId,
                projector.ContractId,
                projector.DecoderId,
                projector.CanonicalSchemaId,
                deterministic,
                explicitGapPolicy,
                staleRejected,
                PrivateDataExcluded: true);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var root in ownedRoots.AsEnumerable().Reverse())
            {
                try
                {
                    DeleteOwnedTemporaryRoot(root);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            if (cleanupFailures.Count != 0)
            {
                if (primaryFailure is not null)
                {
                    throw new AggregateException(
                        [primaryFailure, .. cleanupFailures]);
                }

                if (cleanupFailures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
                }

                throw new AggregateException(cleanupFailures);
            }
        }
    }

    private static ApplicationPaths CreateTemporaryCachePaths() =>
        ApplicationPaths.FromRoot(Path.Combine(
            Path.GetTempPath(),
            $"{TemporaryPrefix}{Guid.NewGuid():N}"));

    private static ApplicationPaths RequireFreshTemporaryPaths(
        ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RequireOwnedTemporaryRoot(paths.RootDirectory);
        if (Directory.Exists(paths.RootDirectory)
            || File.Exists(paths.RootDirectory))
        {
            throw new IOException(
                "A fresh canonical validation cache root is required.");
        }

        return paths;
    }

    private static async Task<byte[]> ReadOnlyManifestAsync(
        ApplicationPaths paths,
        CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(
            paths.DerivedCacheDirectory,
            "*.apxcan.json",
            SearchOption.TopDirectoryOnly);
        if (files.Length != 1)
        {
            throw new InvalidDataException(
                "A forced canonical build did not publish exactly one manifest.");
        }

        var info = new FileInfo(files[0]);
        if (info.Length is < 2 or > 8_192)
        {
            throw new InvalidDataException(
                "A forced canonical manifest exceeded its validation bound.");
        }

        return await File.ReadAllBytesAsync(files[0], cancellationToken)
            .ConfigureAwait(false);
    }

    private static void DeleteOwnedTemporaryRoot(string root)
    {
        RequireOwnedTemporaryRoot(root);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RequireOwnedTemporaryRoot(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var expectedParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Directory.GetParent(normalized)?.FullName,
                expectedParent)
            || !Path.GetFileName(normalized).StartsWith(
                TemporaryPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The canonical validation cache root is not an owned temporary path.");
        }
    }

    private sealed class VersionMutatedProjector : ICanonicalPacketProjector
    {
        private readonly ICanonicalPacketProjector _inner;

        public VersionMutatedProjector(ICanonicalPacketProjector inner) =>
            _inner = inner;

        public string ProtocolId => _inner.ProtocolId;
        public string ContractId => _inner.ContractId;
        public string DecoderId => $"{_inner.DecoderId}.validation-v2";
        public string CanonicalSchemaId => _inner.CanonicalSchemaId;

        public CanonicalProjectionResult Project(ReadOnlySpan<byte> datagram) =>
            _inner.Project(datagram);
    }
}

internal sealed record CanonicalReplayValidationExecutionResult(
    string ProtocolId,
    string ContractId,
    string DecoderId,
    string CanonicalSchemaId,
    bool DeterministicReplay,
    bool ExplicitGapPolicy,
    bool StaleCacheRejected,
    bool PrivateDataExcluded)
{
    public static CanonicalReplayValidationExecutionResult Passed(
        string protocolId,
        string contractId,
        string decoderId,
        string canonicalSchemaId) =>
        new(
            protocolId,
            contractId,
            decoderId,
            canonicalSchemaId,
            DeterministicReplay: true,
            ExplicitGapPolicy: true,
            StaleCacheRejected: true,
            PrivateDataExcluded: true);

    public bool PassedAll =>
        DeterministicReplay
        && ExplicitGapPolicy
        && StaleCacheRejected
        && PrivateDataExcluded;
}
