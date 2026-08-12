using System.ComponentModel;
using System.Text.Json;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Persistence.Raw;

namespace ApexLab.Replay.LapAudit;

internal static class BahrainLapAuditValidationCommand
{
    private const int SchemaVersion = 1;
    private const string SchemaId = "bahrain-lap-audit-validation-v1";
    private const string Command = "validate-bahrain-lap-audit";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<BahrainLapAuditValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            arguments,
            cancellationToken,
            BahrainLapAuditValidationExecution.ExecuteAsync);

    internal static async Task<BahrainLapAuditValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        BahrainLapAuditValidationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!BahrainLapAuditArguments.TryParse(arguments, Command, out var parsed))
        {
            return Failure(
                BahrainLapAuditValidationExitCode.InvalidArguments,
                "invalidArguments");
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
                BahrainLapAuditValidationExitCode.InvalidArguments,
                "invalidArguments");
        }

        try
        {
            var execution = await evaluator(
                    paths,
                    parsed.CaptureId,
                    cancellationToken)
                .ConfigureAwait(false);
            var exitCode = execution.Conclusion switch
            {
                "PASS" => BahrainLapAuditValidationExitCode.Success,
                "ABSTAIN" => BahrainLapAuditValidationExitCode.Abstained,
                _ => BahrainLapAuditValidationExitCode.ValidationFailed,
            };
            var status = execution.Conclusion switch
            {
                "PASS" => "passed",
                "ABSTAIN" => "abstained",
                _ => "failed",
            };
            return new(
                exitCode,
                JsonSerializer.Serialize(
                    new BahrainLapAuditValidationReport(
                        SchemaVersion,
                        SchemaId,
                        status,
                        BahrainLapAuditContract.AuditId,
                        execution.IncludedCount,
                        execution.ExcludedCount,
                        execution.PendingCount,
                        BahrainLapAuditContract.MinimumComparableBaselineLaps,
                        execution.AllCandidatesAudited,
                        execution.IncludedContextsMatch,
                        execution.ProvenanceComplete,
                        execution.DeterministicSelection,
                        execution.PrivateDataExcluded,
                        execution.Conclusion),
                    JsonOptions));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                BahrainLapAuditValidationExitCode.Interrupted,
                "interrupted");
        }
        catch (RawEvidenceReadException exception)
            when (exception.Kind == RawEvidenceReadFailureKind.UnsupportedProtocol)
        {
            return Failure(
                BahrainLapAuditValidationExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (Exception exception) when (exception is
                   BahrainLapAuditStoreException
                   or BahrainLapAuditException)
        {
            return Failure(
                BahrainLapAuditValidationExitCode.InvalidAudit,
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
                BahrainLapAuditValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Failure(
                BahrainLapAuditValidationExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static BahrainLapAuditValidationCommandResult Failure(
        BahrainLapAuditValidationExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new BahrainLapAuditValidationFailureReport(
                    SchemaVersion,
                    SchemaId,
                    status,
                    "FAIL"),
                JsonOptions));
}

internal enum BahrainLapAuditValidationExitCode
{
    Success = 0,
    Abstained = 10,
    InvalidArguments = 40,
    InvalidEvidence = 41,
    UnsupportedProtocol = 42,
    InvalidAudit = 43,
    Interrupted = 44,
    ValidationFailed = 45,
    UnexpectedFailure = 46,
}

internal readonly record struct BahrainLapAuditValidationCommandResult(
    BahrainLapAuditValidationExitCode ExitCode,
    string Json);

internal sealed record BahrainLapAuditValidationReport(
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
    bool ProvenanceComplete,
    bool DeterministicSelection,
    bool PrivateDataExcluded,
    string Conclusion);

internal sealed record BahrainLapAuditValidationFailureReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string Conclusion);

internal delegate Task<BahrainLapAuditValidationExecutionResult>
    BahrainLapAuditValidationEvaluator(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken);
