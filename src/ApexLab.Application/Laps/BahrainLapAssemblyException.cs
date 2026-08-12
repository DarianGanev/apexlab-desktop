namespace ApexLab.Application.Laps;

public enum BahrainLapAssemblyFailureKind
{
    MalformedCanonicalOrder = 1,
    CompletionMismatch = 2,
    SequenceExhausted = 3,
    CandidateLimitExceeded = 4,
}

public sealed class BahrainLapAssemblyException : Exception
{
    public BahrainLapAssemblyException(
        BahrainLapAssemblyFailureKind kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }

    public BahrainLapAssemblyFailureKind Kind { get; }
}
