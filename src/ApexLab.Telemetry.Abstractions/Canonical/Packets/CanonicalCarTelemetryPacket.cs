namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalCarTelemetryPacket
{
    public CanonicalCarTelemetryPacket(
        ushort speedKilometresPerHour,
        float throttleRatio,
        float brakeRatio,
        sbyte gear)
    {
        CanonicalValueValidation.RequireRatio(throttleRatio, nameof(throttleRatio));
        CanonicalValueValidation.RequireRatio(brakeRatio, nameof(brakeRatio));
        if (gear is < -1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(gear));
        }

        SpeedKilometresPerHour = speedKilometresPerHour;
        ThrottleRatio = throttleRatio;
        BrakeRatio = brakeRatio;
        Gear = gear;
    }

    public ushort SpeedKilometresPerHour { get; }
    public float ThrottleRatio { get; }
    public float BrakeRatio { get; }
    public sbyte Gear { get; }
}
