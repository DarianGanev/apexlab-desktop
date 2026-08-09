using System.Buffers.Binary;
using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Protocols.F125.Canonical;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Laps;

[TestClass]
[DoNotParallelize]
public sealed class RawEvidenceBahrainLapAuditTests
{
    [TestMethod]
    public async Task ProductionReplayProducesDeterministicAuditsAndEnforcesFiveLapGate()
    {
        using var first = TemporaryRoot.Create("lap-audit-a");
        using var second = TemporaryRoot.Create("lap-audit-b");
        var captureId = RawEvidenceCaptureId.Parse(
            "0123456789ab4def8123456789abcdef");
        await CreateEvidenceAsync(first.Paths, captureId);
        CopyEvidence(first.Paths, second.Paths, captureId);
        var projector = new F125BahrainCanonicalProjector();

        var firstTemplate = await RawEvidenceBahrainLapAudit.PrepareAsync(
            first.Paths,
            captureId,
            projector,
            TestContext.CancellationToken);
        var secondTemplate = await RawEvidenceBahrainLapAudit.PrepareAsync(
            second.Paths,
            captureId,
            projector,
            TestContext.CancellationToken);

        Assert.HasCount(7, firstTemplate.Entries);
        Assert.HasCount(5, firstTemplate.Entries.Where(entry =>
            entry.Boundary.Completeness == LapBoundaryCompleteness.Complete));
        foreach (var entry in firstTemplate.Entries.Where(entry =>
                     entry.Boundary.Completeness == LapBoundaryCompleteness.Complete))
        {
            Assert.AreEqual(
                LapEvidenceFlags.None,
                entry.EvidenceFlags,
                $"candidate={entry.CandidateId.Value}");
            Assert.AreEqual(
                firstTemplate.ReferenceContext,
                entry.Context,
                $"candidate={entry.CandidateId.Value}");
        }
        CollectionAssert.AreEqual(
            AuditBytes(first.Paths),
            AuditBytes(second.Paths));

        await ReplaceAuditAsync(first.Paths, Complete(firstTemplate, 4));
        await ReplaceAuditAsync(second.Paths, Complete(secondTemplate, 5));
        var abstained = await RawEvidenceBahrainLapAudit.EvaluateAsync(
            first.Paths,
            captureId,
            projector,
            TestContext.CancellationToken);
        var ready = await RawEvidenceBahrainLapAudit.EvaluateAsync(
            second.Paths,
            captureId,
            projector,
            TestContext.CancellationToken);

        Assert.AreEqual(BaselineDisposition.Abstained, abstained.Selection?.Disposition);
        CollectionAssert.Contains(
            abstained.AbstentionReasons.ToArray(),
            BaselineAbstentionReason.InsufficientComparableLaps);
        Assert.AreEqual(BaselineDisposition.Ready, ready.Selection?.Disposition);
        Assert.AreEqual(5, ready.IncludedCount);
        Assert.AreEqual(2, ready.ExcludedCount);
        Assert.IsTrue(ready.AllCandidatesAudited);
        Assert.IsTrue(ready.IncludedContextsMatch);

        DeleteAllEvidence(first.Paths);
        DeleteAllEvidence(second.Paths);
    }

    [TestMethod]
    public async Task StaleAuditCancellationAndCorruptionFailWithoutRetainingHandles()
    {
        using var temporary = TemporaryRoot.Create("lap-audit-failures");
        var captureId = RawEvidenceCaptureId.Parse(
            "1123456789ab4def8123456789abcdef");
        await CreateEvidenceAsync(temporary.Paths, captureId);
        var projector = new F125BahrainCanonicalProjector();
        var template = await RawEvidenceBahrainLapAudit.PrepareAsync(
            temporary.Paths,
            captureId,
            projector,
            TestContext.CancellationToken);
        var stale = new BahrainLapAuditDocument(
            template.SchemaVersion,
            template.LapAuditId,
            new string('c', 64),
            template.CanonicalSha256,
            template.ReferenceContext,
            template.ManualInputs,
            template.Entries);
        await ReplaceAuditAsync(temporary.Paths, stale);

        var mismatch = await Assert.ThrowsExactlyAsync<BahrainLapAuditException>(() =>
            RawEvidenceBahrainLapAudit.EvaluateAsync(
                temporary.Paths,
                captureId,
                projector,
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditFailureKind.ProvenanceMismatch, mismatch.Kind);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RawEvidenceBahrainLapAudit.BuildInventoryAsync(
                temporary.Paths,
                temporary.Paths,
                captureId,
                projector,
                canceled.Token));

        await File.WriteAllTextAsync(
            AuditPath(temporary.Paths),
            "{}\n",
            TestContext.CancellationToken);
        var corrupt = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            RawEvidenceBahrainLapAudit.EvaluateAsync(
                temporary.Paths,
                captureId,
                projector,
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditStoreFailureKind.InvalidDocument, corrupt.Kind);

        DeleteAllEvidence(temporary.Paths);
    }

