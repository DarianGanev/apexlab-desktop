using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Protocols.F125.Tests.Decoding;

[TestClass]
public sealed class F125SessionDecoderTests
{
    private const int DatagramLength = 753;

    [TestMethod]
    public void DecodesEverySelectedSessionFieldAtItsIndependentAbsoluteOffset()
    {
        var datagram = CreateGoldenDatagram();

        var result = F125BahrainPacketDecoder.DecodeSession(datagram);

        Assert.IsTrue(result.IsDecoded);
        var value = result.Value!.Value;
        Assert.AreEqual((byte)5, value.Weather);
        Assert.AreEqual((sbyte)-12, value.TrackTemperatureCelsius);
        Assert.AreEqual((sbyte)-3, value.AirTemperatureCelsius);
        Assert.AreEqual((ushort)5412, value.TrackLengthMetres);
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
        Assert.AreEqual(1439U, value.TimeOfDayMinutesSinceMidnight);
        Assert.IsTrue(value.EqualCarPerformance);
        Assert.AreEqual((byte)2, value.RecoveryMode);
    }

    [TestMethod]
    public void SignedTrackAndTemperatureValuesArePreservedForLaterAudit()
    {
        var datagram = CreateGoldenDatagram();
        datagram[30] = unchecked((byte)-128);
        datagram[31] = unchecked((byte)-1);
        datagram[36] = unchecked((byte)-1);

        var result = F125BahrainPacketDecoder.DecodeSession(datagram);

        Assert.IsTrue(result.IsDecoded);
        Assert.AreEqual((sbyte)-128, result.Value!.Value.TrackTemperatureCelsius);
        Assert.AreEqual((sbyte)-1, result.Value.Value.AirTemperatureCelsius);
        Assert.AreEqual((sbyte)-1, result.Value.Value.TrackId);
    }

    [TestMethod]
    public void StructurallyValidNonBahrainContextIsNotAParserFailure()
    {
        var datagram = CreateGoldenDatagram();
        datagram[35] = 4;
        datagram[36] = 7;
        datagram[694] = 12;
        datagram[695] = 9;

        var result = F125BahrainPacketDecoder.DecodeSession(datagram);

        Assert.IsTrue(result.IsDecoded);
        Assert.AreEqual((byte)4, result.Value!.Value.SessionType);
        Assert.AreEqual((sbyte)7, result.Value.Value.TrackId);
        Assert.AreEqual((byte)12, result.Value.Value.GameMode);
        Assert.AreEqual((byte)9, result.Value.Value.RuleSet);
    }

    [TestMethod]
    public void EveryMalformedBoundedSessionFieldRejectsTheWholePacket()
    {
        (string Name, Action<byte[]> Mutate)[] malformed =
        [
            ("weather", bytes => bytes[29] = 6),
            ("isSpectating", bytes => bytes[44] = 2),
            ("networkGame", bytes => bytes[154] = 2),
            ("steeringAssist", bytes => bytes[685] = 2),
            ("brakingAssist", bytes => bytes[686] = 4),
            ("gearboxAssistLow", bytes => bytes[687] = 0),
            ("gearboxAssistHigh", bytes => bytes[687] = 4),
            ("pitAssist", bytes => bytes[688] = 2),
            ("pitReleaseAssist", bytes => bytes[689] = 2),
            ("ersAssist", bytes => bytes[690] = 2),
            ("drsAssist", bytes => bytes[691] = 2),
            ("dynamicRacingLine", bytes => bytes[692] = 3),
            ("dynamicRacingLineType", bytes => bytes[693] = 2),
            ("timeOfDay", bytes => F125TestDatagramBuilder.WriteUInt32(bytes, 696, 1440)),
            ("equalCarPerformance", bytes => bytes[708] = 2),
            ("recoveryMode", bytes => bytes[709] = 3),
        ];

        foreach (var testCase in malformed)
        {
            var datagram = CreateGoldenDatagram();
            testCase.Mutate(datagram);

            var result = F125BahrainPacketDecoder.DecodeSession(datagram);

            Assert.IsTrue(result.IsRejected, testCase.Name);
            Assert.AreEqual(
                F125DecodeReason.MalformedSelectedField,
                result.Reason,
                testCase.Name);
            Assert.IsFalse(result.Value.HasValue, testCase.Name);
        }
    }

    [TestMethod]
    public void WrongPacketFamilyUsesTheCommonGateWithoutPublishingSessionData()
    {
        var motion = F125TestDatagramBuilder.Create(packetId: 0, length: 1349);

        var result = F125BahrainPacketDecoder.DecodeSession(motion);

        Assert.IsTrue(result.IsRejected);
        Assert.AreEqual(F125DecodeReason.UnexpectedPacketFamily, result.Reason);
        Assert.IsFalse(result.Value.HasValue);
    }

    private static byte[] CreateGoldenDatagram()
    {
        var datagram = F125TestDatagramBuilder.Create(
            packetId: 1,
            length: DatagramLength);
        datagram[29] = 5;
        datagram[30] = unchecked((byte)-12);
        datagram[31] = unchecked((byte)-3);
        F125TestDatagramBuilder.WriteUInt16(datagram, 33, 5412);
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
        F125TestDatagramBuilder.WriteUInt32(datagram, 696, 1439);
        datagram[708] = 1;
        datagram[709] = 2;
        return datagram;
    }
}
