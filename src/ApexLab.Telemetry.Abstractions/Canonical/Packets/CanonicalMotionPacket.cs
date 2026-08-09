namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalMotionPacket
{
    public CanonicalMotionPacket(
        float worldPositionXMetres,
        float worldPositionYMetres,
        float worldPositionZMetres)
    {
        CanonicalValueValidation.RequireFinite(
            worldPositionXMetres,
            nameof(worldPositionXMetres));
        CanonicalValueValidation.RequireFinite(
            worldPositionYMetres,
            nameof(worldPositionYMetres));
        CanonicalValueValidation.RequireFinite(
            worldPositionZMetres,
            nameof(worldPositionZMetres));
        WorldPositionXMetres = worldPositionXMetres;
        WorldPositionYMetres = worldPositionYMetres;
        WorldPositionZMetres = worldPositionZMetres;
    }

    public float WorldPositionXMetres { get; }

    public float WorldPositionYMetres { get; }

    public float WorldPositionZMetres { get; }
}
