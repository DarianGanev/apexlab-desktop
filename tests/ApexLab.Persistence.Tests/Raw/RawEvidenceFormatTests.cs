using System.Buffers.Binary;
using System.Net;
using System.Text;
using ApexLab.Application.Capture;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
public sealed class RawEvidenceFormatTests
{
    private static readonly RawEvidenceCaptureId CaptureId =
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");
    private static readonly RawEvidenceProtocolId ProtocolId =
        RawEvidenceProtocolId.Parse("ea-f1-25-v3");

    [TestMethod]
    public void DataHeaderUsesTheReviewedExactOffsetsAndLittleEndianValues()
    {
        var destination = new byte[RawEvidenceFormat.DataHeaderLength];

        RawEvidenceFormat.WriteDataHeader(
            destination,
            CaptureId,
            stopwatchFrequency: 10_000_000,
            createdUtcTicks: 621_355_968_000_000_000,
            maximumPayloadBytes: 2_048);

        CollectionAssert.AreEqual(
            "APXRAW1\0"u8.ToArray(),
            destination[..8]);
        Assert.AreEqual(
            (ushort)1,
            BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(8, 2)));
        Assert.AreEqual(
            (ushort)72,
            BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(10, 2)));
        Assert.AreEqual(
            0U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(12, 4)));
        Assert.AreEqual(
            CaptureId.Value,
            Encoding.ASCII.GetString(destination, 16, 32));
        Assert.AreEqual(
            10_000_000L,
            BinaryPrimitives.ReadInt64LittleEndian(destination.AsSpan(48, 8)));
        Assert.AreEqual(
            621_355_968_000_000_000L,
            BinaryPrimitives.ReadInt64LittleEndian(destination.AsSpan(56, 8)));
        Assert.AreEqual(
            2_048U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(64, 4)));
        Assert.AreEqual(
            0U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(68, 4)));
    }

    [TestMethod]
    public void RecordHeaderEncodesIpv4SenderAndExactPayloadLength()
    {
        var envelope = CreateEnvelope(
            sequence: 7,
            monotonicTimestamp: 11,
            IPAddress.Parse("192.0.2.4"),
            port: 20_777,
            payload: [0x10, 0x20, 0x30]);
        var destination = new byte[RawEvidenceFormat.RecordHeaderLength];

        RawEvidenceFormat.WriteRecordHeader(destination, envelope);

        CollectionAssert.AreEqual("REC1"u8.ToArray(), destination[..4]);
        Assert.AreEqual(
            63U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(4, 4)));
        Assert.AreEqual(
            7L,
            BinaryPrimitives.ReadInt64LittleEndian(destination.AsSpan(8, 8)));
        Assert.AreEqual(
            11L,
            BinaryPrimitives.ReadInt64LittleEndian(destination.AsSpan(16, 8)));
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.UtcTicks,
            BinaryPrimitives.ReadInt64LittleEndian(destination.AsSpan(24, 8)));
        Assert.AreEqual((byte)4, destination[32]);
        Assert.AreEqual((byte)0, destination[33]);
        Assert.AreEqual(
            (ushort)20_777,
            BinaryPrimitives.ReadUInt16LittleEndian(destination.AsSpan(34, 2)));
        CollectionAssert.AreEqual(
            new byte[]
            {
                192, 0, 2, 4,
                0, 0, 0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
            },
            destination[36..52]);
        Assert.AreEqual(
            0U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(52, 4)));
        Assert.AreEqual(
            3U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(56, 4)));
    }

    [TestMethod]
    public void RecordHeaderEncodesIpv6AddressAndScope()
    {
        var address = IPAddress.Parse("fe80::1234");
        address.ScopeId = 17;
        var envelope = CreateEnvelope(
            sequence: 1,
            monotonicTimestamp: 0,
            address,
            port: 49_152,
            payload: [0x01]);
        var destination = new byte[RawEvidenceFormat.RecordHeaderLength];

        RawEvidenceFormat.WriteRecordHeader(destination, envelope);

        Assert.AreEqual((byte)6, destination[32]);
        CollectionAssert.AreEqual(
            address.GetAddressBytes(),
            destination[36..52]);
        Assert.AreEqual(
            17U,
            BinaryPrimitives.ReadUInt32LittleEndian(destination.AsSpan(52, 4)));
    }

    [TestMethod]
    public void FooterUsesReviewedSentinelsAndCounters()
    {
        var empty = new byte[RawEvidenceFormat.FooterLength];
        var populated = new byte[RawEvidenceFormat.FooterLength];

        RawEvidenceFormat.WriteFooter(
            empty,
            recordCount: 0,
            firstSequence: null,
            lastSequence: null,
            recordsByteLength: 0,
            firstArrivalTimestamp: null,
            lastArrivalTimestamp: null);
        RawEvidenceFormat.WriteFooter(
            populated,
            recordCount: 2,
            firstSequence: 4,
            lastSequence: 8,
            recordsByteLength: 125,
            firstArrivalTimestamp: 10,
            lastArrivalTimestamp: 20);

        CollectionAssert.AreEqual("APXFTR1\0"u8.ToArray(), empty[..8]);
        Assert.AreEqual(
            -1L,
            BinaryPrimitives.ReadInt64LittleEndian(empty.AsSpan(24, 8)));
        Assert.AreEqual(
            -1L,
            BinaryPrimitives.ReadInt64LittleEndian(empty.AsSpan(56, 8)));
        Assert.AreEqual(
            2UL,
            BinaryPrimitives.ReadUInt64LittleEndian(populated.AsSpan(16, 8)));
        Assert.AreEqual(
            4L,
            BinaryPrimitives.ReadInt64LittleEndian(populated.AsSpan(24, 8)));
        Assert.AreEqual(
            8L,
            BinaryPrimitives.ReadInt64LittleEndian(populated.AsSpan(32, 8)));
        Assert.AreEqual(
            125UL,
            BinaryPrimitives.ReadUInt64LittleEndian(populated.AsSpan(40, 8)));
        Assert.AreEqual(
            10L,
            BinaryPrimitives.ReadInt64LittleEndian(populated.AsSpan(48, 8)));
        Assert.AreEqual(
            20L,
            BinaryPrimitives.ReadInt64LittleEndian(populated.AsSpan(56, 8)));
    }

    [TestMethod]
    public void ManifestPreimageIsExactBoundedCanonicalUtf8()
    {
        var manifest = CreateEmptyManifest();

        var bytes = RawEvidenceFormat.SerializeManifestPreimage(manifest);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.AreEqual(
            "{\"schemaVersion\":1,\"dataFormatVersion\":1,"
            + "\"captureId\":\"00112233445546778899aabbccddeeff\","
            + "\"protocolId\":\"ea-f1-25-v3\","
            + "\"dataFileName\":\"00112233445546778899aabbccddeeff.apxraw\","
            + "\"dataLengthBytes\":136,"
            + "\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\","
            + "\"recordCount\":0,\"firstSequence\":null,\"lastSequence\":null,"
            + "\"firstArrivalTimestamp\":null,\"lastArrivalTimestamp\":null,"
            + "\"stopwatchFrequency\":10000000,"
            + "\"createdUtcTicks\":621355968000000000,"
            + "\"finalizedUtcTicks\":621355968000000001,"
            + "\"limits\":{\"maximumDurationMilliseconds\":900000,"
            + "\"maximumFileBytes\":536870912,"
            + "\"minimumFreeSpaceBytes\":1073741824,"
            + "\"maximumPayloadBytes\":65507}}",
            json);
        Assert.IsLessThanOrEqualTo(
            RawEvidenceFormat.MaximumManifestBytes,
            bytes.Length);
    }

    [TestMethod]
    public void DigestReplacementChangesOnlyTheFixedPlaceholder()
    {
        var preimage = RawEvidenceFormat.SerializeManifestPreimage(
            CreateEmptyManifest());
        var digest = Enumerable.Range(0, 32)
            .Select(value => (byte)value)
            .ToArray();

        var final = RawEvidenceFormat.ApplyDigest(preimage, digest);
        var finalJson = Encoding.UTF8.GetString(final);

        Assert.HasCount(preimage.Length, final);
        Assert.Contains(
            "\"sha256\":\"000102030405060708090a0b0c0d0e0f"
            + "101112131415161718191a1b1c1d1e1f\"",
            finalJson,
            StringComparison.Ordinal);
        var differing = preimage
            .Zip(final)
            .Count(pair => pair.First != pair.Second);
        Assert.AreEqual(46, differing);
    }

    private static RawEvidenceManifest CreateEmptyManifest()
    {
        return new RawEvidenceManifest(
            CaptureId,
            ProtocolId,
            dataLengthBytes: RawEvidenceFormat.DataHeaderLength
                + RawEvidenceFormat.FooterLength,
            recordCount: 0,
            firstSequence: null,
            lastSequence: null,
            firstArrivalTimestamp: null,
            lastArrivalTimestamp: null,
            stopwatchFrequency: 10_000_000,
            createdUtcTicks: 621_355_968_000_000_000,
            finalizedUtcTicks: 621_355_968_000_000_001,
            new RawEvidenceLimits());
    }

    private static DatagramEnvelope CreateEnvelope(
        long sequence,
        long monotonicTimestamp,
        IPAddress address,
        int port,
        byte[] payload)
    {
        return DatagramEnvelope.CopyFrom(
            sequence,
            monotonicTimestamp,
            DateTimeOffset.UnixEpoch,
            new DatagramSender(address, port),
            payload);
    }
}
