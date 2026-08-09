using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ApexLab.Application.Canonical;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Canonical;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Tests.Canonical;

[TestClass]
public sealed class CanonicalReplayWorkflowTests
{
    [TestMethod]
    public async Task CacheHitReturnsWithoutStartingSource()
    {
        var request = Request();
        var completion = Completion(request, recordCount: 0);
        var entry = new FakeEntry(completion);
        var store = new FakeStore(entry);
        var source = new FakeSource([]);

        var result = await CanonicalReplayWorkflow.ExecuteAsync(
            request,
            source,
            new FakeProjector(request.Identity),
            store,
            TestContext.CancellationToken);

        Assert.AreEqual(CanonicalCacheDisposition.Reused, result.Disposition);
        Assert.AreEqual(completion, result.Completion);
        Assert.IsFalse(source.StartCalled);
        Assert.IsFalse(source.StopCalled);
        Assert.IsTrue(source.DisposeCalled);
        Assert.IsTrue(entry.DisposeCalled);
        Assert.IsFalse(store.CreateWriterCalled);
    }

    [TestMethod]
    public async Task BuildPreservesOrderAndMakesLeadingInteriorGapsExplicit()
    {
        var request = Request();
        var source = new FakeSource(
        [
            Envelope(3, arrival: 30, projectionCode: 1),
            Envelope(4, arrival: 40, projectionCode: 1),
            Envelope(8, arrival: 80, projectionCode: 2),
        ]);
        var writer = new FakeWriter(request);
        var store = new FakeStore(entry: null, writer);

        var result = await CanonicalReplayWorkflow.ExecuteAsync(
            request,
            source,
            new FakeProjector(request.Identity),
            store,
            TestContext.CancellationToken);

        Assert.AreEqual(CanonicalCacheDisposition.Built, result.Disposition);
        Assert.IsTrue(source.StartCalled);
        Assert.IsTrue(source.StopCalled);
        Assert.IsTrue(source.DisposeCalled);
        Assert.IsTrue(writer.FinalizeCalled);
        Assert.IsTrue(writer.DisposeCalled);
        Assert.HasCount(5, writer.Records);
        AssertGap(writer.Records[0], 1, 2);
        AssertObservation(writer.Records[1], 3, 30);
        AssertObservation(writer.Records[2], 4, 40);
        AssertGap(writer.Records[3], 5, 7);
        Assert.AreEqual(CanonicalRecordKind.Exclusion, writer.Records[4].Kind);
        Assert.AreEqual(8L, writer.Records[4].SourceSequence);
        Assert.AreEqual((byte)7, writer.Records[4].PacketId);
        Assert.AreEqual(
            CanonicalExclusionReason.CompatibleFamilyOutsideSlice,
            writer.Records[4].ExclusionReason);
    }

    [TestMethod]
    public async Task EventExclusionMapsToItsDistinctReason()
    {
        var request = Request();
        var writer = new FakeWriter(request);

        await CanonicalReplayWorkflow.ExecuteAsync(
            request,
            new FakeSource([Envelope(1, 10, projectionCode: 4)]),
            new FakeProjector(request.Identity),
            new FakeStore(null, writer),
            TestContext.CancellationToken);

        Assert.HasCount(1, writer.Records);
        Assert.AreEqual(
            CanonicalExclusionReason.EventCodeOutsideSlice,
            writer.Records[0].ExclusionReason);
        Assert.AreEqual((byte)3, writer.Records[0].PacketId);
    }

    [TestMethod]
    public async Task DuplicateOrDecreasingSequenceNeverFinalizes()
    {
        foreach (var sequences in new long[][] { [1, 1], [2, 1] })
        {
            var request = Request();
            var source = new FakeSource(sequences.Select(
                sequence => Envelope(sequence, sequence, 1)).ToArray());
            var writer = new FakeWriter(request);

            var exception = await Assert.ThrowsAsync<CanonicalReplayException>(() =>
                CanonicalReplayWorkflow.ExecuteAsync(
                    request,
                    source,
                    new FakeProjector(request.Identity),
                    new FakeStore(null, writer),
                    TestContext.CancellationToken));

            Assert.AreEqual(
                CanonicalReplayFailureKind.DuplicateOrReorderedSequence,
                exception.Kind);
            Assert.IsFalse(writer.FinalizeCalled);
            Assert.IsTrue(writer.DisposeCalled);
            Assert.IsTrue(source.StopCalled);
            Assert.IsTrue(source.DisposeCalled);
        }
    }

    [TestMethod]
    public async Task ProjectionRejectionNeverFinalizesOrPublishesAValue()
    {
        var request = Request();
        var writer = new FakeWriter(request);

        var exception = await Assert.ThrowsAsync<CanonicalReplayException>(() =>
            CanonicalReplayWorkflow.ExecuteAsync(
                request,
                new FakeSource([Envelope(1, 10, projectionCode: 3)]),
                new FakeProjector(request.Identity),
                new FakeStore(null, writer),
                TestContext.CancellationToken));

        Assert.AreEqual(CanonicalReplayFailureKind.ProjectionRejected, exception.Kind);
        Assert.AreEqual(
            CanonicalProjectionReason.MalformedSelectedField,
            exception.ProjectionReason);
        Assert.IsFalse(writer.FinalizeCalled);
        Assert.IsEmpty(writer.Records);
    }

