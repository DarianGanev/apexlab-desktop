using System.Buffers.Binary;
using System.Net;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Protocols.F125.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Canonical;

[TestClass]
[DoNotParallelize]
public sealed class CanonicalReplayIntegrationTests
{
    [TestMethod]
    public async Task RealRawReplayBuildsThenReusesExplicitCanonicalRecords()
    {
        using var temporary = TemporaryRoot.Create("canonical-replay");
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            Corpus(),
            IPAddress.Loopback);
        var projector = new F125BahrainCanonicalProjector();

        var built = await RawEvidenceCanonicalReplay.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            projector,
            TestContext.CancellationToken);
        var reused = await RawEvidenceCanonicalReplay.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            projector,
            TestContext.CancellationToken);

        Assert.AreEqual(CanonicalCacheDisposition.Built, built.Disposition);
        Assert.AreEqual(CanonicalCacheDisposition.Reused, reused.Disposition);
        Assert.AreEqual(built.Completion, reused.Completion);
        Assert.AreEqual(9L, built.Completion.RecordCount);
        Assert.AreEqual(5L, built.Completion.ObservationCount);
        Assert.AreEqual(2L, built.Completion.ExclusionCount);
        Assert.AreEqual(2L, built.Completion.GapCount);

        var request = new CanonicalCacheRequest(
            built.Completion.Identity,
            built.Completion.SourceStopwatchFrequency);
        var store = new CanonicalCacheStore(temporary.Paths);
        await using var entry = await store.TryOpenAsync(
            request,
            TestContext.CancellationToken);
        Assert.IsNotNull(entry);
        var records = await CollectAsync(
            entry.ReadAllAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(
            new[]
            {
                CanonicalRecordKind.Gap,
                CanonicalRecordKind.Observation,
                CanonicalRecordKind.Observation,
                CanonicalRecordKind.Gap,
                CanonicalRecordKind.Observation,
                CanonicalRecordKind.Observation,
                CanonicalRecordKind.Exclusion,
                CanonicalRecordKind.Observation,
                CanonicalRecordKind.Exclusion,
            },
            records.Select(record => record.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                CanonicalPacketFamily.Motion,
                CanonicalPacketFamily.Session,
                CanonicalPacketFamily.LapData,
                CanonicalPacketFamily.Event,
                CanonicalPacketFamily.CarTelemetry,
            },
            records
                .Where(record => record.Packet.HasValue)
                .Select(record => record.Packet!.Value.Family)
                .ToArray());
        Assert.AreEqual(
            CanonicalExclusionReason.EventCodeOutsideSlice,
            records[6].ExclusionReason);
        Assert.AreEqual(
            CanonicalExclusionReason.CompatibleFamilyOutsideSlice,
            records[8].ExclusionReason);
        await entry.DisposeAsync();

        DeleteRawEvidence(temporary.Paths, completion.CaptureId);
    }

    [TestMethod]
    public async Task SameEnvelopesIgnoreCaptureMetadataButRetainProvenanceIdentity()
    {
        using var firstEvidence = TemporaryRoot.Create("canonical-source-a");
        using var secondEvidence = TemporaryRoot.Create("canonical-source-b");
        using var firstCache = TemporaryRoot.Create("canonical-cache-a");
        using var secondCache = TemporaryRoot.Create("canonical-cache-b");
        var corpus = Corpus();
        var firstRaw = await CreateEvidenceAsync(
            firstEvidence.Paths,
            corpus,
            IPAddress.Loopback);
        var secondRaw = await CreateEvidenceAsync(
            secondEvidence.Paths,
            corpus,
            IPAddress.Loopback);
        var projector = new F125BahrainCanonicalProjector();

        var first = await RawEvidenceCanonicalReplay.ExecuteAsync(
            firstEvidence.Paths,
            firstCache.Paths,
            firstRaw.CaptureId,
            projector,
            TestContext.CancellationToken);
        var second = await RawEvidenceCanonicalReplay.ExecuteAsync(
            secondEvidence.Paths,
            secondCache.Paths,
            secondRaw.CaptureId,
            projector,
            TestContext.CancellationToken);

        Assert.AreNotEqual(firstRaw.CaptureId, secondRaw.CaptureId);
        Assert.AreNotEqual(firstRaw.Sha256, secondRaw.Sha256);
        Assert.AreEqual(
            first.Completion.DataSha256,
            second.Completion.DataSha256);
        Assert.AreNotEqual(
            first.Completion.CanonicalSha256,
            second.Completion.CanonicalSha256);
        Assert.AreNotEqual(
            first.Completion.Identity.IdentitySha256,
            second.Completion.Identity.IdentitySha256);

        using var changedEvidence = TemporaryRoot.Create("canonical-source-c");
        using var changedCache = TemporaryRoot.Create("canonical-cache-c");
        var changedCorpus = Corpus();
        changedCorpus[0] = changedCorpus[0] with
        {
            Payload = Motion(worldX: 123.5F),
        };
        var changedRaw = await CreateEvidenceAsync(
            changedEvidence.Paths,
            changedCorpus,
            IPAddress.Loopback);
        var changed = await RawEvidenceCanonicalReplay.ExecuteAsync(
            changedEvidence.Paths,
            changedCache.Paths,
            changedRaw.CaptureId,
            projector,
            TestContext.CancellationToken);
        Assert.AreNotEqual(
            first.Completion.DataSha256,
            changed.Completion.DataSha256);
    }

    [TestMethod]
    public async Task NonLoopbackEvidenceFailsWithoutPublishingACache()
    {
        using var temporary = TemporaryRoot.Create("canonical-non-loopback");
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            [new CorpusRecord(1, Motion())],
            IPAddress.Parse("192.0.2.10"));

        var exception = await Assert.ThrowsAsync<CanonicalReplayException>(() =>
            RawEvidenceCanonicalReplay.ExecuteAsync(
                temporary.Paths,
                completion.CaptureId,
                new F125BahrainCanonicalProjector(),
                TestContext.CancellationToken));

        Assert.AreEqual(CanonicalReplayFailureKind.UnexpectedSender, exception.Kind);
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory));
        DeleteRawEvidence(temporary.Paths, completion.CaptureId);
    }

    [TestMethod]
    public async Task PreCanceledReplayDoesNotRetainRawOrCacheHandles()
    {
        using var temporary = TemporaryRoot.Create("canonical-canceled");
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            Corpus(),
            IPAddress.Loopback);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RawEvidenceCanonicalReplay.ExecuteAsync(
                temporary.Paths,
                completion.CaptureId,
                new F125BahrainCanonicalProjector(),
                cancellation.Token));

        DeleteRawEvidence(temporary.Paths, completion.CaptureId);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths,
        IReadOnlyList<CorpusRecord> records,
        IPAddress sender)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        var receivedAt = new DateTimeOffset(
            2026,
            8,
            9,
            12,
            0,
            0,
            TimeSpan.Zero);
        foreach (var record in records)
        {
            await writer.WriteAsync(DatagramEnvelope.CopyFrom(
                record.Sequence,
                1_000 + record.Sequence,
                receivedAt.AddMilliseconds(record.Sequence),
                new DatagramSender(sender, 20_777),
                record.Payload));
        }

        return await writer.FinalizeAsync();
    }

    private static List<CorpusRecord> Corpus() =>
    [
        new(2, Motion()),
        new(3, Session()),
        new(5, Lap()),
        new(6, Event("SSTA")),
        new(7, Event("FTLP")),
        new(8, Telemetry()),
        new(9, Packet(7, 1_239)),
    ];

    private static byte[] Motion(float worldX = 1.5F)
    {
        var packet = Packet(0, 1_349);
        var offset = 29 + (3 * 60);
        WriteSingle(packet, offset, worldX);
        WriteSingle(packet, offset + 4, -2.5F);
        WriteSingle(packet, offset + 8, 3.75F);
        return packet;
    }

    private static byte[] Session()
    {
        var packet = Packet(1, 753);
        packet[29] = 5;
        packet[30] = unchecked((byte)-12);
        packet[31] = unchecked((byte)-3);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(33), 5_412);
        packet[35] = 18;
        packet[36] = 3;
        packet[37] = 9;
        packet[44] = 1;
        packet[154] = 0;
        packet[685] = 1;
        packet[686] = 3;
        packet[687] = 2;
        packet[688] = 1;
        packet[689] = 0;
        packet[690] = 1;
        packet[691] = 0;
        packet[692] = 2;
        packet[693] = 1;
        packet[694] = 5;
        packet[695] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(696), 1_439);
        packet[708] = 1;
        packet[709] = 2;
        return packet;
    }

    private static byte[] Lap()
    {
        var packet = Packet(2, 1_285);
        var offset = 29 + (3 * 57);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), 90_000);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 4), 12_000);
        WriteSingle(packet, offset + 20, -10F);
        WriteSingle(packet, offset + 24, 100F);
        packet[offset + 33] = 4;
        packet[offset + 34] = 1;
        packet[offset + 36] = 2;
        packet[offset + 37] = 1;
        packet[offset + 44] = 3;
        packet[offset + 45] = 4;
        return packet;
    }

    private static byte[] Event(string code)
    {
        var packet = Packet(3, 45);
        for (var index = 0; index < 4; index++)
        {
            packet[29 + index] = (byte)code[index];
        }

        return packet;
    }

    private static byte[] Telemetry()
    {
        var packet = Packet(6, 1_352);
        var offset = 29 + (3 * 60);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset), 312);
        WriteSingle(packet, offset + 2, 0.75F);
        WriteSingle(packet, offset + 10, 0.25F);
        packet[offset + 15] = unchecked((byte)-1);
        return packet;
    }

    private static byte[] Packet(byte packetId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(15),
            unchecked((uint)BitConverter.SingleToInt32Bits(12.5F)));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), 0x10203040);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), 0x50607080);
        packet[27] = 3;
        packet[28] = byte.MaxValue;
        return packet;
    }

    private static void WriteSingle(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset),
            BitConverter.SingleToInt32Bits(value));

    private static async Task<CanonicalRecord[]> CollectAsync(
        IAsyncEnumerable<CanonicalRecord> source)
    {
        var records = new List<CanonicalRecord>();
        await foreach (var record in source)
        {
            records.Add(record);
        }

        return records.ToArray();
    }

    private static void DeleteRawEvidence(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId)
    {
        var prefix = Path.Combine(paths.RawCapturesDirectory, captureId.Value);
        File.Delete(prefix + ".apxraw");
        File.Delete(prefix + ".apxraw.json");
    }

    private sealed record CorpusRecord(long Sequence, byte[] Payload);

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create(string purpose) =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-{purpose}-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
