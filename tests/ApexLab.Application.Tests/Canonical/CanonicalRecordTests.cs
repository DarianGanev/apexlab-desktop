using ApexLab.Application.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Tests.Canonical;

[TestClass]
public sealed class CanonicalRecordTests
{
    [TestMethod]
    public void ObservationCarriesSourceChronologyAndOneCanonicalPacket()
    {
        var packet = MotionPacket();
        var record = CanonicalRecord.Observation(
            sourceSequence: 7,
            arrivalTimestamp: 123,
            packet);

        Assert.AreEqual(CanonicalRecordKind.Observation, record.Kind);
        Assert.AreEqual(7L, record.SourceSequence);
        Assert.AreEqual(123L, record.ArrivalTimestamp);
        Assert.AreEqual(packet, record.Packet);
        Assert.IsNull(record.PacketId);
        Assert.IsNull(record.ExclusionReason);
        Assert.IsNull(record.FirstMissingSequence);
        Assert.IsNull(record.LastMissingSequence);
        Assert.IsNull(record.GapReason);
    }

    [TestMethod]
    public void ExclusionAndGapHaveDisjointExactShapes()
    {
        var exclusion = CanonicalRecord.Exclusion(
            sourceSequence: 8,
            packetId: 7,
            CanonicalExclusionReason.CompatibleFamilyOutsideSlice);
        var gap = CanonicalRecord.Gap(
            firstMissingSequence: 9,
            lastMissingSequence: 11,
            CanonicalGapReason.UnretainedOrMissingSourceRange);

        Assert.AreEqual(CanonicalRecordKind.Exclusion, exclusion.Kind);
        Assert.AreEqual(8L, exclusion.SourceSequence);
        Assert.AreEqual((byte)7, exclusion.PacketId);
        Assert.AreEqual(
            CanonicalExclusionReason.CompatibleFamilyOutsideSlice,
            exclusion.ExclusionReason);
        Assert.IsNull(exclusion.Packet);
        Assert.IsNull(exclusion.ArrivalTimestamp);

        Assert.AreEqual(CanonicalRecordKind.Gap, gap.Kind);
        Assert.AreEqual(9L, gap.FirstMissingSequence);
        Assert.AreEqual(11L, gap.LastMissingSequence);
        Assert.AreEqual(
            CanonicalGapReason.UnretainedOrMissingSourceRange,
            gap.GapReason);
        Assert.IsNull(gap.SourceSequence);
        Assert.IsNull(gap.Packet);
    }

    [TestMethod]
    public void RecordsRejectImpossibleSequenceAndKindValues()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Observation(0, 1, MotionPacket()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Observation(1, -1, MotionPacket()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Observation(1, 1, default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Exclusion(
                1,
                7,
                CanonicalExclusionReason.Unspecified));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Gap(
                0,
                1,
                CanonicalGapReason.UnretainedOrMissingSourceRange));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CanonicalRecord.Gap(
                2,
                1,
                CanonicalGapReason.UnretainedOrMissingSourceRange));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalRecord.Gap(1, 2, CanonicalGapReason.Unspecified));
    }

    [TestMethod]
    public void CacheCompletionRequiresExactAccountingAndDigests()
    {
        var identity = CanonicalReplayIdentityTests.Identity();
        var completion = new CanonicalCacheCompletion(
            identity,
            sourceStopwatchFrequency: 10_000_000,
            dataLengthBytes: 512,
            dataSha256: new string('a', 64),
            canonicalSha256: new string('b', 64),
            recordCount: 4,
            observationCount: 2,
            exclusionCount: 1,
            gapCount: 1,
            firstSourceSequence: 1,
            lastSourceSequence: 5);

        Assert.AreEqual(4L, completion.RecordCount);
        Assert.AreEqual(1L, completion.GapCount);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CanonicalCacheCompletion(
                identity,
                10_000_000,
                512,
                new string('a', 64),
                new string('b', 64),
                recordCount: 3,
                observationCount: 2,
                exclusionCount: 1,
                gapCount: 1,
                firstSourceSequence: 1,
                lastSourceSequence: 5));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CanonicalCacheCompletion(
                identity,
                10_000_000,
                512,
                new string('A', 64),
                new string('b', 64),
                0,
                0,
                0,
                0,
                null,
                null));
    }

    private static CanonicalPacket MotionPacket() =>
        CanonicalPacket.CreateMotion(
            new CanonicalPacketHeader(
                12.5F,
                frameIdentifier: 1,
                overallFrameIdentifier: 2,
                playerCarIndex: 3,
                secondaryPlayerCarIndex: byte.MaxValue),
            new CanonicalMotionPacket(1F, 2F, 3F));
}
