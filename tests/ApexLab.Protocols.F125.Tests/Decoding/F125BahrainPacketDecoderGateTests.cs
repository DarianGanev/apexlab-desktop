using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125BahrainPacketDecoderGateTests
{
    [TestMethod]
    public void ExposesTheFrozenDecoderIdentity()
    {
        Assert.AreEqual(
            "f125-v3-minimal-decoder-v1",
            F125BahrainPacketDecoder.DecoderId);
    }

    [TestMethod]
    public void EveryTruncatedHeaderIsRejectedBeforeFamilyChecks()
    {
        for (var length = 0; length < 29; length++)
        {
            var result = F125BahrainPacketDecoder.Validate(
                new byte[length],
                expectedPacketId: 0);

            AssertRejected(result, F125DecodeReason.MalformedHeader);
            Assert.IsFalse(result.Header.HasValue);
        }
    }

    [TestMethod]
    public void CompatibilityFailuresKeepTheExistingDeterministicOrder()
    {
        var unsupportedFormat = F125TestDatagramBuilder.Create(
            packetId: byte.MaxValue,
            length: 29,
            packetVersion: byte.MaxValue,
            packetFormat: 2026,
            gameYear: 26);
        var unsupportedYear = F125TestDatagramBuilder.Create(
            packetId: byte.MaxValue,
            length: 29,
            packetVersion: byte.MaxValue,
            gameYear: 26);
        var unknownPacket = F125TestDatagramBuilder.Create(
            packetId: byte.MaxValue,
            length: 29);
        var wrongVersion = F125TestDatagramBuilder.Create(
            packetId: 0,
            length: 1349,
            packetVersion: 2);

        AssertRejected(
            F125BahrainPacketDecoder.Validate(unsupportedFormat, 0),
            F125DecodeReason.UnsupportedFormat);
        AssertRejected(
            F125BahrainPacketDecoder.Validate(unsupportedYear, 0),
            F125DecodeReason.UnsupportedYear);
        AssertRejected(
            F125BahrainPacketDecoder.Validate(unknownPacket, 0),
            F125DecodeReason.UnknownPacketId);
        AssertRejected(
            F125BahrainPacketDecoder.Validate(wrongVersion, 0),
            F125DecodeReason.UnsupportedPacketVersion);
    }

    [TestMethod]
    public void ValidDifferentFamilyIsRejectedAfterCompatibilityInspection()
    {
        var session = F125TestDatagramBuilder.Create(packetId: 1, length: 753);

        var result = F125BahrainPacketDecoder.Validate(
            session,
            expectedPacketId: 0);

        AssertRejected(result, F125DecodeReason.UnexpectedPacketFamily);
        Assert.AreEqual((byte)1, result.Header!.Value.PacketId);
    }

    [TestMethod]
    public void EverySelectedFamilyRequiresItsExactLength()
    {
        (byte PacketId, int Length)[] selected =
        [
            (0, 1349),
            (1, 753),
            (2, 1285),
            (3, 45),
            (6, 1352),
        ];

        foreach (var packet in selected)
        {
            var tooShort = F125TestDatagramBuilder.Create(
                packet.PacketId,
                packet.Length - 1);
            var tooLong = F125TestDatagramBuilder.Create(
                packet.PacketId,
                packet.Length + 1);

            AssertRejected(
                F125BahrainPacketDecoder.Validate(tooShort, packet.PacketId),
                F125DecodeReason.InvalidPacketLength);
            AssertRejected(
                F125BahrainPacketDecoder.Validate(tooLong, packet.PacketId),
                F125DecodeReason.InvalidPacketLength);
        }
    }

    [TestMethod]
    public void PrivacyExcludedFamilyNeverReachesFieldDecoding()
    {
        var participants = F125TestDatagramBuilder.Create(
            packetId: 4,
            length: 1284);

        var result = F125BahrainPacketDecoder.Validate(
            participants,
            expectedPacketId: 4);

        AssertRejected(result, F125DecodeReason.PrivacyExcludedPacket);
    }

    [TestMethod]
    public void PlayerArrayBoundsAreCheckedBeforeOffsetCalculation()
    {
        foreach (var invalidIndex in new byte[] { 22, byte.MaxValue })
        {
            var datagram = F125TestDatagramBuilder.Create(
                packetId: 0,
                length: 1349,
                playerCarIndex: invalidIndex);

            var result = F125BahrainPacketDecoder.Validate(
                datagram,
                expectedPacketId: 0);

            AssertRejected(result, F125DecodeReason.InvalidPlayerCarIndex);
            Assert.AreEqual(invalidIndex, result.Header!.Value.PlayerCarIndex);
        }
    }

    [TestMethod]
    public void BothPlayerArrayBoundaryIndexesPassTheCommonGate()
    {
        foreach (var playerIndex in new byte[] { 0, 21 })
        {
            var datagram = F125TestDatagramBuilder.Create(
                packetId: 0,
                length: 1349,
                playerCarIndex: playerIndex);

            var result = F125BahrainPacketDecoder.Validate(
                datagram,
                expectedPacketId: 0);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(F125DecodeReason.None, result.Reason);
            Assert.AreEqual(playerIndex, result.Header!.Value.PlayerCarIndex);
        }
    }

    [TestMethod]
    public void FixedSeedArbitraryDatagramsNeverThrowOrReportUnspecified()
    {
        const int seed = 0xB4125;
        var random = new Random(seed);

        for (var sample = 0; sample < 2_048; sample++)
        {
            var datagram = new byte[random.Next(0, 2_050)];
            random.NextBytes(datagram);

            AssertKnown(F125BahrainPacketDecoder.DecodeMotion(datagram), sample);
            AssertKnown(F125BahrainPacketDecoder.DecodeSession(datagram), sample);
            AssertKnown(F125BahrainPacketDecoder.DecodeLapData(datagram), sample);
            AssertKnown(F125BahrainPacketDecoder.DecodeEvent(datagram), sample);
            AssertKnown(F125BahrainPacketDecoder.DecodeCarTelemetry(datagram), sample);
        }
    }

    private static void AssertRejected(
        F125DecodeGateResult result,
        F125DecodeReason expected)
    {
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(expected, result.Reason);
    }

    private static void AssertKnown<TPacket>(
        F125DecodeResult<TPacket> result,
        int sample)
        where TPacket : struct
    {
        Assert.AreNotEqual(
            F125DecodeDisposition.Unspecified,
            result.Disposition,
            $"Seed 0xB4125 sample {sample} returned an unspecified disposition.");
        Assert.IsTrue(Enum.IsDefined(result.Reason), $"Seed 0xB4125 sample {sample}.");
    }
}
