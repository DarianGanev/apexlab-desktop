namespace ApexLab.Application.Tests;

internal static class WindowsPathComponentCases
{
    public static IEnumerable<string> UnsafeComponents()
    {
        var reservedBaseNames = new List<string>
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
        };
        reservedBaseNames.AddRange(Enumerable.Range(1, 9).Select(number => $"COM{number}"));
        reservedBaseNames.AddRange(Enumerable.Range(1, 9).Select(number => $"LPT{number}"));

        foreach (var reservedBaseName in reservedBaseNames)
        {
            yield return reservedBaseName;
            yield return $"{reservedBaseName.ToLowerInvariant()}.txt";
        }

        yield return "capture:stream";
        yield return "folder:name:stream";
        yield return "wild*card";
        yield return "wild?card";
        yield return "bad<name";
        yield return "bad>name";
        yield return "bad\"name";
        yield return "bad|name";
        yield return "bad\u0001name";
    }

    public static IEnumerable<string> AcceptedComponents()
    {
        yield return "CONSOLE";
        yield return "NUL-data";
        yield return "COM10";
        yield return "LPT10";
        yield return "report.con";
        yield return "telemetry_2026-07-19";
        yield return "Driver Data (Local)";
    }
}
