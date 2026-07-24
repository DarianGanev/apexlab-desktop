using System.Net;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class CaptureIngestionCoordinatorTests
{
    [TestMethod]
    public async Task ClassifiesEveryAdapterOutcomeExactlyOnceInSourceOrder()
    {
        TelemetryPacketClassification[] classifications =
        [
            TelemetryPacketClassification.Compatible,
            TelemetryPacketClassification.MalformedHeader,
            TelemetryPacketClassification.UnsupportedFormat,
            TelemetryPacketClassification.UnsupportedYear,
            TelemetryPacketClassification.UnknownPacketId,
            TelemetryPacketClassification.UnsupportedPacketVersion,
            TelemetryPacketClassification.InvalidPacketLength,
            TelemetryPacketClassification.ExcludedPrivacyPacket,
        ];
        var source = new FiniteDatagramSource(
            classifications.Select(
                (classification, index) => CreateEnvelope(
                    sequence: index + 1,
                    senderAddress: IPAddress.Loopback,
                    marker: (byte)classification)));
        var adapter = new MarkerProtocolAdapter();
        var observer = new RecordingObserver();
        var subject = new CaptureIngestionCoordinator(
            source,
            adapter,
            SenderPolicy.LoopbackOnly,
            observer);

        await subject.RunAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            classifications,
            observer.Observations.Select(
                observation => observation.Result.Classification).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, classifications.Length).Select(value => (long)value).ToArray(),
            observer.Observations.Select(observation => observation.Sequence).ToArray());
        Assert.AreEqual(classifications.Length, adapter.InspectionCalls);
        Assert.AreEqual(1, source.StartCalls);
        Assert.AreEqual(1, source.StopCalls);

        var counters = subject.Counters;
        Assert.AreEqual(classifications.Length, counters.Source.SourceEnqueued);
        Assert.AreEqual(classifications.Length, counters.Classifier.SourceDequeued);
        Assert.AreEqual(1L, counters.Classifier.Compatible);
        Assert.AreEqual(1L, counters.Classifier.MalformedHeader);
        Assert.AreEqual(1L, counters.Classifier.UnsupportedFormat);
        Assert.AreEqual(1L, counters.Classifier.UnsupportedYear);
        Assert.AreEqual(1L, counters.Classifier.UnknownPacketId);
        Assert.AreEqual(1L, counters.Classifier.UnsupportedPacketVersion);
        Assert.AreEqual(1L, counters.Classifier.InvalidPacketLength);
        Assert.AreEqual(1L, counters.Classifier.ExcludedPrivacyPacket);
        Assert.AreEqual(0L, counters.Classifier.UnexpectedSender);
        Assert.AreEqual(0L, counters.Classifier.ClassifierAbandonedOnInterrupt);
        Assert.IsTrue(counters.HasCompleteSourceAccounting);
    }

    [TestMethod]
    public async Task UnexpectedSenderWinsBeforeProtocolInspection()
    {
        var source = new FiniteDatagramSource(
        [
            CreateEnvelope(1, IPAddress.Parse("192.0.2.10"), marker: 0xFF),
        ]);
        var adapter = new MarkerProtocolAdapter();
        var observer = new RecordingObserver();
        var subject = new CaptureIngestionCoordinator(
            source,
            adapter,
            SenderPolicy.LoopbackOnly,
            observer);

        await subject.RunAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, adapter.InspectionCalls);
        Assert.HasCount(1, observer.Observations);
        Assert.AreEqual(
            TelemetryPacketClassification.UnexpectedSender,
            observer.Observations[0].Result.Classification);
        Assert.IsFalse(observer.Observations[0].Result.Header.HasValue);
        Assert.IsNull(observer.Observations[0].Result.Descriptor);
        Assert.AreEqual(1L, subject.Counters.Classifier.UnexpectedSender);
    }

    [TestMethod]
    public async Task CancellationTransfersTheUnreadBacklogToAbandonment()
    {
        using var interruption = new CancellationTokenSource();
        var source = new FiniteDatagramSource(
        [
            CreateEnvelope(1, IPAddress.Loopback, (byte)TelemetryPacketClassification.MalformedHeader),
            CreateEnvelope(2, IPAddress.Loopback, (byte)TelemetryPacketClassification.MalformedHeader),
            CreateEnvelope(3, IPAddress.Loopback, (byte)TelemetryPacketClassification.MalformedHeader),
        ]);
        var observer = new RecordingObserver(_ => interruption.Cancel());
        var subject = new CaptureIngestionCoordinator(
            source,
            new MarkerProtocolAdapter(),
            SenderPolicy.LoopbackOnly,
            observer);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => subject.RunAsync(interruption.Token));

        Assert.HasCount(1, observer.Observations);
        Assert.AreEqual(1L, subject.Counters.Classifier.SourceDequeued);
        Assert.AreEqual(2L, subject.Counters.Classifier.ClassifierAbandonedOnInterrupt);
        Assert.IsTrue(subject.Counters.HasCompleteSourceAccounting);
        Assert.AreEqual(1, source.StopCalls);
    }

    [TestMethod]
    public async Task ACoordinatorInstanceIsOneShot()
    {
        var subject = new CaptureIngestionCoordinator(
            new FiniteDatagramSource([]),
            new MarkerProtocolAdapter(),
            SenderPolicy.LoopbackOnly);

        await subject.RunAsync(TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => subject.RunAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AdapterFailureStillStopsAndAccountsForUnreadPacket()
    {
        var processingFailure = new InvalidDataException("adapter failed");
        var source = new FiniteDatagramSource(
        [
            CreateEnvelope(1, IPAddress.Loopback, 0),
        ]);
        var subject = new CaptureIngestionCoordinator(
            source,
            new ThrowingProtocolAdapter(processingFailure),
            SenderPolicy.LoopbackOnly);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => subject.RunAsync(TestContext.CancellationToken));

        Assert.AreSame(processingFailure, thrown);
        Assert.AreEqual(1, source.StopCalls);
        Assert.AreEqual(0L, subject.Counters.Classifier.SourceDequeued);
        Assert.AreEqual(
            1L,
            subject.Counters.Classifier.ClassifierAbandonedOnInterrupt);
        Assert.IsTrue(subject.Counters.HasCompleteSourceAccounting);
    }

    [TestMethod]
    public async Task ObserverFailurePreservesClassifiedOutcomeAndStillStops()
    {
        var observerFailure = new InvalidDataException("observer failed");
        var source = new FiniteDatagramSource(
        [
            CreateEnvelope(
                1,
                IPAddress.Loopback,
                (byte)TelemetryPacketClassification.Compatible),
        ]);
        var subject = new CaptureIngestionCoordinator(
            source,
            new MarkerProtocolAdapter(),
            SenderPolicy.LoopbackOnly,
            new ThrowingObserver(observerFailure));

        var thrown = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => subject.RunAsync(TestContext.CancellationToken));

        Assert.AreSame(observerFailure, thrown);
        Assert.AreEqual(1, source.StopCalls);
        Assert.AreEqual(1L, subject.Counters.Classifier.Compatible);
        Assert.AreEqual(
            0L,
            subject.Counters.Classifier.ClassifierAbandonedOnInterrupt);
        Assert.IsTrue(subject.Counters.HasCompleteSourceAccounting);
    }

    [TestMethod]
    public async Task ProcessingAndStopFailuresAreBothReported()
    {
        var processingFailure = new InvalidDataException("adapter failed");
        var stopFailure = new IOException("stop failed");
        var source = new FiniteDatagramSource(
            [
                CreateEnvelope(1, IPAddress.Loopback, 0),
            ],
            stopFailure);
        var subject = new CaptureIngestionCoordinator(
            source,
            new ThrowingProtocolAdapter(processingFailure),
            SenderPolicy.LoopbackOnly);

        var thrown = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => subject.RunAsync(TestContext.CancellationToken));

        CollectionAssert.AreEquivalent(
            new Exception[] { processingFailure, stopFailure },
            thrown.InnerExceptions.ToArray());
        Assert.AreEqual(1, source.StopCalls);
        Assert.IsTrue(subject.Counters.HasCompleteSourceAccounting);
    }

    [TestMethod]
    public async Task CountersRemainValidWhenAdmissionAndClassificationAdvanceDuringSnapshot()
    {
        using var secondObserved = new ManualResetEventSlim();
        var firstObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SnapshotRaceDatagramSource(secondObserved);
        var observer = new RecordingObserver(
            observation =>
            {
                if (observation.Sequence == 1)
                {
                    firstObserved.TrySetResult();
                }
                else if (observation.Sequence == 2)
                {
                    secondObserved.Set();
                }
            });
        var subject = new CaptureIngestionCoordinator(
            source,
            new MarkerProtocolAdapter(),
            SenderPolicy.LoopbackOnly,
            observer);
        var runTask = subject.RunAsync(TestContext.CancellationToken);

        await firstObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.CancellationToken);

        var snapshot = await Task.Run(
            () => subject.Counters,
            TestContext.CancellationToken);
        source.Complete();
        await runTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.CancellationToken);

        Assert.AreEqual(1L, snapshot.Source.SourceEnqueued);
        Assert.AreEqual(1L, snapshot.Classifier.SourceDequeued);
        Assert.AreEqual(0L, snapshot.EnqueuedAwaitingClassifier);
        Assert.AreEqual(2L, subject.Counters.Source.SourceEnqueued);
        Assert.AreEqual(2L, subject.Counters.Classifier.SourceDequeued);
    }

    [TestMethod]
    public void ObservationContractCannotExposePayloadOrSenderIdentity()
    {
        var propertyNames = typeof(CapturePacketObservation)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(propertyNames, "Payload");
        CollectionAssert.DoesNotContain(propertyNames, "Sender");
        CollectionAssert.DoesNotContain(propertyNames, "SenderAddress");
        CollectionAssert.DoesNotContain(propertyNames, "SenderPort");
    }

    public TestContext TestContext { get; set; } = null!;

    private static DatagramEnvelope CreateEnvelope(
        long sequence,
        IPAddress senderAddress,
        byte marker)
    {
        return DatagramEnvelope.CopyFrom(
            sequence,
            monotonicTimestamp: sequence * 10,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            new DatagramSender(senderAddress, 49_152),
            [marker]);
    }

    private sealed class FiniteDatagramSource : IDatagramSource
    {
        private readonly DatagramEnvelope[] _envelopes;
        private readonly Exception? _stopFailure;
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();

        public FiniteDatagramSource(
            IEnumerable<DatagramEnvelope> envelopes,
            Exception? stopFailure = null)
        {
            _envelopes = envelopes.ToArray();
            _stopFailure = stopFailure;
        }

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters =>
            new(
                _envelopes.LongLength,
                _envelopes.LongLength,
                sourceDroppedFull: 0,
                sourceRejectedOversized: 0,
                socketErrors: 0);

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            foreach (var envelope in _envelopes)
            {
                Assert.IsTrue(_channel.Writer.TryWrite(envelope));
            }

            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            _channel.Writer.TryComplete();
            return _stopFailure is null
                ? Task.CompletedTask
                : Task.FromException(_stopFailure);
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SnapshotRaceDatagramSource : IDatagramSource
    {
        private readonly ManualResetEventSlim _secondObserved;
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();
        private long _sourceEnqueued;
        private int _snapshotRaceTriggered;

        public SnapshotRaceDatagramSource(ManualResetEventSlim secondObserved)
        {
            _secondObserved = secondObserved;
        }

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters
        {
            get
            {
                var pointInTimeEnqueued = Interlocked.Read(ref _sourceEnqueued);
                if (pointInTimeEnqueued == 1
                    && Interlocked.Exchange(ref _snapshotRaceTriggered, 1) == 0)
                {
                    Interlocked.Increment(ref _sourceEnqueued);
                    Assert.IsTrue(
                        _channel.Writer.TryWrite(
                            CreateEnvelope(
                                2,
                                IPAddress.Loopback,
                                (byte)TelemetryPacketClassification.Compatible)));
                    Assert.IsTrue(_secondObserved.Wait(TimeSpan.FromSeconds(5)));
                }

                return new(
                    datagramsObserved: pointInTimeEnqueued,
                    sourceEnqueued: pointInTimeEnqueued,
                    sourceDroppedFull: 0,
                    sourceRejectedOversized: 0,
                    socketErrors: 0);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _sourceEnqueued);
            Assert.IsTrue(
                _channel.Writer.TryWrite(
                    CreateEnvelope(
                        1,
                        IPAddress.Loopback,
                        (byte)TelemetryPacketClassification.Compatible)));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MarkerProtocolAdapter : ITelemetryProtocolAdapter
    {
        private static readonly TelemetryPacketDescriptor CompatibleDescriptor =
            new(
                "Synthetic",
                packetId: 1,
                packetVersion: 1,
                datagramLength: 1,
                TelemetryPacketPrivacyDisposition.EvidenceAllowed);

        private static readonly TelemetryHeaderMetadata CompatibleHeader =
            new(
                packetFormat: 2025,
                gameYear: 25,
                gameMajorVersion: 1,
                gameMinorVersion: 0,
                packetVersion: 1,
                packetId: 1,
                sessionUid: 42,
                sessionTimeSeconds: 1,
                frameIdentifier: 1,
                overallFrameIdentifier: 1,
                playerCarIndex: 0,
                secondaryPlayerCarIndex: byte.MaxValue);

        public string ProtocolId => "synthetic";

        public int HeaderLength => 1;

        public int InspectionCalls { get; private set; }

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
        {
            InspectionCalls++;
            var classification = (TelemetryPacketClassification)datagram[0];
            return classification == TelemetryPacketClassification.Compatible
                ? TelemetryPacketResult.Compatible(
                    CompatibleHeader,
                    CompatibleDescriptor)
                : TelemetryPacketResult.Rejected(classification);
        }
    }

    private sealed class ThrowingProtocolAdapter : ITelemetryProtocolAdapter
    {
        private readonly Exception _failure;

        public ThrowingProtocolAdapter(Exception failure)
        {
            _failure = failure;
        }

        public string ProtocolId => "synthetic-throwing";

        public int HeaderLength => 1;

        public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
        {
            throw _failure;
        }
    }

    private sealed class RecordingObserver : ICapturePacketObserver
    {
        private readonly Action<CapturePacketObservation>? _onObservation;

        public RecordingObserver(
            Action<CapturePacketObservation>? onObservation = null)
        {
            _onObservation = onObservation;
        }

        public List<CapturePacketObservation> Observations { get; } = [];

        public void Observe(CapturePacketObservation observation)
        {
            Observations.Add(observation);
            _onObservation?.Invoke(observation);
        }
    }

    private sealed class ThrowingObserver : ICapturePacketObserver
    {
        private readonly Exception _failure;

        public ThrowingObserver(Exception failure)
        {
            _failure = failure;
        }

        public void Observe(CapturePacketObservation observation)
        {
            throw _failure;
        }
    }
}
