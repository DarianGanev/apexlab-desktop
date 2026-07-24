using ApexLab.App.Capture;
using ApexLab.Application.Capture;

namespace ApexLab.IntegrationTests.Capture;

[TestClass]
public sealed class CaptureStatusTextTests
{
    [TestMethod]
    [DataRow(CaptureState.Idle, CaptureFailureKind.None, "Ready to arm")]
    [DataRow(CaptureState.Binding, CaptureFailureKind.None, "Binding UDP port 20777")]
    [DataRow(CaptureState.WaitingForTraffic, CaptureFailureKind.None, "Waiting for F1 25 telemetry")]
    [DataRow(CaptureState.ReceivingCompatibleTraffic, CaptureFailureKind.None, "Receiving compatible F1 25 telemetry")]
    [DataRow(CaptureState.IncompatibleTraffic, CaptureFailureKind.None, "Traffic received, but it is not compatible")]
    [DataRow(CaptureState.Interrupted, CaptureFailureKind.Interrupted, "Cleanup continues in the background")]
    [DataRow(CaptureState.Faulted, CaptureFailureKind.PortConflict, "UDP port 20777 is already in use")]
    [DataRow(CaptureState.Faulted, CaptureFailureKind.EvidenceWrite, "Evidence could not be written")]
    public void MapsWorkflowStateToActionableText(
        CaptureState state,
        CaptureFailureKind failure,
        string expected)
    {
        Assert.AreEqual(expected, CaptureStatusText.For(state, failure));
    }
}
