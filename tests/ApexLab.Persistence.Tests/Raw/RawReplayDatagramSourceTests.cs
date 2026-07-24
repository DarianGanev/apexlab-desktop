using System.Net;
using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RawReplayDatagramSourceTests
{
    private static readonly RawEvidenceCaptureId CaptureId =
        RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");
    private static readonly RawEvidenceProtocolId ProtocolId =
        RawEvidenceProtocolId.Parse("ea-f1-25-v3");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 13, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ImmediateReplayUsesAwaitedAdmissionAndPreservesGaps()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var capture = await CreateCaptureAsync(temporary);
        await using var source = new RawReplayDatagramSource(
            capture,
            new RawReplayOptions(channelCapacity: 1));

        await source.StartAsync(TestContext.CancellationToken);
        var received = new List<DatagramEnvelope>();
        await foreach (var envelope in source.Output.ReadAllAsync(
                           TestContext.CancellationToken))
        {
            received.Add(envelope);
        }
        await source.StopAsync(TestContext.CancellationToken);

        Assert.HasCount(2, received);
        Assert.AreEqual(10L, received[0].Sequence);
        Assert.AreEqual(14L, received[1].Sequence);
        Assert.AreEqual(
            new DatagramSourceCounters(2, 2, 0, 0, 0),
            source.Counters);
    }

    [TestMethod]
    public async Task RecordedReplayUsesIntegerHalfUpTargetTiming()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var capture = await CreateCaptureAsync(temporary);
        var delays = new List<TimeSpan>();
        await using var source = new RawReplayDatagramSource(
            capture,
            new RawReplayOptions(
                RawReplayTimingMode.Recorded,
                speedPermille: 2_000),
            new FixedTimeProvider(),
            (delay, _, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await source.StartAsync(TestContext.CancellationToken);
        await foreach (var _ in source.Output.ReadAllAsync(
                           TestContext.CancellationToken))
        {
        }

        Assert.HasCount(1, delays);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), delays[0]);
        Assert.AreEqual(
            1L,
            RawReplayDatagramSource.CalculateTargetElapsedTicks(
                arrivalDelta: 1,
                recordedStopwatchFrequency: 20_000_000,
                speedPermille: 1_000));
    }

    [TestMethod]
    public async Task StopCancelsAnOutstandingRecordedDelay()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        var capture = await CreateCaptureAsync(temporary);
        var delayStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new RawReplayDatagramSource(
            capture,
            new RawReplayOptions(RawReplayTimingMode.Recorded),
            new FixedTimeProvider(),
            async (_, _, token) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    token);
            });

        await source.StartAsync(TestContext.CancellationToken);
        _ = await source.Output.ReadAsync(
            TestContext.CancellationToken);
        await delayStarted.Task.WaitAsync(TestContext.CancellationToken);

        await source.StopAsync(TestContext.CancellationToken);

        Assert.IsTrue(source.Output.Completion.IsCompleted);
        Assert.AreEqual(1L, source.Counters.SourceEnqueued);
    }

    [TestMethod]
    public void ReplayOptionsEnforceTheReviewedBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RawReplayOptions(speedPermille: 99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RawReplayOptions(speedPermille: 100_001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RawReplayOptions(channelCapacity: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RawReplayOptions(
                (RawReplayTimingMode)99));
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCapture> CreateCaptureAsync(
        TemporaryEvidenceRoot temporary)
    {
        await using (var writer =
                     await RawEvidenceWriter.CreateForTestingAsync(
                         temporary.Paths,
                         CaptureId,
                         ProtocolId,
                         new RawEvidenceLimits(
                             minimumFreeSpaceBytes: 0),
                         stopwatchFrequency: 1_000,
                         new FixedTimeProvider(),
                         () => long.MaxValue,
                         CancellationToken.None))
        {
            await writer.WriteAsync(Envelope(10, 100, [1]));
            await writer.WriteAsync(Envelope(14, 600, [2]));
            await writer.FinalizeAsync();
        }

        return await RawEvidenceReader.OpenAsync(
            temporary.Paths,
            CaptureId,
            ProtocolId);
    }

    private static DatagramEnvelope Envelope(
        long sequence,
        long timestamp,
        byte[] payload) =>
        DatagramEnvelope.CopyFrom(
            sequence,
            timestamp,
            Now,
            new DatagramSender(IPAddress.Loopback, 20_777),
            payload);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => 0;
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
                $"apexlab-raw-replay-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
