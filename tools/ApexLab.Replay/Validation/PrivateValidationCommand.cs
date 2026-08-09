using System.ComponentModel;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Replay.Replay;

namespace ApexLab.Replay.Validation;

internal static class PrivateValidationCommand
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<PrivateValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
                arguments,
                cancellationToken,
                PrivateProbeEvidence.ReadAndEvaluateAsync,
                ReplayExecution.ExecuteAsync)
            .ConfigureAwait(false);
    }

    internal static async Task<PrivateValidationCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        PrivateProbeReader probeReader,
        PrivateReplayExecutor replayExecutor)
    {
        ArgumentNullException.ThrowIfNull(probeReader);
        ArgumentNullException.ThrowIfNull(replayExecutor);
        if (!PrivateValidationArguments.TryParse(arguments, out var parsed))
        {
            return Status(
                PrivateValidationExitCode.InvalidArguments,
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
            return Status(
                PrivateValidationExitCode.InvalidArguments,
                "invalidArguments");
        }

        try
        {
            var probe = await probeReader(
                parsed.ProbeReportPath,
                cancellationToken).ConfigureAwait(false);
            var options = new RawReplayOptions(
                RawReplayTimingMode.Immediate);
            var first = await replayExecutor(
                paths,
                parsed.CaptureId,
                options,
                cancellationToken).ConfigureAwait(false);
            var second = await replayExecutor(
                paths,
                parsed.CaptureId,
                options,
                cancellationToken).ConfigureAwait(false);

            if (HasPrivacyFailure(first) || HasPrivacyFailure(second))
            {
                return Status(
                    PrivateValidationExitCode.PrivacyFailure,
                    "privacyFailure");
            }

            if (!HasValidEvidenceAccounting(first)
                || !HasValidEvidenceAccounting(second))
            {
                return Status(
                    PrivateValidationExitCode.InvalidEvidence,
                    "invalidEvidence");
            }

            if (!StableEquals(first, second))
            {
                return Status(
                    PrivateValidationExitCode.NondeterministicReplay,
                    "nondeterministicReplay");
            }

            if (!MatchesProbe(first, probe))
            {
                return Status(
                    PrivateValidationExitCode.ProbeMismatch,
                    "probeMismatch");
            }

            return Success(first.ProtocolId);
        }
        catch (PrivateProbeFailureException)
        {
            return Status(
                PrivateValidationExitCode.ProbeMismatch,
                "probeMismatch");
        }
        catch (ReplayUnsupportedProtocolException)
        {
            return Status(
                PrivateValidationExitCode.UnsupportedProtocol,
                "unsupportedProtocol");
        }
        catch (RawEvidenceReadException)
        {
            return Status(
                PrivateValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (ReplayExecutionInterruptedException)
        {
            return Status(
                PrivateValidationExitCode.Interrupted,
                "interrupted");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Status(
                PrivateValidationExitCode.Interrupted,
                "interrupted");
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or IOException
                or Win32Exception
                or UnauthorizedAccessException)
        {
            return Status(
                PrivateValidationExitCode.InvalidEvidence,
                "invalidEvidence");
        }
        catch (Exception)
        {
            return Status(
                PrivateValidationExitCode.UnexpectedFailure,
                "unexpectedFailure");
        }
    }

    private static bool HasPrivacyFailure(ReplayExecutionResult result) =>
        result.Counters.Classifier.ExcludedPrivacyPacket != 0
        || result.Counters.Classifier.UnexpectedSender != 0;

    private static bool HasValidEvidenceAccounting(
        ReplayExecutionResult result)
    {
        var source = result.Counters.Source;
        var classifier = result.Counters.Classifier;
        return result.RecordCount > 0
            && result.RecordCount == source.DatagramsObserved
            && result.RecordCount == source.SourceEnqueued
            && result.RecordCount == classifier.SourceDequeued
            && result.RecordCount == classifier.Compatible
            && source.SourceDroppedFull == 0
            && source.SourceRejectedOversized == 0
            && source.SocketErrors == 0
            && classifier.MalformedHeader == 0
            && classifier.UnsupportedFormat == 0
            && classifier.UnsupportedYear == 0
            && classifier.UnknownPacketId == 0
            && classifier.UnsupportedPacketVersion == 0
            && classifier.InvalidPacketLength == 0
            && classifier.ExcludedPrivacyPacket == 0
            && classifier.UnexpectedSender == 0
            && classifier.ClassifierAbandonedOnTermination == 0
            && result.Counters.EnqueuedAwaitingClassifier == 0;
    }

    private static bool StableEquals(
        ReplayExecutionResult first,
        ReplayExecutionResult second) =>
        string.Equals(
            first.ProtocolId,
            second.ProtocolId,
            StringComparison.Ordinal)
        && first.TimingMode == second.TimingMode
        && first.SpeedPermille == second.SpeedPermille
        && first.RecordCount == second.RecordCount
        && first.Counters.Source == second.Counters.Source
        && first.Counters.Classifier == second.Counters.Classifier
        && first.Counters.EnqueuedAwaitingClassifier
            == second.Counters.EnqueuedAwaitingClassifier
        && first.SequenceGapCount == second.SequenceGapCount
        && first.Descriptors.SequenceEqual(second.Descriptors);

    private static bool MatchesProbe(
        ReplayExecutionResult replay,
        PrivateProbeEvaluation probe) =>
        string.Equals(
            replay.ProtocolId,
            probe.ProtocolId,
            StringComparison.Ordinal)
        && replay.Descriptors.All(descriptor =>
            probe.ObservedDescriptors.Contains(
                new PrivateDescriptorShape(
                    descriptor.PacketId,
                    descriptor.PacketVersion,
                    descriptor.DatagramLength)));

    private static PrivateValidationCommandResult Success(
        string protocolId) =>
        new(
            PrivateValidationExitCode.Success,
            JsonSerializer.Serialize(
                new PrivateValidationSuccessReport(
                    SchemaVersion,
                    "validated",
                    protocolId,
                    ManifestIntegrity: true,
                    DeterministicReplay: true,
                    SequenceGapPreservation: true,
                    ZeroPrivacyExcludedEvidence: true,
                    ProbeAssumptions: true),
                JsonOptions));

    private static PrivateValidationCommandResult Status(
        PrivateValidationExitCode exitCode,
        string status) =>
        new(
            exitCode,
            JsonSerializer.Serialize(
                new PrivateValidationStatusReport(
                    SchemaVersion,
                    status),
                JsonOptions));
}

internal enum PrivateValidationExitCode
{
    Success = 0,
    InvalidArguments = 20,
    InvalidEvidence = 21,
    UnsupportedProtocol = 22,
    ProbeMismatch = 23,
    NondeterministicReplay = 24,
    PrivacyFailure = 25,
    Interrupted = 26,
    UnexpectedFailure = 27,
}

internal readonly record struct PrivateValidationCommandResult(
    PrivateValidationExitCode ExitCode,
    string Json);

internal sealed record PrivateValidationSuccessReport(
    int SchemaVersion,
    string Status,
    string ProtocolId,
    bool ManifestIntegrity,
    bool DeterministicReplay,
    bool SequenceGapPreservation,
    bool ZeroPrivacyExcludedEvidence,
    bool ProbeAssumptions);

internal sealed record PrivateValidationStatusReport(
    int SchemaVersion,
    string Status);

internal delegate Task<PrivateProbeEvaluation> PrivateProbeReader(
    string absolutePath,
    CancellationToken cancellationToken);

internal delegate Task<ReplayExecutionResult> PrivateReplayExecutor(
    ApplicationPaths paths,
    RawEvidenceCaptureId captureId,
    RawReplayOptions options,
    CancellationToken cancellationToken);
