using System.Runtime.CompilerServices;
using ApexLab.Application.Canonical;
using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Tests.Laps;

[TestClass]
public sealed class BahrainLapCandidateAssemblerTests
{
    [TestMethod]
    public async Task ConsecutiveTransitionsProduceLeadingCompleteAndTrailingCandidates()
    {
        CanonicalRecord[] records =
        [
            Session(1),
            Lap(2, lapNumber: 1),
            Lap(3, lapNumber: 2, lastLapMilliseconds: 90_000),
            Lap(4, lapNumber: 3, lastLapMilliseconds: 91_000),
        ];

        var inventory = await AssembleAsync(records);

        Assert.HasCount(3, inventory.Candidates);
        AssertBoundary(
            inventory.Candidates[0],
            LapBoundaryCompleteness.LeadingPartial,
            2,
            3,
            lapNumber: 1);
        AssertBoundary(
            inventory.Candidates[1],
            LapBoundaryCompleteness.Complete,
            3,
            4,
            lapNumber: 2,
            officialTime: 91_000);
        AssertBoundary(
            inventory.Candidates[2],
            LapBoundaryCompleteness.TrailingPartial,
            4,
            5,
            lapNumber: 3);
        Assert.AreEqual(
            BahrainTelemetryContextTests.Context(
                BahrainTelemetryContextTests.Session()),
            inventory.ReferenceContext);
        Assert.IsTrue(inventory.Candidates.All(candidate =>
            candidate.EvidenceFlags == LapEvidenceFlags.None));
    }

    [TestMethod]
    public async Task LapJumpAndMissingCompletionTimeAreExplicitlyIncoherent()
    {
        var jump = await AssembleAsync(
        [
            Session(1),
            Lap(2, lapNumber: 1),
            Lap(3, lapNumber: 3, lastLapMilliseconds: 90_000),
        ]);
        var missingTime = await AssembleAsync(
        [
            Session(1),
            Lap(2, lapNumber: 1),
            Lap(3, lapNumber: 2, lastLapMilliseconds: 0),
        ]);

        Assert.AreEqual(
            LapBoundaryCompleteness.Incoherent,
            jump.Candidates[0].Boundary.Completeness);
        Assert.AreEqual(
            LapBoundaryCompleteness.Incoherent,
            missingTime.Candidates[0].Boundary.Completeness);
        Assert.AreEqual(
            LapBoundaryCompleteness.TrailingPartial,
            jump.Candidates[1].Boundary.Completeness);
    }

    [TestMethod]
    public async Task DirectFactsAreAccumulatedOnTheCandidateTheyIntersect()
    {
        var invalid = await CompletedCandidateAsync(
            Lap(4, lapNumber: 2, invalid: true));
        var pit = await CompletedCandidateAsync(
            Lap(4, lapNumber: 2, pitStatus: 1));
        var flashback = await CompletedCandidateAsync(Event(4));
        var gap = await CompletedCandidateAsync(
            CanonicalRecord.Gap(
                4,
                4,
                CanonicalGapReason.UnretainedOrMissingSourceRange),
            completionSequence: 5);

        AssertFlag(invalid, LapEvidenceFlags.InvalidationObserved);
        AssertFlag(pit, LapEvidenceFlags.PitObserved);
        AssertFlag(flashback, LapEvidenceFlags.FlashbackObserved);
        AssertFlag(gap, LapEvidenceFlags.MaterialGapObserved);
    }

    [TestMethod]
    public async Task ContextChangesUnsupportedContextAndMissingContextFailClosed()
    {
        var changed = await CompletedCandidateAsync(
            Session(
                4,
                BahrainTelemetryContextTests.Session(
                    trackTemperatureCelsius: 31)));
        var unsupported = await AssembleAsync(
        [
            Session(1, BahrainTelemetryContextTests.Session(trackId: 4)),
            Lap(2, 1),
            Lap(3, 2, 90_000),
            Lap(4, 3, 91_000),
        ]);
        var missing = await AssembleAsync(
        [
            Lap(1, 1),
            Lap(2, 2, 90_000),
            Lap(3, 3, 91_000),
        ]);

        AssertFlag(changed, LapEvidenceFlags.ContextChanged);
        AssertFlag(
            unsupported.Candidates[1],
            LapEvidenceFlags.UnsupportedContext);
        Assert.IsNull(unsupported.ReferenceContext);
        AssertFlag(missing.Candidates[1], LapEvidenceFlags.MissingContext);
        Assert.IsNull(missing.ReferenceContext);
    }

