using System.ComponentModel;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Replay.BahrainValidation;

internal static class BahrainDecoderValidationCommand
{
    private const int SchemaVersion = 1;
    private const string SchemaId = "bahrain-decoder-validation-v1";
    private const string ContractId = "apexlab-bahrain-tt-slice-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<BahrainDecoderValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            arguments,
            cancellationToken,
            BahrainDecoderReplayExecution.ExecuteAsync);

    internal static async Task<BahrainDecoderValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        BahrainDecoderEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!BahrainDecoderValidationArguments.TryParse(arguments, out var parsed))
        {
            return Failure(
                BahrainDecoderValidationExitCode.InvalidArguments,
                "invalidArguments");
        }

        ApplicationPaths paths;
        try
        {
            paths = ApplicationPaths.FromRoot(parsed.DataRoot);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
        {
            return Failure(
                BahrainDecoderValidationExitCode.InvalidArguments,
                "invalidArguments");
        }

        try
        {
            var result = await evaluator(
                paths,
                parsed.CaptureId,
                cancellationToken).ConfigureAwait(false);
            if (result.SelectedPacketsRejected)
            {
                return Evaluation(
                    BahrainDecoderValidationExitCode.SelectedPacketRejected,
                    "selectedPacketRejected",
                    result,
                    "FAIL");
            }

            if (!result.HasAllContinuousFamilies)
            {
                return Evaluation(
                    BahrainDecoderValidationExitCode.IncompleteSlice,
                    "incompleteSlice",
                    result,
                    "FAIL");
            }

            return Evaluation(
                BahrainDecoderValidationExitCode.Success,
                "passed",
                result,
                "PASS");
        }
        catch (RawEvidenceReadException exception)
            when (exception.Kind == RawEvidenceReadFailureKind.UnsupportedProtocol)
        {
            return Failure(
                BahrainDecoderValidationExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (RawEvidenceReadException)
        {
            return Failure(
                BahrainDecoderValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                BahrainDecoderValidationExitCode.Interrupted,
                "interrupted");
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or IOException
                or Win32Exception
                or UnauthorizedAccessException)
        {
            return Failure(
                BahrainDecoderValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Failure(
                BahrainDecoderValidationExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static BahrainDecoderValidationCommandResult Evaluation(
        BahrainDecoderValidationExitCode exitCode,
        string status,
        BahrainDecoderReplayResult result,
        string conclusion) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new BahrainDecoderValidationSuccessReport(
                    SchemaVersion,
                    SchemaId,
                    status,
                    F125Protocol.Id,
                    ContractId,
                    F125BahrainPacketDecoder.DecoderId,
                    result.MotionDecoded,
                    result.SessionDecoded,
                    result.LapDecoded,
                    result.EventDecoded,
                    result.CarTelemetryDecoded,
                    result.SelectedPacketsRejected,
                    PrivateDataExcluded: true,
                    conclusion),
                JsonOptions));

    private static BahrainDecoderValidationCommandResult Failure(
        BahrainDecoderValidationExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new BahrainDecoderValidationFailureReport(
                    SchemaVersion,
                    SchemaId,
                    status,
                    "FAIL"),
                JsonOptions));
}

internal enum BahrainDecoderValidationExitCode
{
    Success = 0,
    InvalidArguments = 30,
    InvalidEvidence = 31,
    UnsupportedProtocol = 32,
    IncompleteSlice = 33,
    SelectedPacketRejected = 34,
    Interrupted = 35,
    UnexpectedFailure = 36,
}

internal readonly record struct BahrainDecoderValidationCommandResult(
    BahrainDecoderValidationExitCode ExitCode,
    string Json);

internal sealed record BahrainDecoderValidationSuccessReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string ProtocolId,
    string ContractId,
    string DecoderId,
    bool MotionDecoded,
    bool SessionDecoded,
    bool LapDecoded,
    bool SelectedEventDecoded,
    bool CarTelemetryDecoded,
    bool SelectedPacketsRejected,
    bool PrivateDataExcluded,
    string Conclusion);

internal sealed record BahrainDecoderValidationFailureReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string Conclusion);

internal delegate Task<BahrainDecoderReplayResult> BahrainDecoderEvaluator(
    ApplicationPaths paths,
    RawEvidenceCaptureId captureId,
    CancellationToken cancellationToken);
