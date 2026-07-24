namespace ApexLab.Application.Capture;

public sealed record CaptureIngestionCounters
{
    public CaptureIngestionCounters(
        DatagramSourceCounters source,
        DatagramClassificationCounters classifier)
    {
        var accounted = CounterMath.CheckedAdd(
            classifier.SourceDequeued,
            classifier.ClassifierAbandonedOnInterrupt,
            nameof(classifier));
        if (accounted > source.SourceEnqueued)
        {
            throw new ArgumentException(
                "Classifier outcomes and abandonment cannot exceed enqueued datagrams.",
                nameof(classifier));
        }

        Source = source;
        Classifier = classifier;
        EnqueuedAwaitingClassifier = source.SourceEnqueued - accounted;
    }

    public DatagramSourceCounters Source { get; }

    public DatagramClassificationCounters Classifier { get; }

    public long EnqueuedAwaitingClassifier { get; }

    public bool HasCompleteSourceAccounting => EnqueuedAwaitingClassifier == 0;
}
