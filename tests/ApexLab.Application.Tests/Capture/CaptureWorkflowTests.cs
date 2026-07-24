using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class CaptureWorkflowTests
{
    [TestMethod]
    public async Task ConcurrentArmCallersShareOneBindingOperation()
    {
        var factory = new ControlledSessionFactory();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));

        var first = subject.ArmAsync(TestContext.CancellationToken);
        var second = subject.ArmAsync(TestContext.CancellationToken);

        await factory.CreationStarted.WaitAsync(
            TestContext.CancellationToken);
        Assert.AreSame(first, second);
        Assert.AreEqual(CaptureState.Binding, subject.Snapshot.State);
        Assert.IsNotNull(subject.Snapshot.CaptureId);
        Assert.AreEqual(1, factory.CreateCalls);

        factory.CompleteCreation();
        var armed = await first.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            CaptureState.WaitingForTraffic,
            armed.State);
        Assert.AreEqual(armed, subject.Snapshot);
    }

    [TestMethod]
    public async Task StopWhileBindingCancelsCreationAndSettlesStopped()
    {
        var factory = new ControlledSessionFactory();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var armTask = subject.ArmAsync(TestContext.CancellationToken);
        await factory.CreationStarted.WaitAsync(
            TestContext.CancellationToken);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            timeout.Token);

        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => armTask);
        Assert.AreEqual(0, factory.Source.DisposeCalls);
        Assert.AreEqual(0, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task CleanStopDrainsFinalizesAndDisposesTheSession()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        await subject.ArmAsync(TestContext.CancellationToken);

        var stopped = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, stopped.State);
        Assert.AreEqual(
            CaptureStopReason.User,
            stopped.StopReason);
        Assert.IsNotNull(stopped.Completion);
        Assert.AreEqual(1, factory.Source.StopCalls);
        Assert.AreEqual(1, factory.Evidence.FinalizeCalls);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
        Assert.IsTrue(subject.DeferredCleanupCompletion.IsCompletedSuccessfully);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class ControlledSessionFactory : ICaptureSessionFactory
    {
        private readonly TaskCompletionSource _creation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _creationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledDatagramSource Source { get; } = new();

        public RecordingEvidenceStore Evidence { get; } = new();

        public int CreateCalls { get; private set; }

        public Task CreationStarted => _creationStarted.Task;

        public async Task<CaptureSessionComponents> CreateAsync(
            RawEvidenceCaptureId captureId,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            _creationStarted.TrySetResult();
            await _creation.Task.WaitAsync(cancellationToken);
            Evidence.SetCaptureId(captureId);
            return new CaptureSessionComponents(
                Source,
                new CompatibleProtocolAdapter(),
                SenderPolicy.LoopbackOnly,
                Evidence);
        }

        public void CompleteCreation() => _creation.TrySetResult();
    }

    private sealed class ControlledDatagramSource : IDatagramSource
    {
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters => default;

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingEvidenceStore : IRawEvidenceStore
    {
        public RawEvidenceCaptureId CaptureId { get; private set; } =
            RawEvidenceCaptureId.Parse(
                "00112233445546778899aabbccddeeff");

        public RawEvidenceProtocolId ProtocolId { get; } =
            RawEvidenceProtocolId.Parse("synthetic-v1");

        public RawEvidenceLimits Limits { get; } =
            new(minimumFreeSpaceBytes: 0);

        public int FinalizeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public void SetCaptureId(RawEvidenceCaptureId captureId) =>
            CaptureId = captureId;

        public ValueTask WriteAsync(
            DatagramEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public Task<RawEvidenceCompletion> FinalizeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCalls++;
            return Task.FromResult(
                new RawEvidenceCompletion(
                    CaptureId,
                    ProtocolId,
                    recordCount: 0,
                    RawEvidenceLimits.MinimumFileBytes,
                    new string('0', 64),
                    DateTimeOffset.UnixEpoch));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibleProtocolAdapter : ITelemetryProtocolAdapter
    {
        public string ProtocolId => "synthetic-v1";

        public int HeaderLength => 1;

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram) =>
            TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.MalformedHeader);
    }
}
