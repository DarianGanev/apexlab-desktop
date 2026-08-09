namespace ApexLab.Application.Capture;

public enum RawEvidenceLimitKind
{
    FileSize,
    Duration,
    FreeSpace,
}

public sealed class RawEvidenceLimitReachedException : Exception
{
    public RawEvidenceLimitReachedException(
        RawEvidenceLimitKind kind)
        : base(MessageFor(kind))
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
    }

    public RawEvidenceLimitKind Kind { get; }

    private static string MessageFor(RawEvidenceLimitKind kind) =>
        kind switch
        {
            RawEvidenceLimitKind.FileSize =>
                "The raw evidence file-size limit was reached.",
            RawEvidenceLimitKind.Duration =>
                "The raw evidence duration limit was reached.",
            RawEvidenceLimitKind.FreeSpace =>
                "The raw evidence free-space floor was reached.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
