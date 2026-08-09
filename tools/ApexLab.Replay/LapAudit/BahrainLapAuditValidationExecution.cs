using System.Runtime.ExceptionServices;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Protocols.F125.Canonical;

namespace ApexLab.Replay.LapAudit;

internal static class BahrainLapAuditValidationExecution
{
    private const string TemporaryPrefix = "apexlab-lap-audit-validation-";

    public static Task<BahrainLapAuditValidationExecutionResult> ExecuteAsync(
        ApplicationPaths evidencePaths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            evidencePaths,
            captureId,
            CreateTemporaryCachePaths,
            cancellationToken);

    internal static async Task<BahrainLapAuditValidationExecutionResult> ExecuteAsync(
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
        Exception? primary = null;
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
                    "Lap audit validation cache roots must be distinct.");
            }

            ownedRoots.Add(secondPaths.RootDirectory);
            var projector = new F125BahrainCanonicalProjector();
            var firstInventory = await RawEvidenceBahrainLapAudit.BuildInventoryAsync(
                    evidencePaths,
                    firstPaths,
                    captureId,
                    projector,
                    cancellationToken)
                .ConfigureAwait(false);
            var secondInventory = await RawEvidenceBahrainLapAudit.BuildInventoryAsync(
                    evidencePaths,
                    secondPaths,
                    captureId,
                    projector,
                    cancellationToken)
                .ConfigureAwait(false);
            var audit = await BahrainLapAuditStore.OpenAsync(
                    evidencePaths,
                    captureId,
                    cancellationToken)
                .ConfigureAwait(false);
            var first = BahrainLapAuditEvaluator.Evaluate(firstInventory, audit);
            var second = BahrainLapAuditEvaluator.Evaluate(secondInventory, audit);
            var deterministic = InventoriesEqual(firstInventory, secondInventory)
                && EvaluationsEqual(first, second);
            return BahrainLapAuditValidationExecutionResult.From(
                first,
                deterministic);
        }
        catch (Exception exception)
        {
            primary = exception;
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
                if (primary is not null)
                {
                    throw new AggregateException([primary, .. cleanupFailures]);
                }

                if (cleanupFailures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
                }

                throw new AggregateException(cleanupFailures);
            }
        }
    }

    private static bool InventoriesEqual(
        BahrainLapInventory first,
        BahrainLapInventory second) =>
        StringComparer.Ordinal.Equals(
            first.CanonicalIdentitySha256,
            second.CanonicalIdentitySha256)
        && StringComparer.Ordinal.Equals(
            first.CanonicalSha256,
            second.CanonicalSha256)
        && first.ReferenceContext == second.ReferenceContext
        && first.Candidates.SequenceEqual(second.Candidates);

    private static bool EvaluationsEqual(
        BahrainLapAuditEvaluation first,
        BahrainLapAuditEvaluation second) =>
        first.IncludedCount == second.IncludedCount
        && first.ExcludedCount == second.ExcludedCount
        && first.PendingCount == second.PendingCount
        && first.IncludedContextsMatch == second.IncludedContextsMatch
        && first.ProvenanceComplete == second.ProvenanceComplete
        && first.AbstentionReasons.SequenceEqual(second.AbstentionReasons)
        && first.Selection?.Disposition == second.Selection?.Disposition
        && CandidateIds(first.Selection?.IncludedLaps)
            .SequenceEqual(CandidateIds(second.Selection?.IncludedLaps))
        && CandidateIds(first.Selection?.ExcludedLaps)
            .SequenceEqual(CandidateIds(second.Selection?.ExcludedLaps));

    private static IEnumerable<string> CandidateIds(
        IReadOnlyList<AuditedLapCandidate>? candidates) =>
        candidates?.Select(candidate => candidate.CandidateId.Value)
        ?? Enumerable.Empty<string>();

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
                "A fresh lap audit validation cache root is required.");
        }

        return paths;
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
                "The lap audit validation cache root is not owned.");
        }
    }
}

internal sealed record BahrainLapAuditValidationExecutionResult
{
    private BahrainLapAuditValidationExecutionResult(
        int includedCount,
        int excludedCount,
        int pendingCount,
        bool allCandidatesAudited,
        bool includedContextsMatch,
        bool provenanceComplete,
        bool deterministicSelection,
        bool privateDataExcluded,
        BaselineDisposition disposition)
    {
        IncludedCount = includedCount;
        ExcludedCount = excludedCount;
        PendingCount = pendingCount;
        AllCandidatesAudited = allCandidatesAudited;
        IncludedContextsMatch = includedContextsMatch;
        ProvenanceComplete = provenanceComplete;
        DeterministicSelection = deterministicSelection;
        PrivateDataExcluded = privateDataExcluded;
        Disposition = disposition;
    }

    public int IncludedCount { get; }
    public int ExcludedCount { get; }
    public int PendingCount { get; }
    public bool AllCandidatesAudited { get; }
    public bool IncludedContextsMatch { get; }
    public bool ProvenanceComplete { get; }
    public bool DeterministicSelection { get; }
    public bool PrivateDataExcluded { get; }
    public BaselineDisposition Disposition { get; }

    public string Conclusion =>
        !DeterministicSelection || !ProvenanceComplete || !PrivateDataExcluded
            ? "FAIL"
            : Disposition == BaselineDisposition.Ready
              && AllCandidatesAudited
              && IncludedContextsMatch
              && IncludedCount >= BahrainLapAuditContract.MinimumComparableBaselineLaps
                ? "PASS"
                : "ABSTAIN";

    public static BahrainLapAuditValidationExecutionResult From(
        BahrainLapAuditEvaluation evaluation,
        bool deterministicSelection)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new(
            evaluation.IncludedCount,
            evaluation.ExcludedCount,
            evaluation.PendingCount,
            evaluation.AllCandidatesAudited,
            evaluation.IncludedContextsMatch,
            evaluation.ProvenanceComplete,
            deterministicSelection,
            privateDataExcluded: true,
            evaluation.Selection?.Disposition ?? BaselineDisposition.Abstained);
    }

    internal static BahrainLapAuditValidationExecutionResult Passed(
        int included,
        int excluded) =>
        Create(included, excluded, BaselineDisposition.Ready, deterministic: true);

    internal static BahrainLapAuditValidationExecutionResult Abstained(
        int included,
        int excluded) =>
        Create(included, excluded, BaselineDisposition.Abstained, deterministic: true);

    internal static BahrainLapAuditValidationExecutionResult Failed(
        int included,
        int excluded) =>
        Create(included, excluded, BaselineDisposition.Ready, deterministic: false);

    private static BahrainLapAuditValidationExecutionResult Create(
        int included,
        int excluded,
        BaselineDisposition disposition,
        bool deterministic) =>
        new(
            included,
            excluded,
            pendingCount: 0,
            allCandidatesAudited: true,
            includedContextsMatch: true,
            provenanceComplete: true,
            deterministic,
            privateDataExcluded: true,
            disposition);
}
