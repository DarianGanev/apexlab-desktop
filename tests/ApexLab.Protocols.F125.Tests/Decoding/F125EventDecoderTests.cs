using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125EventDecoderTests
{
    private const int DatagramLength = 45;

    [TestMethod]
    public void DecodesSelectedEventsWithoutReadingUnrelatedUnionBytes()
    {
        (string Code, F125SliceEventKind Kind)[] selected =
        [
            ("SSTA", F125SliceEventKind.SessionStarted),
            ("SEND", F125SliceEventKind.SessionEnded),
        ];

        foreach (var testCase in selected)
        {
            var datagram = CreateEvent(testCase.Code);
            F125TestDatagramBuilder.WriteUInt32(datagram, 37, 0x7FC01234);

            var result = F125BahrainPacketDecoder.DecodeEvent(datagram);

            Assert.IsTrue(result.IsDecoded, testCase.Code);
            Assert.AreEqual(testCase.Kind, result.Value!.Value.Kind);
            Assert.IsFalse(result.Value.Value.FlashbackFrameIdentifier.HasValue);
            Assert.IsFalse(result.Value.Value.FlashbackSessionTimeSeconds.HasValue);
        }
    }

    [TestMethod]
    public void FlashbackDecodesItsExactUnionMember()
    {
        var datagram = CreateEvent("FLBK");
        F125TestDatagramBuilder.WriteUInt32(datagram, 33, 0xF1234567U);
        F125TestDatagramBuilder.WriteSingle(datagram, 37, -12.5F);

        var result = F125BahrainPacketDecoder.DecodeEvent(datagram);

        Assert.IsTrue(result.IsDecoded);
        Assert.AreEqual(F125SliceEventKind.Flashback, result.Value!.Value.Kind);
        Assert.AreEqual(0xF1234567U, result.Value.Value.FlashbackFrameIdentifier);
        Assert.AreEqual(-12.5F, result.Value.Value.FlashbackSessionTimeSeconds);
    }

    [TestMethod]
    public void OtherValidAsciiEventIsDeliberatelyIgnored()
    {
        var datagram = CreateEvent("FTLP");

        var result = F125BahrainPacketDecoder.DecodeEvent(datagram);

        Assert.IsTrue(result.IsIgnored);
        Assert.AreEqual(F125DecodeReason.EventCodeOutsideSlice, result.Reason);
        Assert.IsFalse(result.Value.HasValue);
        Assert.AreEqual((byte)3, result.Header!.Value.PacketId);
    }

    [TestMethod]
    public void NonAsciiEventCodeIsRejectedAsMalformed()
    {
        var datagram = CreateEvent("SSTA");
        datagram[31] = 0xFF;

        var result = F125BahrainPacketDecoder.DecodeEvent(datagram);

        Assert.IsTrue(result.IsRejected);
        Assert.AreEqual(F125DecodeReason.MalformedSelectedField, result.Reason);
        Assert.IsFalse(result.Value.HasValue);
    }

    [TestMethod]
    public void NonFiniteFlashbackTimeRejectsWithoutPublishingEventData()
    {
        foreach (var bits in new[] { 0x7F800000, 0x7FC01234 })
        {
            var datagram = CreateEvent("FLBK");
            F125TestDatagramBuilder.WriteUInt32(datagram, 33, 42);
            F125TestDatagramBuilder.WriteUInt32(datagram, 37, unchecked((uint)bits));

            var result = F125BahrainPacketDecoder.DecodeEvent(datagram);

            Assert.IsTrue(result.IsRejected);
            Assert.AreEqual(F125DecodeReason.MalformedSelectedField, result.Reason);
            Assert.IsFalse(result.Value.HasValue);
        }
    }

    private static byte[] CreateEvent(string code)
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 3,
            length: DatagramLength);
        F125TestDatagramBuilder.WriteAscii4(datagram, 29, code);
        return datagram;
    }
}
