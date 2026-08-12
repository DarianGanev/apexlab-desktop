using ApexLab.Application.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Tests.Laps;

[TestClass]
public sealed class BahrainTelemetryContextTests
{
    [TestMethod]
    public void FrozenBahrainTimeTrialContextRetainsEverySelectedValue()
    {
        var session = Session();
        var context = new BahrainTelemetryContext(
            session,
            playerCarIndex: 7,
            secondaryPlayerCarIndex: byte.MaxValue);

        Assert.AreEqual(session.Weather, context.Weather);
        Assert.AreEqual(session.TrackTemperatureCelsius, context.TrackTemperatureCelsius);
        Assert.AreEqual(session.AirTemperatureCelsius, context.AirTemperatureCelsius);
        Assert.AreEqual(session.TrackLengthMetres, context.TrackLengthMetres);
        Assert.AreEqual(session.SessionType, context.SessionType);
        Assert.AreEqual(session.TrackId, context.TrackId);
        Assert.AreEqual(session.Formula, context.Formula);
        Assert.AreEqual(session.IsSpectating, context.IsSpectating);
        Assert.AreEqual(session.IsNetworkGame, context.IsNetworkGame);
        Assert.AreEqual(session.SteeringAssist, context.SteeringAssist);
        Assert.AreEqual(session.BrakingAssist, context.BrakingAssist);
        Assert.AreEqual(session.GearboxAssist, context.GearboxAssist);
        Assert.AreEqual(session.PitAssist, context.PitAssist);
        Assert.AreEqual(session.PitReleaseAssist, context.PitReleaseAssist);
        Assert.AreEqual(session.ErsAssist, context.ErsAssist);
        Assert.AreEqual(session.DrsAssist, context.DrsAssist);
        Assert.AreEqual(session.DynamicRacingLine, context.DynamicRacingLine);
        Assert.AreEqual(session.DynamicRacingLineType, context.DynamicRacingLineType);
        Assert.AreEqual(session.GameMode, context.GameMode);
        Assert.AreEqual(session.RuleSet, context.RuleSet);
        Assert.AreEqual(session.TimeOfDayMinutesSinceMidnight, context.TimeOfDayMinutesSinceMidnight);
        Assert.AreEqual(session.EqualCarPerformance, context.EqualCarPerformance);
        Assert.AreEqual(session.RecoveryMode, context.RecoveryMode);
        Assert.AreEqual((byte)7, context.PlayerCarIndex);
        Assert.AreEqual(byte.MaxValue, context.SecondaryPlayerCarIndex);
        Assert.IsTrue(context.IsSupported);
    }

    [TestMethod]
    public void EveryFrozenSupportConstraintFailsClosed()
    {
        BahrainTelemetryContext[] unsupported =
        [
            Context(Session(trackId: 4)),
            Context(Session(gameMode: 4)),
            Context(Session(sessionType: 17)),
            Context(Session(ruleSet: 1)),
            Context(Session(networkGame: true)),
            Context(Session(isSpectating: true)),
            Context(Session(), playerCarIndex: 22),
            Context(Session(), secondaryPlayerCarIndex: 0),
        ];

        foreach (var context in unsupported)
        {
            Assert.IsFalse(context.IsSupported);
        }
    }

    [TestMethod]
    public void ContextIdentityIsDeterministicAndSensitiveToSelectedValues()
    {
        var original = Context(Session());
        var same = Context(Session());
        var changed = Context(Session(trackTemperatureCelsius: 31));

        Assert.AreEqual(original, same);
        Assert.AreEqual(original.IdentitySha256, same.IdentitySha256);
        Assert.AreEqual(
            "5a8eb9f71008107defca45cb65e78d7c145c8f543c222363ec6a5c71ed986400",
            original.IdentitySha256);
        Assert.AreNotEqual(original.IdentitySha256, changed.IdentitySha256);
        Assert.AreEqual(64, original.IdentitySha256.Length);
        Assert.IsTrue(original.IdentitySha256.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    internal static BahrainTelemetryContext Context(
        CanonicalSessionPacket session,
        byte playerCarIndex = 7,
        byte secondaryPlayerCarIndex = byte.MaxValue) =>
        new(session, playerCarIndex, secondaryPlayerCarIndex);

    internal static CanonicalSessionPacket Session(
        sbyte trackTemperatureCelsius = 30,
        byte sessionType = 18,
        sbyte trackId = 3,
        bool isSpectating = false,
        bool networkGame = false,
        byte gameMode = 5,
        byte ruleSet = 2) =>
        new(
            weather: 0,
            trackTemperatureCelsius,
            airTemperatureCelsius: 24,
            trackLengthMetres: 5_412,
            sessionType,
            trackId,
            formula: 0,
            isSpectating,
            isNetworkGame: networkGame,
            steeringAssist: 0,
            brakingAssist: 0,
            gearboxAssist: 1,
            pitAssist: 0,
            pitReleaseAssist: 0,
            ersAssist: 0,
            drsAssist: 0,
            dynamicRacingLine: 0,
            dynamicRacingLineType: 0,
            gameMode,
            ruleSet,
            timeOfDayMinutesSinceMidnight: 900,
            equalCarPerformance: true,
            recoveryMode: 0);
}
