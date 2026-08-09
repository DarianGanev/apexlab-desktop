using System.Text.Json;

namespace ApexLab.Replay.Probe;

internal static class ProbeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(ProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static string SerializeStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return JsonSerializer.Serialize(
            new ProbeStatusReport(
                ProbeAggregator.SchemaVersion,
                status),
            Options);
    }
}
