using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Telemetry.Tests.Protocol;

[TestClass]
public sealed class TelemetryProtocolContractTests
{
    [TestMethod]
    public void Descriptor_PreservesValidatedPacketContract()
    {
        var descriptor = new TelemetryPacketDescriptor(
            "Car telemetry",
            packetId: 6,
            packetVersion: 1,
            datagramLength: 1_352,
            TelemetryPacketPrivacyDisposition.EvidenceAllowed);

        Assert.AreEqual("Car telemetry", descriptor.FamilyName);
        Assert.AreEqual((byte)6, descriptor.PacketId);
        Assert.AreEqual((byte)1, descriptor.PacketVersion);
        Assert.AreEqual(1_352, descriptor.DatagramLength);
        Assert.AreEqual(
            TelemetryPacketPrivacyDisposition.EvidenceAllowed,
            descriptor.PrivacyDisposition);
    }

    [TestMethod]
    public void Descriptor_RejectsInvalidMetadata()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDescriptor(familyName: " "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDescriptor(datagramLength: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDescriptor(
                datagramLength: TelemetryPacketDescriptor.MaximumUdpPayloadLength + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDescriptor(
                privacyDisposition: (TelemetryPacketPrivacyDisposition)byte.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDescriptor(
                privacyDisposition: TelemetryPacketPrivacyDisposition.Unspecified));
    }

    [TestMethod]
    public void Descriptor_AcceptsInclusiveUdpPayloadBoundaries()
    {
        var minimum = CreateDescriptor(datagramLength: 1);
        var maximum = CreateDescriptor(
            datagramLength: TelemetryPacketDescriptor.MaximumUdpPayloadLength);

        Assert.AreEqual(1, minimum.DatagramLength);
        Assert.AreEqual(
            TelemetryPacketDescriptor.MaximumUdpPayloadLength,
            maximum.DatagramLength);
    }

    [TestMethod]
    public void Header_PreservesFiniteProtocolMetadata()
    {
        var header = CreateHeader(sessionTimeSeconds: 12.5F);

        Assert.AreEqual((ushort)2025, header.PacketFormat);
        Assert.AreEqual((byte)25, header.GameYear);
        Assert.AreEqual((byte)1, header.PacketVersion);
        Assert.AreEqual((byte)6, header.PacketId);
        Assert.AreEqual(123UL, header.SessionUid);
        Assert.AreEqual(12.5F, header.SessionTimeSeconds);
        Assert.AreEqual(456U, header.FrameIdentifier);
        Assert.AreEqual(457U, header.OverallFrameIdentifier);
        Assert.AreEqual((byte)3, header.PlayerCarIndex);
        Assert.AreEqual(byte.MaxValue, header.SecondaryPlayerCarIndex);
    }

    [TestMethod]
    public void Header_AllowsFiniteNegativeSessionTimeButRejectsNonFiniteValues()
    {
        var preStartHeader = CreateHeader(sessionTimeSeconds: -0.001F);

        Assert.AreEqual(-0.001F, preStartHeader.SessionTimeSeconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateHeader(sessionTimeSeconds: float.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateHeader(sessionTimeSeconds: float.PositiveInfinity));
    }

    [TestMethod]
    public void CompatibleResult_RequiresAndPublishesHeaderAndDescriptor()
    {
        var header = CreateHeader();
        var descriptor = CreateDescriptor();

        var result = TelemetryPacketResult.Compatible(header, descriptor);

        Assert.IsTrue(result.IsCompatible);
        Assert.AreEqual(TelemetryPacketClassification.Compatible, result.Classification);
        Assert.AreEqual(header, result.Header);
        Assert.AreSame(descriptor, result.Descriptor);
        Assert.ThrowsExactly<ArgumentNullException>(
            () => TelemetryPacketResult.Compatible(header, null!));
    }

    [TestMethod]
    public void CompatibleResult_RejectsExcludedOrContradictoryDescriptors()
    {
        var header = CreateHeader();

        Assert.ThrowsExactly<ArgumentException>(
            () => TelemetryPacketResult.Compatible(
                header,
                CreateDescriptor(
                    privacyDisposition:
                        TelemetryPacketPrivacyDisposition.IdentityBearingExcluded)));
        Assert.ThrowsExactly<ArgumentException>(
            () => TelemetryPacketResult.Compatible(
                header,
                new TelemetryPacketDescriptor(
                    "Different packet ID",
                    packetId: 7,
                    packetVersion: header.PacketVersion,
                    datagramLength: 29,
                    TelemetryPacketPrivacyDisposition.EvidenceAllowed)));
        Assert.ThrowsExactly<ArgumentException>(
            () => TelemetryPacketResult.Compatible(
                header,
                new TelemetryPacketDescriptor(
                    "Different packet version",
                    packetId: header.PacketId,
                    packetVersion: 2,
                    datagramLength: 29,
                    TelemetryPacketPrivacyDisposition.EvidenceAllowed)));
    }

    [TestMethod]
    public void RejectedResult_PreservesAvailableEvidenceAndCannotClaimCompatibility()
    {
        var header = CreateHeader();
        var descriptor = CreateDescriptor();

        var result = TelemetryPacketResult.Rejected(
            TelemetryPacketClassification.InvalidPacketLength,
            header,
            descriptor);

        Assert.IsFalse(result.IsCompatible);
        Assert.AreEqual(TelemetryPacketClassification.InvalidPacketLength, result.Classification);
        Assert.AreEqual(header, result.Header);
        Assert.AreSame(descriptor, result.Descriptor);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.Compatible,
                header,
                descriptor));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TelemetryPacketResult.Rejected(
                (TelemetryPacketClassification)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.Unspecified));
    }

