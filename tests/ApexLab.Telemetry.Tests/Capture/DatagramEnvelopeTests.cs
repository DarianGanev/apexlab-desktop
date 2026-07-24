using System.Net;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Telemetry.Tests.Capture;

[TestClass]
public sealed class DatagramEnvelopeTests
{
    [TestMethod]
    public void CopyFrom_SnapshotsPayloadAndPreservesReceiveMetadata()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var sender = new DatagramSender(IPAddress.Loopback, 20_777);
        var receivedAtUtc = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);

        var envelope = DatagramEnvelope.CopyFrom(
            sequence: 42,
            monotonicTimestamp: 123_456,
            receivedAtUtc,
            sender,
            payload);
        payload.AsSpan().Fill(0xFF);

        Assert.AreEqual(42L, envelope.Sequence);
        Assert.AreEqual(123_456L, envelope.MonotonicTimestamp);
        Assert.AreEqual(receivedAtUtc, envelope.ReceivedAtUtc);
        Assert.AreEqual(sender, envelope.Sender);
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x02, 0x03 },
            envelope.Payload.ToArray());
    }

    [TestMethod]
    public void CopyFrom_AcceptsInclusiveUdpPayloadBoundaries()
    {
        var empty = CreateEnvelope([]);
        var maximum = CreateEnvelope(new byte[UdpDatagramLimits.MaximumPayloadLength]);

        Assert.AreEqual(0, empty.Payload.Length);
        Assert.AreEqual(UdpDatagramLimits.MaximumPayloadLength, maximum.Payload.Length);
    }

    [TestMethod]
    public void CopyFrom_RejectsInvalidSequenceTimestampOrUtcMetadata()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateEnvelope([0x01], sequence: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateEnvelope([0x01], sequence: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateEnvelope([0x01], monotonicTimestamp: -1));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateEnvelope(
                [0x01],
                receivedAtUtc:
                    new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.FromHours(2))));
        Assert.ThrowsExactly<ArgumentException>(
            () => DatagramEnvelope.CopyFrom(
                sequence: 1,
                monotonicTimestamp: 0,
                DateTimeOffset.UnixEpoch,
                sender: default,
                [0x01]));
    }

    [TestMethod]
    public void CopyFrom_RejectsOversizedPayload()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateEnvelope(new byte[UdpDatagramLimits.MaximumPayloadLength + 1]));
    }

    [TestMethod]
    public void Sender_ValidatesPortAndSnapshotsMutableIpv6Scope()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DatagramSender(null!, 20_777));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DatagramSender(IPAddress.Loopback, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DatagramSender(IPAddress.Loopback, 65_536));

        var address = new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), 7);
        var sender = new DatagramSender(address, 0);
        address.ScopeId = 9;

        Assert.IsNotNull(sender.Address);
        CollectionAssert.AreEqual(
            IPAddress.IPv6Loopback.GetAddressBytes(),
            sender.Address.GetAddressBytes());
        Assert.AreEqual(7L, sender.Address.ScopeId);
        Assert.AreEqual(0, sender.Port);
    }

    private static DatagramEnvelope CreateEnvelope(
        byte[] payload,
        long sequence = 1,
        long monotonicTimestamp = 0,
        DateTimeOffset? receivedAtUtc = null) =>
        DatagramEnvelope.CopyFrom(
            sequence,
            monotonicTimestamp,
            receivedAtUtc ?? DateTimeOffset.UnixEpoch,
            new DatagramSender(IPAddress.Loopback, 20_777),
            payload);
}
