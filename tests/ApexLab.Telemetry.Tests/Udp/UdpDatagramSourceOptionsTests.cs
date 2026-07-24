using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Udp;

namespace ApexLab.Telemetry.Tests.Udp;

[TestClass]
public sealed class UdpDatagramSourceOptionsTests
{
    [TestMethod]
    public void Defaults_AreBoundedAndUseTheF1LoopbackPort()
    {
        var options = new UdpDatagramSourceOptions();

        Assert.AreEqual(20_777, options.Port);
        Assert.AreEqual(256, options.ChannelCapacity);
        Assert.AreEqual(
            UdpDatagramLimits.MaximumPayloadLength,
            options.MaximumDatagramBytes);
    }

    [TestMethod]
    public void Constructor_ValidatesInclusiveConfigurationBoundaries()
    {
        var minimum = new UdpDatagramSourceOptions(
            port: 0,
            channelCapacity: 1,
            maximumDatagramBytes: 1);
        var maximum = new UdpDatagramSourceOptions(
            port: 65_535,
            channelCapacity: 65_536,
            maximumDatagramBytes: UdpDatagramLimits.MaximumPayloadLength);

        Assert.AreEqual(0, minimum.Port);
        Assert.AreEqual(1, minimum.ChannelCapacity);
        Assert.AreEqual(1, minimum.MaximumDatagramBytes);
        Assert.AreEqual(65_535, maximum.Port);
        Assert.AreEqual(65_536, maximum.ChannelCapacity);
        Assert.AreEqual(
            UdpDatagramLimits.MaximumPayloadLength,
            maximum.MaximumDatagramBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(port: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(port: 65_536));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(channelCapacity: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(channelCapacity: 65_537));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(maximumDatagramBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new UdpDatagramSourceOptions(
                maximumDatagramBytes: UdpDatagramLimits.MaximumPayloadLength + 1));
    }
}