    [TestMethod]
    public void DefaultResultCannotAppearCompatible()
    {
        var result = default(TelemetryPacketResult);

        Assert.AreEqual(TelemetryPacketClassification.Unspecified, result.Classification);
        Assert.IsFalse(result.IsCompatible);
        Assert.IsNull(result.Header);
        Assert.IsNull(result.Descriptor);
    }

    [TestMethod]
    public void AdapterContract_InspectsCallerOwnedSpanSynchronously()
    {
        ITelemetryProtocolAdapter adapter = new RecordingAdapter();
        ReadOnlySpan<byte> datagram = [1, 2, 3, 4];

        var result = adapter.Inspect(datagram);

        Assert.AreEqual("recording", adapter.ProtocolId);
        Assert.AreEqual(4, ((RecordingAdapter)adapter).ObservedLength);
        Assert.AreEqual(TelemetryPacketClassification.MalformedHeader, result.Classification);
    }

    private static TelemetryPacketDescriptor CreateDescriptor(
        string familyName = "Car telemetry",
        int datagramLength = 1_352,
        TelemetryPacketPrivacyDisposition privacyDisposition =
            TelemetryPacketPrivacyDisposition.EvidenceAllowed) =>
        new(familyName, 6, 1, datagramLength, privacyDisposition);

    private static TelemetryHeaderMetadata CreateHeader(float sessionTimeSeconds = 0F) =>
        new(
            packetFormat: 2025,
            gameYear: 25,
            gameMajorVersion: 1,
            gameMinorVersion: 0,
            packetVersion: 1,
            packetId: 6,
            sessionUid: 123,
            sessionTimeSeconds,
            frameIdentifier: 456,
            overallFrameIdentifier: 457,
            playerCarIndex: 3,
            secondaryPlayerCarIndex: byte.MaxValue);

    private sealed class RecordingAdapter : ITelemetryProtocolAdapter
    {
        public string ProtocolId => "recording";

        public int HeaderLength => 1;

        public int ObservedLength { get; private set; }

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
        {
            ObservedLength = datagram.Length;
            return TelemetryPacketResult.Rejected(TelemetryPacketClassification.MalformedHeader);
        }
    }
}
