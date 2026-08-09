using System.Diagnostics.CodeAnalysis;
using ApexLab.Application.Capture;

namespace ApexLab.Replay.LapAudit;

internal sealed record BahrainLapAuditArguments(
    string DataRoot,
    RawEvidenceCaptureId CaptureId)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        string command,
        [NotNullWhen(true)] out BahrainLapAuditArguments? parsed)
    {
        parsed = null;
        if (arguments.Count != 5
            || !StringComparer.Ordinal.Equals(arguments[0], command))
        {
            return false;
        }

        string? dataRoot = null;
        RawEvidenceCaptureId? captureId = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
        {
            var option = arguments[index];
            var value = arguments[index + 1];
            if (!seen.Add(option))
            {
                return false;
            }

            switch (option)
            {
                case "--data-root" when TryNormalizeRoot(value, out var root):
                    dataRoot = root;
                    break;
                case "--capture-id"
                    when RawEvidenceCaptureId.TryParse(value, out var id):
                    captureId = id;
                    break;
                default:
                    return false;
            }
        }

        if (dataRoot is null || captureId is null)
        {
            return false;
        }

        parsed = new(dataRoot, captureId);
        return true;
    }

    private static bool TryNormalizeRoot(
        string value,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is
                   ArgumentException
                   or IOException
                   or NotSupportedException
                   or PathTooLongException)
        {
            return false;
        }
    }
}
