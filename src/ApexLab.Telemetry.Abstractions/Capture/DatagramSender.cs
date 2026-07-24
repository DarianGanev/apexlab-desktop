using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ApexLab.Telemetry.Abstractions.Capture;

public readonly record struct DatagramSender
{
    public DatagramSender(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                $"A UDP sender port must be between {IPEndPoint.MinPort} and {IPEndPoint.MaxPort}.");
        }

        Address = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => new IPAddress(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 =>
                new IPAddress(address.GetAddressBytes(), address.ScopeId),
            _ => throw new ArgumentException(
                "A UDP sender address must be IPv4 or IPv6.",
                nameof(address)),
        };
        Port = port;
    }

    public IPAddress? Address { get; }

    public int Port { get; }

    [MemberNotNullWhen(true, nameof(Address))]
    public bool IsSpecified => Address is not null;
}
