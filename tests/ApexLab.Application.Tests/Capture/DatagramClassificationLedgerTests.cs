using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class DatagramClassificationLedgerTests
{
    [TestMethod]
    public void CompatibleAdmissionAfterTransferRemainsDeferred()
    {
        var subject = new DatagramClassificationLedger();
        subject.Record(
            TelemetryPacketClassification.Compatible,
            trackCompatibleEvidence: true);
        subject.TransferPendingEvidenceToDeferredCleanup();

        subject.Record(
            TelemetryPacketClassification.Compatible,
            trackCompatibleEvidence: true);

        var snapshot = subject.CaptureSnapshot();
        Assert.AreEqual(0L, snapshot.Evidence.SinkPending);
        Assert.AreEqual(
            2L,
            snapshot.Evidence.SinkPendingDeferredCleanup);
    }
}
