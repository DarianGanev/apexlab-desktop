using System.Text;
using ApexLab.Application.Canonical;
using ApexLab.Persistence.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Tests.Canonical;

[TestClass]
public sealed class CanonicalCacheFormatTests
{
    private const long Sequence = 0x0102030405060708;
    private const long Arrival = 0x1112131415161718;

    [TestMethod]
    public void HeaderAndFooterHaveFrozenLittleEndianBytes()
    {
        var header = CanonicalCacheFormat.SerializeHeader(10_000_000);
        var expectedHeader = Convert.FromHexString(
            "41505843414e3100010014008096980000000000");
        CollectionAssert.AreEqual(expectedHeader, header);
        Assert.AreEqual(
            new CanonicalCacheHeader(10_000_000),
            CanonicalCacheFormat.DeserializeHeader(header));

        var footer = new CanonicalCacheFooter(9, 5, 2, 2, 3, 11);
        var bytes = CanonicalCacheFormat.SerializeFooter(footer);
        var expectedFooter = Convert.FromHexString(
            "00000000415058454e44310001003d00"
            + "09000000000000000500000000000000"
            + "02000000000000000200000000000000"
            + "0103000000000000000b00000000000000");
        CollectionAssert.AreEqual(expectedFooter, bytes);
        Assert.AreEqual(footer, CanonicalCacheFormat.DeserializeFooter(bytes));
    }

    [TestMethod]
    public void EveryObservationUnionHasIndependentGoldenBytes()
    {
        AssertGoldenObservation(
            CanonicalPacket.CreateMotion(
                Header(),
                new CanonicalMotionPacket(2F, -3F, 4.5F)),
            writer =>
            {
                writer.Write(SingleBits(2F));
                writer.Write(SingleBits(-3F));
                writer.Write(SingleBits(4.5F));
            });

        AssertGoldenObservation(
            CanonicalPacket.CreateSession(
                Header(),
                new CanonicalSessionPacket(
                    1, -2, -3, 4_000, 5, -1, 2, true, false,
                    1, 3, 2, 1, 0, 1, 0, 2, 1, 4, 5, 600, true, 2)),
            writer =>
            {
                writer.Write((byte)1);
                writer.Write((sbyte)-2);
                writer.Write((sbyte)-3);
                writer.Write((ushort)4_000);
                writer.Write((byte)5);
                writer.Write((sbyte)-1);
                writer.Write((byte)2);
                writer.Write((byte)1);
                writer.Write((byte)0);
                writer.Write((byte)1);
                writer.Write((byte)3);
                writer.Write((byte)2);
                writer.Write((byte)1);
                writer.Write((byte)0);
                writer.Write((byte)1);
                writer.Write((byte)0);
                writer.Write((byte)2);
                writer.Write((byte)1);
                writer.Write((byte)4);
                writer.Write((byte)5);
                writer.Write(600U);
                writer.Write((byte)1);
                writer.Write((byte)2);
            });

        AssertGoldenObservation(
            CanonicalPacket.CreateLapData(
                Header(),
                new CanonicalLapPacket(
                    90_001, 12_345, -10.5F, 321.25F, 4, 1, 2, true, 3, 4)),
            writer =>
            {
                writer.Write(90_001U);
                writer.Write(12_345U);
                writer.Write(SingleBits(-10.5F));
                writer.Write(SingleBits(321.25F));
                writer.Write((byte)4);
                writer.Write((byte)1);
                writer.Write((byte)2);
                writer.Write((byte)1);
                writer.Write((byte)3);
                writer.Write((byte)4);
            });

        AssertGoldenObservation(
            CanonicalPacket.CreateEvent(
                Header(),
                CanonicalEventPacket.SessionStarted()),
            writer =>
            {
                writer.Write((byte)CanonicalEventKind.SessionStarted);
                writer.Write((byte)0);
            });
        AssertGoldenObservation(
            CanonicalPacket.CreateEvent(
                Header(),
                CanonicalEventPacket.Flashback(0xA1B2C3D4, 8.25F)),
            writer =>
            {
                writer.Write((byte)CanonicalEventKind.Flashback);
                writer.Write((byte)1);
                writer.Write(0xA1B2C3D4U);
                writer.Write(SingleBits(8.25F));
            });

        AssertGoldenObservation(
            CanonicalPacket.CreateCarTelemetry(
                Header(),
                new CanonicalCarTelemetryPacket(321, 0.5F, 0.25F, -1)),
            writer =>
            {
                writer.Write((ushort)321);
                writer.Write(SingleBits(0.5F));
                writer.Write(SingleBits(0.25F));
                writer.Write((sbyte)-1);
            });
    }

