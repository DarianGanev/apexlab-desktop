using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Telemetry.Udp;

public sealed class UdpDatagramSourceOptions
{
    public const int DefaultPort = 20_777;
    public const int DefaultChannelCapacity = 256;
    public const int MaximumChannelCapacity = 65_536;

    public UdpDatagramSourceOptions(
        int port = DefaultPort,
        int channelCapacity = DefaultChannelCapacity,
        int maximumDatagramBytes = UdpDatagramLimits.MaximumPayloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            channelCapacity,
            MaximumChannelCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDatagramBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumDatagramBytes,
            UdpDatagramLimits.MaximumPayloadLength);

        Port = port;
        ChannelCapacity = channelCapacity;
        MaximumDatagramBytes = maximumDatagramBytes;
    }

    public int Port { get; }

    public int ChannelCapacity { get; }

    public int MaximumDatagramBytes { get; }
}
