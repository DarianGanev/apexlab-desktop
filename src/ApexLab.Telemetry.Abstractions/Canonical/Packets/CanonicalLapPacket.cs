namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalLapPacket
{
    public CanonicalLapPacket(
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
        CanonicalValueValidation.RequireFinite(
            lapDistanceMetres,
            nameof(lapDistanceMetres));
        CanonicalValueValidation.RequireFinite(
            totalDistanceMetres,
            nameof(totalDistanceMetres));
        CanonicalValueValidation.RequireAtMost(pitStatus, 2, nameof(pitStatus));
        CanonicalValueValidation.RequireAtMost(sector, 2, nameof(sector));
        CanonicalValueValidation.RequireAtMost(
            driverStatus,
            4,
            nameof(driverStatus));
        CanonicalValueValidation.RequireAtMost(
            resultStatus,
            7,
            nameof(resultStatus));

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
