using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125LapDataDecoderTests
{
    private const int DatagramLength = 1285;
    private const int ArrayBaseOffset = 29;
    private const int Stride = 57;

    [TestMethod]
    public void DecodesOnlyTheSelectedPlayerAtEveryArrayBoundary()
    {
        foreach (var playerIndex in new byte[] { 0, 7, 21 })
        {
            var datagram = CreateGoldenDatagram(playerIndex);

            var result = F125BahrainPacketDecoder.DecodeLapData(datagram);

            Assert.IsTrue(result.IsDecoded);
            var value = result.Value!.Value;
            Assert.AreEqual(0xF1020304U + playerIndex, value.LastLapTimeMilliseconds);
            Assert.AreEqual(0xA5060708U + playerIndex, value.CurrentLapTimeMilliseconds);
            Assert.AreEqual(-25.5F + playerIndex, value.LapDistanceMetres);
            Assert.AreEqual(12345.25F + playerIndex, value.TotalDistanceMetres);
            Assert.AreEqual((byte)(40 + playerIndex), value.CurrentLapNumber);
            Assert.AreEqual((byte)2, value.PitStatus);
            Assert.AreEqual((byte)2, value.Sector);
            Assert.IsTrue(value.CurrentLapInvalid);
            Assert.AreEqual((byte)4, value.DriverStatus);
            Assert.AreEqual((byte)7, value.ResultStatus);
        }
    }

    [TestMethod]
    public void FiniteNegativePreLineDistancesArePreserved()
    {
        var datagram = CreateGoldenDatagram(playerIndex: 0);
        F125TestDatagramBuilder.WriteSingle(datagram, ArrayBaseOffset + 20, -500.75F);
        F125TestDatagramBuilder.WriteSingle(datagram, ArrayBaseOffset + 24, -12.25F);

        var result = F125BahrainPacketDecoder.DecodeLapData(datagram);

        Assert.IsTrue(result.IsDecoded);
        Assert.AreEqual(-500.75F, result.Value!.Value.LapDistanceMetres);
        Assert.AreEqual(-12.25F, result.Value.Value.TotalDistanceMetres);
    }

    [TestMethod]
    public void NonFiniteSelectedDistanceRejectsWithoutPublishingLapData()
    {
        foreach (var memberOffset in new[] { 20, 24 })
        {
            foreach (var bits in new[] { 0x7F800000, 0x7FC01234 })
            {
                var datagram = CreateGoldenDatagram(playerIndex: 7);
                F125TestDatagramBuilder.WriteUInt32(
                    datagram,
                    ArrayBaseOffset + (7 * Stride) + memberOffset,
                    unchecked((uint)bits));

                var result = F125BahrainPacketDecoder.DecodeLapData(datagram);

                Assert.IsTrue(result.IsRejected);
                Assert.AreEqual(F125DecodeReason.MalformedSelectedField, result.Reason);
                Assert.IsFalse(result.Value.HasValue);
            }
        }
    }

    [TestMethod]
    public void EveryMalformedBoundedStatusRejectsTheWholePacket()
    {
        (string Name, int MemberOffset, byte Value)[] malformed =
        [
            ("pitStatus", 34, 3),
            ("sector", 36, 3),
            ("currentLapInvalid", 37, 2),
            ("driverStatus", 44, 5),
            ("resultStatus", 45, 8),
        ];

        foreach (var testCase in malformed)
        {
            var datagram = CreateGoldenDatagram(playerIndex: 7);
            datagram[ArrayBaseOffset + (7 * Stride) + testCase.MemberOffset] = testCase.Value;

            var result = F125BahrainPacketDecoder.DecodeLapData(datagram);

            Assert.IsTrue(result.IsRejected, testCase.Name);
            Assert.AreEqual(
                F125DecodeReason.MalformedSelectedField,
                result.Reason,
                testCase.Name);
            Assert.IsFalse(result.Value.HasValue, testCase.Name);
        }
    }

    private static byte[] CreateGoldenDatagram(byte playerIndex)
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 2,
            length: DatagramLength,
            playerCarIndex: playerIndex);
        for (var index = 0; index < 22; index++)
        {
            var memberBase = ArrayBaseOffset + (index * Stride);
            F125TestDatagramBuilder.WriteUInt32(
                datagram,
                memberBase,
                0xF1020304U + (uint)index);
            F125TestDatagramBuilder.WriteUInt32(
                datagram,
                memberBase + 4,
                0xA5060708U + (uint)index);
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 20,
                -25.5F + index);
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 24,
                12345.25F + index);
            datagram[memberBase + 33] = (byte)(40 + index);
            datagram[memberBase + 34] = 2;
            datagram[memberBase + 36] = 2;
            datagram[memberBase + 37] = 1;
            datagram[memberBase + 44] = 4;
            datagram[memberBase + 45] = 7;
        }

        return datagram;
    }
}
