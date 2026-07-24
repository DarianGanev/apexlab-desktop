using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ApexLab.Application.Capture;
using ApexLab.Persistence.Raw;

namespace ApexLab.Replay.Replay;

internal sealed record ReplayArguments(
    string DataRoot,
    RawEvidenceCaptureId CaptureId,
    RawReplayTimingMode TimingMode,
    int SpeedPermille)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        [NotNullWhen(true)] out ReplayArguments? parsed)
    {
        parsed = null;
        if (arguments.Count == 0
            || arguments[0] != "replay")
        {
            return false;
        }

        string? dataRoot = null;
        RawEvidenceCaptureId? captureId = null;
        var timingMode = RawReplayTimingMode.Immediate;
        var speedPermille = 1_000;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count
                || !seen.Add(arguments[index]))
            {
                return false;
            }

            var value = arguments[index + 1];
            switch (arguments[index])
            {
                case "--data-root"
                    when !string.IsNullOrWhiteSpace(value):
                    dataRoot = value;
                    break;
                case "--capture-id"
                    when RawEvidenceCaptureId.TryParse(
                        value,
                        out var parsedCaptureId):
                    captureId = parsedCaptureId;
                    break;
                case "--timing" when value == "immediate":
                    timingMode = RawReplayTimingMode.Immediate;
                    break;
                case "--timing" when value == "recorded":
                    timingMode = RawReplayTimingMode.Recorded;
                    break;
                case "--speed-permille"
                    when int.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedSpeed)
                    && parsedSpeed is >= 100 and <= 100_000:
                    speedPermille = parsedSpeed;
                    break;
                default:
                    return false;
            }
        }

        if (dataRoot is null || captureId is null)
        {
            return false;
        }

        parsed = new ReplayArguments(
            dataRoot,
            captureId,
            timingMode,
            speedPermille);
        return true;
    }
}
