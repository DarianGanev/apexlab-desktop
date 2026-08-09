using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapAuditManualInputs
{
    public BahrainLapAuditManualInputs(
        string? gameBuild,
        string? playerVehicle,
        string? controllerProfile,
        string? setupDescriptor,
        string? tyreCompound,
        bool? evidenceIntegrityPassed,
        bool? trackAndModeVisuallyConfirmed,
        bool? setupUnchanged,
        bool? contextCrossCheckPassed)
    {
        GameBuild = gameBuild;
        PlayerVehicle = playerVehicle;
        ControllerProfile = controllerProfile;
        SetupDescriptor = setupDescriptor;
        TyreCompound = tyreCompound;
        EvidenceIntegrityPassed = evidenceIntegrityPassed;
        TrackAndModeVisuallyConfirmed = trackAndModeVisuallyConfirmed;
        SetupUnchanged = setupUnchanged;
        ContextCrossCheckPassed = contextCrossCheckPassed;
    }

    public string? GameBuild { get; }
    public string? PlayerVehicle { get; }
    public string? ControllerProfile { get; }
    public string? SetupDescriptor { get; }
    public string? TyreCompound { get; }
    public bool? EvidenceIntegrityPassed { get; }
    public bool? TrackAndModeVisuallyConfirmed { get; }
    public bool? SetupUnchanged { get; }
    public bool? ContextCrossCheckPassed { get; }

    public bool IsComplete =>
        GameBuild is not null
        && PlayerVehicle is not null
        && ControllerProfile is not null
        && SetupDescriptor is not null
        && TyreCompound is not null
        && EvidenceIntegrityPassed.HasValue
        && TrackAndModeVisuallyConfirmed.HasValue
        && SetupUnchanged.HasValue
        && ContextCrossCheckPassed.HasValue;

    public static BahrainLapAuditManualInputs Empty() =>
        new(null, null, null, null, null, null, null, null, null);

    public BahrainManualContext ToDomain()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                "Every manual lap-audit input must be completed.");
        }

        return new(
            GameBuild!,
            PlayerVehicle!,
            ControllerProfile!,
            SetupDescriptor!,
            TyreCompound!,
            EvidenceIntegrityPassed!.Value,
            TrackAndModeVisuallyConfirmed!.Value,
            SetupUnchanged!.Value,
            ContextCrossCheckPassed!.Value);
    }
}
