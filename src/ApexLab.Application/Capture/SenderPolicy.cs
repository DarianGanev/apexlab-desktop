using System.Net;
using System.Net.Sockets;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Capture;

public sealed class SenderPolicy
{
    private SenderPolicy()
    {
    }

    public static SenderPolicy LoopbackOnly { get; } = new();

    public bool IsExpected(DatagramSender sender)
    {
        return sender.Address is { AddressFamily: AddressFamily.InterNetwork } address
            && IPAddress.IsLoopback(address);
    }
}