    public TestContext TestContext { get; set; } = null!;

    internal static BahrainLapAuditDocument Complete(
        BahrainLapAuditDocument template,
        int includedCompleteCount)
    {
        var included = 0;
        var entries = template.Entries.Select(entry =>
        {
            if (entry.Boundary.Completeness != LapBoundaryCompleteness.Complete)
            {
                return entry with
                {
                    Decision = LapAuditDecision.Excluded,
                    ExclusionReason = LapExclusionReason.IncompleteLap,
                };
            }

            if (included++ < includedCompleteCount)
            {
                return entry with { Decision = LapAuditDecision.Included };
            }

            return entry with
            {
                Decision = LapAuditDecision.Excluded,
                ExclusionReason = LapExclusionReason.OtherFactual,
                FactualNote = "Visually observed steering interruption.",
            };
        });
        return new(
            template.SchemaVersion,
            template.LapAuditId,
            template.CanonicalIdentitySha256,
            template.CanonicalSha256,
            template.ReferenceContext,
            new BahrainLapAuditManualInputs(
                "F1 25 current PC build",
                "Ferrari",
                "Wheel and pedals",
                "Unchanged baseline setup",
                "Soft",
                true,
                true,
                true,
                true),
            entries);
    }

    internal static async Task CreateEvidenceAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            captureId,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        var receivedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var packets = new List<byte[]> { Session() };
        packets.AddRange(Enumerable.Range(1, 7).Select(lapNumber =>
            Lap((byte)lapNumber, lapNumber == 1 ? 0U : (uint)(89_000 + lapNumber))));
        for (var index = 0; index < packets.Count; index++)
        {
            var sequence = index + 1L;
            await writer.WriteAsync(DatagramEnvelope.CopyFrom(
                sequence,
                1_000 + sequence,
                receivedAt.AddMilliseconds(sequence),
                new DatagramSender(IPAddress.Loopback, 20_777),
                packets[index]));
        }

        await writer.FinalizeAsync();
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
        packet[44] = 0;
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

    private static byte[] Lap(byte lapNumber, uint lastLapMilliseconds)
    {
        var packet = Packet(2, 1_285);
        var offset = 29 + (3 * 57);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(offset),
            lastLapMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 4), 12_000);
        packet[offset + 33] = lapNumber;
        packet[offset + 36] = 2;
        packet[offset + 37] = 0;
        return packet;
    }

    private static byte[] Packet(byte packetId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, F125Protocol.PacketFormat);
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

    private static void CopyEvidence(
        ApplicationPaths source,
        ApplicationPaths destination,
        RawEvidenceCaptureId captureId)
    {
        Directory.CreateDirectory(destination.RawCapturesDirectory);
        var prefix = captureId.Value;
        foreach (var suffix in new[] { ".apxraw", ".apxraw.json" })
        {
            File.Copy(
                Path.Combine(source.RawCapturesDirectory, prefix + suffix),
                Path.Combine(destination.RawCapturesDirectory, prefix + suffix));
        }
    }

    internal static async Task ReplaceAuditAsync(
        ApplicationPaths paths,
        BahrainLapAuditDocument document) =>
        await File.WriteAllBytesAsync(
            AuditPath(paths),
            BahrainLapAuditJson.Serialize(document));

    private static byte[] AuditBytes(ApplicationPaths paths) =>
        File.ReadAllBytes(AuditPath(paths));

    private static string AuditPath(ApplicationPaths paths) =>
        Directory.GetFiles(paths.LapAuditsDirectory).Single();

    internal static void DeleteAllEvidence(ApplicationPaths paths)
    {
        foreach (var directory in new[]
                 {
                     paths.RawCapturesDirectory,
                     paths.DerivedCacheDirectory,
                     paths.LapAuditsDirectory,
                 })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create(string purpose) =>
            new(Path.Combine(Path.GetTempPath(), $"apexlab-{purpose}-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
