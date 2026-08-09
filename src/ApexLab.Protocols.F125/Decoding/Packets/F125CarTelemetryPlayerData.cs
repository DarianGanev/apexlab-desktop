using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125CarTelemetryPlayerData
{
    internal F125CarTelemetryPlayerData(
        TelemetryHeaderMetadata header,
        ushort speedKilometresPerHour,
        float throttleRatio,
        float brakeRatio,
        sbyte gear)
    {
        Header = header;
        SpeedKilometresPerHour = speedKilometresPerHour;
        ThrottleRatio = throttleRatio;
        BrakeRatio = brakeRatio;
        Gear = gear;
    }

    public TelemetryHeaderMetadata Header { get; }

    public ushort SpeedKilometresPerHour { get; }

    public float ThrottleRatio { get; }

    public float BrakeRatio { get; }

    public sbyte Gear { get; }
}
