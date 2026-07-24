using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class SenderPolicyTests
{
    [TestMethod]
    public void LoopbackOnlyAcceptsAnyIpv4LoopbackSenderPort()
    {
        var policy = SenderPolicy.LoopbackOnly;

        Assert.IsTrue(
            policy.IsExpected(new DatagramSender(IPAddress.Loopback, 1)));
        Assert.IsTrue(
            policy.IsExpected(new DatagramSender(IPAddress.Parse("127.42.7.9"), 65_535)));
    }

    [TestMethod]
    public void LoopbackOnlyRejectsLanPublicAndIpv6Addresses()
    {
        var policy = SenderPolicy.LoopbackOnly;
        IPAddress[] rejectedAddresses =
        [
            IPAddress.Parse("192.168.1.5"),
            IPAddress.Parse("203.0.113.17"),
            IPAddress.IPv6Loopback,
        ];

        foreach (var address in rejectedAddresses)
        {
            Assert.IsFalse(
                policy.IsExpected(new DatagramSender(address, 20_777)),
                address.ToString());
        }
    }
}
