using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Tests;

[TestClass]
public sealed class F125TelemetryProtocolAdapterTests
{
    private readonly F125TelemetryProtocolAdapter adapter = new();

    [TestMethod]
    public void ExposesStableProtocolIdentityAndHeaderLength()
    {
        Assert.AreEqual("ea-f1-25-v3", adapter.ProtocolId);
        Assert.AreEqual(29, adapter.HeaderLength);
    }

    [TestMethod]
    public void EveryTruncatedHeaderIsMalformed()
    {
        for (var length = 0; length < 29; length++)
        {
            var result = adapter.Inspect(new byte[length]);

            AssertRejected(result, TelemetryPacketClassification.MalformedHeader);
        }
    }

    [TestMethod]
    public void NonFiniteSessionTimeIsMalformed()
    {
        var datagram = CreateDatagram(packetId: 3, packetVersion: 1, length: 45);
        WriteUInt32(datagram, 15, 0x7FC01234);

        var result = adapter.Inspect(datagram);

        AssertRejected(result, TelemetryPacketClassification.MalformedHeader);
    }

    [TestMethod]
    public void UnsupportedFormatWinsBeforeOtherCompatibilityChecks()
    {
        var datagram = CreateDatagram(
            packetId: byte.MaxValue,
            packetVersion: byte.MaxValue,
            length: 29,
            packetFormat: 2026,
            gameYear: 26);

        var result = adapter.Inspect(datagram);

        AssertRejected(result, TelemetryPacketClassification.UnsupportedFormat);
        AssertHeader(result, packetFormat: 2026, gameYear: 26, packetId: byte.MaxValue);
    }

    [TestMethod]
    public void BigEndianPacketFormatBytesAreNotMisinterpretedAsSupported()
    {
        var datagram = CreateDatagram(packetId: 3, packetVersion: 1, length: 45);
        datagram[0] = 0x07;
        datagram[1] = 0xE9;

        var result = adapter.Inspect(datagram);

        AssertRejected(result, TelemetryPacketClassification.UnsupportedFormat);
        Assert.AreEqual((ushort)0xE907, result.Header!.Value.PacketFormat);
    }

    [TestMethod]
    public void UnsupportedYearWinsBeforePacketChecks()
    {
        var datagram = CreateDatagram(
            packetId: byte.MaxValue,
            packetVersion: byte.MaxValue,
            length: 29,
            gameYear: 26);

        var result = adapter.Inspect(datagram);

        AssertRejected(result, TelemetryPacketClassification.UnsupportedYear);
        AssertHeader(result, packetFormat: 2025, gameYear: 26, packetId: byte.MaxValue);
    }

    [TestMethod]
    public void UnknownPacketIdIsRejectedWithoutDescriptor()
    {
        var datagram = CreateDatagram(
            packetId: byte.MaxValue,
            packetVersion: 1,
            length: 29);

        var result = adapter.Inspect(datagram);

        AssertRejected(result, TelemetryPacketClassification.UnknownPacketId);
        AssertHeader(result, packetFormat: 2025, gameYear: 25, packetId: byte.MaxValue);
    }

    [TestMethod]
    public void UnsupportedPacketVersionWinsBeforeLengthCheck()
    {
        var datagram = CreateDatagram(packetId: 6, packetVersion: 2, length: 29);

        var result = adapter.Inspect(datagram);

        AssertRejected(
            result,
            TelemetryPacketClassification.UnsupportedPacketVersion,
            descriptorExpected: true);
        Assert.AreEqual((byte)1, result.Descriptor!.PacketVersion);
    }

    [TestMethod]
    public void EveryKnownPacketRequiresItsExactDatagramLength()
    {
        foreach (var descriptor in F125PacketDescriptorCatalog.Descriptors)
        {
            var tooShort = CreateDatagram(
                descriptor.PacketId,
                descriptor.PacketVersion,
                descriptor.DatagramLength - 1);
            var tooLong = CreateDatagram(
                descriptor.PacketId,
                descriptor.PacketVersion,
                descriptor.DatagramLength + 1);

            AssertRejected(
                adapter.Inspect(tooShort),
                TelemetryPacketClassification.InvalidPacketLength,
                descriptorExpected: true);
            AssertRejected(
                adapter.Inspect(tooLong),
                TelemetryPacketClassification.InvalidPacketLength,
                descriptorExpected: true);
        }
    }

    [TestMethod]
    public void IdentityBearingFamiliesAreRecognizedButExcluded()
    {
        byte[] excludedPacketIds = [4, 9];

        foreach (var packetId in excludedPacketIds)
        {
            Assert.IsTrue(F125PacketDescriptorCatalog.TryGet(packetId, out var descriptor));
            var result = adapter.Inspect(
                CreateDatagram(packetId, packetVersion: 1, descriptor.DatagramLength));

            AssertRejected(
                result,
                TelemetryPacketClassification.ExcludedPrivacyPacket,
                descriptorExpected: true);
            Assert.AreEqual(
                TelemetryPacketPrivacyDisposition.IdentityBearingExcluded,
                result.Descriptor!.PrivacyDisposition);
        }
    }

