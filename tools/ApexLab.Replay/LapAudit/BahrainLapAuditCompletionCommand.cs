using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125.Canonical;

namespace ApexLab.Replay.LapAudit;

internal static class BahrainLapAuditCompletionCommand
{
    private const int SchemaVersion = 1;
    private const string SchemaId = "bahrain-lap-audit-completion-v1";
    private const string Command = "complete-bahrain-lap-audit";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<BahrainLapAuditCompletionCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TextReader privateInput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            arguments,
            privateInput,
            privateInputRedirected: true,
            cancellationToken,
            CompleteAsync);

    internal static Task<BahrainLapAuditCompletionCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TextReader privateInput,
        bool privateInputRedirected,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            arguments,
            privateInput,
            privateInputRedirected,
            cancellationToken,
            CompleteAsync);

    internal static Task<BahrainLapAuditCompletionCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TextReader privateInput,
        CancellationToken cancellationToken,
        BahrainLapAuditCompletionEvaluator evaluator)
        => ExecuteAsync(
            arguments,
            privateInput,
            privateInputRedirected: true,
            cancellationToken,
            evaluator);

    internal static async Task<BahrainLapAuditCompletionCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TextReader privateInput,
        bool privateInputRedirected,
        CancellationToken cancellationToken,
        BahrainLapAuditCompletionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(privateInput);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!BahrainLapAuditCompletionArguments.TryParse(
                arguments,
                Command,
                out var parsed))
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidArguments,
                "invalidArguments");
        }

        if (!privateInputRedirected)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
                "invalidPrivateInput");
        }

        ApplicationPaths paths;
        try
        {
            paths = ApplicationPaths.FromRoot(parsed.DataRoot);
        }
        catch (Exception exception) when (exception is
                   ArgumentException
                   or IOException
                   or NotSupportedException
                   or PathTooLongException
                   or UnauthorizedAccessException)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidArguments,
                "invalidArguments");
        }

        BahrainLapAuditManualInputs? inputs;
        try
        {
            inputs = await BahrainLapAuditPrivateInput.TryReadAsync(
                    privateInput,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.Interrupted,
                "interrupted");
        }
        catch (Exception exception) when (exception is
                   IOException or DecoderFallbackException)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
                "invalidPrivateInput");
        }

        if (inputs is null)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
                "invalidPrivateInput");
        }

        try
        {
            var execution = await evaluator(
                    paths,
                    parsed.CaptureId,
                    inputs,
                    cancellationToken)
                .ConfigureAwait(false);
            var abstained = execution.BaselineDisposition
                == BaselineDisposition.Abstained;
            return new(
                abstained
                    ? BahrainLapAuditCompletionExitCode.Abstained
                    : BahrainLapAuditCompletionExitCode.Success,
                JsonSerializer.Serialize(
                    new BahrainLapAuditCompletionSuccessReport(
                        SchemaVersion,
                        SchemaId,
                        abstained ? "abstained" : "completed",
                        BahrainLapAuditContract.AuditId,
                        execution.IncludedCount,
                        execution.ExcludedCount,
                        execution.PendingCount,
                        BahrainLapAuditContract.MinimumComparableBaselineLaps,
                        execution.AllCandidatesAudited,
                        execution.IncludedContextsMatch,
                        abstained ? "abstained" : "ready",
                        PrivateDataExcluded: true,
                        abstained
                            ? "captureMoreLaps"
                            : "validateBahrainLapAudit"),
                    JsonOptions));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.Interrupted,
                "interrupted");
        }
        catch (BahrainLapAuditCompletionException exception)
            when (exception.Kind
                  == BahrainLapAuditCompletionFailureKind.RequiresIndividualReview)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.RequiresIndividualReview,
                "requiresIndividualReview");
        }
        catch (BahrainLapAuditCompletionException exception)
            when (exception.Kind
                  == BahrainLapAuditCompletionFailureKind.ManualContextNotConfirmed)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
                "invalidPrivateInput");
        }
        catch (BahrainLapAuditStoreException exception)
            when (exception.Kind == BahrainLapAuditStoreFailureKind.AlreadyExists)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.AlreadyCompleted,
                "alreadyCompleted");
        }
        catch (RawEvidenceReadException exception)
            when (exception.Kind == RawEvidenceReadFailureKind.UnsupportedProtocol)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (Exception exception) when (exception is
                   BahrainLapAuditCompletionException
                   or BahrainLapAuditException
                   or BahrainLapAuditStoreException)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidAudit,
                "invalidAudit");
        }
        catch (Exception exception) when (exception is
                   RawEvidenceReadException
                   or CanonicalReplayException
                   or BahrainLapAssemblyException
                   or RawEvidenceBahrainLapAuditException
                   or InvalidDataException
                   or IOException
                   or Win32Exception
                   or UnauthorizedAccessException)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Failure(
                BahrainLapAuditCompletionExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static async Task<BahrainLapAuditCompletionExecutionResult> CompleteAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        BahrainLapAuditManualInputs inputs,
        CancellationToken cancellationToken)
    {
        var evaluation = await RawEvidenceBahrainLapAudit.CompleteAllEligibleAsync(
                paths,
                captureId,
                new F125BahrainCanonicalProjector(),
                inputs,
                cancellationToken)
            .ConfigureAwait(false);
        return BahrainLapAuditCompletionExecutionResult.From(evaluation);
    }

    private static BahrainLapAuditCompletionCommandResult Failure(
        BahrainLapAuditCompletionExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new BahrainLapAuditCompletionFailureReport(
                    SchemaVersion,
                    SchemaId,
                    status),
                JsonOptions));
}

