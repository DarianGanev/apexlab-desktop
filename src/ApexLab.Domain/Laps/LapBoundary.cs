namespace ApexLab.Domain.Laps;

public sealed record LapBoundary
{
    private LapBoundary(
        LapBoundaryCompleteness completeness,
        long startSourceSequence,
        long endSourceSequenceExclusive,
        long? completionEvidenceSourceSequence,
        byte? lapNumber,
        uint? officialLapTimeMilliseconds)
    {
        if (!Enum.IsDefined(completeness))
        {
            throw new ArgumentOutOfRangeException(nameof(completeness));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(startSourceSequence, 1);
        if (endSourceSequenceExclusive <= startSourceSequence)
        {
            throw new ArgumentException(
                "A lap boundary must contain a non-empty half-open source range.",
                nameof(endSourceSequenceExclusive));
        }

        if (lapNumber is 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lapNumber));
        }

        if (completeness == LapBoundaryCompleteness.Complete)
        {
            if (completionEvidenceSourceSequence != endSourceSequenceExclusive)
            {
                throw new ArgumentException(
                    "A complete boundary must end at its completion evidence.",
                    nameof(completionEvidenceSourceSequence));
            }

            if (!lapNumber.HasValue)
            {
                throw new ArgumentNullException(nameof(lapNumber));
            }

            if (officialLapTimeMilliseconds is null or 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(officialLapTimeMilliseconds));
            }
        }
        else if (completionEvidenceSourceSequence.HasValue
                 || officialLapTimeMilliseconds.HasValue)
        {
            throw new ArgumentException(
                "An incomplete boundary cannot claim completion evidence.",
                nameof(completionEvidenceSourceSequence));
        }

        Completeness = completeness;
        StartSourceSequence = startSourceSequence;
        EndSourceSequenceExclusive = endSourceSequenceExclusive;
        CompletionEvidenceSourceSequence = completionEvidenceSourceSequence;
        LapNumber = lapNumber;
        OfficialLapTimeMilliseconds = officialLapTimeMilliseconds;
    }

    public LapBoundaryCompleteness Completeness { get; }

    public long StartSourceSequence { get; }

    public long EndSourceSequenceExclusive { get; }

    public long? CompletionEvidenceSourceSequence { get; }

    public byte? LapNumber { get; }

    public uint? OfficialLapTimeMilliseconds { get; }

    public static LapBoundary Complete(
        long startSourceSequence,
        long completionEvidenceSourceSequence,
        byte lapNumber,
        uint officialLapTimeMilliseconds) =>
        new(
            LapBoundaryCompleteness.Complete,
            startSourceSequence,
            completionEvidenceSourceSequence,
            completionEvidenceSourceSequence,
            lapNumber,
            officialLapTimeMilliseconds);

    public static LapBoundary Partial(
        LapBoundaryCompleteness completeness,
        long startSourceSequence,
        long endSourceSequenceExclusive,
        byte? lapNumber) =>
        completeness == LapBoundaryCompleteness.Complete
            ? throw new ArgumentOutOfRangeException(nameof(completeness))
            : new(
                completeness,
                startSourceSequence,
                endSourceSequenceExclusive,
                completionEvidenceSourceSequence: null,
                lapNumber,
                officialLapTimeMilliseconds: null);
}
