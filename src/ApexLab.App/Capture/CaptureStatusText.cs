using ApexLab.Application.Capture;

namespace ApexLab.App.Capture;

public sealed record CaptureStatusPresentation(
    string StatusText,
    string DiagnosticText,
    string NextStepText,
    string DurabilityText);

public static class CaptureStatusText
{
    public static CaptureStatusPresentation For(
        CaptureWorkflowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var limit = snapshot.EvidenceLimitKind
            ?? FindLimit(snapshot.Failure);
        return new CaptureStatusPresentation(
            Status(snapshot, limit),
            Diagnostics(snapshot, limit),
            NextSteps(snapshot, limit),
            Durability(snapshot));
    }

    private static string Status(
        CaptureWorkflowSnapshot snapshot,
        RawEvidenceLimitKind? limit)
    {
        if (snapshot.FailureKind == CaptureFailureKind.PortConflict)
        {
            return "UDP port 20777 is already in use";
        }

        if (snapshot.FailureKind == CaptureFailureKind.EvidenceWrite)
        {
            return "Evidence could not be written";
        }

        if (snapshot.State == CaptureState.Interrupted)
        {
            return "Cleanup continues in the background";
        }

        if (limit is not null
            || snapshot.FailureKind == CaptureFailureKind.EvidenceLimit)
        {
            if (snapshot.State == CaptureState.Stopping)
            {
                return LimitName(limit)
                    + " limit reached; stopping and finalizing evidence";
            }

            if (snapshot.State == CaptureState.Stopped
                && snapshot.Completion is not null)
            {
                return "Evidence finalized at the "
                    + LimitName(limit)
                    + " limit";
            }

            if (snapshot.State == CaptureState.Stopped)
            {
                return "Capture stopped at the "
                    + LimitName(limit)
                    + " limit without finalized evidence";
            }

            return LimitName(limit) + " evidence limit reached";
        }

        return snapshot.State switch
        {
            CaptureState.Idle => "Ready to arm",
            CaptureState.Binding => "Binding UDP port 20777",
            CaptureState.WaitingForTraffic =>
                "Waiting for F1 25 telemetry",
            CaptureState.ReceivingCompatibleTraffic =>
                "Receiving compatible F1 25 telemetry",
            CaptureState.IncompatibleTraffic =>
                "Traffic received, but it is not compatible",
            CaptureState.Stopping => "Stopping and draining capture",
            CaptureState.Stopped => "Capture stopped",
            CaptureState.Interrupted =>
                "Cleanup continues in the background",
            CaptureState.Faulted => "Capture needs attention",
            CaptureState.Disposed => "Capture is unavailable",
            _ => "Capture status is unavailable",
        };
    }

