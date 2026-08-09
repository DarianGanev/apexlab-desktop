using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125LapPlayerData
{
    internal F125LapPlayerData(
        TelemetryHeaderMetadata header,
        uint lastLapTimeMilliseconds,
        uint currentLapTimeMilliseconds,
        float lapDistanceMetres,
        float totalDistanceMetres,
        byte currentLapNumber,
        byte pitStatus,
        byte sector,
        bool currentLapInvalid,
        byte driverStatus,
        byte resultStatus)
    {
        Header = header;
        LastLapTimeMilliseconds = lastLapTimeMilliseconds;
        CurrentLapTimeMilliseconds = currentLapTimeMilliseconds;
        LapDistanceMetres = lapDistanceMetres;
        TotalDistanceMetres = totalDistanceMetres;
        CurrentLapNumber = currentLapNumber;
        PitStatus = pitStatus;
        Sector = sector;
        CurrentLapInvalid = currentLapInvalid;
        DriverStatus = driverStatus;
        ResultStatus = resultStatus;
    }

    public TelemetryHeaderMetadata Header { get; }

    public uint LastLapTimeMilliseconds { get; }

    public uint CurrentLapTimeMilliseconds { get; }

    public float LapDistanceMetres { get; }

    public float TotalDistanceMetres { get; }

    public byte CurrentLapNumber { get; }

    public byte PitStatus { get; }

    public byte Sector { get; }

    public bool CurrentLapInvalid { get; }

    public byte DriverStatus { get; }

    public byte ResultStatus { get; }
}
