using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125CarTelemetryDecoderTests
{
    private const int DatagramLength = 1352;
    private const int ArrayBaseOffset = 29;
    private const int Stride = 60;

    [TestMethod]
    public void DecodesOnlyTheSelectedPlayerAtEveryArrayBoundary()
    {
        foreach (var playerIndex in new byte[] { 0, 11, 21 })
        {
            var datagram = CreateGoldenDatagram(playerIndex);

            var result = F125BahrainPacketDecoder.DecodeCarTelemetry(datagram);

            Assert.IsTrue(result.IsDecoded);
            var value = result.Value!.Value;
            Assert.AreEqual((ushort)(300 + playerIndex), value.SpeedKilometresPerHour);
            Assert.AreEqual(0.10F + (playerIndex / 100F), value.ThrottleRatio);
            Assert.AreEqual(0.90F - (playerIndex / 100F), value.BrakeRatio);
            Assert.AreEqual((sbyte)-1, value.Gear);
        }
    }

    [TestMethod]
    public void ReverseNeutralAndHighestForwardGearArePreserved()
    {
        foreach (var gear in new sbyte[] { -1, 0, 8 })
        {
            var datagram = CreateGoldenDatagram(playerIndex: 11);
            datagram[ArrayBaseOffset + (11 * Stride) + 15] = unchecked((byte)gear);

            var result = F125BahrainPacketDecoder.DecodeCarTelemetry(datagram);

            Assert.IsTrue(result.IsDecoded);
            Assert.AreEqual(gear, result.Value!.Value.Gear);
        }
    }

    [TestMethod]
    public void EveryNonFiniteOrOutOfRangeRatioRejectsTheWholePacket()
    {
        (string Name, int MemberOffset, uint Bits)[] malformed =
        [
            ("negativeThrottle", 2, unchecked((uint)BitConverter.SingleToInt32Bits(-0.01F))),
            ("highThrottle", 2, unchecked((uint)BitConverter.SingleToInt32Bits(1.01F))),
            ("nanThrottle", 2, 0x7FC01234),
            ("negativeBrake", 10, unchecked((uint)BitConverter.SingleToInt32Bits(-0.01F))),
            ("highBrake", 10, unchecked((uint)BitConverter.SingleToInt32Bits(1.01F))),
            ("infiniteBrake", 10, 0x7F800000),
        ];

        foreach (var testCase in malformed)
        {
            var datagram = CreateGoldenDatagram(playerIndex: 11);
            F125TestDatagramBuilder.WriteUInt32(
                datagram,
                ArrayBaseOffset + (11 * Stride) + testCase.MemberOffset,
                testCase.Bits);

            var result = F125BahrainPacketDecoder.DecodeCarTelemetry(datagram);

            Assert.IsTrue(result.IsRejected, testCase.Name);
            Assert.AreEqual(
                F125DecodeReason.MalformedSelectedField,
                result.Reason,
                testCase.Name);
            Assert.IsFalse(result.Value.HasValue, testCase.Name);
        }
    }

    [TestMethod]
    public void GearOutsidePublishedRangeRejectsTheWholePacket()
    {
        foreach (var gear in new sbyte[] { -2, 9 })
        {
            var datagram = CreateGoldenDatagram(playerIndex: 11);
            datagram[ArrayBaseOffset + (11 * Stride) + 15] = unchecked((byte)gear);

            var result = F125BahrainPacketDecoder.DecodeCarTelemetry(datagram);

            Assert.IsTrue(result.IsRejected, gear.ToString());
            Assert.AreEqual(F125DecodeReason.MalformedSelectedField, result.Reason);
            Assert.IsFalse(result.Value.HasValue);
        }
    }

    private static byte[] CreateGoldenDatagram(byte playerIndex)
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 6,
            length: DatagramLength,
            playerCarIndex: playerIndex);
        for (var index = 0; index < 22; index++)
        {
            var memberBase = ArrayBaseOffset + (index * Stride);
            F125TestDatagramBuilder.WriteUInt16(
                datagram,
                memberBase,
                (ushort)(300 + index));
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 2,
                0.10F + (index / 100F));
            F125TestDatagramBuilder.WriteSingle(
                datagram,
                memberBase + 10,
                0.90F - (index / 100F));
            datagram[memberBase + 15] = unchecked((byte)-1);
        }

        return datagram;
    }
}
