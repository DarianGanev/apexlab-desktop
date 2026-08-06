using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Protocols.F125;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.IntegrationTests.Capture;

[TestClass]
public sealed class CaptureAdversityTests
{
    private const int CorpusSeed = 25_082_026;
    private const int CorpusSize = 4_096;

    [TestMethod]
    public async Task FixedSeedMixedCorpusHasExactClassifierAndEvidenceAccounting()
    {
        TestContext.WriteLine($"capture-adversity-seed={CorpusSeed}");
        var corpus = CreateCorpus(CorpusSeed, CorpusSize);
        await using var source = new CorpusSource(corpus);
        await using var evidence = new SlowRecordingEvidenceStore();
        var observer = new RecordingObserver();
        var coordinator = new CaptureIngestionCoordinator(
            source,
            new F125TelemetryProtocolAdapter(),
            SenderPolicy.LoopbackOnly,
            observer,
            evidence);

        await coordinator.RunAsync(TestContext.CancellationToken);
        var completion = await coordinator.FinalizeEvidenceAsync(
            TestContext.CancellationToken);
        var counters = coordinator.CaptureCounters;

        Assert.AreEqual(CorpusSize, counters.Source.DatagramsObserved);
        Assert.AreEqual(CorpusSize, counters.Source.SourceEnqueued);
        Assert.AreEqual(0L, counters.Source.SourceDroppedFull);
        Assert.AreEqual(0L, counters.Source.SourceRejectedOversized);
        Assert.AreEqual(CorpusSize, counters.Classifier.SourceDequeued);
        Assert.AreEqual(0L, counters.Classifier.ClassifierAbandonedOnTermination);
        Assert.IsTrue(counters.HasCompleteSourceAccounting);

        foreach (var classification in Enum.GetValues<TelemetryPacketClassification>()
                     .Where(value => value != TelemetryPacketClassification.Unspecified))
        {
            Assert.AreEqual(
                corpus.LongCount(item => item.Expected == classification),
                ClassificationCount(counters.Classifier, classification),
                $"classification={classification}, seed={CorpusSeed}");
        }

        var compatibleSequences = corpus
            .Select((item, index) => (item, sequence: checked((index * 2L) + 1L)))
            .Where(pair => pair.item.Expected == TelemetryPacketClassification.Compatible)
            .Select(pair => pair.sequence)
            .ToArray();
        CollectionAssert.AreEqual(
            compatibleSequences,
            evidence.WrittenSequences.ToArray());
        Assert.AreEqual(compatibleSequences.LongLength, completion.RecordCount);
        Assert.AreEqual(compatibleSequences.LongLength, counters.Evidence.SinkWritten);
        Assert.AreEqual(compatibleSequences.LongLength, counters.Evidence.FinalizedRecords);
        Assert.AreEqual(0L, counters.Evidence.SinkWriteFailed);
        Assert.AreEqual(0L, counters.Evidence.SinkPending);
        Assert.AreEqual(0L, counters.Evidence.SinkPendingDeferredCleanup);
        Assert.IsTrue(counters.AllWrittenRecordsAreFinalized);
        Assert.AreEqual(1, source.StartCalls);
        Assert.AreEqual(1, source.StopCalls);

        CollectionAssert.AreEqual(
            corpus.Select(item => item.Expected).ToArray(),
            observer.Observations.Select(item => item.Result.Classification).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, CorpusSize)
                .Select(index => checked((index * 2L) + 1L))
                .ToArray(),
            observer.Observations.Select(item => item.Sequence).ToArray());
    }

    public TestContext TestContext { get; set; } = null!;

    private static IReadOnlyList<CorpusItem> CreateCorpus(int seed, int count)
    {
        var templates = new Func<CorpusItem>[]
        {
            () => new(
                CreatePacket(packetId: 3, packetVersion: 1, length: 45),
                IPAddress.Loopback,
                TelemetryPacketClassification.Compatible),
            () => new(
                new byte[7],
                IPAddress.Loopback,
                TelemetryPacketClassification.MalformedHeader),
            () => new(
                CreatePacket(packetId: 3, packetVersion: 1, length: 45, packetFormat: 2024),
                IPAddress.Loopback,
                TelemetryPacketClassification.UnsupportedFormat),
            () => new(
                CreatePacket(packetId: 3, packetVersion: 1, length: 45, gameYear: 24),
                IPAddress.Loopback,
                TelemetryPacketClassification.UnsupportedYear),
            () => new(
                CreatePacket(packetId: 99, packetVersion: 1, length: 45),
                IPAddress.Loopback,
                TelemetryPacketClassification.UnknownPacketId),
            () => new(
                CreatePacket(packetId: 3, packetVersion: 2, length: 45),
                IPAddress.Loopback,
                TelemetryPacketClassification.UnsupportedPacketVersion),
            () => new(
                CreatePacket(packetId: 3, packetVersion: 1, length: 44),
                IPAddress.Loopback,
                TelemetryPacketClassification.InvalidPacketLength),
            () => new(
                CreatePacket(packetId: 4, packetVersion: 1, length: 1_284),
                IPAddress.Loopback,
                TelemetryPacketClassification.ExcludedPrivacyPacket),
            () => new(
                CreatePacket(packetId: 3, packetVersion: 1, length: 45),
                IPAddress.Parse("192.0.2.25"),
                TelemetryPacketClassification.UnexpectedSender),
        };
        var random = new Random(seed);
        var corpus = new List<CorpusItem>(count);
        foreach (var template in templates)
        {
            corpus.Add(template());
        }
        while (corpus.Count < count)
        {
            corpus.Add(templates[random.Next(templates.Length)]());
        }
        for (var index = corpus.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (corpus[index], corpus[swap]) = (corpus[swap], corpus[index]);
        }
        return corpus;
    }

    private static byte[] CreatePacket(
        byte packetId,
        byte packetVersion,
        int length,
        ushort packetFormat = F125Protocol.PacketFormat,
        byte gameYear = F125Protocol.GameYear)
    {
        var packet = new byte[length];
        if (length < F125Protocol.HeaderLength)
        {
            return packet;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet, packetFormat);
        packet[2] = gameYear;
        packet[3] = 1;
        packet[4] = 0;
        packet[5] = packetVersion;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), 1UL);
        return packet;
    }

    private static long ClassificationCount(
        DatagramClassificationCounters counters,
        TelemetryPacketClassification classification) =>
        classification switch
        {
            TelemetryPacketClassification.Compatible => counters.Compatible,
            TelemetryPacketClassification.MalformedHeader => counters.MalformedHeader,
            TelemetryPacketClassification.UnsupportedFormat => counters.UnsupportedFormat,
            TelemetryPacketClassification.UnsupportedYear => counters.UnsupportedYear,
            TelemetryPacketClassification.UnknownPacketId => counters.UnknownPacketId,
            TelemetryPacketClassification.UnsupportedPacketVersion =>
                counters.UnsupportedPacketVersion,
            TelemetryPacketClassification.InvalidPacketLength => counters.InvalidPacketLength,
            TelemetryPacketClassification.ExcludedPrivacyPacket => counters.ExcludedPrivacyPacket,
            TelemetryPacketClassification.UnexpectedSender => counters.UnexpectedSender,
            _ => throw new ArgumentOutOfRangeException(nameof(classification)),
        };

    private sealed record CorpusItem(
        byte[] Payload,
        IPAddress SenderAddress,
        TelemetryPacketClassification Expected);

    private sealed class CorpusSource : IDatagramSource
    {
        private readonly IReadOnlyList<CorpusItem> _corpus;
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                });

        public CorpusSource(IReadOnlyList<CorpusItem> corpus) =>
            _corpus = corpus;

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters => new(
            _corpus.Count,
            _corpus.Count,
            sourceDroppedFull: 0,
            sourceRejectedOversized: 0,
            socketErrors: 0);

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            for (var index = 0; index < _corpus.Count; index++)
            {
                var item = _corpus[index];
                Assert.IsTrue(_channel.Writer.TryWrite(
                    DatagramEnvelope.CopyFrom(
                        sequence: checked((index * 2L) + 1L),
                        monotonicTimestamp: index,
                        DateTimeOffset.UnixEpoch.AddTicks(index),
                        new DatagramSender(item.SenderAddress, 20_777),
                        item.Payload)));
            }
            _channel.Writer.TryComplete();
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
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SlowRecordingEvidenceStore : IRawEvidenceStore
    {
        private readonly List<long> _writtenSequences = new();

        public RawEvidenceCaptureId CaptureId { get; } =
            RawEvidenceCaptureId.Parse("00112233445546778899aabbccddeeff");

        public RawEvidenceProtocolId ProtocolId { get; } =
            RawEvidenceProtocolId.Parse(F125Protocol.Id);

        public RawEvidenceLimits Limits { get; } =
            new(minimumFreeSpaceBytes: 0);

        public IReadOnlyList<long> WrittenSequences => _writtenSequences;

        public async ValueTask WriteAsync(
            DatagramEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            _writtenSequences.Add(envelope.Sequence);
        }

        public Task<RawEvidenceCompletion> FinalizeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RawEvidenceCompletion(
                CaptureId,
                ProtocolId,
                _writtenSequences.Count,
                RawEvidenceLimits.MinimumFileBytes,
                new string('0', 64),
                DateTimeOffset.UnixEpoch));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingObserver : ICapturePacketObserver
    {
        public List<CapturePacketObservation> Observations { get; } = new();

        public void Observe(CapturePacketObservation observation) =>
            Observations.Add(observation);
    }
}
