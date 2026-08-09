namespace ApexLab.Application.Storage;

public static class WindowsPathSegment
{
    public static bool IsSafe(string segment)
    {
        if (string.IsNullOrEmpty(segment)
            || segment[^1] is ' ' or '.'
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var firstPeriodIndex = segment.IndexOf('.');
        var baseName = firstPeriodIndex >= 0 ? segment[..firstPeriodIndex] : segment;
        if (baseName.EndsWith(' ')
            || baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return baseName.Length != 4
            || !IsReservedDeviceDigit(baseName[3])
            || (!baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                && !baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReservedDeviceDigit(char value) =>
        value is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';
}
