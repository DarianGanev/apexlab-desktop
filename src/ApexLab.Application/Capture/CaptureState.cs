namespace ApexLab.Application.Capture;

public enum CaptureState
{
    Idle,
    Binding,
    WaitingForTraffic,
    ReceivingCompatibleTraffic,
    IncompatibleTraffic,
    Stopping,
    Stopped,
    Interrupted,
    Faulted,
    Disposed,
}

public enum CaptureStopReason
{
    User,
    HostShutdown,
    LimitReached,
    WriteFailure,
    Fault,
}

public enum CaptureFailureKind
{
    None,
    PortConflict,
    Configuration,
    EvidenceLimit,
    EvidenceWrite,
    Interrupted,
    Unexpected,
}

public sealed record CaptureWorkflowSnapshot(
    CaptureState State,
    RawEvidenceCaptureId? CaptureId,
    CaptureCounters Counters,
    CaptureStopReason? StopReason,
    CaptureFailureKind FailureKind,
    Exception? Failure,
    RawEvidenceCompletion? Completion)
{
    public RawEvidenceLimitKind? EvidenceLimitKind { get; init; }

    public static CaptureWorkflowSnapshot Idle { get; } =
        new(
            CaptureState.Idle,
            CaptureId: null,
            new CaptureCounters(default, default, default),
            StopReason: null,
            CaptureFailureKind.None,
            Failure: null,
            Completion: null);

    public bool CanArm =>
        State is CaptureState.Idle or CaptureState.Stopped;

    public bool CanStop =>
        State is CaptureState.Binding
            or CaptureState.WaitingForTraffic
            or CaptureState.ReceivingCompatibleTraffic
            or CaptureState.IncompatibleTraffic;

    public bool CanReset =>
        State == CaptureState.Faulted;

    public bool IsProvisional =>
        State == CaptureState.Interrupted
        || Counters.Evidence.HasDeferredCleanup;
}
