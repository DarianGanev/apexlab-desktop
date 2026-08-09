namespace ApexLab.Application.Capture;

public readonly record struct DatagramSourceCounters
{
    public DatagramSourceCounters(
        long datagramsObserved,
        long sourceEnqueued,
        long sourceDroppedFull,
        long sourceRejectedOversized,
        long socketErrors)
    {
        CounterMath.RequireNonNegative(datagramsObserved, nameof(datagramsObserved));
        CounterMath.RequireNonNegative(sourceEnqueued, nameof(sourceEnqueued));
        CounterMath.RequireNonNegative(sourceDroppedFull, nameof(sourceDroppedFull));
        CounterMath.RequireNonNegative(
            sourceRejectedOversized,
            nameof(sourceRejectedOversized));
        CounterMath.RequireNonNegative(socketErrors, nameof(socketErrors));

        var accounted = CounterMath.CheckedAdd(
            CounterMath.CheckedAdd(
                sourceEnqueued,
                sourceDroppedFull,
                nameof(sourceDroppedFull)),
            sourceRejectedOversized,
            nameof(sourceRejectedOversized));
        if (datagramsObserved != accounted)
        {
            throw new ArgumentException(
                "Observed datagrams must equal enqueued, full-drop, and oversized outcomes.",
                nameof(datagramsObserved));
        }

        DatagramsObserved = datagramsObserved;
        SourceEnqueued = sourceEnqueued;
        SourceDroppedFull = sourceDroppedFull;
        SourceRejectedOversized = sourceRejectedOversized;
        SocketErrors = socketErrors;
    }

    public long DatagramsObserved { get; }

    public long SourceEnqueued { get; }

    public long SourceDroppedFull { get; }

    public long SourceRejectedOversized { get; }

    public long SocketErrors { get; }
}

public readonly record struct DatagramClassificationCounters
{
    public DatagramClassificationCounters(
        long sourceDequeued,
        long compatible,
        long malformedHeader,
        long unsupportedFormat,
        long unsupportedYear,
        long unknownPacketId,
        long unsupportedPacketVersion,
        long invalidPacketLength,
        long excludedPrivacyPacket,
        long unexpectedSender,
        long classifierAbandonedOnTermination)
    {
        CounterMath.RequireNonNegative(sourceDequeued, nameof(sourceDequeued));
        CounterMath.RequireNonNegative(compatible, nameof(compatible));
        CounterMath.RequireNonNegative(malformedHeader, nameof(malformedHeader));
        CounterMath.RequireNonNegative(unsupportedFormat, nameof(unsupportedFormat));
        CounterMath.RequireNonNegative(unsupportedYear, nameof(unsupportedYear));
        CounterMath.RequireNonNegative(unknownPacketId, nameof(unknownPacketId));
        CounterMath.RequireNonNegative(
            unsupportedPacketVersion,
            nameof(unsupportedPacketVersion));
        CounterMath.RequireNonNegative(invalidPacketLength, nameof(invalidPacketLength));
        CounterMath.RequireNonNegative(
            excludedPrivacyPacket,
            nameof(excludedPrivacyPacket));
        CounterMath.RequireNonNegative(unexpectedSender, nameof(unexpectedSender));
        CounterMath.RequireNonNegative(
            classifierAbandonedOnTermination,
            nameof(classifierAbandonedOnTermination));

        var classified = CounterMath.CheckedAdd(compatible, malformedHeader, nameof(malformedHeader));
        classified = CounterMath.CheckedAdd(
            classified,
            unsupportedFormat,
            nameof(unsupportedFormat));
        classified = CounterMath.CheckedAdd(
            classified,
            unsupportedYear,
            nameof(unsupportedYear));
        classified = CounterMath.CheckedAdd(
            classified,
            unknownPacketId,
            nameof(unknownPacketId));
        classified = CounterMath.CheckedAdd(
            classified,
            unsupportedPacketVersion,
            nameof(unsupportedPacketVersion));
        classified = CounterMath.CheckedAdd(
            classified,
            invalidPacketLength,
            nameof(invalidPacketLength));
        classified = CounterMath.CheckedAdd(
            classified,
            excludedPrivacyPacket,
            nameof(excludedPrivacyPacket));
        classified = CounterMath.CheckedAdd(
            classified,
            unexpectedSender,
            nameof(unexpectedSender));
        if (sourceDequeued != classified)
        {
            throw new ArgumentException(
                "Dequeued datagrams must have exactly one classifier outcome.",
                nameof(sourceDequeued));
        }

        SourceDequeued = sourceDequeued;
        Compatible = compatible;
        MalformedHeader = malformedHeader;
        UnsupportedFormat = unsupportedFormat;
        UnsupportedYear = unsupportedYear;
        UnknownPacketId = unknownPacketId;
        UnsupportedPacketVersion = unsupportedPacketVersion;
        InvalidPacketLength = invalidPacketLength;
        ExcludedPrivacyPacket = excludedPrivacyPacket;
        UnexpectedSender = unexpectedSender;
        ClassifierAbandonedOnTermination = classifierAbandonedOnTermination;
    }

    public long SourceDequeued { get; }

    public long Compatible { get; }

    public long MalformedHeader { get; }

    public long UnsupportedFormat { get; }

    public long UnsupportedYear { get; }

    public long UnknownPacketId { get; }

    public long UnsupportedPacketVersion { get; }

    public long InvalidPacketLength { get; }

    public long ExcludedPrivacyPacket { get; }

    public long UnexpectedSender { get; }

    public long ClassifierAbandonedOnTermination { get; }

    public bool WasAbandonedOnTermination =>
        ClassifierAbandonedOnTermination != 0;
}

