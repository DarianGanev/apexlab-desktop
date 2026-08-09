using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125MotionDecoderTests
{
    private const int DatagramLength = 1349;
    private const int ArrayBaseOffset = 29;
    private const int Stride = 60;

    [TestMethod]
    public void DecodesOnlyTheValidatedPlayerAtEveryArrayBoundary()
    {
        foreach (var playerIndex in new byte[] { 0, 3, 21 })
        {
            var datagram = CreateGoldenDatagram(playerIndex);

            var result = F125BahrainPacketDecoder.DecodeMotion(datagram);

            Assert.IsTrue(result.IsDecoded);
            Assert.AreEqual(F125DecodeReason.None, result.Reason);
            Assert.IsTrue(result.Value.HasValue);
            var value = result.Value.Value;
            Assert.AreEqual(playerIndex, value.Header.PlayerCarIndex);
            Assert.AreEqual(1000.25F + playerIndex, value.WorldPositionXMetres);
            Assert.AreEqual(-2000.5F - playerIndex, value.WorldPositionYMetres);
            Assert.AreEqual(3000.75F + playerIndex, value.WorldPositionZMetres);
        }
    }

    [TestMethod]
    public void FiniteNegativeCoordinatesArePreservedWithoutConversion()
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 0,
            length: DatagramLength,
            playerCarIndex: 0);
        F125TestDatagramBuilder.WriteSingle(datagram, ArrayBaseOffset, -0.0F);
        F125TestDatagramBuilder.WriteSingle(datagram, ArrayBaseOffset + 4, -1.25F);
        F125TestDatagramBuilder.WriteSingle(datagram, ArrayBaseOffset + 8, -98765.5F);

        var result = F125BahrainPacketDecoder.DecodeMotion(datagram);

        Assert.IsTrue(result.IsDecoded);
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(-0.0F),
            BitConverter.SingleToInt32Bits(result.Value!.Value.WorldPositionXMetres));
        Assert.AreEqual(-1.25F, result.Value.Value.WorldPositionYMetres);
        Assert.AreEqual(-98765.5F, result.Value.Value.WorldPositionZMetres);
    }

    [TestMethod]
    public void EveryNonFiniteSelectedCoordinateRejectsTheWholePacket()
    {
        int[] memberOffsets = [0, 4, 8];
        int[] invalidBits =
        [
            0x7F800000,
            unchecked((int)0xFF800000),
            0x7FC01234,
        ];

        foreach (var memberOffset in memberOffsets)
        {
            foreach (var bits in invalidBits)
            {
                var datagram = CreateGoldenDatagram(playerIndex: 3);
                F125TestDatagramBuilder.WriteUInt32(
                    datagram,
                    ArrayBaseOffset + (3 * Stride) + memberOffset,
                    unchecked((uint)bits));

                var result = F125BahrainPacketDecoder.DecodeMotion(datagram);

                Assert.IsTrue(result.IsRejected);
                Assert.AreEqual(F125DecodeReason.MalformedSelectedField, result.Reason);
                Assert.IsFalse(result.Value.HasValue);
                Assert.AreEqual((byte)3, result.Header!.Value.PlayerCarIndex);
            }
        }
    }

    [TestMethod]
    public void CommonGateFailureNeverPublishesMotionValue()
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 0,
            length: DatagramLength,
            playerCarIndex: 22);

        var result = F125BahrainPacketDecoder.DecodeMotion(datagram);

        Assert.IsTrue(result.IsRejected);
        Assert.AreEqual(F125DecodeReason.InvalidPlayerCarIndex, result.Reason);
        Assert.IsFalse(result.Value.HasValue);
    }

    private static byte[] CreateGoldenDatagram(byte playerIndex)
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 0,
            length: DatagramLength,
            playerCarIndex: playerIndex);
        for (var index = 0; index < 22; index++)
        {
            var memberBase = ArrayBaseOffset + (index * Stride);
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase,
                1000.25F + index);
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 4,
                -2000.5F - index);
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 8,
                3000.75F + index);
        }

        return datagram;
    }
}