    [TestMethod]
    public void OnlyExplicitEvidenceAllowedDispositionPassesPrivacyPolicy()
    {
        TelemetryPacketPrivacyDisposition[] rejectedDispositions =
        [
            TelemetryPacketPrivacyDisposition.Unspecified,
            TelemetryPacketPrivacyDisposition.IdentityBearingExcluded,
            (TelemetryPacketPrivacyDisposition)byte.MaxValue,
        ];

        Assert.IsTrue(
            F125TelemetryProtocolAdapter.IsEvidenceAllowed(
                TelemetryPacketPrivacyDisposition.EvidenceAllowed));

        foreach (var disposition in rejectedDispositions)
        {
            Assert.IsFalse(
                F125TelemetryProtocolAdapter.IsEvidenceAllowed(disposition),
                $"Disposition {disposition} unexpectedly passed the privacy policy.");
        }
    }

    [TestMethod]
    public void EveryEvidenceAllowedFamilyIsCompatible()
    {
        foreach (var descriptor in F125PacketDescriptorCatalog.Descriptors.Where(
                     descriptor =>
                         descriptor.PrivacyDisposition
                         == TelemetryPacketPrivacyDisposition.EvidenceAllowed))
        {
            var datagram = CreateDatagram(
                descriptor.PacketId,
                descriptor.PacketVersion,
                descriptor.DatagramLength);

            var result = adapter.Inspect(datagram);

            Assert.IsTrue(result.IsCompatible, descriptor.FamilyName);
            Assert.AreEqual(TelemetryPacketClassification.Compatible, result.Classification);
            Assert.AreSame(descriptor, result.Descriptor);
            AssertHeader(
                result,
                packetFormat: 2025,
                gameYear: 25,
                packetId: descriptor.PacketId);
            Assert.AreEqual((byte)1, result.Header!.Value.GameMajorVersion);
            Assert.AreEqual((byte)7, result.Header.Value.GameMinorVersion);
            Assert.AreEqual((byte)1, result.Header.Value.PacketVersion);
            Assert.AreEqual(0x0123456789ABCDEFUL, result.Header.Value.SessionUid);
            Assert.AreEqual(12.5F, result.Header.Value.SessionTimeSeconds);
            Assert.AreEqual(0x10203040U, result.Header.Value.FrameIdentifier);
            Assert.AreEqual(0x50607080U, result.Header.Value.OverallFrameIdentifier);
            Assert.AreEqual((byte)3, result.Header.Value.PlayerCarIndex);
            Assert.AreEqual(byte.MaxValue, result.Header.Value.SecondaryPlayerCarIndex);
        }
    }

    [TestMethod]
    public void FixedSeedRandomCorpusNeverThrowsOrReturnsUnspecified()
    {
        const int seed = 0x25C0;
        var random = new Random(seed);

        for (var sample = 0; sample < 2_048; sample++)
        {
            var datagram = new byte[random.Next(0, 2_050)];
            random.NextBytes(datagram);

            try
            {
                var result = adapter.Inspect(datagram);

                Assert.AreNotEqual(
                    TelemetryPacketClassification.Unspecified,
                    result.Classification,
                    $"Seed {seed}, sample {sample}, bytes {Convert.ToHexString(datagram)}.");
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"Seed {seed}, sample {sample}, bytes {Convert.ToHexString(datagram)}: "
                    + exception);
            }
        }
    }

    private static byte[] CreateDatagram(
        byte packetId,
        byte packetVersion,
        int length,
        ushort packetFormat = 2025,
        byte gameYear = 25)
    {
        var datagram = new byte[length];
        WriteUInt16(datagram, 0, packetFormat);
        datagram[2] = gameYear;
        datagram[3] = 1;
        datagram[4] = 7;
        datagram[5] = packetVersion;
        datagram[6] = packetId;
        WriteUInt64(datagram, 7, 0x0123456789ABCDEFUL);
        WriteUInt32(datagram, 15, unchecked((uint)BitConverter.SingleToInt32Bits(12.5F)));
        WriteUInt32(datagram, 19, 0x10203040U);
        WriteUInt32(datagram, 23, 0x50607080U);
        datagram[27] = 3;
        datagram[28] = byte.MaxValue;
        return datagram;
    }

    private static void WriteUInt16(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
        destination[offset + 2] = (byte)(value >> 16);
        destination[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt64(byte[] destination, int offset, ulong value)
    {
        for (var byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
        {
            destination[offset + byteIndex] = (byte)(value >> (byteIndex * 8));
        }
    }

    private static void AssertRejected(
        TelemetryPacketResult result,
        TelemetryPacketClassification expected,
        bool descriptorExpected = false)
    {
        Assert.IsFalse(result.IsCompatible);
        Assert.AreEqual(expected, result.Classification);
        Assert.AreEqual(descriptorExpected, result.Descriptor is not null);
    }

    private static void AssertHeader(
        TelemetryPacketResult result,
        ushort packetFormat,
        byte gameYear,
        byte packetId)
    {
        Assert.IsTrue(result.Header.HasValue);
        Assert.AreEqual(packetFormat, result.Header.Value.PacketFormat);
        Assert.AreEqual(gameYear, result.Header.Value.GameYear);
        Assert.AreEqual(packetId, result.Header.Value.PacketId);
    }
}
