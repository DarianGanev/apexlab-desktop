using System.Diagnostics.CodeAnalysis;
using ApexLab.Application.Capture;

namespace ApexLab.Replay.Validation;

internal sealed record PrivateValidationArguments(
    string DataRoot,
    RawEvidenceCaptureId CaptureId,
    string ProbeReportPath)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        [NotNullWhen(true)] out PrivateValidationArguments? parsed)
    {
        parsed = null;
        if (arguments.Count != 7
            || !string.Equals(
                arguments[0],
                "validate",
                StringComparison.Ordinal))
        {
            return false;
        }

        string? dataRoot = null;
        string? probeReportPath = null;
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
                case "--data-root"
                    when TryNormalizeAbsolutePath(value, out var root):
                    dataRoot = root;
                    break;
                case "--capture-id"
                    when RawEvidenceCaptureId.TryParse(value, out var id):
                    captureId = id;
                    break;
                case "--probe-report"
                    when TryNormalizeAbsolutePath(value, out var report):
                    probeReportPath = report;
                    break;
                default:
                    return false;
            }
        }

        if (dataRoot is null
            || captureId is null
            || probeReportPath is null)
        {
            return false;
        }

        parsed = new PrivateValidationArguments(
            dataRoot,
            captureId,
            probeReportPath);
        return true;
    }

    private static bool TryNormalizeAbsolutePath(
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
        catch (Exception exception)
            when (exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }
}
