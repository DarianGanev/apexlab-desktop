namespace ApexLab.Persistence.Raw;

public enum RawEvidenceReadFailureKind
{
    MissingOrIncomplete,
    UnsafePath,
    UnsupportedVersion,
    MalformedStructure,
    DeclaredLimitViolation,
    TruncatedData,
    TrailingData,
    HashMismatch,
    UnsupportedProtocol,
}

public sealed class RawEvidenceReadException : IOException
{
    internal RawEvidenceReadException(
        RawEvidenceReadFailureKind kind,
        Exception? innerException = null)
        : base(MessageFor(kind), innerException)
    {
        Kind = kind;
    }

    public RawEvidenceReadFailureKind Kind { get; }

    private static string MessageFor(
        RawEvidenceReadFailureKind kind) =>
        kind switch
        {
            RawEvidenceReadFailureKind.MissingOrIncomplete =>
                "Raw evidence is missing or incomplete.",
            RawEvidenceReadFailureKind.UnsafePath =>
                "The raw evidence path could not be trusted.",
            RawEvidenceReadFailureKind.UnsupportedVersion =>
                "The raw evidence format version is unsupported.",
            RawEvidenceReadFailureKind.MalformedStructure =>
                "The raw evidence structure is malformed.",
            RawEvidenceReadFailureKind.DeclaredLimitViolation =>
                "The raw evidence violates a declared safety limit.",
            RawEvidenceReadFailureKind.TruncatedData =>
                "The raw evidence data is truncated.",
            RawEvidenceReadFailureKind.TrailingData =>
                "The raw evidence contains trailing data.",
            RawEvidenceReadFailureKind.HashMismatch =>
                "The raw evidence integrity check failed.",
            RawEvidenceReadFailureKind.UnsupportedProtocol =>
                "The raw evidence protocol is unsupported.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
