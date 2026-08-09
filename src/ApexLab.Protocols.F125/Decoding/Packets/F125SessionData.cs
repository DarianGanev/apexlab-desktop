using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125SessionData
{
    internal F125SessionData(
        TelemetryHeaderMetadata header,
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
        Header = header;
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

    public TelemetryHeaderMetadata Header { get; }

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
