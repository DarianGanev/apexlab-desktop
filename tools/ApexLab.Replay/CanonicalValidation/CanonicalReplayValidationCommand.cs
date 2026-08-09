using System.ComponentModel;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;

namespace ApexLab.Replay.CanonicalValidation;

internal static class CanonicalReplayValidationCommand
{
    private const int SchemaVersion = 1;
    private const string SchemaId = "canonical-replay-validation-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<CanonicalReplayValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            arguments,
            cancellationToken,
            CanonicalReplayValidationExecution.ExecuteAsync);

    internal static async Task<CanonicalReplayValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        CanonicalReplayValidationEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!CanonicalReplayValidationArguments.TryParse(arguments, out var parsed))
        {
            return Failure(
                CanonicalReplayValidationExitCode.InvalidArguments,
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
                CanonicalReplayValidationExitCode.InvalidArguments,
                "invalidArguments");
        }

        try
        {
            var result = await evaluator(
                    paths,
                    parsed.CaptureId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.PassedAll)
            {
                return Failure(
                    CanonicalReplayValidationExitCode.ValidationFailed,
                    "validationFailed");
            }

            return new(
                CanonicalReplayValidationExitCode.Success,
                JsonSerializer.Serialize(
                    new CanonicalReplayValidationSuccessReport(
                        SchemaVersion,
                        SchemaId,
                        "passed",
                        result.ProtocolId,
                        result.ContractId,
                        result.DecoderId,
                        result.CanonicalSchemaId,
                        result.DeterministicReplay,
                        result.ExplicitGapPolicy,
                        result.StaleCacheRejected,
                        result.PrivateDataExcluded,
                        "PASS"),
                    JsonOptions));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                CanonicalReplayValidationExitCode.Interrupted,
                "interrupted");
        }
        catch (RawEvidenceReadException exception)
            when (exception.Kind == RawEvidenceReadFailureKind.UnsupportedProtocol)
        {
            return Failure(
                CanonicalReplayValidationExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (Exception exception) when (exception is
                   RawEvidenceReadException
                   or InvalidDataException
                   or IOException
                   or Win32Exception
                   or UnauthorizedAccessException)
        {
            return Failure(
                CanonicalReplayValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Failure(
                CanonicalReplayValidationExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static CanonicalReplayValidationCommandResult Failure(
        CanonicalReplayValidationExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new CanonicalReplayValidationFailureReport(
                    SchemaVersion,
                    SchemaId,
                    status,
                    "FAIL"),
                JsonOptions));
}

internal enum CanonicalReplayValidationExitCode
{
    Success = 0,
    InvalidArguments = 40,
    InvalidEvidence = 41,
    UnsupportedProtocol = 42,
    ValidationFailed = 43,
    Interrupted = 44,
    UnexpectedFailure = 45,
}

internal readonly record struct CanonicalReplayValidationCommandResult(
    CanonicalReplayValidationExitCode ExitCode,
    string Json);

internal sealed record CanonicalReplayValidationSuccessReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string ProtocolId,
    string ContractId,
    string DecoderId,
    string CanonicalSchemaId,
    bool DeterministicReplay,
    bool ExplicitGapPolicy,
    bool StaleCacheRejected,
    bool PrivateDataExcluded,
    string Conclusion);

internal sealed record CanonicalReplayValidationFailureReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string Conclusion);

internal delegate Task<CanonicalReplayValidationExecutionResult>
    CanonicalReplayValidationEvaluator(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken);