    private static string Diagnostics(
        CaptureWorkflowSnapshot snapshot,
        RawEvidenceLimitKind? limit)
    {
        var diagnostics = new List<string>();
        var source = snapshot.Counters.Source;
        var classifier = snapshot.Counters.Classifier;
        var evidence = snapshot.Counters.Evidence;

        AddCount(diagnostics, "Malformed header", classifier.MalformedHeader);
        AddCount(diagnostics, "Wrong packet format", classifier.UnsupportedFormat);
        AddCount(diagnostics, "Wrong game year", classifier.UnsupportedYear);
        AddCount(diagnostics, "Unknown packet ID", classifier.UnknownPacketId);
        AddCount(
            diagnostics,
            "Unsupported packet version",
            classifier.UnsupportedPacketVersion);
        AddCount(
            diagnostics,
            "Invalid packet length",
            classifier.InvalidPacketLength);
        AddCount(
            diagnostics,
            "Privacy-excluded packets",
            classifier.ExcludedPrivacyPacket);
        AddCount(
            diagnostics,
            "Unexpected same-PC sender",
            classifier.UnexpectedSender);
        AddCount(
            diagnostics,
            "Source queue drops",
            source.SourceDroppedFull);
        AddCount(
            diagnostics,
            "Oversized datagrams",
            source.SourceRejectedOversized);
        AddCount(diagnostics, "Socket errors", source.SocketErrors);
        AddCount(
            diagnostics,
            "Evidence write failures",
            evidence.SinkWriteFailed);

        if (snapshot.State == CaptureState.WaitingForTraffic)
        {
            diagnostics.Add("No compatible traffic observed");
        }
        else if (StoppedWithoutCompatibleTraffic(snapshot))
        {
            diagnostics.Add("No compatible traffic captured");
        }
        if (snapshot.FailureKind == CaptureFailureKind.PortConflict)
        {
            diagnostics.Add("Loopback UDP port conflict");
        }
        if (limit is not null
            || snapshot.StopReason == CaptureStopReason.LimitReached)
        {
            diagnostics.Add(
                $"Evidence limit reached: {limit?.ToString() ?? "Unknown"}");
        }
        if (snapshot.FailureKind == CaptureFailureKind.EvidenceWrite)
        {
            diagnostics.Add("Evidence write failure");
        }
        if (snapshot.State == CaptureState.Interrupted)
        {
            diagnostics.Add("Cleanup ownership unresolved");
        }
        if (snapshot.State == CaptureState.Faulted)
        {
            diagnostics.Add("Capture faulted");
        }
        if (snapshot.State == CaptureState.Stopped)
        {
            diagnostics.Add("Capture stopped");
        }

        return diagnostics.Count == 0
            ? "No capture diagnostics reported"
            : string.Join(" • ", diagnostics);
    }

    private static string NextSteps(
        CaptureWorkflowSnapshot snapshot,
        RawEvidenceLimitKind? limit)
    {
        var actions = new List<string>();
        var source = snapshot.Counters.Source;
        var classifier = snapshot.Counters.Classifier;

        if (snapshot.FailureKind == CaptureFailureKind.PortConflict)
        {
            AddUnique(
                actions,
                "Stop the other same-PC app using UDP port 20777, then arm again.");
        }
        if (snapshot.State == CaptureState.WaitingForTraffic
            || StoppedWithoutCompatibleTraffic(snapshot))
        {
            AddUnique(
                actions,
                "Verify UDP On, F1 25 mode, 127.0.0.1:20777, then drive on track.");
        }
        if (classifier.UnsupportedFormat != 0
            || classifier.UnsupportedYear != 0)
        {
            AddUnique(
                actions,
                "Select F1 25 UDP mode and the current F1 25 game year.");
        }
        if (classifier.MalformedHeader != 0
            || classifier.UnknownPacketId != 0
            || classifier.UnsupportedPacketVersion != 0
            || classifier.InvalidPacketLength != 0)
        {
            AddUnique(
                actions,
                "Use the expected base-v3 source or update ApexLab for this packet layout.");
        }
        if (classifier.ExcludedPrivacyPacket != 0)
        {
            AddUnique(
                actions,
                "Privacy-excluded packet classes are intentionally not retained.");
        }
        if (classifier.UnexpectedSender != 0)
        {
            AddUnique(
                actions,
                "Stop other same-PC telemetry senders, then arm again.");
        }
        if (source.SourceDroppedFull != 0
            || source.SourceRejectedOversized != 0)
        {
            AddUnique(
                actions,
                "Verify F1 25 UDP mode and reduce the UDP send rate.");
        }
        if (source.SocketErrors != 0)
        {
            AddUnique(
                actions,
                "Check that the local UDP setup remains available, then arm again.");
        }

        if (limit is not null
            || snapshot.StopReason == CaptureStopReason.LimitReached)
        {
            AddUnique(
                actions,
                limit == RawEvidenceLimitKind.FreeSpace
                    ? "Free local disk space before arming again."
                    : "Arm a new capture when more evidence is needed.");
        }
        if (snapshot.FailureKind == CaptureFailureKind.EvidenceWrite)
        {
            AddUnique(
                actions,
                "Check local storage, wait for cleanup to finish, reset, then arm again.");
        }
        if (snapshot.State == CaptureState.Interrupted)
        {
            AddUnique(
                actions,
                "Keep ApexLab open while provisional cleanup finishes.");
        }
        if (snapshot.State == CaptureState.Faulted
            && snapshot.FailureKind is not CaptureFailureKind.PortConflict
            and not CaptureFailureKind.EvidenceWrite)
        {
            AddUnique(
                actions,
                "Wait for capture cleanup to resolve, then reset and arm again.");
        }

        if (actions.Count == 0)
        {
            AddUnique(actions, DefaultAction(snapshot));
        }

        return string.Join(" ", actions);
    }