internal enum BahrainLapAuditCompletionExitCode
{
    Success = 0,
    Abstained = 10,
    InvalidArguments = 40,
    InvalidPrivateInput = 41,
    InvalidEvidence = 42,
    UnsupportedProtocol = 43,
    RequiresIndividualReview = 44,
    InvalidAudit = 45,
    AlreadyCompleted = 46,
    Interrupted = 47,
    UnexpectedFailure = 48,
}

internal readonly record struct BahrainLapAuditCompletionCommandResult(
    BahrainLapAuditCompletionExitCode ExitCode,
    string Json);

internal sealed record BahrainLapAuditCompletionSuccessReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string LapAuditId,
    int IncludedCount,
    int ExcludedCount,
    int PendingCount,
    int MinimumRequiredCount,
    bool AllCandidatesAudited,
    bool IncludedContextsMatch,
    string BaselineDisposition,
    bool PrivateDataExcluded,
    string NextAction);

internal sealed record BahrainLapAuditCompletionFailureReport(
    int SchemaVersion,
    string SchemaId,
    string Status);

internal sealed record BahrainLapAuditCompletionExecutionResult(
    int IncludedCount,
    int ExcludedCount,
    int PendingCount,
    bool AllCandidatesAudited,
    bool IncludedContextsMatch,
    BaselineDisposition BaselineDisposition)
{
    public static BahrainLapAuditCompletionExecutionResult From(
        BahrainLapAuditEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new(
            evaluation.IncludedCount,
            evaluation.ExcludedCount,
            evaluation.PendingCount,
            evaluation.AllCandidatesAudited,
            evaluation.IncludedContextsMatch,
            evaluation.Selection?.Disposition ?? BaselineDisposition.Abstained);
    }

    internal static BahrainLapAuditCompletionExecutionResult Completed(
        int includedCount,
        int excludedCount) =>
        new(
            includedCount,
            excludedCount,
            PendingCount: 0,
            AllCandidatesAudited: true,
            IncludedContextsMatch: true,
            BaselineDisposition.Ready);

    internal static BahrainLapAuditCompletionExecutionResult Abstained(
        int includedCount,
        int excludedCount) =>
        new(
            includedCount,
            excludedCount,
            PendingCount: 0,
            AllCandidatesAudited: true,
            IncludedContextsMatch: true,
            BaselineDisposition.Abstained);
}

internal delegate Task<BahrainLapAuditCompletionExecutionResult>
    BahrainLapAuditCompletionEvaluator(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        BahrainLapAuditManualInputs inputs,
        CancellationToken cancellationToken);
