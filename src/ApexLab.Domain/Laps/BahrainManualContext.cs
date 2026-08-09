namespace ApexLab.Domain.Laps;

public sealed record BahrainManualContext
{
    public BahrainManualContext(
        string gameBuild,
        string playerVehicle,
        string controllerProfile,
        string setupDescriptor,
        string tyreCompound,
        bool evidenceIntegrityPassed,
        bool trackAndModeVisuallyConfirmed,
        bool setupUnchanged,
        bool contextCrossCheckPassed)
    {
        GameBuild = LapAuditText.Require(gameBuild, 80, nameof(gameBuild));
        PlayerVehicle = LapAuditText.Require(
            playerVehicle,
            80,
            nameof(playerVehicle));
        ControllerProfile = LapAuditText.Require(
            controllerProfile,
            80,
            nameof(controllerProfile));
        SetupDescriptor = LapAuditText.Require(
            setupDescriptor,
            120,
            nameof(setupDescriptor));
        TyreCompound = LapAuditText.Require(
            tyreCompound,
            40,
            nameof(tyreCompound));
        EvidenceIntegrityPassed = evidenceIntegrityPassed;
        TrackAndModeVisuallyConfirmed = trackAndModeVisuallyConfirmed;
        SetupUnchanged = setupUnchanged;
        ContextCrossCheckPassed = contextCrossCheckPassed;
    }

    public string GameBuild { get; }

    public string PlayerVehicle { get; }

    public string ControllerProfile { get; }

    public string SetupDescriptor { get; }

    public string TyreCompound { get; }

    public bool EvidenceIntegrityPassed { get; }

    public bool TrackAndModeVisuallyConfirmed { get; }

    public bool SetupUnchanged { get; }

    public bool ContextCrossCheckPassed { get; }

    public bool IsFullyConfirmed =>
        EvidenceIntegrityPassed
        && TrackAndModeVisuallyConfirmed
        && SetupUnchanged
        && ContextCrossCheckPassed;
}

internal static class LapAuditText
{
    public static string Require(
        string value,
        int maximumLength,
        string parameterName)
    {
        if (value is null
            || value.Length is < 1
            || value.Length > maximumLength
            || !StringComparer.Ordinal.Equals(value, value.Trim())
            || value.Any(character => char.IsControl(character)
                || char.IsSurrogate(character)))
        {
            throw new ArgumentException(
                "A trimmed, bounded, single-line local value is required.",
                parameterName);
        }

        return value;
    }
}