    [TestMethod]
    public async Task PlayerIndexChangeFlagsBothCompletionProofAndNewLapContext()
    {
        var inventory = await AssembleAsync(
        [
            Session(1),
            Lap(2, 1),
            Lap(3, 2, 90_000),
            Lap(4, 3, 91_000, playerIndex: 8),
        ]);

        AssertFlag(inventory.Candidates[1], LapEvidenceFlags.PlayerIndexChanged);
        AssertFlag(inventory.Candidates[2], LapEvidenceFlags.PlayerIndexChanged);
    }

    [TestMethod]
    public async Task InterleavedExcludedRecordsDoNotCreateTelemetryFacts()
    {
        var inventory = await AssembleAsync(
        [
            Session(1),
            Lap(2, 1),
            Lap(3, 2, 90_000),
            CanonicalRecord.Exclusion(
                4,
                packetId: 7,
                CanonicalExclusionReason.CompatibleFamilyOutsideSlice),
            Lap(5, 3, 91_000),
        ]);

        Assert.AreEqual(LapEvidenceFlags.None, inventory.Candidates[1].EvidenceFlags);
        Assert.AreEqual(3L, inventory.Candidates[1].Boundary.StartSourceSequence);
        Assert.AreEqual(5L, inventory.Candidates[1].Boundary.EndSourceSequenceExclusive);
    }

