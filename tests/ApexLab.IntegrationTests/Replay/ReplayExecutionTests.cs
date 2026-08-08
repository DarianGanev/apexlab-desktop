using System.Buffers.Binary;
using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Replay;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Replay;

[TestClass]
public sealed class ReplayExecutionTests
{
    [TestMethod]
    public async Task ReplaysEvidenceIntoStableTypedAccounting()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(
            temporary.Paths,
            [1, 2, 5]);

        var result = await ReplayExecution.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            new RawReplayOptions(RawReplayTimingMode.Immediate),
            TestContext.CancellationToken);

        Assert.AreEqual(F125Protocol.Id, result.ProtocolId);
        Assert.AreEqual(RawReplayTimingMode.Immediate, result.TimingMode);
        Assert.AreEqual(1_000, result.SpeedPermille);
        Assert.AreEqual(3L, result.RecordCount);
        Assert.AreEqual(2L, result.SequenceGapCount);
        Assert.IsTrue(result.Counters.HasCompleteSourceAccounting);
        Assert.AreEqual(
            result.RecordCount,
            result.Counters.Source.DatagramsObserved);
        Assert.AreEqual(
            result.RecordCount,
            result.Counters.Classifier.SourceDequeued);
        Assert.AreEqual(
            result.RecordCount,
            result.Counters.Classifier.Compatible);
        Assert.HasCount(1, result.Descriptors);
        Assert.AreEqual((byte)0, result.Descriptors[0].PacketId);
        Assert.AreEqual((byte)1, result.Descriptors[0].PacketVersion);
        Assert.AreEqual(1_349, result.Descriptors[0].DatagramLength);
        Assert.AreEqual(3L, result.Descriptors[0].Count);
    }

    [TestMethod]
    public async Task ReplaysEmptyEvidenceWithoutInventingObservations()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths, []);

        var result = await ReplayExecution.ExecuteAsync(
            temporary.Paths,
            completion.CaptureId,
            new RawReplayOptions(RawReplayTimingMode.Immediate),
            TestContext.CancellationToken);

        Assert.AreEqual(0L, result.RecordCount);
        Assert.AreEqual(0L, result.SequenceGapCount);
        Assert.AreEqual(0L, result.Counters.Source.DatagramsObserved);
        Assert.AreEqual(0L, result.Counters.Classifier.SourceDequeued);
        Assert.IsEmpty(result.Descriptors);
    }

    [TestMethod]
    public async Task CancellationReleasesEvidenceHandlesForImmediateDeletion()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths, [1, 2, 5]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ReplayExecution.ExecuteAsync(
                temporary.Paths,
                completion.CaptureId,
                new RawReplayOptions(RawReplayTimingMode.Immediate),
                cancellation.Token));

        var prefix = Path.Combine(
            temporary.Paths.RawCapturesDirectory,
            completion.CaptureId.Value);
        File.Delete(prefix + ".apxraw");
        File.Delete(prefix + ".apxraw.json");
        Assert.IsFalse(File.Exists(prefix + ".apxraw"));
        Assert.IsFalse(File.Exists(prefix + ".apxraw.json"));
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths,
        IReadOnlyList<long> sequences)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        var receivedAt = new DateTimeOffset(
            2026,
            8,
            8,
            12,
            0,
            0,
            TimeSpan.Zero);
        for (var index = 0; index < sequences.Count; index++)
        {
            await writer.WriteAsync(DatagramEnvelope.CopyFrom(
                sequences[index],
                monotonicTimestamp: 100 + index,
                receivedAt.AddMilliseconds(index),
                new DatagramSender(IPAddress.Loopback, 20_777),
                CreateMotionPacket()));
        }

        return await writer.FinalizeAsync();
    }

    private static byte[] CreateMotionPacket()
    {
        var packet = new byte[1_349];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(7),
            0x0123456789ABCDEFUL);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(15),
            unchecked((uint)BitConverter.SingleToInt32Bits(12.5F)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(19),
            0x10203040U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(23),
            0x50607080U);
        packet[27] = 3;
        packet[28] = byte.MaxValue;
        return packet;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path)
        {
            Paths = ApplicationPaths.FromRoot(path);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-replay-execution-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
