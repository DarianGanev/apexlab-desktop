namespace ApexLab.Telemetry.Abstractions.Canonical;

internal static class CanonicalValueValidation
{
    public static void RequireFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A finite canonical value is required.");
        }
    }

    public static void RequireRatio(float value, string parameterName)
    {
        RequireFinite(value, parameterName);
        if (value is < 0F or > 1F)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A canonical ratio must be between zero and one.");
        }
    }

    public static void RequireAtMost(
        byte value,
        byte maximum,
        string parameterName)
    {
        if (value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
