using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Udp;

namespace ApexLab.Replay.Probe;

internal sealed record ProbeArguments(
    int Port,
    int ChannelCapacity,
    int MaximumDatagramBytes,
    TimeSpan Duration)
{
    public const int DefaultDurationSeconds = 30;
    public const int MaximumDurationSeconds = 3_600;

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        [NotNullWhen(true)] out ProbeArguments? parsed)
    {
        parsed = null;
        if (arguments.Count == 0
            || !string.Equals(arguments[0], "probe", StringComparison.Ordinal))
        {
            return false;
        }

        var port = UdpDatagramSourceOptions.DefaultPort;
        var channelCapacity = UdpDatagramSourceOptions.DefaultChannelCapacity;
        var maximumDatagramBytes = UdpDatagramLimits.MaximumPayloadLength;
        var durationSeconds = DefaultDurationSeconds;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count
                || !seenOptions.Add(arguments[index])
                || !int.TryParse(
                    arguments[index + 1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return false;
            }

            switch (arguments[index])
            {
                case "--port" when value is >= 1 and <= 65_535:
                    port = value;
                    break;
                case "--capacity"
                    when value is >= 1
                    and <= UdpDatagramSourceOptions.MaximumChannelCapacity:
                    channelCapacity = value;
                    break;
                case "--max-datagram-bytes"
                    when value is >= 29
                    and <= UdpDatagramLimits.MaximumPayloadLength:
                    maximumDatagramBytes = value;
                    break;
                case "--duration-seconds"
                    when value is >= 1 and <= MaximumDurationSeconds:
                    durationSeconds = value;
                    break;
                default:
                    return false;
            }
        }

        parsed = new ProbeArguments(
            port,
            channelCapacity,
            maximumDatagramBytes,
            TimeSpan.FromSeconds(durationSeconds));
        return true;
    }
}
