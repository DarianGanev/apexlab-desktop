using System.Buffers.Binary;
using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.BahrainValidation;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Replay;

[TestClass]
public sealed class BahrainDecoderReplayExecutionTests
{
    [TestMethod]
    public async Task FindsAllFiveFamiliesWithoutReturningDecodedValues()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            [
                Packet(7, 1_239),
                Motion(),
                Session(),
                Lap(),
                Event("FTLP"),
                Event("SSTA"),
                Telemetry(),
            ]);

        var result = await BahrainDecoderReplayExecution.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            TestContext.CancellationToken);

        Assert.AreEqual(BahrainDecoderReplayResult.Complete, result);
    }

    [TestMethod]
    public async Task ReportsMissingFamilyWithoutInventingPresence()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            [Motion(), Session(), Lap(), Event("SSTA")]);

        var result = await BahrainDecoderReplayExecution.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            TestContext.CancellationToken);

        Assert.IsTrue(result.MotionDecoded);
        Assert.IsTrue(result.SessionDecoded);
        Assert.IsTrue(result.LapDecoded);
        Assert.IsTrue(result.EventDecoded);
        Assert.IsFalse(result.CarTelemetryDecoded);
        Assert.IsFalse(result.SelectedPacketsRejected);
    }

    [TestMethod]
    public async Task ReportsSelectedRejectionWithoutMarkingItDecoded()
    {
        using var temporary = TemporaryRoot.Create();
        var packets = new[] { Motion(), Session(), Lap(), Event("SSTA"), Telemetry() };
        packets[4][44] = 0x7F;
        var completion = await CreateEvidenceAsync(temporary.Paths, packets);

        var result = await BahrainDecoderReplayExecution.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            TestContext.CancellationToken);

        Assert.IsTrue(result.MotionDecoded);
        Assert.IsTrue(result.SessionDecoded);
        Assert.IsTrue(result.LapDecoded);
        Assert.IsTrue(result.EventDecoded);
        Assert.IsFalse(result.CarTelemetryDecoded);
        Assert.IsTrue(result.SelectedPacketsRejected);
    }

    [TestMethod]
    public async Task CorruptEvidenceFailsBeforeAnyDecodeConclusion()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths, [Motion()]);
        var dataPath = Path.Combine(
            temporary.Paths.RawCapturesDirectory,
            completion.CaptureId.Value + ".apxraw");
        await using (var stream = new FileStream(
                         dataPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var original = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(original ^ 0xFF));
        }

        var exception = await Assert.ThrowsAsync<RawEvidenceReadException>(() =>
            BahrainDecoderReplayExecution.ExecuteAsync(
                temporary.Paths,
                completion.CaptureId,
                TestContext.CancellationToken));

        Assert.AreEqual(RawEvidenceReadFailureKind.MalformedStructure, exception.Kind);
    }

    [TestMethod]
    public async Task RejectsUnexpectedSenderAndWrongProtocol()
    {
        using var unexpectedSenderRoot = TemporaryRoot.Create();
        var unexpectedSender = await CreateEvidenceAsync(
            unexpectedSenderRoot.Paths,
            [Motion()],
            IPAddress.Parse("192.0.2.10"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BahrainDecoderReplayExecution.ExecuteAsync(
                unexpectedSenderRoot.Paths,
                unexpectedSender.CaptureId,
                TestContext.CancellationToken));

        using var wrongProtocolRoot = TemporaryRoot.Create();
        var wrongProtocol = await CreateEvidenceAsync(
            wrongProtocolRoot.Paths,
            [],
            IPAddress.Loopback,
            "synthetic-v1");
        var exception = await Assert.ThrowsAsync<RawEvidenceReadException>(() =>
            BahrainDecoderReplayExecution.ExecuteAsync(
                wrongProtocolRoot.Paths,
                wrongProtocol.CaptureId,
                TestContext.CancellationToken));
        Assert.AreEqual(RawEvidenceReadFailureKind.UnsupportedProtocol, exception.Kind);
    }

    [TestMethod]
    public async Task CancellationReleasesEvidenceHandles()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths, [Motion()]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BahrainDecoderReplayExecution.ExecuteAsync(
                temporary.Paths,
                completion.CaptureId,
                cancellation.Token));

        var prefix = Path.Combine(temporary.Paths.RawCapturesDirectory, completion.CaptureId.Value);
        File.Delete(prefix + ".apxraw");
        File.Delete(prefix + ".apxraw.json");
        Assert.IsFalse(File.Exists(prefix + ".apxraw"));
        Assert.IsFalse(File.Exists(prefix + ".apxraw.json"));
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths,
        IReadOnlyList<byte[]> packets,
        IPAddress? sender = null,
        string protocolId = F125Protocol.Id)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(protocolId),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        for (var index = 0; index < packets.Count; index++)
        {
            await writer.WriteAsync(DatagramEnvelope.CopyFrom(
                index + 1,
                index + 100,
                new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(index),
                new DatagramSender(sender ?? IPAddress.Loopback, 20_777),
                packets[index]));
        }

        return await writer.FinalizeAsync();
    }

    private static byte[] Motion() => Packet(0, 1_349);

    private static byte[] Session()
    {
        var packet = Packet(1, 753);
        packet[687] = 1;
        return packet;
    }

    private static byte[] Lap() => Packet(2, 1_285);

    private static byte[] Event(string code)
    {
        var packet = Packet(3, 45);
        for (var index = 0; index < 4; index++)
        {
            packet[29 + index] = (byte)code[index];
        }

        return packet;
    }

    private static byte[] Telemetry() => Packet(6, 1_352);

    private static byte[] Packet(byte packetId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = packetId;
        packet[27] = 0;
        packet[28] = byte.MaxValue;
        return packet;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(Path.GetTempPath(), $"apexlab-bahrain-validator-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
