using ApexLab.Protocols.F125.Canonical;
using ApexLab.Protocols.F125.Decoding;
using ApexLab.Protocols.F125.Tests.Decoding;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Protocols.F125.Tests.Canonical;

[TestClass]
public sealed class F125BahrainCanonicalProjectorTests
{
    private readonly F125BahrainCanonicalProjector _projector = new();

    [TestMethod]
    public void PublishesTheFrozenVersionIdentities()
    {
        Assert.AreEqual(F125Protocol.Id, _projector.ProtocolId);
        Assert.AreEqual("apexlab-bahrain-tt-slice-v1", _projector.ContractId);
        Assert.AreEqual(F125BahrainPacketDecoder.DecoderId, _projector.DecoderId);
        Assert.AreEqual("apexlab-canonical-sample-v1", _projector.CanonicalSchemaId);
    }

    [TestMethod]
    public void ProjectsMotionWithExactHeaderAndFloatBits()
    {
        var datagram = Packet(0, 1_349, playerIndex: 21);
        var memberBase = 29 + (21 * 60);
        F125TestDatagramBuilder.WriteSingle(datagram, memberBase, -0.0F);
        F125TestDatagramBuilder.WriteSingle(datagram, memberBase + 4, -2.5F);
        F125TestDatagramBuilder.WriteSingle(datagram, memberBase + 8, 3.75F);

        var result = _projector.Project(datagram);

        AssertProjected(result, CanonicalPacketFamily.Motion);
        var packet = result.Packet!.Value;
        AssertHeader(packet.Header, playerIndex: 21);
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(-0.0F),
            BitConverter.SingleToInt32Bits(
                packet.Motion!.Value.WorldPositionXMetres));
        Assert.AreEqual(-2.5F, packet.Motion.Value.WorldPositionYMetres);
        Assert.AreEqual(3.75F, packet.Motion.Value.WorldPositionZMetres);
    }

    [TestMethod]
    public void ProjectsEverySelectedSessionField()
    {
        var datagram = SessionPacket();

        var result = _projector.Project(datagram);

        AssertProjected(result, CanonicalPacketFamily.Session);
        var value = result.Packet!.Value.Session!.Value;
        Assert.AreEqual((byte)5, value.Weather);
        Assert.AreEqual((sbyte)-12, value.TrackTemperatureCelsius);
        Assert.AreEqual((sbyte)-3, value.AirTemperatureCelsius);
        Assert.AreEqual((ushort)5_412, value.TrackLengthMetres);
        Assert.AreEqual((byte)18, value.SessionType);
        Assert.AreEqual((sbyte)3, value.TrackId);
        Assert.AreEqual((byte)9, value.Formula);
        Assert.IsTrue(value.IsSpectating);
        Assert.IsFalse(value.IsNetworkGame);
        Assert.AreEqual((byte)1, value.SteeringAssist);
        Assert.AreEqual((byte)3, value.BrakingAssist);
        Assert.AreEqual((byte)2, value.GearboxAssist);
        Assert.AreEqual((byte)1, value.PitAssist);
        Assert.AreEqual((byte)0, value.PitReleaseAssist);
        Assert.AreEqual((byte)1, value.ErsAssist);
        Assert.AreEqual((byte)0, value.DrsAssist);
        Assert.AreEqual((byte)2, value.DynamicRacingLine);
        Assert.AreEqual((byte)1, value.DynamicRacingLineType);
        Assert.AreEqual((byte)5, value.GameMode);
        Assert.AreEqual((byte)2, value.RuleSet);
        Assert.AreEqual(1_439U, value.TimeOfDayMinutesSinceMidnight);
        Assert.IsTrue(value.EqualCarPerformance);
        Assert.AreEqual((byte)2, value.RecoveryMode);
    }

    [TestMethod]
    public void ProjectsLapAndTelemetryAtTheSelectedPlayerStride()
    {
        const byte playerIndex = 7;
        var lap = Packet(2, 1_285, playerIndex);
        var lapBase = 29 + (playerIndex * 57);
        F125TestDatagramBuilder.WriteUInt32(lap, lapBase, 0xF1020304U);
        F125TestDatagramBuilder.WriteUInt32(lap, lapBase + 4, 0xA5060708U);
        F125TestDatagramBuilder.WriteSingle(lap, lapBase + 20, -25.5F);
        F125TestDatagramBuilder.WriteSingle(lap, lapBase + 24, 12_345.25F);
        lap[lapBase + 33] = 40;
        lap[lapBase + 34] = 2;
        lap[lapBase + 36] = 1;
        lap[lapBase + 37] = 1;
        lap[lapBase + 44] = 4;
        lap[lapBase + 45] = 7;

        var telemetry = Packet(6, 1_352, playerIndex);
        var telemetryBase = 29 + (playerIndex * 60);
        F125TestDatagramBuilder.WriteUInt16(telemetry, telemetryBase, 312);
        F125TestDatagramBuilder.WriteSingle(telemetry, telemetryBase + 2, 0.75F);
        F125TestDatagramBuilder.WriteSingle(telemetry, telemetryBase + 10, 0.25F);
        telemetry[telemetryBase + 15] = unchecked((byte)-1);

        var lapResult = _projector.Project(lap);
        var telemetryResult = _projector.Project(telemetry);

        AssertProjected(lapResult, CanonicalPacketFamily.LapData);
        var lapValue = lapResult.Packet!.Value.Lap!.Value;
        Assert.AreEqual(0xF1020304U, lapValue.LastLapTimeMilliseconds);
        Assert.AreEqual(0xA5060708U, lapValue.CurrentLapTimeMilliseconds);
        Assert.AreEqual(-25.5F, lapValue.LapDistanceMetres);
        Assert.AreEqual(12_345.25F, lapValue.TotalDistanceMetres);
        Assert.AreEqual((byte)40, lapValue.CurrentLapNumber);
        Assert.AreEqual((byte)2, lapValue.PitStatus);
        Assert.AreEqual((byte)1, lapValue.Sector);
        Assert.IsTrue(lapValue.CurrentLapInvalid);
        Assert.AreEqual((byte)4, lapValue.DriverStatus);
        Assert.AreEqual((byte)7, lapValue.ResultStatus);

        AssertProjected(telemetryResult, CanonicalPacketFamily.CarTelemetry);
        var telemetryValue = telemetryResult.Packet!.Value.CarTelemetry!.Value;
        Assert.AreEqual((ushort)312, telemetryValue.SpeedKilometresPerHour);
        Assert.AreEqual(0.75F, telemetryValue.ThrottleRatio);
        Assert.AreEqual(0.25F, telemetryValue.BrakeRatio);
        Assert.AreEqual((sbyte)-1, telemetryValue.Gear);
    }

    [TestMethod]
    public void ProjectsAllSelectedEventsAndExcludesOtherAsciiEvents()
    {
        var started = _projector.Project(EventPacket("SSTA"));
        var ended = _projector.Project(EventPacket("SEND"));
        var flashbackBytes = EventPacket("FLBK");
        F125TestDatagramBuilder.WriteUInt32(flashbackBytes, 33, 0xF1234567U);
        F125TestDatagramBuilder.WriteSingle(flashbackBytes, 37, -12.5F);
        var flashback = _projector.Project(flashbackBytes);
        var ignored = _projector.Project(EventPacket("FTLP"));

        Assert.AreEqual(
            CanonicalEventKind.SessionStarted,
            started.Packet!.Value.Event!.Value.Kind);
        Assert.AreEqual(
            CanonicalEventKind.SessionEnded,
            ended.Packet!.Value.Event!.Value.Kind);
        var flashbackValue = flashback.Packet!.Value.Event!.Value;
        Assert.AreEqual(CanonicalEventKind.Flashback, flashbackValue.Kind);
        Assert.AreEqual(0xF1234567U, flashbackValue.FlashbackFrameIdentifier);
        Assert.AreEqual(-12.5F, flashbackValue.FlashbackSessionTimeSeconds);
        Assert.AreEqual(CanonicalProjectionDisposition.Excluded, ignored.Disposition);
        Assert.AreEqual(CanonicalProjectionReason.EventCodeOutsideSlice, ignored.Reason);
        Assert.AreEqual((byte)3, ignored.PacketId);
        Assert.IsNull(ignored.Packet);
    }

    [TestMethod]
    public void ExcludesCompatibleNonSliceFamilyAndRejectsInvalidInput()
    {
        var outsideSlice = _projector.Project(Packet(7, 1_239));
        var invalidLength = _projector.Project(Packet(0, 1_348));
        var invalidPlayer = _projector.Project(Packet(0, 1_349, playerIndex: 22));
        var privacyExcluded = _projector.Project(Packet(4, 1_284));

        Assert.AreEqual(CanonicalProjectionDisposition.Excluded, outsideSlice.Disposition);
        Assert.AreEqual(
            CanonicalProjectionReason.CompatibleFamilyOutsideSlice,
            outsideSlice.Reason);
        Assert.AreEqual((byte)7, outsideSlice.PacketId);
        AssertRejected(invalidLength, CanonicalProjectionReason.InvalidPacketLength);
        AssertRejected(invalidPlayer, CanonicalProjectionReason.InvalidPlayerCarIndex);
        AssertRejected(privacyExcluded, CanonicalProjectionReason.PrivacyExcludedPacket);
    }

    [TestMethod]
    public void FixedSeedArbitraryDatagramsNeverThrowOrReportUnspecified()
    {
        var random = new Random(0xCA125);
        for (var sample = 0; sample < 2_048; sample++)
        {
            var datagram = new byte[random.Next(0, 2_050)];
            random.NextBytes(datagram);

            var result = _projector.Project(datagram);

            Assert.AreNotEqual(
                CanonicalProjectionDisposition.Unspecified,
                result.Disposition,
                $"sample {sample}");
            Assert.IsTrue(Enum.IsDefined(result.Reason), $"sample {sample}");
        }
    }

    private static byte[] Packet(byte packetId, int length, byte playerIndex = 3) =>
        F125TestDatagramBuilder.Create(
            packetId,
            length,
            playerCarIndex: playerIndex);

    private static byte[] SessionPacket()
    {
        var datagram = Packet(1, 753);
        datagram[29] = 5;
        datagram[30] = unchecked((byte)-12);
        datagram[31] = unchecked((byte)-3);
        F125TestDatagramBuilder.WriteUInt16(datagram, 33, 5_412);
        datagram[35] = 18;
        datagram[36] = 3;
        datagram[37] = 9;
        datagram[44] = 1;
        datagram[154] = 0;
        datagram[685] = 1;
        datagram[686] = 3;
        datagram[687] = 2;
        datagram[688] = 1;
        datagram[689] = 0;
        datagram[690] = 1;
        datagram[691] = 0;
        datagram[692] = 2;
        datagram[693] = 1;
        datagram[694] = 5;
        datagram[695] = 2;
        F125TestDatagramBuilder.WriteUInt32(datagram, 696, 1_439);
        datagram[708] = 1;
        datagram[709] = 2;
        return datagram;
    }

    private static byte[] EventPacket(string code)
    {
        var datagram = Packet(3, 45);
        F125TestDatagramBuilder.WriteAscii4(datagram, 29, code);
        return datagram;
    }

    private static void AssertHeader(
        CanonicalPacketHeader header,
        byte playerIndex)
    {
        Assert.AreEqual(12.5F, header.SessionTimeSeconds);
        Assert.AreEqual(0x10203040U, header.FrameIdentifier);
        Assert.AreEqual(0x50607080U, header.OverallFrameIdentifier);
        Assert.AreEqual(playerIndex, header.PlayerCarIndex);
        Assert.AreEqual(byte.MaxValue, header.SecondaryPlayerCarIndex);
    }

    private static void AssertProjected(
        CanonicalProjectionResult result,
        CanonicalPacketFamily family)
    {
        Assert.AreEqual(CanonicalProjectionDisposition.Projected, result.Disposition);
        Assert.AreEqual(CanonicalProjectionReason.None, result.Reason);
        Assert.IsTrue(result.Packet.HasValue);
        Assert.AreEqual(family, result.Packet.Value.Family);
        Assert.IsNull(result.PacketId);
    }

    private static void AssertRejected(
        CanonicalProjectionResult result,
        CanonicalProjectionReason reason)
    {
        Assert.AreEqual(CanonicalProjectionDisposition.Rejected, result.Disposition);
        Assert.AreEqual(reason, result.Reason);
        Assert.IsNull(result.Packet);
    }
}
