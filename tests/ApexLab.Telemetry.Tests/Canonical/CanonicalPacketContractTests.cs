using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Telemetry.Tests.Canonical;

[TestClass]
public sealed class CanonicalPacketContractTests
{
    [TestMethod]
    public void HeaderRequiresFiniteSessionTimeAndSupportedPlayerIndex()
    {
        var header = Header();

        Assert.AreEqual(12.5F, header.SessionTimeSeconds);
        Assert.AreEqual(0x10203040U, header.FrameIdentifier);
        Assert.AreEqual(0x50607080U, header.OverallFrameIdentifier);
        Assert.AreEqual((byte)3, header.PlayerCarIndex);
        Assert.AreEqual(byte.MaxValue, header.SecondaryPlayerCarIndex);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Header(float.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Header(float.PositiveInfinity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Header(playerCarIndex: 22));
    }

    [TestMethod]
    public void PacketFactoriesPublishExactlyOneTypedPayload()
    {
        var header = Header();
        var motion = new CanonicalMotionPacket(1.25F, -2.5F, 3.75F);
        var packet = CanonicalPacket.CreateMotion(header, motion);

        Assert.AreEqual(CanonicalPacketFamily.Motion, packet.Family);
        Assert.AreEqual(header, packet.Header);
        Assert.AreEqual(motion, packet.Motion);
        Assert.IsNull(packet.Session);
        Assert.IsNull(packet.Lap);
        Assert.IsNull(packet.Event);
        Assert.IsNull(packet.CarTelemetry);

        Assert.AreEqual(
            CanonicalPacketFamily.Session,
            CanonicalPacket.CreateSession(header, Session()).Family);
        Assert.AreEqual(
            CanonicalPacketFamily.LapData,
            CanonicalPacket.CreateLapData(header, Lap()).Family);
        Assert.AreEqual(
            CanonicalPacketFamily.Event,
            CanonicalPacket.CreateEvent(
                header,
                CanonicalEventPacket.SessionStarted()).Family);
        Assert.AreEqual(
            CanonicalPacketFamily.CarTelemetry,
            CanonicalPacket.CreateCarTelemetry(header, Telemetry()).Family);
    }

    [TestMethod]
    public void FamilyPayloadsEnforceFiniteAndBoundedDomains()
    {
        _ = new CanonicalMotionPacket(-1F, 0F, 1F);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new CanonicalMotionPacket(float.NaN, 0F, 0F));

        _ = Session();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Session(weather: 6));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Session(gearboxAssist: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Session(timeOfDayMinutesSinceMidnight: 1_440));

        _ = Lap();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Lap(lapDistanceMetres: float.NegativeInfinity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Lap(sector: 3));