    [TestMethod]
    public async Task IdentityMismatchFailsBeforeCacheOrSourceUse()
    {
        var request = Request();
        var source = new FakeSource([]);
        var store = new FakeStore(null, new FakeWriter(request));
        var projector = new FakeProjector(
            new CanonicalReplayIdentity(
                request.Identity.SourceEvidenceSha256,
                request.Identity.ProtocolId,
                request.Identity.ContractId,
                "f125-v3-minimal-decoder-v2",
                request.Identity.CanonicalSchemaId));

        var exception = await Assert.ThrowsAsync<CanonicalReplayException>(() =>
            CanonicalReplayWorkflow.ExecuteAsync(
                request,
                source,
                projector,
                store,
                TestContext.CancellationToken));

        Assert.AreEqual(CanonicalReplayFailureKind.IdentityMismatch, exception.Kind);
        Assert.IsFalse(store.TryOpenCalled);
        Assert.IsFalse(source.StartCalled);
        Assert.IsTrue(source.DisposeCalled);
    }

    [TestMethod]
    public async Task MismatchedCacheCompletionFailsWithoutReplay()
    {
        var request = Request();
        var otherRequest = new CanonicalCacheRequest(
            new CanonicalReplayIdentity(
                request.Identity.SourceEvidenceSha256,
                request.Identity.ProtocolId,
                request.Identity.ContractId,
                request.Identity.DecoderId,
                "apexlab-canonical-sample-v2"),
            request.SourceStopwatchFrequency);
        var source = new FakeSource([]);

        var exception = await Assert.ThrowsAsync<CanonicalReplayException>(() =>
            CanonicalReplayWorkflow.ExecuteAsync(
                request,
                source,
                new FakeProjector(request.Identity),
                new FakeStore(new FakeEntry(Completion(otherRequest, 0))),
                TestContext.CancellationToken));

        Assert.AreEqual(CanonicalReplayFailureKind.InvalidCacheCompletion, exception.Kind);
        Assert.IsFalse(source.StartCalled);
        Assert.IsTrue(source.DisposeCalled);
    }

