namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalSessionPacket
{
    public CanonicalSessionPacket(
        byte weather,
        sbyte trackTemperatureCelsius,
        sbyte airTemperatureCelsius,
        ushort trackLengthMetres,
        byte sessionType,
        sbyte trackId,
        byte formula,
        bool isSpectating,
        bool isNetworkGame,
        byte steeringAssist,
        byte brakingAssist,
        byte gearboxAssist,
        byte pitAssist,
        byte pitReleaseAssist,
        byte ersAssist,
        byte drsAssist,
        byte dynamicRacingLine,
        byte dynamicRacingLineType,
        byte gameMode,
        byte ruleSet,
        uint timeOfDayMinutesSinceMidnight,
        bool equalCarPerformance,
        byte recoveryMode)
    {
        CanonicalValueValidation.RequireAtMost(weather, 5, nameof(weather));
        CanonicalValueValidation.RequireAtMost(
            steeringAssist,
            1,
            nameof(steeringAssist));
        CanonicalValueValidation.RequireAtMost(
            brakingAssist,
            3,
            nameof(brakingAssist));
        if (gearboxAssist is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(gearboxAssist));
        }

        CanonicalValueValidation.RequireAtMost(pitAssist, 1, nameof(pitAssist));
        CanonicalValueValidation.RequireAtMost(
            pitReleaseAssist,
            1,
            nameof(pitReleaseAssist));
        CanonicalValueValidation.RequireAtMost(ersAssist, 1, nameof(ersAssist));
        CanonicalValueValidation.RequireAtMost(drsAssist, 1, nameof(drsAssist));
        CanonicalValueValidation.RequireAtMost(
            dynamicRacingLine,
            2,
            nameof(dynamicRacingLine));
        CanonicalValueValidation.RequireAtMost(
            dynamicRacingLineType,
            1,
            nameof(dynamicRacingLineType));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            timeOfDayMinutesSinceMidnight,
            1_440U);
        CanonicalValueValidation.RequireAtMost(
            recoveryMode,
            2,
            nameof(recoveryMode));

        Weather = weather;
        TrackTemperatureCelsius = trackTemperatureCelsius;
        AirTemperatureCelsius = airTemperatureCelsius;
        TrackLengthMetres = trackLengthMetres;
        SessionType = sessionType;
        TrackId = trackId;
        Formula = formula;
        IsSpectating = isSpectating;
        IsNetworkGame = isNetworkGame;
        SteeringAssist = steeringAssist;
        BrakingAssist = brakingAssist;
        GearboxAssist = gearboxAssist;
        PitAssist = pitAssist;
        PitReleaseAssist = pitReleaseAssist;
        ErsAssist = ersAssist;
        DrsAssist = drsAssist;
        DynamicRacingLine = dynamicRacingLine;
        DynamicRacingLineType = dynamicRacingLineType;
        GameMode = gameMode;
        RuleSet = ruleSet;
        TimeOfDayMinutesSinceMidnight = timeOfDayMinutesSinceMidnight;
        EqualCarPerformance = equalCarPerformance;
        RecoveryMode = recoveryMode;
    }

    public byte Weather { get; }
    public sbyte TrackTemperatureCelsius { get; }
    public sbyte AirTemperatureCelsius { get; }
    public ushort TrackLengthMetres { get; }
    public byte SessionType { get; }
    public sbyte TrackId { get; }
    public byte Formula { get; }
    public bool IsSpectating { get; }
    public bool IsNetworkGame { get; }
    public byte SteeringAssist { get; }
    public byte BrakingAssist { get; }
    public byte GearboxAssist { get; }
    public byte PitAssist { get; }
    public byte PitReleaseAssist { get; }
    public byte ErsAssist { get; }
    public byte DrsAssist { get; }
    public byte DynamicRacingLine { get; }
    public byte DynamicRacingLineType { get; }
    public byte GameMode { get; }
    public byte RuleSet { get; }
    public uint TimeOfDayMinutesSinceMidnight { get; }
    public bool EqualCarPerformance { get; }
    public byte RecoveryMode { get; }
}