        _ = Telemetry();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Telemetry(throttleRatio: -0.01F));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Telemetry(brakeRatio: float.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Telemetry(gear: 9));
    }

    [TestMethod]
    public void EventFactoriesEnforceTheDiscriminatedUnion()
    {
        var started = CanonicalEventPacket.SessionStarted();
        var ended = CanonicalEventPacket.SessionEnded();
        var flashback = CanonicalEventPacket.Flashback(42, 9.5F);

        Assert.AreEqual(CanonicalEventKind.SessionStarted, started.Kind);
        Assert.IsNull(started.FlashbackFrameIdentifier);
        Assert.IsNull(started.FlashbackSessionTimeSeconds);
        Assert.AreEqual(CanonicalEventKind.SessionEnded, ended.Kind);
        Assert.AreEqual(CanonicalEventKind.Flashback, flashback.Kind);
        Assert.AreEqual(42U, flashback.FlashbackFrameIdentifier);
        Assert.AreEqual(9.5F, flashback.FlashbackSessionTimeSeconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CanonicalEventPacket.Flashback(1, float.NaN));
    }

    [TestMethod]
    public void ProjectionResultsAllowOnlySpecifiedDispositionReasonPairs()
    {
        var packet = CanonicalPacket.CreateMotion(
            Header(),
            new CanonicalMotionPacket(1F, 2F, 3F));
        var projected = CanonicalProjectionResult.Projected(packet);
        var excluded = CanonicalProjectionResult.Excluded(
            packetId: 7,
            CanonicalProjectionReason.CompatibleFamilyOutsideSlice);
        var rejected = CanonicalProjectionResult.Rejected(
            CanonicalProjectionReason.InvalidPacketLength,
            packetId: 0);

        Assert.AreEqual(CanonicalProjectionDisposition.Projected, projected.Disposition);
        Assert.AreEqual(CanonicalProjectionReason.None, projected.Reason);
        Assert.AreEqual(packet, projected.Packet);
        Assert.AreEqual(CanonicalProjectionDisposition.Excluded, excluded.Disposition);
        Assert.AreEqual((byte)7, excluded.PacketId);
        Assert.IsNull(excluded.Packet);
        Assert.AreEqual(CanonicalProjectionDisposition.Rejected, rejected.Disposition);
        Assert.AreEqual((byte)0, rejected.PacketId);
        Assert.IsNull(rejected.Packet);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalProjectionResult.Excluded(
                7,
                CanonicalProjectionReason.InvalidPacketLength));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalProjectionResult.Rejected(CanonicalProjectionReason.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalProjectionResult.Rejected(
                CanonicalProjectionReason.EventCodeOutsideSlice));
    }

    [TestMethod]
    public void PublicCanonicalModelDoesNotExposeRawTransportOrStorage()
    {
        Type[] publicTypes =
        [
            typeof(CanonicalPacketHeader),
            typeof(CanonicalPacket),
            typeof(CanonicalMotionPacket),
            typeof(CanonicalSessionPacket),
            typeof(CanonicalLapPacket),
            typeof(CanonicalEventPacket),
            typeof(CanonicalCarTelemetryPacket),
            typeof(CanonicalProjectionResult),
        ];
        string[] forbidden =
        [
            "Payload", "Sender", "ReceivedAt", "SessionUid", "Path", "File",
            "Storage", "CaptureId", "OtherCar", "Participant",
        ];

        foreach (var type in publicTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.IsFalse(
                    forbidden.Any(term => property.Name.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name}.{property.Name}");
            }
        }
    }

    private static CanonicalPacketHeader Header(
        float sessionTimeSeconds = 12.5F,
        byte playerCarIndex = 3) =>
        new(
            sessionTimeSeconds,
            frameIdentifier: 0x10203040U,
            overallFrameIdentifier: 0x50607080U,
            playerCarIndex,
            secondaryPlayerCarIndex: byte.MaxValue);

    private static CanonicalSessionPacket Session(
        byte weather = 1,
        byte gearboxAssist = 2,
        uint timeOfDayMinutesSinceMidnight = 720) =>
        new(
            weather,
            trackTemperatureCelsius: 31,
            airTemperatureCelsius: 24,
            trackLengthMetres: 5_412,
            sessionType: 18,
            trackId: 3,
            formula: 0,
            isSpectating: false,
            isNetworkGame: false,
            steeringAssist: 0,
            brakingAssist: 0,
            gearboxAssist,
            pitAssist: 0,
            pitReleaseAssist: 0,
            ersAssist: 0,
            drsAssist: 0,
            dynamicRacingLine: 0,
            dynamicRacingLineType: 0,
            gameMode: 5,
            ruleSet: 2,
            timeOfDayMinutesSinceMidnight,
            equalCarPerformance: true,
            recoveryMode: 0);

    private static CanonicalLapPacket Lap(
        float lapDistanceMetres = 123.5F,
        byte sector = 1) =>
        new(
            lastLapTimeMilliseconds: 90_000,
            currentLapTimeMilliseconds: 10_000,
            lapDistanceMetres,
            totalDistanceMetres: 5_535.5F,
            currentLapNumber: 2,
            pitStatus: 0,
            sector,
            currentLapInvalid: false,
            driverStatus: 1,
            resultStatus: 2);

    private static CanonicalCarTelemetryPacket Telemetry(
        float throttleRatio = 0.75F,
        float brakeRatio = 0.25F,
        sbyte gear = 4) =>
        new(
            speedKilometresPerHour: 287,
            throttleRatio,
            brakeRatio,
            gear);
}
