using System.Buffers.Binary;
using System.Security.Cryptography;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Laps;

public sealed record BahrainTelemetryContext
{
    private static ReadOnlySpan<byte> HashDomain =>
        "ApexLab.BahrainTelemetryContext.v1\0"u8;

    public BahrainTelemetryContext(
        CanonicalSessionPacket session,
        byte playerCarIndex,
        byte secondaryPlayerCarIndex)
    {
        Weather = session.Weather;
        TrackTemperatureCelsius = session.TrackTemperatureCelsius;
        AirTemperatureCelsius = session.AirTemperatureCelsius;
        TrackLengthMetres = session.TrackLengthMetres;
        SessionType = session.SessionType;
        TrackId = session.TrackId;
        Formula = session.Formula;
        IsSpectating = session.IsSpectating;
        IsNetworkGame = session.IsNetworkGame;
        SteeringAssist = session.SteeringAssist;
        BrakingAssist = session.BrakingAssist;
        GearboxAssist = session.GearboxAssist;
        PitAssist = session.PitAssist;
        PitReleaseAssist = session.PitReleaseAssist;
        ErsAssist = session.ErsAssist;
        DrsAssist = session.DrsAssist;
        DynamicRacingLine = session.DynamicRacingLine;
        DynamicRacingLineType = session.DynamicRacingLineType;
        GameMode = session.GameMode;
        RuleSet = session.RuleSet;
        TimeOfDayMinutesSinceMidnight = session.TimeOfDayMinutesSinceMidnight;
        EqualCarPerformance = session.EqualCarPerformance;
        RecoveryMode = session.RecoveryMode;
        PlayerCarIndex = playerCarIndex;
        SecondaryPlayerCarIndex = secondaryPlayerCarIndex;
        IdentitySha256 = CalculateIdentity();
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
    public byte PlayerCarIndex { get; }
    public byte SecondaryPlayerCarIndex { get; }
    public string IdentitySha256 { get; }

    public bool IsSupported =>
        TrackId == 3
        && GameMode == 5
        && SessionType == 18
        && RuleSet == 2
        && !IsNetworkGame
        && !IsSpectating
        && PlayerCarIndex <= 21
        && SecondaryPlayerCarIndex == byte.MaxValue;

    private string CalculateIdentity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HashDomain);
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        AppendByte(hash, Weather);
        AppendByte(hash, unchecked((byte)TrackTemperatureCelsius));
        AppendByte(hash, unchecked((byte)AirTemperatureCelsius));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, TrackLengthMetres);
        hash.AppendData(buffer[..sizeof(ushort)]);
        AppendByte(hash, SessionType);
        AppendByte(hash, unchecked((byte)TrackId));
        AppendByte(hash, Formula);
        AppendBoolean(hash, IsSpectating);
        AppendBoolean(hash, IsNetworkGame);
        AppendByte(hash, SteeringAssist);
        AppendByte(hash, BrakingAssist);
        AppendByte(hash, GearboxAssist);
        AppendByte(hash, PitAssist);
        AppendByte(hash, PitReleaseAssist);
        AppendByte(hash, ErsAssist);
        AppendByte(hash, DrsAssist);
        AppendByte(hash, DynamicRacingLine);
        AppendByte(hash, DynamicRacingLineType);
        AppendByte(hash, GameMode);
        AppendByte(hash, RuleSet);
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer,
            TimeOfDayMinutesSinceMidnight);
        hash.AppendData(buffer);
        AppendBoolean(hash, EqualCarPerformance);
        AppendByte(hash, RecoveryMode);
        AppendByte(hash, PlayerCarIndex);
        AppendByte(hash, SecondaryPlayerCarIndex);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendBoolean(IncrementalHash hash, bool value) =>
        AppendByte(hash, value ? (byte)1 : (byte)0);

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        hash.AppendData(buffer);
    }
}
