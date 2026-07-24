using System.ComponentModel;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Replay.Replay;

internal static class ReplayCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ReplayCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!ReplayArguments.TryParse(arguments, out var parsed))
        {
            return Status(
                ReplayExitCode.InvalidArguments,
                "invalidArguments");
        }

        ApplicationPaths paths;
        try
        {
            paths = ApplicationPaths.FromRoot(parsed.DataRoot);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            return Status(
                ReplayExitCode.InvalidArguments,
                "invalidArguments");
        }

        ITelemetryProtocolAdapter? adapter = null;
        CaptureIngestionCoordinator? coordinator = null;
        RawEvidenceCapture? unownedCapture = null;
        try
        {
            unownedCapture = await RawEvidenceReader.OpenAsync(
                paths,
                parsed.CaptureId,
                cancellationToken).ConfigureAwait(false);
            if (!ReplayProtocolRegistry.TryResolve(
                    unownedCapture.Completion.ProtocolId.Value,
                    out adapter))
            {
                await unownedCapture.DisposeAsync().ConfigureAwait(false);
                unownedCapture = null;
                return Status(
                    ReplayExitCode.UnsupportedProtocol,
                    "unsupportedProtocol");
            }

            await using var source = new RawReplayDatagramSource(
                unownedCapture,
                new RawReplayOptions(
                    parsed.TimingMode,
                    parsed.SpeedPermille));
            var capture = unownedCapture;
            unownedCapture = null;
            coordinator = new CaptureIngestionCoordinator(
                source,
                adapter,
                SenderPolicy.LoopbackOnly);
            await coordinator.RunAsync(
                cancellationToken).ConfigureAwait(false);
            return Report(
                ReplayExitCode.Success,
                "replayed",
                adapter.ProtocolId,
                parsed,
                capture.Completion.RecordCount,
                coordinator.Counters);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return coordinator is null
                ? Status(ReplayExitCode.Interrupted, "interrupted")
                : Report(
                    ReplayExitCode.Interrupted,
                    "interrupted",
                    adapter!.ProtocolId,
                    parsed,
                    recordCount: null,
                    coordinator.Counters);
        }
        catch (RawEvidenceReadException exception)
        {
            return Status(
                ReplayExitCode.InvalidEvidence,
                StatusFor(exception.Kind));
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or IOException
                or Win32Exception
                or UnauthorizedAccessException)
        {
            return Status(
                ReplayExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Status(
                ReplayExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
        finally
        {
            if (unownedCapture is not null)
            {
                try
                {
                    await unownedCapture.DisposeAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Disposal attempts every retained handle internally.
                }
            }
        }
    }

    private static string StatusFor(
        RawEvidenceReadFailureKind kind) =>
        kind switch
        {
            RawEvidenceReadFailureKind.MissingOrIncomplete =>
                "missingOrIncompleteEvidence",
            RawEvidenceReadFailureKind.UnsafePath =>
                "unsafeEvidencePath",
            RawEvidenceReadFailureKind.UnsupportedVersion =>
                "unsupportedEvidenceVersion",
            RawEvidenceReadFailureKind.MalformedStructure =>
                "malformedEvidence",
            RawEvidenceReadFailureKind.DeclaredLimitViolation =>
                "evidenceLimitViolation",
            RawEvidenceReadFailureKind.TruncatedData =>
                "truncatedEvidence",
            RawEvidenceReadFailureKind.TrailingData =>
                "trailingEvidence",
            RawEvidenceReadFailureKind.HashMismatch =>
                "evidenceHashMismatch",
            RawEvidenceReadFailureKind.UnsupportedProtocol =>
                "unsupportedProtocol",
            _ => "invalidEvidence",
        };

    private static ReplayCommandResult Report(
        ReplayExitCode exitCode,
        string status,
        string protocolId,
        ReplayArguments arguments,
        long? recordCount,
        CaptureIngestionCounters counters)
    {
        var report = new ReplayReport(
            SchemaVersion: 1,
            status,
            protocolId,
            Timing: arguments.TimingMode == RawReplayTimingMode.Immediate
                ? "immediate"
                : "recorded",
            arguments.SpeedPermille,
            recordCount,
            new ReplaySourceReport(
                counters.Source.DatagramsObserved,
                counters.Source.SourceEnqueued,
                counters.Source.SourceDroppedFull,
                counters.Source.SourceRejectedOversized,
                counters.Source.SocketErrors),
            new ReplayClassificationReport(
                counters.Classifier.SourceDequeued,
                counters.Classifier.Compatible,
                counters.Classifier.MalformedHeader,
                counters.Classifier.UnsupportedFormat,
                counters.Classifier.UnsupportedYear,
                counters.Classifier.UnknownPacketId,
                counters.Classifier.UnsupportedPacketVersion,
                counters.Classifier.InvalidPacketLength,
                counters.Classifier.ExcludedPrivacyPacket,
                counters.Classifier.UnexpectedSender,
                counters.Classifier.ClassifierAbandonedOnTermination));
        return new ReplayCommandResult(
            exitCode,
            JsonSerializer.Serialize(report, JsonOptions));
    }

    private static ReplayCommandResult Status(
        ReplayExitCode exitCode,
        string status)
    {
        return new ReplayCommandResult(
            exitCode,
            JsonSerializer.Serialize(
                new ReplayStatusReport(1, status),
                JsonOptions));
    }
}

internal enum ReplayExitCode
{
    Success = 0,
    InvalidArguments = 2,
    InvalidEvidence = 3,
    UnsupportedProtocol = 4,
    Interrupted = 6,
    UnexpectedFailure = 7,
}

internal readonly record struct ReplayCommandResult(
    ReplayExitCode ExitCode,
    string Json);

internal sealed record ReplayReport(
    int SchemaVersion,
    string Status,
    string ProtocolId,
    string Timing,
    int SpeedPermille,
    long? RecordCount,
    ReplaySourceReport Source,
    ReplayClassificationReport Classification);

internal sealed record ReplaySourceReport(
    long DatagramsObserved,
    long SourceEnqueued,
    long SourceDroppedFull,
    long SourceRejectedOversized,
    long SocketErrors);

internal sealed record ReplayClassificationReport(
    long SourceDequeued,
    long Compatible,
    long MalformedHeader,
    long UnsupportedFormat,
    long UnsupportedYear,
    long UnknownPacketId,
    long UnsupportedPacketVersion,
    long InvalidPacketLength,
    long ExcludedPrivacyPacket,
    long UnexpectedSender,
    long ClassifierAbandonedOnTermination);

internal sealed record ReplayStatusReport(
    int SchemaVersion,
    string Status);
