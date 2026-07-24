using System.Net;
using System.Runtime.Versioning;
using System.Text;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RawEvidenceReaderTests
{
    private static readonly RawEvidenceCaptureId CaptureId =
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");
    private static readonly RawEvidenceProtocolId ProtocolId =
        RawEvidenceProtocolId.Parse("ea-f1-25-v3");
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task VerifiesBeforePublishingExactRecordedEnvelopes()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);

        await using var capture = await RawEvidenceReader.OpenAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            TestContext.CancellationToken);
        var envelopes = new List<DatagramEnvelope>();
        await foreach (var envelope in capture.ReadAllAsync(
                           TestContext.CancellationToken))
        {
            envelopes.Add(envelope);
        }

        Assert.AreEqual(2L, capture.Completion.RecordCount);
        Assert.AreEqual(1_000L, capture.StopwatchFrequency);
        Assert.HasCount(2, envelopes);
        Assert.AreEqual(2L, envelopes[0].Sequence);
        Assert.AreEqual(5L, envelopes[1].Sequence);
        Assert.AreEqual(100L, envelopes[0].MonotonicTimestamp);
        Assert.AreEqual(100L, envelopes[1].MonotonicTimestamp);
        CollectionAssert.AreEqual(
            new byte[] { 0x10, 0x20 },
            envelopes[0].Payload.ToArray());
        Assert.AreEqual(
            IPAddress.Parse("2001:db8::25"),
            envelopes[1].Sender.Address);
    }

    [TestMethod]
    public async Task RejectsDataCorruptionBeforeReturningACapture()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);
        var dataPath = temporary.DataPath();
        await using (var stream = new FileStream(
                         dataPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            stream.Position = 132;
            var original = stream.ReadByte();
            stream.Position = 132;
            stream.WriteByte((byte)(original ^ 0xff));
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RejectsManifestDigestCorruptionAndWrongProtocol()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);
        var manifestPath = temporary.ManifestPath();
        var bytes = await File.ReadAllBytesAsync(
            manifestPath,
            TestContext.CancellationToken);
        var prefix = "\"sha256\":\""u8;
        var digestOffset = bytes.AsSpan().IndexOf(prefix) + prefix.Length;
        bytes[digestOffset] = bytes[digestOffset] == (byte)'0'
            ? (byte)'1'
            : (byte)'0';
        await File.WriteAllBytesAsync(
            manifestPath,
            bytes,
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));

        await CreateEvidenceAsync(
            temporary,
            RawEvidenceCaptureId.Parse(
                "112233445566478899aabbccddeeff00"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                RawEvidenceCaptureId.Parse(
                    "112233445566478899aabbccddeeff00"),
                RawEvidenceProtocolId.Parse("ea-f1-26-v1"),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RejectsCaptureWhenAnyStagingMarkerExists()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);
        await File.WriteAllTextAsync(
            Path.Combine(
                temporary.Paths.RawCapturesDirectory,
                $"{CaptureId.Value}.apxraw.partial"),
            "incomplete",
            Encoding.UTF8,
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task VerifiedCaptureCanBeEnumeratedOnlyOnce()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);
        await using var capture = await RawEvidenceReader.OpenAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            TestContext.CancellationToken);

        await foreach (var _ in capture.ReadAllAsync(
                           TestContext.CancellationToken))
        {
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (var _ in capture.ReadAllAsync(
                                   TestContext.CancellationToken))
                {
                }
            });
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task CreateEvidenceAsync(
        TemporaryEvidenceRoot temporary,
        RawEvidenceCaptureId? captureId = null)
    {
        var resolvedId = captureId ?? CaptureId;
        await using var writer =
            await RawEvidenceWriter.CreateForTestingAsync(
                temporary.Paths,
                resolvedId,
                ProtocolId,
                new RawEvidenceLimits(minimumFreeSpaceBytes: 0),
                stopwatchFrequency: 1_000,
                new FixedTimeProvider(ReceivedAt),
                () => long.MaxValue,
                CancellationToken.None);
        await writer.WriteAsync(DatagramEnvelope.CopyFrom(
            sequence: 2,
            monotonicTimestamp: 100,
            receivedAtUtc: ReceivedAt,
            sender: new DatagramSender(
                IPAddress.Parse("192.0.2.25"),
                20_777),
            payload: [0x10, 0x20]));
        await writer.WriteAsync(DatagramEnvelope.CopyFrom(
            sequence: 5,
            monotonicTimestamp: 100,
            receivedAtUtc: ReceivedAt.AddMilliseconds(1),
            sender: new DatagramSender(
                IPAddress.Parse("2001:db8::25"),
                20_778),
            payload: [0x30]));
        await writer.FinalizeAsync();
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryEvidenceRoot : IDisposable
    {
        private TemporaryEvidenceRoot(string root)
        {
            Paths = ApplicationPaths.FromRoot(root);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryEvidenceRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-raw-reader-{Guid.NewGuid():N}"));

        public string DataPath() => Path.Combine(
            Paths.RawCapturesDirectory,
            $"{CaptureId.Value}.apxraw");

        public string ManifestPath() => Path.Combine(
            Paths.RawCapturesDirectory,
            $"{CaptureId.Value}.apxraw.json");

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
