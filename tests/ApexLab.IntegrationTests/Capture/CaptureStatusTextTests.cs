using ApexLab.App.Capture;
using ApexLab.Application.Capture;

namespace ApexLab.IntegrationTests.Capture;

[TestClass]
public sealed class CaptureStatusTextTests
{
    [TestMethod]
    [DataRow(CaptureState.Idle, "Ready to arm")]
    [DataRow(CaptureState.Binding, "Binding UDP port 20777")]
    [DataRow(CaptureState.WaitingForTraffic, "Waiting for F1 25 telemetry")]
    [DataRow(CaptureState.ReceivingCompatibleTraffic, "Receiving compatible F1 25 telemetry")]
    [DataRow(CaptureState.IncompatibleTraffic, "Traffic received, but it is not compatible")]
    [DataRow(CaptureState.Interrupted, "Cleanup continues in the background")]
    [DataRow(CaptureState.Faulted, "Capture needs attention")]
    [DataRow(CaptureState.Stopped, "Capture stopped")]
    public void MapsWorkflowStateToPrivacySafeStatus(
        CaptureState state,
        string expected)
    {
        var snapshot = CaptureWorkflowSnapshot.Idle with
        {
            State = state,
            FailureKind = state == CaptureState.Interrupted
                ? CaptureFailureKind.Interrupted
                : CaptureFailureKind.None,
        };

        Assert.AreEqual(
            expected,
            CaptureStatusText.For(snapshot).StatusText);
    }

    [TestMethod]
    [DataRow(
        RawEvidenceLimitKind.FileSize,
        "Evidence finalized at the file-size limit",
        "Arm a new capture when more evidence is needed")]
    [DataRow(
        RawEvidenceLimitKind.Duration,
        "Evidence finalized at the duration limit",
        "Arm a new capture when more evidence is needed")]
    [DataRow(
        RawEvidenceLimitKind.FreeSpace,
        "Capture stopped at the disk safety limit",
        "Free local disk space before arming again")]
    public void TypedEvidenceLimitsRemainDistinctAndActionable(
        RawEvidenceLimitKind limit,
        string expectedStatus,
        string expectedAction)
    {
        var snapshot = CaptureWorkflowSnapshot.Idle with
        {
            State = CaptureState.Stopped,
            StopReason = CaptureStopReason.LimitReached,
            FailureKind = CaptureFailureKind.EvidenceLimit,
            Failure = new RawEvidenceLimitReachedException(limit),
        };

        var presentation = CaptureStatusText.For(snapshot);

        Assert.AreEqual(expectedStatus, presentation.StatusText);
        StringAssert.Contains(presentation.NextStepText, expectedAction);
        StringAssert.Contains(presentation.DiagnosticText, limit.ToString());
    }

    [TestMethod]
    [DataRow(
        CaptureFailureKind.PortConflict,
        "UDP port 20777 is already in use",
        "Stop the other same-PC app using UDP port 20777")]
    [DataRow(
        CaptureFailureKind.EvidenceWrite,
        "Evidence could not be written",
        "Check local storage")]
    [DataRow(
        CaptureFailureKind.Unexpected,
        "Capture needs attention",
        "Wait for capture cleanup to resolve")]
    public void FailuresUseFixedActionsWithoutExceptionDetails(
        CaptureFailureKind failureKind,
        string expectedStatus,
        string expectedAction)
    {
        const string poison =
            @"C:\private\capture.raw 127.0.0.1:20777 UID=secret hash=abcdef payload=marker";
        var snapshot = CaptureWorkflowSnapshot.Idle with
        {
            State = CaptureState.Faulted,
            FailureKind = failureKind,
            Failure = new IOException(poison),
        };

        var presentation = CaptureStatusText.For(snapshot);
        var projected = string.Join(
            " ",
            presentation.StatusText,
            presentation.DiagnosticText,
            presentation.NextStepText,
            presentation.DurabilityText);

        Assert.AreEqual(expectedStatus, presentation.StatusText);
        StringAssert.Contains(presentation.NextStepText, expectedAction);
        Assert.IsFalse(projected.Contains(poison, StringComparison.Ordinal));
        Assert.IsFalse(projected.Contains("UID=secret", StringComparison.Ordinal));
        Assert.IsFalse(projected.Contains("payload=marker", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StoppedWithoutTrafficKeepsTheSetupDiagnosisAndNextStep()
    {
        var snapshot = CaptureWorkflowSnapshot.Idle with
        {
            State = CaptureState.Stopped,
            StopReason = CaptureStopReason.User,
        };

        var presentation = CaptureStatusText.For(snapshot);

        StringAssert.Contains(
            presentation.DiagnosticText,
            "No compatible traffic captured");
        StringAssert.Contains(
            presentation.NextStepText,
            "Verify UDP On, F1 25 mode, 127.0.0.1:20777");
    }
}
