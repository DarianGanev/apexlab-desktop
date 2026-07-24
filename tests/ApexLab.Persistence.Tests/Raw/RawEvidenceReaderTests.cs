using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    public async Task ReadsAnIndependentlyAuthoredCompleteEvidencePackage()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var expectedDigest =
            await CreateIndependentEvidenceAsync(temporary);

        await using var capture = await RawEvidenceReader.OpenAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId,
            TestContext.CancellationToken);

        Assert.AreEqual(0L, capture.Completion.RecordCount);
        Assert.AreEqual(136L, capture.Completion.DataLengthBytes);
        Assert.AreEqual(expectedDigest, capture.Completion.Sha256);
        Assert.AreEqual(1_000L, capture.StopwatchFrequency);
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

        var exception =
            await Assert.ThrowsExactlyAsync<RawEvidenceReadException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));
        Assert.AreEqual(
            RawEvidenceReadFailureKind.HashMismatch,
            exception.Kind);
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

        var digestFailure =
            await Assert.ThrowsExactlyAsync<RawEvidenceReadException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));
        Assert.AreEqual(
            RawEvidenceReadFailureKind.HashMismatch,
            digestFailure.Kind);

        await CreateEvidenceAsync(
            temporary,
            RawEvidenceCaptureId.Parse(
                "112233445566478899aabbccddeeff00"));
        var protocolFailure =
            await Assert.ThrowsExactlyAsync<RawEvidenceReadException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                RawEvidenceCaptureId.Parse(
                    "112233445566478899aabbccddeeff00"),
                RawEvidenceProtocolId.Parse("ea-f1-26-v1"),
                TestContext.CancellationToken));
        Assert.AreEqual(
            RawEvidenceReadFailureKind.UnsupportedProtocol,
            protocolFailure.Kind);
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

        var exception =
            await Assert.ThrowsExactlyAsync<RawEvidenceReadException>(
            () => RawEvidenceReader.OpenAsync(
                temporary.Paths,
                CaptureId,
                ProtocolId,
                TestContext.CancellationToken));
        Assert.AreEqual(
            RawEvidenceReadFailureKind.MissingOrIncomplete,
            exception.Kind);
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

    [TestMethod]
    [DataRow(
        "missing",
        RawEvidenceReadFailureKind.MissingOrIncomplete)]
    [DataRow(
        "unsupported-version",
        RawEvidenceReadFailureKind.UnsupportedVersion)]
    [DataRow(
        "malformed",
        RawEvidenceReadFailureKind.MalformedStructure)]
    [DataRow(
        "limit",
        RawEvidenceReadFailureKind.DeclaredLimitViolation)]
    [DataRow(
        "truncated",
        RawEvidenceReadFailureKind.TruncatedData)]
    [DataRow(
        "trailing",
        RawEvidenceReadFailureKind.TrailingData)]
    public async Task ReaderExposesPrivacySafeTypedFailures(
        string mutation,
        RawEvidenceReadFailureKind expectedKind)
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        await CreateEvidenceAsync(temporary);
        await MutateEvidenceAsync(temporary, mutation);

        var exception =
            await Assert.ThrowsExactlyAsync<RawEvidenceReadException>(
                () => RawEvidenceReader.OpenAsync(
                    temporary.Paths,
                    CaptureId,
                    ProtocolId,
                    TestContext.CancellationToken));

        Assert.AreEqual(expectedKind, exception.Kind);
        Assert.DoesNotContain(
            temporary.Paths.RootDirectory,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            CaptureId.Value,
            exception.ToString(),
            StringComparison.Ordinal);
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

    private static async Task MutateEvidenceAsync(
        TemporaryEvidenceRoot temporary,
        string mutation)
    {
        var dataPath = temporary.DataPath();
        var manifestPath = temporary.ManifestPath();
        switch (mutation)
        {
            case "missing":
                File.Delete(manifestPath);
                break;
            case "unsupported-version":
            case "malformed":
                {
                    var data = await File.ReadAllBytesAsync(dataPath);
                    data[mutation == "unsupported-version" ? 8 : 0] =
                        mutation == "unsupported-version"
                            ? (byte)2
                            : (byte)'X';
                    await File.WriteAllBytesAsync(dataPath, data);
                    break;
                }
            case "limit":
                {
                    var text = await File.ReadAllTextAsync(manifestPath);
                    text = text.Replace(
                        "\"maximumFileBytes\":536870912",
                        "\"maximumFileBytes\":136",
                        StringComparison.Ordinal);
                    await File.WriteAllTextAsync(
                        manifestPath,
                        text,
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false));
                    break;
                }
            case "truncated":
                {
                    await using var stream = new FileStream(
                        dataPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);
                    stream.SetLength(stream.Length - 1);
                    break;
                }
            case "trailing":
                {
                    await using var stream = new FileStream(
                        dataPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.None);
                    stream.WriteByte(0);
                    break;
                }
            default:
                throw new AssertFailedException("Unknown mutation.");
        }
    }

    private static async Task<string> CreateIndependentEvidenceAsync(
        TemporaryEvidenceRoot temporary)
    {
        Directory.CreateDirectory(
            temporary.Paths.RawCapturesDirectory);
        var data = new byte[136];
        "APXRAW1\0"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(8, 2),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(10, 2),
            72);
        Encoding.ASCII.GetBytes(
            CaptureId.Value,
            data.AsSpan(16, 32));
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(48, 8),
            1_000);
        const long createdTicks = 639_205_200_000_000_000;
        const long finalizedTicks = 639_205_200_010_000_000;
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(56, 8),
            createdTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(64, 4),
            65_507);
        "APXFTR1\0"u8.CopyTo(data.AsSpan(72));
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(80, 2),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(82, 2),
            64);
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(96, 8),
            -1);
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(104, 8),
            -1);
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(120, 8),
            -1);
        BinaryPrimitives.WriteInt64LittleEndian(
            data.AsSpan(128, 8),
            -1);

        var preimageText =
            $"{{\"schemaVersion\":1,\"dataFormatVersion\":1,\"captureId\":\"{CaptureId.Value}\",\"protocolId\":\"{ProtocolId.Value}\",\"dataFileName\":\"{CaptureId.Value}.apxraw\",\"dataLengthBytes\":136,\"sha256\":\"{new string('0', 64)}\",\"recordCount\":0,\"firstSequence\":null,\"lastSequence\":null,\"firstArrivalTimestamp\":null,\"lastArrivalTimestamp\":null,\"stopwatchFrequency\":1000,\"createdUtcTicks\":{createdTicks},\"finalizedUtcTicks\":{finalizedTicks},\"limits\":{{\"maximumDurationMilliseconds\":900000,\"maximumFileBytes\":536870912,\"minimumFreeSpaceBytes\":0,\"maximumPayloadBytes\":65507}}}}";
        var preimage = Encoding.UTF8.GetBytes(preimageText);
        var digestInput = new byte[data.Length + preimage.Length];
        data.CopyTo(digestInput, 0);
        preimage.CopyTo(digestInput, data.Length);
        var digest = SHA256.HashData(digestInput);
        var digestText = Convert.ToHexStringLower(digest);
        var manifest = preimageText.Replace(
            new string('0', 64),
            digestText,
            StringComparison.Ordinal);

        await File.WriteAllBytesAsync(
            temporary.DataPath(),
            data);
        await File.WriteAllTextAsync(
            temporary.ManifestPath(),
            manifest,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
        return digestText;
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