    [TestMethod]
    public async Task CancellationStillStopsAndDisposesSourceAndWriter()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new FakeSource([]);
        var writer = new FakeWriter(request);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CanonicalReplayWorkflow.ExecuteAsync(
                request,
                source,
                new FakeProjector(request.Identity),
                new FakeStore(null, writer),
                cancellation.Token));

        Assert.IsFalse(source.StartCalled);
        Assert.IsTrue(source.DisposeCalled);
        Assert.IsFalse(writer.FinalizeCalled);
        Assert.IsFalse(writer.DisposeCalled);
    }

    [TestMethod]
    public async Task WriteAndStopFailuresPreserveBothCausesAndCleanup()
    {
        var request = Request();
        var source = new FakeSource([Envelope(1, 10, 1)])
        {
            StopFailure = new IOException("stop failure"),
        };
        var writer = new FakeWriter(request)
        {
            WriteFailure = new InvalidDataException("write failure"),
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            CanonicalReplayWorkflow.ExecuteAsync(
                request,
                source,
                new FakeProjector(request.Identity),
                new FakeStore(null, writer),
                TestContext.CancellationToken));

        Assert.HasCount(2, exception.InnerExceptions);
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerExceptions[0]);
        Assert.IsInstanceOfType<IOException>(exception.InnerExceptions[1]);
        Assert.IsTrue(source.DisposeCalled);
        Assert.IsTrue(writer.DisposeCalled);
        Assert.IsFalse(writer.FinalizeCalled);
    }

    public TestContext TestContext { get; set; } = null!;

    private static CanonicalCacheRequest Request() =>
        new(
            CanonicalReplayIdentityTests.Identity(),
            sourceStopwatchFrequency: 10_000_000);

    private static DatagramEnvelope Envelope(
        long sequence,
        long arrival,
        byte projectionCode) =>
        DatagramEnvelope.CopyFrom(
            sequence,
            arrival,
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            new DatagramSender(IPAddress.Loopback, 20_777),
            [projectionCode]);

    private static CanonicalPacket Packet() =>
        CanonicalPacket.CreateMotion(
            new CanonicalPacketHeader(1F, 2, 3, 0, byte.MaxValue),
            new CanonicalMotionPacket(4F, 5F, 6F));

    private static CanonicalCacheCompletion Completion(
        CanonicalCacheRequest request,
        long recordCount) =>
        new(
            request.Identity,
            request.SourceStopwatchFrequency,
            dataLengthBytes: 128,
            dataSha256: new string('a', 64),
            canonicalSha256: new string('b', 64),
            recordCount,
            observationCount: recordCount,
            exclusionCount: 0,
            gapCount: 0,
            firstSourceSequence: recordCount == 0 ? null : 1,
            lastSourceSequence: recordCount == 0 ? null : recordCount);

    private static void AssertGap(CanonicalRecord record, long first, long last)
    {
        Assert.AreEqual(CanonicalRecordKind.Gap, record.Kind);
        Assert.AreEqual(first, record.FirstMissingSequence);
        Assert.AreEqual(last, record.LastMissingSequence);
        Assert.AreEqual(
            CanonicalGapReason.UnretainedOrMissingSourceRange,
            record.GapReason);
    }

    private static void AssertObservation(
        CanonicalRecord record,
        long sequence,
        long arrival)
    {
        Assert.AreEqual(CanonicalRecordKind.Observation, record.Kind);
        Assert.AreEqual(sequence, record.SourceSequence);
        Assert.AreEqual(arrival, record.ArrivalTimestamp);
    }

    private sealed class FakeProjector : ICanonicalPacketProjector
    {
        public FakeProjector(CanonicalReplayIdentity identity)
        {
            ProtocolId = identity.ProtocolId;
            ContractId = identity.ContractId;
            DecoderId = identity.DecoderId;
            CanonicalSchemaId = identity.CanonicalSchemaId;
        }

        public string ProtocolId { get; }
        public string ContractId { get; }
        public string DecoderId { get; }
        public string CanonicalSchemaId { get; }

        public CanonicalProjectionResult Project(ReadOnlySpan<byte> datagram) =>
            datagram[0] switch
            {
                1 => CanonicalProjectionResult.Projected(Packet()),
                2 => CanonicalProjectionResult.Excluded(
                    7,
                    CanonicalProjectionReason.CompatibleFamilyOutsideSlice),
                3 => CanonicalProjectionResult.Rejected(
                    CanonicalProjectionReason.MalformedSelectedField,
                    0),
                4 => CanonicalProjectionResult.Excluded(
                    3,
                    CanonicalProjectionReason.EventCodeOutsideSlice),
                _ => throw new InvalidOperationException(),
            };
    }

    private sealed class FakeSource : IDatagramSource
    {
        private readonly IReadOnlyList<DatagramEnvelope> _envelopes;
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();

        public FakeSource(IReadOnlyList<DatagramEnvelope> envelopes) =>
            _envelopes = envelopes;

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;
        public DatagramSourceCounters Counters => default;
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        public Exception? StopFailure { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalled = true;
            foreach (var envelope in _envelopes)
            {
                _channel.Writer.TryWrite(envelope);
            }

            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStore : ICanonicalCacheStore
    {
        private readonly ICanonicalCacheEntry? _entry;
        private readonly ICanonicalCacheWriter? _writer;

        public FakeStore(
            ICanonicalCacheEntry? entry,
            ICanonicalCacheWriter? writer = null)
        {
            _entry = entry;
            _writer = writer;
        }

        public bool TryOpenCalled { get; private set; }
        public bool CreateWriterCalled { get; private set; }

        public Task<ICanonicalCacheEntry?> TryOpenAsync(
            CanonicalCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryOpenCalled = true;
            return Task.FromResult(_entry);
        }

        public Task<ICanonicalCacheWriter> CreateWriterAsync(
            CanonicalCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateWriterCalled = true;
            return Task.FromResult(
                _writer ?? throw new InvalidOperationException());
        }
    }

    private sealed class FakeEntry : ICanonicalCacheEntry
    {
        public FakeEntry(CanonicalCacheCompletion completion) =>
            Completion = completion;

        public CanonicalCacheCompletion Completion { get; }
        public bool DisposeCalled { get; private set; }

        public async IAsyncEnumerable<CanonicalRecord> ReadAllAsync(
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWriter : ICanonicalCacheWriter
    {
        private readonly CanonicalCacheRequest _request;

        public FakeWriter(CanonicalCacheRequest request) => _request = request;

        public List<CanonicalRecord> Records { get; } = [];
        public bool FinalizeCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        public Exception? WriteFailure { get; init; }

        public ValueTask WriteAsync(
            CanonicalRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure is not null)
            {
                return ValueTask.FromException(WriteFailure);
            }

            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public Task<CanonicalCacheCompletion> FinalizeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCalled = true;
            var observations = Records.Count(record =>
                record.Kind == CanonicalRecordKind.Observation);
            var exclusions = Records.Count(record =>
                record.Kind == CanonicalRecordKind.Exclusion);
            var gaps = Records.Count(record =>
                record.Kind == CanonicalRecordKind.Gap);
            var sourceSequences = Records
                .Where(record => record.SourceSequence.HasValue)
                .Select(record => record.SourceSequence!.Value)
                .ToArray();
            return Task.FromResult(new CanonicalCacheCompletion(
                _request.Identity,
                _request.SourceStopwatchFrequency,
                dataLengthBytes: 128 + Records.Count,
                dataSha256: new string('a', 64),
                canonicalSha256: new string('b', 64),
                Records.Count,
                observations,
                exclusions,
                gaps,
                sourceSequences.Length == 0 ? null : sourceSequences.Min(),
                sourceSequences.Length == 0 ? null : sourceSequences.Max()));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }
}
