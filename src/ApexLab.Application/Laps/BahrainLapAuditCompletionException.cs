namespace ApexLab.Application.Laps;

public enum BahrainLapAuditCompletionFailureKind
{
    NotPendingTemplate = 1,
    ManualContextNotConfirmed = 2,
    RequiresIndividualReview = 3,
}

public sealed class BahrainLapAuditCompletionException : Exception
{
    public BahrainLapAuditCompletionException(
        BahrainLapAuditCompletionFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BahrainLapAuditCompletionFailureKind Kind { get; }
}
