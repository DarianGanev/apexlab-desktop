using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125MotionPlayerData
{
    internal F125MotionPlayerData(
        TelemetryHeaderMetadata header,
        float worldPositionXMetres,
        float worldPositionYMetres,
        float worldPositionZMetres)
    {
        Header = header;
        WorldPositionXMetres = worldPositionXMetres;
        WorldPositionYMetres = worldPositionYMetres;
        WorldPositionZMetres = worldPositionZMetres;
    }

    public TelemetryHeaderMetadata Header { get; }

    public float WorldPositionXMetres { get; }

    public float WorldPositionYMetres { get; }

    public float WorldPositionZMetres { get; }
}
