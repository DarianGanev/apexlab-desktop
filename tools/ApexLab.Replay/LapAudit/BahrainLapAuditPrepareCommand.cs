using System.ComponentModel;
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

internal static class BahrainLapAuditPrepareCommand
{
    private const int SchemaVersion = 1;
    private const string SchemaId = "bahrain-lap-audit-prepare-v1";
    private const string Command = "prepare-bahrain-lap-audit";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Task<BahrainLapAuditPrepareCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(arguments, cancellationToken, PrepareAsync);

    internal static async Task<BahrainLapAuditPrepareCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        BahrainLapAuditPrepareEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!BahrainLapAuditArguments.TryParse(arguments, Command, out var parsed))
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.InvalidArguments,
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
                BahrainLapAuditPrepareExitCode.InvalidArguments,
                "invalidArguments");
        }

        try
        {
            var document = await evaluator(
                    paths,
                    parsed.CaptureId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new(
                BahrainLapAuditPrepareExitCode.Success,
                JsonSerializer.Serialize(
                    new BahrainLapAuditPrepareSuccessReport(
                        SchemaVersion,
                        SchemaId,
                        "prepared",
                        BahrainLapAuditContract.AuditId,
                        document.Entries.Count,
                        PrivateDataExcluded: true,
                        document.Entries.Count(entry =>
                            entry.Boundary.Completeness
                            == LapBoundaryCompleteness.Complete)
                        >= BahrainLapAuditContract.MinimumComparableBaselineLaps
                            ? "completePrivateManualAudit"
                            : "captureMoreLaps"),
                    JsonOptions));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.Interrupted,
                "interrupted");
        }
        catch (BahrainLapAuditStoreException exception)
            when (exception.Kind == BahrainLapAuditStoreFailureKind.AlreadyExists)
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.AlreadyExists,
                "alreadyExists");
        }
        catch (RawEvidenceReadException exception)
            when (exception.Kind == RawEvidenceReadFailureKind.UnsupportedProtocol)
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (Exception exception) when (exception is
                   RawEvidenceReadException
                   or CanonicalReplayException
                   or BahrainLapAssemblyException
                   or RawEvidenceBahrainLapAuditException
                   or BahrainLapAuditStoreException
                   or InvalidDataException
                   or IOException
                   or Win32Exception
                   or UnauthorizedAccessException)
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Failure(
                BahrainLapAuditPrepareExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static Task<BahrainLapAuditDocument> PrepareAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken) =>
        RawEvidenceBahrainLapAudit.PrepareAsync(
            paths,
            captureId,
            new F125BahrainCanonicalProjector(),
            cancellationToken);

    private static BahrainLapAuditPrepareCommandResult Failure(
        BahrainLapAuditPrepareExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new BahrainLapAuditPrepareFailureReport(
                    SchemaVersion,
                    SchemaId,
                    status),
                JsonOptions));
}

internal enum BahrainLapAuditPrepareExitCode
{
    Success = 0,
    InvalidArguments = 40,
    InvalidEvidence = 41,
    UnsupportedProtocol = 42,
    AlreadyExists = 43,
    Interrupted = 44,
    UnexpectedFailure = 45,
}

internal readonly record struct BahrainLapAuditPrepareCommandResult(
    BahrainLapAuditPrepareExitCode ExitCode,
    string Json);

internal sealed record BahrainLapAuditPrepareSuccessReport(
    int SchemaVersion,
    string SchemaId,
    string Status,
    string LapAuditId,
    int CandidateCount,
    bool PrivateDataExcluded,
    string NextAction);

internal sealed record BahrainLapAuditPrepareFailureReport(
    int SchemaVersion,
    string SchemaId,
    string Status);

internal delegate Task<BahrainLapAuditDocument> BahrainLapAuditPrepareEvaluator(
    ApplicationPaths paths,
    RawEvidenceCaptureId captureId,
    CancellationToken cancellationToken);
