using System.Net;
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

    [TestMethod]
    public async Task TimedOutWriteRetainsOwnershipUntilDeferredCleanupFinishes()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.BlockWrites();
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromMilliseconds(50));
        await subject.ArmAsync(TestContext.CancellationToken);
        factory.Source.Publish(marker: 1);
        await factory.Evidence.WriteStarted.WaitAsync(
            TestContext.CancellationToken);

        var interrupted = await subject.StopAsync(
            CaptureStopReason.User,
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Interrupted, interrupted.State);
        Assert.IsFalse(subject.DeferredCleanupCompletion.IsCompleted);
        Assert.AreEqual(0, factory.Source.DisposeCalls);
        Assert.AreEqual(0, factory.Evidence.DisposeCalls);

        factory.Evidence.ReleaseWrites();
        await subject.DeferredCleanupCompletion.WaitAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(CaptureState.Stopped, subject.Snapshot.State);
        Assert.AreEqual(1L, subject.Snapshot.Counters.Evidence.FinalizedRecords);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
    }

    [TestMethod]
    public async Task WriteFailureFaultsAndReleasesUnfinalizedOwnership()
    {
        var factory = new ControlledSessionFactory();
        factory.CompleteCreation();
        factory.Evidence.FailWrites = true;
        await using var subject = new CaptureWorkflow(
            factory,
            TimeSpan.FromSeconds(2));
        var faulted = new TaskCompletionSource<CaptureWorkflowSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        subject.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == CaptureState.Faulted)
            {
                faulted.TrySetResult(snapshot);
            }
        };
        await subject.ArmAsync(TestContext.CancellationToken);

        factory.Source.Publish(marker: 1);
        var snapshot = await faulted.Task.WaitAsync(
            TestContext.CancellationToken);

        Assert.AreEqual(
            CaptureFailureKind.EvidenceWrite,
            snapshot.FailureKind);
        Assert.IsInstanceOfType<IOException>(snapshot.Failure);
        Assert.IsNull(snapshot.Completion);
        Assert.AreEqual(0, factory.Evidence.FinalizeCalls);
        Assert.AreEqual(1, factory.Source.DisposeCalls);
        Assert.AreEqual(1, factory.Evidence.DisposeCalls);
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
        private long _enqueued;

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters
        {
            get
            {
                var enqueued = Interlocked.Read(ref _enqueued);
                return new DatagramSourceCounters(
                    enqueued,
                    enqueued,
                    sourceDroppedFull: 0,
                    sourceRejectedOversized: 0,
                    socketErrors: 0);
            }
        }

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

        public void Publish(byte marker)
        {
            var sequence = Interlocked.Increment(ref _enqueued);
            Assert.IsTrue(
                _channel.Writer.TryWrite(
                    DatagramEnvelope.CopyFrom(
                        sequence,
                        sequence,
                        DateTimeOffset.UnixEpoch,
                        new DatagramSender(
                            IPAddress.Loopback,
                            20_777),
                        [marker])));
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
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _writeRelease;
        private long _written;

        public RawEvidenceCaptureId CaptureId { get; private set; } =
            RawEvidenceCaptureId.Parse(
                "00112233445546778899aabbccddeeff");

        public RawEvidenceProtocolId ProtocolId { get; } =
            RawEvidenceProtocolId.Parse("synthetic-v1");

        public RawEvidenceLimits Limits { get; } =
            new(minimumFreeSpaceBytes: 0);

        public int FinalizeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool FailWrites { get; set; }

        public Task WriteStarted => _writeStarted.Task;

        public void SetCaptureId(RawEvidenceCaptureId captureId) =>
            CaptureId = captureId;

        public async ValueTask WriteAsync(
            DatagramEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            var release = _writeRelease;
            if (release is not null)
            {
                await release.Task.ConfigureAwait(false);
            }

            if (FailWrites)
            {
                throw new IOException("synthetic evidence write failed");
            }

            Interlocked.Increment(ref _written);
        }

        public void BlockWrites() =>
            _writeRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites() => _writeRelease?.TrySetResult();

        public Task<RawEvidenceCompletion> FinalizeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCalls++;
            return Task.FromResult(
                new RawEvidenceCompletion(
                    CaptureId,
                    ProtocolId,
                    Interlocked.Read(ref _written),
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
        private static readonly TelemetryPacketDescriptor Descriptor =
            new(
                "Synthetic",
                packetId: 1,
                packetVersion: 1,
                datagramLength: 1,
                TelemetryPacketPrivacyDisposition.EvidenceAllowed);

        public string ProtocolId => "synthetic-v1";

        public int HeaderLength => 1;

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
        {
            if (datagram.Length == 1 && datagram[0] == 1)
            {
                return TelemetryPacketResult.Compatible(
                    new TelemetryHeaderMetadata(
                        packetFormat: 2025,
                        gameYear: 25,
                        gameMajorVersion: 1,
                        gameMinorVersion: 0,
                        packetVersion: 1,
                        packetId: 1,
                        sessionUid: 0,
                        sessionTimeSeconds: 0,
                        frameIdentifier: 0,
                        overallFrameIdentifier: 0,
                        playerCarIndex: 0,
                        secondaryPlayerCarIndex: byte.MaxValue),
                    Descriptor);
            }

            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.MalformedHeader);
        }
    }
}