    private static string Durability(CaptureWorkflowSnapshot snapshot)
    {
        var evidence = snapshot.Counters.Evidence;
        if (snapshot.IsProvisional)
        {
            return evidence.SinkPendingDeferredCleanup != 0
                ? "Deferred writes are provisional and not yet durable; counts may still change."
                : "Cleanup is provisional; counts may still change until ownership resolves.";
        }

        if (snapshot.Completion is not null
            || evidence.FinalizedRecords != 0)
        {
            return "Finalized means the completion manifest was committed and file contents were flushed; directory-entry crash durability is not claimed.";
        }

        if (evidence.SinkPending != 0)
        {
            return "Pending records are not yet written or durable.";
        }

        if (evidence.StagedRecords != 0)
        {
            return "Written records are staged, not final evidence until the completion manifest is committed.";
        }

        return "No finalized evidence is available.";
    }

    private static string DefaultAction(CaptureWorkflowSnapshot snapshot) =>
        snapshot.State switch
        {
            CaptureState.Idle =>
                "Use the F1 25 loopback setup above, then arm capture.",
            CaptureState.Binding =>
                "Wait for the loopback UDP bind to finish.",
            CaptureState.ReceivingCompatibleTraffic =>
                "Continue driving or stop when enough evidence is staged.",
            CaptureState.IncompatibleTraffic =>
                "Verify F1 25 UDP mode and the expected base-v3 source.",
            CaptureState.Stopping =>
                "Keep ApexLab open while capture drains and finalizes.",
            CaptureState.Stopped when snapshot.Completion is not null =>
                "Finalized evidence is ready for local replay; arm again when needed.",
            CaptureState.Stopped =>
                "Arm a new capture when ready.",
            CaptureState.Disposed =>
                "Restart ApexLab to capture again.",
            _ =>
                "Review the aggregate diagnostics before trying again.",
        };

    private static RawEvidenceLimitKind? FindLimit(Exception? failure)
    {
        if (failure is RawEvidenceLimitReachedException limit)
        {
            return limit.Kind;
        }

        if (failure is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var found = FindLimit(inner);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string LimitName(RawEvidenceLimitKind? limit) =>
        limit switch
        {
            RawEvidenceLimitKind.FileSize => "file-size",
            RawEvidenceLimitKind.Duration => "duration",
            RawEvidenceLimitKind.FreeSpace => "disk safety",
            _ => "evidence",
        };

    private static bool StoppedWithoutCompatibleTraffic(
        CaptureWorkflowSnapshot snapshot) =>
        snapshot.State == CaptureState.Stopped
        && snapshot.FailureKind == CaptureFailureKind.None
        && snapshot.StopReason is CaptureStopReason.User
            or CaptureStopReason.HostShutdown
        && snapshot.Counters.Classifier.Compatible == 0;

    private static void AddCount(
        ICollection<string> diagnostics,
        string label,
        long value)
    {
        if (value != 0)
        {
            diagnostics.Add($"{label}: {value}");
        }
    }

    private static void AddUnique(
        ICollection<string> actions,
        string action)
    {
        if (!actions.Contains(action, StringComparer.Ordinal))
        {
            actions.Add(action);
        }
    }
}