    [TestMethod]
    public void ExclusionAndGapHaveIndependentGoldenBytes()
    {
        var exclusion = CanonicalRecord.Exclusion(
            Sequence,
            7,
            CanonicalExclusionReason.EventCodeOutsideSlice);
        var expectedExclusion = Frame(
            (byte)CanonicalRecordKind.Exclusion,
            writer =>
            {
                writer.Write(Sequence);
                writer.Write((byte)7);
                writer.Write((byte)CanonicalExclusionReason.EventCodeOutsideSlice);
            });
        CollectionAssert.AreEqual(
            expectedExclusion,
            CanonicalCacheFormat.SerializeRecord(exclusion));
        Assert.AreEqual(
            exclusion,
            CanonicalCacheFormat.DeserializeRecord(expectedExclusion));

        var gap = CanonicalRecord.Gap(
            5,
            9,
            CanonicalGapReason.UnretainedOrMissingSourceRange);
        var expectedGap = Frame(
            (byte)CanonicalRecordKind.Gap,
            writer =>
            {
                writer.Write(5L);
                writer.Write(9L);
                writer.Write((byte)CanonicalGapReason.UnretainedOrMissingSourceRange);
            });
        CollectionAssert.AreEqual(
            expectedGap,
            CanonicalCacheFormat.SerializeRecord(gap));
        Assert.AreEqual(gap, CanonicalCacheFormat.DeserializeRecord(expectedGap));
    }

    [TestMethod]
    public void ParserRejectsUnknownKindsLengthsBooleansAndNonFiniteFloats()
    {
        var unknown = Frame(99, _ => { });
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheFormat.DeserializeRecord(unknown));