    [TestMethod]
    public async Task MalformedCoverageCompletionMismatchAndCancellationAreRejected()
    {
        CanonicalRecord[] reordered = [Session(1), Lap(3, 1)];
        await Assert.ThrowsExactlyAsync<BahrainLapAssemblyException>(() =>
            AssembleAsync(reordered));

        CanonicalRecord[] records = [Session(1), Lap(2, 1)];
        var wrongCompletion = Completion(records) with { };
        wrongCompletion = new CanonicalCacheCompletion(
            wrongCompletion.Identity,
            wrongCompletion.SourceStopwatchFrequency,
            wrongCompletion.DataLengthBytes,
            wrongCompletion.DataSha256,
            wrongCompletion.CanonicalSha256,
            recordCount: 3,
            observationCount: 3,
            exclusionCount: 0,
            gapCount: 0,
            firstSourceSequence: 1,
            lastSourceSequence: 2);
        await Assert.ThrowsExactlyAsync<BahrainLapAssemblyException>(() =>
            BahrainLapCandidateAssembler.AssembleAsync(
                wrongCompletion,
                AsAsync(records),
                TestContext.CancellationToken));

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            BahrainLapCandidateAssembler.AssembleAsync(
                Completion(records),
                AsAsync(records),
                canceled.Token));
    }

    [TestMethod]
    public async Task IdenticalInputsProduceIdenticalInventoryAndCandidateIds()
    {
        CanonicalRecord[] records =
        [
            Session(1),
            Lap(2, 1),
            Lap(3, 2, 90_000),
            Lap(4, 3, 91_000),
        ];

        var first = await AssembleAsync(records);
        var second = await AssembleAsync(records);

        Assert.AreEqual(first.ReferenceContext, second.ReferenceContext);
        CollectionAssert.AreEqual(
            first.Candidates.Select(candidate => candidate.CandidateId.Value).ToArray(),
            second.Candidates.Select(candidate => candidate.CandidateId.Value).ToArray());
        CollectionAssert.AreEqual(
            first.Candidates.Select(candidate => candidate.Boundary).ToArray(),
            second.Candidates.Select(candidate => candidate.Boundary).ToArray());
    }

    [TestMethod]
    public async Task CandidateInventoryIsBoundedAgainstHostileTransitions()
    {
        var records = new List<CanonicalRecord> { Session(1), Lap(2, 1) };
        for (var index = 0;
             index <= BahrainLapAuditContract.MaximumInventoryCandidates;
             index++)
        {
            records.Add(Lap(
                sequence: index + 3L,
                lapNumber: index % 2 == 0 ? (byte)2 : (byte)1,
                lastLapMilliseconds: 90_000));
        }

        var exception = await Assert.ThrowsExactlyAsync<BahrainLapAssemblyException>(() =>
            AssembleAsync(records));

        Assert.AreEqual(
            BahrainLapAssemblyFailureKind.CandidateLimitExceeded,
            exception.Kind);
    }

    public TestContext TestContext { get; set; }

    private static async Task<BahrainLapCandidate> CompletedCandidateAsync(
        CanonicalRecord middle,
        long completionSequence = 5)
    {
        var records = new List<CanonicalRecord>
        {
            Session(1),
            Lap(2, 1),
            Lap(3, 2, 90_000),
            middle,
            Lap(completionSequence, 3, 91_000),
        };
        var inventory = await AssembleAsync(records);
        return inventory.Candidates.Single(candidate =>
            candidate.Boundary.Completeness == LapBoundaryCompleteness.Complete);
    }

    private static void AssertFlag(
        BahrainLapCandidate candidate,
        LapEvidenceFlags flag) =>
        Assert.IsTrue(candidate.EvidenceFlags.HasFlag(flag), candidate.CandidateId.Value);

    private static void AssertBoundary(
        BahrainLapCandidate candidate,
        LapBoundaryCompleteness completeness,
        long start,
        long end,
        byte lapNumber,
        uint? officialTime = null)
    {
        Assert.AreEqual(completeness, candidate.Boundary.Completeness);
        Assert.AreEqual(start, candidate.Boundary.StartSourceSequence);
        Assert.AreEqual(end, candidate.Boundary.EndSourceSequenceExclusive);
        Assert.AreEqual(lapNumber, candidate.Boundary.LapNumber);
        Assert.AreEqual(officialTime, candidate.Boundary.OfficialLapTimeMilliseconds);
    }

    private static Task<BahrainLapInventory> AssembleAsync(
        IReadOnlyCollection<CanonicalRecord> records) =>
        BahrainLapCandidateAssembler.AssembleAsync(
            Completion(records),
            AsAsync(records),
            CancellationToken.None);

    private static CanonicalCacheCompletion Completion(
        IReadOnlyCollection<CanonicalRecord> records)
    {
        var sourceSequences = records
            .Where(record => record.SourceSequence.HasValue)
            .Select(record => record.SourceSequence!.Value)
            .ToArray();
        return new(
            Canonical.CanonicalReplayIdentityTests.Identity(),
            sourceStopwatchFrequency: 10_000_000,
            dataLengthBytes: 512,
            dataSha256: new string('a', 64),
            canonicalSha256: new string('b', 64),
            recordCount: records.Count,
            observationCount: records.Count(record =>
                record.Kind == CanonicalRecordKind.Observation),
            exclusionCount: records.Count(record =>
                record.Kind == CanonicalRecordKind.Exclusion),
            gapCount: records.Count(record =>
                record.Kind == CanonicalRecordKind.Gap),
            firstSourceSequence: sourceSequences.Length == 0
                ? null
                : sourceSequences.Min(),
            lastSourceSequence: sourceSequences.Length == 0
                ? null
                : sourceSequences.Max());
    }

    private static CanonicalRecord Session(
        long sequence,
        CanonicalSessionPacket? value = null) =>
        CanonicalRecord.Observation(
            sequence,
            sequence,
            CanonicalPacket.CreateSession(
                Header(playerIndex: 7),
                value ?? BahrainTelemetryContextTests.Session()));

    private static CanonicalRecord Lap(
        long sequence,
        byte lapNumber,
        uint lastLapMilliseconds = 0,
        bool invalid = false,
        byte pitStatus = 0,
        byte playerIndex = 7) =>
        CanonicalRecord.Observation(
            sequence,
            sequence,
            CanonicalPacket.CreateLapData(
                Header(playerIndex),
                new CanonicalLapPacket(
                    lastLapMilliseconds,
                    currentLapTimeMilliseconds: 1_000,
                    lapDistanceMetres: 10,
                    totalDistanceMetres: 10,
                    lapNumber,
                    pitStatus,
                    sector: 0,
                    currentLapInvalid: invalid,
                    driverStatus: 1,
                    resultStatus: 2)));

    private static CanonicalRecord Event(long sequence) =>
        CanonicalRecord.Observation(
            sequence,
            sequence,
            CanonicalPacket.CreateEvent(
                Header(playerIndex: 7),
                CanonicalEventPacket.Flashback(
                    frameIdentifier: 1,
                    sessionTimeSeconds: 1)));

    private static CanonicalPacketHeader Header(byte playerIndex) =>
        new(
            sessionTimeSeconds: 1,
            frameIdentifier: 1,
            overallFrameIdentifier: 1,
            playerIndex,
            secondaryPlayerCarIndex: byte.MaxValue);

    private static async IAsyncEnumerable<CanonicalRecord> AsAsync(
        IEnumerable<CanonicalRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }
}
