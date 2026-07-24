using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RawEvidenceWriterTests
{
    private static readonly RawEvidenceCaptureId CaptureId =
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");
    private static readonly RawEvidenceProtocolId ProtocolId =
        RawEvidenceProtocolId.Parse("ea-f1-25-v3");
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinalizedAt =
        CreatedAt.AddMinutes(1);

    [TestMethod]
    public async Task EmptyCaptureFinalizesCanonicalIntegrityBoundFiles()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var time = new QueueTimeProvider(CreatedAt, FinalizedAt);
        await using var writer = await RawEvidenceWriter.CreateForTestingAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0),
            stopwatchFrequency: 10_000_000,
            time,
            () => long.MaxValue,
            TestContext.CancellationToken);

        var completion = await writer.FinalizeAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureId, completion.CaptureId);
        Assert.AreEqual(ProtocolId, completion.ProtocolId);
        Assert.AreEqual(0L, completion.RecordCount);
        Assert.AreEqual(136L, completion.DataLengthBytes);
        Assert.AreEqual(FinalizedAt, completion.FinalizedAtUtc);

        var data = await File.ReadAllBytesAsync(
            temporary.DataPath(CaptureId),
            TestContext.CancellationToken);
        var manifest = await File.ReadAllBytesAsync(
            temporary.ManifestPath(CaptureId),
            TestContext.CancellationToken);
        Assert.HasCount(136, data);
        CollectionAssert.AreEqual("APXRAW1\0"u8.ToArray(), data[..8]);
        CollectionAssert.AreEqual("APXFTR1\0"u8.ToArray(), data[72..80]);
        Assert.AreEqual(
            ulong.MaxValue,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(96, 8)));
        Assert.IsTrue(VerifyDigest(data, manifest, completion.Sha256));
        AssertNoStagingFiles(temporary.Paths);
    }

    [TestMethod]
    public async Task PopulatedCapturePreservesExactEnvelopeAndFooter()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await using var writer = await CreateWriterAsync(temporary);
        var envelope = DatagramEnvelope.CopyFrom(
            sequence: 7,
            monotonicTimestamp: 1_000,
            receivedAtUtc: CreatedAt.AddSeconds(1),
            sender: new DatagramSender(
                IPAddress.Parse("192.0.2.25"),
                20_777),
            payload: [0x10, 0x20, 0x30]);

        await writer.WriteAsync(
            envelope,
            TestContext.CancellationToken);
        var completion = await writer.FinalizeAsync(
            TestContext.CancellationToken);

        var data = await File.ReadAllBytesAsync(
            temporary.DataPath(CaptureId),
            TestContext.CancellationToken);
        Assert.HasCount(199, data);
        CollectionAssert.AreEqual("REC1"u8.ToArray(), data[72..76]);
        Assert.AreEqual(
            7L,
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(80, 8)));
        Assert.AreEqual(
            1_000L,
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(88, 8)));
        CollectionAssert.AreEqual(
            new byte[] { 0x10, 0x20, 0x30 },
            data[132..135]);
        Assert.AreEqual(
            1UL,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(151, 8)));
        Assert.AreEqual(
            63UL,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(175, 8)));
        Assert.AreEqual(1L, completion.RecordCount);
    }

    [TestMethod]
    public async Task RejectsInvalidPayloadOrderingDurationAndFileGrowth()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var limits = new RawEvidenceLimits(
            maximumDuration: TimeSpan.FromMilliseconds(100),
            maximumFileBytes: 210,
            minimumFreeSpaceBytes: 0,
            maximumPayloadBytes: 10);
        await using var writer = await CreateWriterAsync(
            temporary,
            limits);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => writer.WriteAsync(
                Envelope(1, 100, []),
                TestContext.CancellationToken).AsTask());
        await writer.WriteAsync(
            Envelope(2, 1_000, [1]),
            TestContext.CancellationToken);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                Envelope(2, 1_001, [2]),
                TestContext.CancellationToken).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                Envelope(3, 999, [2]),
                TestContext.CancellationToken).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                Envelope(3, 1_101, [2]),
                TestContext.CancellationToken).AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => writer.WriteAsync(
                Envelope(3, 1_050, new byte[11]),
                TestContext.CancellationToken).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                Envelope(3, 1_050, new byte[10]),
                TestContext.CancellationToken).AsTask());

        var completion = await writer.FinalizeAsync(
            TestContext.CancellationToken);
        Assert.AreEqual(1L, completion.RecordCount);
    }

    [TestMethod]
    public async Task ConcurrentFinalizationSharesOneTaskAndStopsWrites()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await using var writer = await CreateWriterAsync(temporary);

        var first = writer.FinalizeAsync(TestContext.CancellationToken);
        var second = writer.FinalizeAsync(TestContext.CancellationToken);

        Assert.AreSame(first, second);
        var completions = await Task.WhenAll(first, second);
        Assert.AreSame(completions[0], completions[1]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                Envelope(1, 1, [1]),
                TestContext.CancellationToken).AsTask());
    }

    [TestMethod]
    public async Task FreeSpaceFloorIsEnforcedBeforeCreatingOrGrowingFiles()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await Assert.ThrowsExactlyAsync<IOException>(
            () => RawEvidenceWriter.CreateForTestingAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                new RawEvidenceLimits(minimumFreeSpaceBytes: 100),
                stopwatchFrequency: 1_000,
                new QueueTimeProvider(CreatedAt),
                () => 171,
                TestContext.CancellationToken));
        AssertNoStagingFiles(temporary.Paths);

        long available = long.MaxValue;
        await using var writer = await RawEvidenceWriter.CreateForTestingAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            new RawEvidenceLimits(minimumFreeSpaceBytes: 100),
            stopwatchFrequency: 1_000,
            new QueueTimeProvider(CreatedAt, FinalizedAt),
            () => available,
            TestContext.CancellationToken);
        available = 160;

        await Assert.ThrowsExactlyAsync<IOException>(
            () => writer.WriteAsync(
                Envelope(1, 1, [1]),
                TestContext.CancellationToken).AsTask());
    }

    [TestMethod]
    public async Task DisposalRemovesIncompleteStagingEvidence()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var writer = await CreateWriterAsync(temporary);
        await writer.WriteAsync(
            Envelope(1, 1, [1]),
            TestContext.CancellationToken);

        await writer.DisposeAsync();

        AssertNoEvidenceFiles(temporary.Paths);
    }

    public TestContext TestContext { get; set; } = null!;

    private static Task<RawEvidenceWriter> CreateWriterAsync(
        TemporaryEvidenceRoot temporary,
        RawEvidenceLimits? limits = null)
    {
        return RawEvidenceWriter.CreateForTestingAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            limits ?? new RawEvidenceLimits(minimumFreeSpaceBytes: 0),
            stopwatchFrequency: 1_000,
            new QueueTimeProvider(CreatedAt, FinalizedAt),
            () => long.MaxValue,
            CancellationToken.None);
    }

    private static DatagramEnvelope Envelope(
        long sequence,
        long timestamp,
        byte[] payload)
    {
        return DatagramEnvelope.CopyFrom(
            sequence,
            timestamp,
            CreatedAt,
            new DatagramSender(IPAddress.Loopback, 20_777),
            payload);
    }

    private static bool VerifyDigest(
        byte[] data,
        byte[] manifest,
        string expectedDigest)
    {
        using var document = JsonDocument.Parse(manifest);
        Assert.AreEqual(
            expectedDigest,
            document.RootElement.GetProperty("sha256").GetString());
        var preimage = manifest.ToArray();
        var digestBytes = System.Text.Encoding.ASCII.GetBytes(expectedDigest);
        var offset = preimage.AsSpan().IndexOf(digestBytes);
        Assert.IsGreaterThanOrEqualTo(0, offset);
        preimage.AsSpan(offset, 64).Fill((byte)'0');
        var combined = new byte[data.Length + preimage.Length];
        data.CopyTo(combined, 0);
        preimage.CopyTo(combined, data.Length);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(combined),
            Convert.FromHexString(expectedDigest));
    }

    private static void AssertNoStagingFiles(ApplicationPaths paths)
    {
        Assert.IsFalse(Directory.EnumerateFiles(
            paths.RawCapturesDirectory,
            "*.partial").Any());
    }

    private static void AssertNoEvidenceFiles(ApplicationPaths paths)
    {
        Assert.IsFalse(Directory.EnumerateFiles(
            paths.RawCapturesDirectory).Any());
    }

    private sealed class QueueTimeProvider(
        params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> _values = new(values);
        private DateTimeOffset _last =
            values.FirstOrDefault(DateTimeOffset.UnixEpoch);

        public override DateTimeOffset GetUtcNow()
        {
            if (_values.TryDequeue(out var value))
            {
                _last = value;
            }

            return _last;
        }
    }

    private sealed class TemporaryEvidenceRoot : IDisposable
    {
        private TemporaryEvidenceRoot(string path)
        {
            Paths = ApplicationPaths.FromRoot(path);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryEvidenceRoot Create()
        {
            return new TemporaryEvidenceRoot(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-raw-writer-{Guid.NewGuid():N}"));
        }

        public string DataPath(RawEvidenceCaptureId captureId) =>
            Path.Combine(
                Paths.RawCapturesDirectory,
                $"{captureId.Value}.apxraw");

        public string ManifestPath(RawEvidenceCaptureId captureId) =>
            Path.Combine(
                Paths.RawCapturesDirectory,
                $"{captureId.Value}.apxraw.json");

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
