using ApexLab.Application.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class CaptureCountersTests
{
    [TestMethod]
    public void SourceCounters_RequireCompleteDatagramAccounting()
    {
        var counters = new DatagramSourceCounters(
            datagramsObserved: 10,
            sourceEnqueued: 7,
            sourceDroppedFull: 2,
            sourceRejectedOversized: 1,
            socketErrors: 3);

        Assert.AreEqual(10L, counters.DatagramsObserved);
        Assert.AreEqual(7L, counters.SourceEnqueued);
        Assert.AreEqual(2L, counters.SourceDroppedFull);
        Assert.AreEqual(1L, counters.SourceRejectedOversized);
        Assert.AreEqual(3L, counters.SocketErrors);
        Assert.ThrowsExactly<ArgumentException>(
            () => new DatagramSourceCounters(10, 7, 1, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DatagramSourceCounters(-1, 0, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DatagramSourceCounters(
                long.MaxValue,
                long.MaxValue,
                1,
                0,
                0));
    }

    [TestMethod]
    public void ClassifierCounters_RequireExactlyOneOutcomePerDequeuedDatagram()
    {
        var counters = CreateClassifier(
            sourceDequeued: 10,
            compatible: 4,
            malformedHeader: 1,
            unsupportedFormat: 1,
            unsupportedYear: 1,
            unknownPacketId: 1,
            unsupportedPacketVersion: 1,
            invalidPacketLength: 1,
            classifierAbandonedOnInterrupt: 2);

        Assert.AreEqual(10L, counters.SourceDequeued);
        Assert.AreEqual(4L, counters.Compatible);
        Assert.AreEqual(2L, counters.ClassifierAbandonedOnInterrupt);
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateClassifier(
                sourceDequeued: 11,
                compatible: 10,
                malformedHeader: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateClassifier(sourceDequeued: 0, compatible: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateClassifier(
                sourceDequeued: long.MaxValue,
                compatible: long.MaxValue,
                malformedHeader: 1));
    }

    [TestMethod]
    public void EvidenceCounters_RequireWrittenRecordsToBeStagedOrFinalized()
    {
        var counters = new EvidenceSinkCounters(
            sinkWritten: 3,
            sinkWriteFailed: 1,
            sinkPending: 2,
            sinkPendingDeferredCleanup: 0,
            finalizedRecords: 0,
            stagedRecords: 3);

        Assert.AreEqual(6L, counters.AccountedCompatible);
        Assert.IsTrue(counters.HasPendingWrites);
        Assert.IsFalse(counters.HasDeferredCleanup);
        Assert.ThrowsExactly<ArgumentException>(
            () => new EvidenceSinkCounters(3, 0, 0, 0, 1, 1));
        Assert.ThrowsExactly<ArgumentException>(
            () => new EvidenceSinkCounters(0, 0, 1, 1, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(
            () => new EvidenceSinkCounters(2, 0, 0, 0, 1, 1));
        Assert.ThrowsExactly<ArgumentException>(
            () => new EvidenceSinkCounters(1, 0, 1, 0, 1, 0));
        Assert.ThrowsExactly<ArgumentException>(
            () => new EvidenceSinkCounters(1, 0, 0, 1, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new EvidenceSinkCounters(0, 0, -1, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new EvidenceSinkCounters(
                long.MaxValue,
                1,
                0,
                0,
                long.MaxValue,
                0));
    }

    [TestMethod]
    public void Aggregate_ReportsActiveBacklogWithoutLosingCompatibleAccounting()
    {
        var counters = new CaptureCounters(
            new DatagramSourceCounters(12, 12, 0, 0, 0),
            CreateClassifier(sourceDequeued: 10, compatible: 4),
            new EvidenceSinkCounters(2, 1, 1, 0, 0, 2));

        Assert.AreEqual(2L, counters.EnqueuedAwaitingClassifier);
        Assert.IsFalse(counters.HasCompleteSourceAccounting);
        Assert.IsTrue(counters.Evidence.HasPendingWrites);
        Assert.IsFalse(counters.AllWrittenRecordsAreFinalized);
    }

    [TestMethod]
    public void Aggregate_DistinguishesCleanAndInterruptedSettledSnapshots()
    {
        var clean = new CaptureCounters(
            new DatagramSourceCounters(10, 10, 0, 0, 0),
            CreateClassifier(sourceDequeued: 10, compatible: 4),
            new EvidenceSinkCounters(3, 1, 0, 0, 3, 0));
        var interrupted = new CaptureCounters(
            new DatagramSourceCounters(12, 12, 0, 0, 0),
            CreateClassifier(
                sourceDequeued: 10,
                compatible: 4,
                classifierAbandonedOnInterrupt: 2),
            new EvidenceSinkCounters(2, 1, 0, 1, 0, 2));

        Assert.IsTrue(clean.HasCompleteSourceAccounting);
        Assert.IsFalse(clean.Classifier.WasAbandonedOnInterrupt);
        Assert.IsFalse(clean.Evidence.HasPendingWrites);
        Assert.IsTrue(clean.AllWrittenRecordsAreFinalized);
        Assert.IsTrue(interrupted.HasCompleteSourceAccounting);
        Assert.IsTrue(interrupted.Classifier.WasAbandonedOnInterrupt);
        Assert.IsTrue(interrupted.Evidence.HasDeferredCleanup);
        Assert.IsFalse(interrupted.AllWrittenRecordsAreFinalized);
    }

    [TestMethod]
    public void Aggregate_RejectsImpossibleCrossOwnerLedgers()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new CaptureCounters(
                new DatagramSourceCounters(10, 10, 0, 0, 0),
                CreateClassifier(
                    sourceDequeued: 10,
                    compatible: 4,
                    classifierAbandonedOnInterrupt: 1),
                new EvidenceSinkCounters(3, 1, 0, 0, 0, 3)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new CaptureCounters(
                new DatagramSourceCounters(10, 10, 0, 0, 0),
                CreateClassifier(sourceDequeued: 10, compatible: 4),
                new EvidenceSinkCounters(2, 1, 0, 0, 0, 2)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new CaptureCounters(
                new DatagramSourceCounters(12, 12, 0, 0, 0),
                CreateClassifier(
                    sourceDequeued: 10,
                    compatible: 4,
                    classifierAbandonedOnInterrupt: 1),
                new EvidenceSinkCounters(3, 1, 0, 0, 0, 3)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new CaptureCounters(
                new DatagramSourceCounters(12, 12, 0, 0, 0),
                CreateClassifier(sourceDequeued: 10, compatible: 4),
                new EvidenceSinkCounters(2, 1, 0, 1, 0, 2)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new CaptureCounters(
                new DatagramSourceCounters(12, 12, 0, 0, 0),
                CreateClassifier(sourceDequeued: 10, compatible: 4),
                new EvidenceSinkCounters(3, 1, 0, 0, 3, 0)));
    }

    [TestMethod]
    public void DefaultOwnerSnapshotsComposeAsAnEmptyBalancedLedger()
    {
        var counters = new CaptureCounters(default, default, default);

        Assert.AreEqual(0L, counters.EnqueuedAwaitingClassifier);
        Assert.IsTrue(counters.HasCompleteSourceAccounting);
        Assert.IsTrue(counters.AllWrittenRecordsAreFinalized);
    }

    private static DatagramClassificationCounters CreateClassifier(
        long sourceDequeued,
        long compatible,
        long? malformedHeader = null,
        long unsupportedFormat = 0,
        long unsupportedYear = 0,
        long unknownPacketId = 0,
        long unsupportedPacketVersion = 0,
        long invalidPacketLength = 0,
        long excludedPrivacyPacket = 0,
        long unexpectedSender = 0,
        long classifierAbandonedOnInterrupt = 0) =>
        new(
            sourceDequeued,
            compatible,
            malformedHeader
                ?? sourceDequeued
                - compatible
                - unsupportedFormat
                - unsupportedYear
                - unknownPacketId
                - unsupportedPacketVersion
                - invalidPacketLength
                - excludedPrivacyPacket
                - unexpectedSender,
            unsupportedFormat,
            unsupportedYear,
            unknownPacketId,
            unsupportedPacketVersion,
            invalidPacketLength,
            excludedPrivacyPacket,
            unexpectedSender,
            classifierAbandonedOnInterrupt);
}