public readonly record struct EvidenceSinkCounters
{
    public EvidenceSinkCounters(
        long sinkWritten,
        long sinkWriteFailed,
        long sinkPending,
        long sinkPendingDeferredCleanup,
        long finalizedRecords,
        long stagedRecords)
    {
        CounterMath.RequireNonNegative(sinkWritten, nameof(sinkWritten));
        CounterMath.RequireNonNegative(sinkWriteFailed, nameof(sinkWriteFailed));
        CounterMath.RequireNonNegative(sinkPending, nameof(sinkPending));
        CounterMath.RequireNonNegative(
            sinkPendingDeferredCleanup,
            nameof(sinkPendingDeferredCleanup));
        CounterMath.RequireNonNegative(finalizedRecords, nameof(finalizedRecords));
        CounterMath.RequireNonNegative(stagedRecords, nameof(stagedRecords));
        if (sinkPending != 0 && sinkPendingDeferredCleanup != 0)
        {
            throw new ArgumentException(
                "Pending writes must be either active or transferred to deferred cleanup.",
                nameof(sinkPendingDeferredCleanup));
        }
        if (finalizedRecords != 0 && stagedRecords != 0)
        {
            throw new ArgumentException(
                "Written records must be entirely staged or entirely finalized.",
                nameof(finalizedRecords));
        }
        if (finalizedRecords != 0 &&
            (sinkPending != 0 || sinkPendingDeferredCleanup != 0))
        {
            throw new ArgumentException(
                "Finalized evidence cannot retain unresolved writes.",
                nameof(finalizedRecords));
        }

        var accountedWritten = CounterMath.CheckedAdd(
            finalizedRecords,
            stagedRecords,
            nameof(stagedRecords));
        if (sinkWritten != accountedWritten)
        {
            throw new ArgumentException(
                "Written records must be either staged or finalized.",
                nameof(sinkWritten));
        }

        var accountedCompatible = CounterMath.CheckedAdd(
            sinkWritten,
            sinkWriteFailed,
            nameof(sinkWriteFailed));
        accountedCompatible = CounterMath.CheckedAdd(
            accountedCompatible,
            sinkPending,
            nameof(sinkPending));
        accountedCompatible = CounterMath.CheckedAdd(
            accountedCompatible,
            sinkPendingDeferredCleanup,
            nameof(sinkPendingDeferredCleanup));

        SinkWritten = sinkWritten;
        SinkWriteFailed = sinkWriteFailed;
        SinkPending = sinkPending;
        SinkPendingDeferredCleanup = sinkPendingDeferredCleanup;
        FinalizedRecords = finalizedRecords;
        StagedRecords = stagedRecords;
        AccountedCompatible = accountedCompatible;
    }

    public long SinkWritten { get; }

    public long SinkWriteFailed { get; }

    public long SinkPending { get; }

    public long SinkPendingDeferredCleanup { get; }

    public long FinalizedRecords { get; }

    public long StagedRecords { get; }

    public long AccountedCompatible { get; }

    public bool HasPendingWrites =>
        SinkPending != 0 || SinkPendingDeferredCleanup != 0;

    public bool HasDeferredCleanup => SinkPendingDeferredCleanup != 0;
}

public sealed record CaptureCounters
{
    public CaptureCounters(
        DatagramSourceCounters source,
        DatagramClassificationCounters classifier,
        EvidenceSinkCounters evidence)
    {
        var classifierAccounted = CounterMath.CheckedAdd(
            classifier.SourceDequeued,
            classifier.ClassifierAbandonedOnTermination,
            nameof(classifier));
        if (classifierAccounted > source.SourceEnqueued)
        {
            throw new ArgumentException(
                "Classifier outcomes and abandonment cannot exceed enqueued datagrams.",
                nameof(classifier));
        }

        if (classifier.Compatible != evidence.AccountedCompatible)
        {
            throw new ArgumentException(
                "Every compatible datagram must have exactly one evidence-sink disposition.",
                nameof(evidence));
        }

        var enqueuedAwaitingClassifier = source.SourceEnqueued - classifierAccounted;
        if (classifier.WasAbandonedOnTermination && enqueuedAwaitingClassifier != 0)
        {
            throw new ArgumentException(
                "A terminated classifier snapshot must transfer the entire backlog to abandonment.",
                nameof(classifier));
        }
        if ((evidence.HasDeferredCleanup || evidence.FinalizedRecords != 0) &&
            enqueuedAwaitingClassifier != 0)
        {
            throw new ArgumentException(
                "Deferred or finalized evidence requires complete source accounting.",
                nameof(evidence));
        }

        Source = source;
        Classifier = classifier;
        Evidence = evidence;
        EnqueuedAwaitingClassifier = enqueuedAwaitingClassifier;
    }

    public DatagramSourceCounters Source { get; }

    public DatagramClassificationCounters Classifier { get; }

    public EvidenceSinkCounters Evidence { get; }

    public long EnqueuedAwaitingClassifier { get; }

    public bool HasCompleteSourceAccounting => EnqueuedAwaitingClassifier == 0;

    public bool AllWrittenRecordsAreFinalized =>
        !Evidence.HasPendingWrites && Evidence.StagedRecords == 0;
}

internal static class CounterMath
{
    public static void RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A capture counter cannot be negative.");
        }
    }

    public static long CheckedAdd(long left, long right, string parameterName)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Capture counter accounting exceeded Int64 capacity.");
        }
    }
}