        var trailing = CanonicalCacheFormat.SerializeRecord(
            CanonicalRecord.Gap(
                1,
                2,
                CanonicalGapReason.UnretainedOrMissingSourceRange))
            .Concat(new byte[] { 0xCC })
            .ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheFormat.DeserializeRecord(trailing));

        var lap = CanonicalCacheFormat.SerializeRecord(
            CanonicalRecord.Observation(
                Sequence,
                Arrival,
                CanonicalPacket.CreateLapData(
                    Header(),
                    new CanonicalLapPacket(1, 2, 3F, 4F, 1, 1, 1, false, 1, 1))));
        lap[^3] = 2;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheFormat.DeserializeRecord(lap));

        var motion = CanonicalCacheFormat.SerializeRecord(
            CanonicalRecord.Observation(
                Sequence,
                Arrival,
                CanonicalPacket.CreateMotion(
                    Header(),
                    new CanonicalMotionPacket(2F, 3F, 4F))));
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(motion, motion.Length - 4);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheFormat.DeserializeRecord(motion));
    }

    [TestMethod]
    public void ManifestHasFrozenClosedDeterministicJson()
    {
        var completion = Completion();
        var manifest = CanonicalCacheManifest.FromCompletion(completion);
        var utf8 = manifest.Serialize();
        var expected = "{"
            + "\"manifestFormatVersion\":1,"
            + "\"dataFormatVersion\":1,"
            + "\"identitySha256\":\"75a1f0f8f2993b77daf1b67fcc7144a2fdec751ad765bc199f7a9a1123e65154\","
            + "\"sourceEvidenceSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\","
            + "\"protocolId\":\"ea-f1-25-v3\","
            + "\"contractId\":\"apexlab-bahrain-tt-slice-v1\","
            + "\"decoderId\":\"f125-v3-minimal-decoder-v1\","
            + "\"canonicalSchemaId\":\"apexlab-canonical-sample-v1\","
            + "\"dataLeafName\":\"75a1f0f8f2993b77daf1b67fcc7144a2fdec751ad765bc199f7a9a1123e65154.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.apxcan\","
            + "\"dataLengthBytes\":256,"
            + "\"dataSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\","
            + "\"canonicalSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\","
            + "\"sourceStopwatchFrequency\":10000000,"
            + "\"recordCount\":9,"
            + "\"observationCount\":5,"
            + "\"exclusionCount\":2,"
            + "\"gapCount\":2,"
            + "\"firstSourceSequence\":3,"
            + "\"lastSourceSequence\":11}"
            + "\n";

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), utf8);
        Assert.AreEqual(manifest, CanonicalCacheManifest.Deserialize(utf8));

        var withUnknown = Encoding.UTF8.GetBytes(
            expected.TrimEnd('\n')[..^1] + ",\"unknown\":1}\n");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheManifest.Deserialize(withUnknown));
    }

    [TestMethod]
    public void EmptyManifestUsesNullBoundariesAndAllLimitsAreEnforced()
    {
        var completion = new CanonicalCacheCompletion(
            Identity(),
            1,
            CanonicalCacheFormat.HeaderLength + CanonicalCacheFormat.FooterLength,
            new string('a', 64),
            new string('b', 64),
            0,
            0,
            0,
            0,
            null,
            null);
        var json = Encoding.UTF8.GetString(
            CanonicalCacheManifest.FromCompletion(completion).Serialize());
        StringAssert.Contains(json, "\"firstSourceSequence\":null");
        StringAssert.Contains(json, "\"lastSourceSequence\":null");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            CanonicalCacheManifest.Deserialize(
                new byte[CanonicalCacheManifest.MaximumLengthBytes + 1]));

        var impossibleCount = new CanonicalCacheCompletion(
            Identity(),
            1,
            CanonicalCacheFormat.HeaderLength + CanonicalCacheFormat.FooterLength,
            new string('a', 64),
            new string('b', 64),
            1,
            1,
            0,
            0,
            1,
            1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CanonicalCacheManifest.FromCompletion(impossibleCount));
    }

    private static void AssertGoldenObservation(
        CanonicalPacket packet,
        Action<BinaryWriter> writePayload)
    {
        var record = CanonicalRecord.Observation(Sequence, Arrival, packet);
        var expected = Frame(
            (byte)CanonicalRecordKind.Observation,
            writer =>
            {
                writer.Write(Sequence);
                writer.Write(Arrival);
                writer.Write((byte)packet.Family);
                WriteHeader(writer);
                writePayload(writer);
            });

        CollectionAssert.AreEqual(
            expected,
            CanonicalCacheFormat.SerializeRecord(record));
        Assert.AreEqual(record, CanonicalCacheFormat.DeserializeRecord(expected));
    }

    private static byte[] Frame(byte kind, Action<BinaryWriter> writeBody)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(kind);
            writeBody(writer);
        }

        using var framed = new MemoryStream();
        using (var writer = new BinaryWriter(framed, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(checked((uint)body.Length));
            writer.Write(body.ToArray());
        }

        return framed.ToArray();
    }

    private static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(SingleBits(1F));
        writer.Write(0x01020304U);
        writer.Write(0xA0B0C0D0U);
        writer.Write((byte)2);
        writer.Write(byte.MaxValue);
    }

    private static int SingleBits(float value) =>
        BitConverter.SingleToInt32Bits(value);

    private static CanonicalPacketHeader Header() =>
        new(1F, 0x01020304, 0xA0B0C0D0, 2, byte.MaxValue);

    private static CanonicalCacheCompletion Completion() =>
        new(
            Identity(),
            10_000_000,
            256,
            new string('a', 64),
            new string('b', 64),
            9,
            5,
            2,
            2,
            3,
            11);

    private static CanonicalReplayIdentity Identity() =>
        new(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "ea-f1-25-v3",
            "apexlab-bahrain-tt-slice-v1",
            "f125-v3-minimal-decoder-v1",
            "apexlab-canonical-sample-v1");
}
