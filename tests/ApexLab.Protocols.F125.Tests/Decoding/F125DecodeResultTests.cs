using ApexLab.Protocols.F125.Decoding;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125DecodeResultTests
{
    [TestMethod]
    public void DecodedResultPublishesOnlyValueAndValidatedHeader()
    {
        var header = CreateHeader();
        var value = new TestPacket(42);

        var result = F125DecodeResult<TestPacket>.Decoded(value, header);

        Assert.AreEqual(F125DecodeDisposition.Decoded, result.Disposition);
        Assert.AreEqual(F125DecodeReason.None, result.Reason);
        Assert.IsTrue(result.IsDecoded);
        Assert.IsFalse(result.IsIgnored);
        Assert.IsFalse(result.IsRejected);
        Assert.AreEqual(header, result.Header);
        Assert.AreEqual(value, result.Value);
    }

    [TestMethod]
    public void IgnoredResultHasNoValueAndOnlyTheOutOfSliceEventReason()
    {
        var header = CreateHeader();

        var result = F125DecodeResult<TestPacket>.Ignored(
            F125DecodeReason.EventCodeOutsideSlice,
            header);

        Assert.AreEqual(F125DecodeDisposition.Ignored, result.Disposition);
        Assert.AreEqual(F125DecodeReason.EventCodeOutsideSlice, result.Reason);
        Assert.IsFalse(result.IsDecoded);
        Assert.IsTrue(result.IsIgnored);
        Assert.IsFalse(result.IsRejected);
        Assert.AreEqual(header, result.Header);
        Assert.IsFalse(result.Value.HasValue);
    }

    [TestMethod]
    public void RejectedResultHasNoTypedValue()
    {
        var header = CreateHeader();

        var result = F125DecodeResult<TestPacket>.Rejected(
            F125DecodeReason.InvalidPlayerCarIndex,
            header);

        Assert.AreEqual(F125DecodeDisposition.Rejected, result.Disposition);
        Assert.AreEqual(F125DecodeReason.InvalidPlayerCarIndex, result.Reason);
        Assert.IsFalse(result.IsDecoded);
        Assert.IsFalse(result.IsIgnored);
        Assert.IsTrue(result.IsRejected);
        Assert.AreEqual(header, result.Header);
        Assert.IsFalse(result.Value.HasValue);
    }

    [TestMethod]
    public void FactoriesRejectInvalidReasonCombinations()
    {
        var header = CreateHeader();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            F125DecodeResult<TestPacket>.Ignored(F125DecodeReason.None, header));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            F125DecodeResult<TestPacket>.Ignored(F125DecodeReason.MalformedHeader, header));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            F125DecodeResult<TestPacket>.Rejected(F125DecodeReason.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            F125DecodeResult<TestPacket>.Rejected(
                F125DecodeReason.EventCodeOutsideSlice,
                header));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            F125DecodeResult<TestPacket>.Rejected((F125DecodeReason)int.MaxValue));
    }

    [TestMethod]
    public void PublicResultSurfaceCannotRetainTransportOrStorageData()
    {
        string[] forbidden =
        [
            "Payload",
            "Datagram",
            "Span",
            "Memory",
            "Sender",
            "Path",
            "CaptureId",
            "Sha256",
        ];
        var names = typeof(F125DecodeResult<TestPacket>)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, names);
        }

        Assert.HasCount(0, typeof(F125DecodeResult<TestPacket>).GetConstructors());
    }

    private static TelemetryHeaderMetadata CreateHeader() =>
        new(
            packetFormat: 2025,
            gameYear: 25,
            gameMajorVersion: 1,
            gameMinorVersion: 7,
            packetVersion: 1,
            packetId: 6,
            sessionUid: 1,
            sessionTimeSeconds: 2F,
            frameIdentifier: 3,
            overallFrameIdentifier: 4,
            playerCarIndex: 5,
            secondaryPlayerCarIndex: byte.MaxValue);

    private readonly record struct TestPacket(int Value);
}
