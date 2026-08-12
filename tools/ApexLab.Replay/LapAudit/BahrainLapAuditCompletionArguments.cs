using System.Diagnostics.CodeAnalysis;
using ApexLab.Application.Capture;

namespace ApexLab.Replay.LapAudit;

internal sealed record BahrainLapAuditCompletionArguments(
    string DataRoot,
    RawEvidenceCaptureId CaptureId)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        string command,
        [NotNullWhen(true)] out BahrainLapAuditCompletionArguments? parsed)
    {
        parsed = null;
        if (arguments.Count != 7
            || !StringComparer.Ordinal.Equals(arguments[0], command))
        {
            return false;
        }

        string? dataRoot = null;
        RawEvidenceCaptureId? captureId = null;
        var contextStdin = false;
        var confirmedEveryEligibleCandidate = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count;)
        {
            var option = arguments[index];
            if (!seen.Add(option))
            {
                return false;
            }

            switch (option)
            {
                case "--data-root" when index + 1 < arguments.Count
                    && BahrainLapAuditArguments.TryNormalizeRoot(
                        arguments[index + 1],
                        out var root):
                    dataRoot = root;
                    index += 2;
                    break;
                case "--capture-id" when index + 1 < arguments.Count
                    && RawEvidenceCaptureId.TryParse(
                        arguments[index + 1],
                        out var id):
                    captureId = id;
                    index += 2;
                    break;
                case "--context-stdin":
                    contextStdin = true;
                    index++;
                    break;
                case "--confirm-every-eligible-candidate":
                    confirmedEveryEligibleCandidate = true;
                    index++;
                    break;
                default:
                    return false;
            }
        }

        if (dataRoot is null
            || captureId is null
            || !contextStdin
            || !confirmedEveryEligibleCandidate)
        {
            return false;
        }

        parsed = new(dataRoot, captureId);
        return true;
    }
}
