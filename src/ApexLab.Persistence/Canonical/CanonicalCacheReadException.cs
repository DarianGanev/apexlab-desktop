namespace ApexLab.Persistence.Canonical;

internal enum CanonicalCacheReadFailureKind
{
    Missing = 1,
    MalformedManifest = 2,
    IdentityMismatch = 3,
    MissingData = 4,
    MalformedData = 5,
    IntegrityMismatch = 6,
    UnsafeStorage = 7,
}

internal sealed class CanonicalCacheReadException : Exception
{
    public CanonicalCacheReadException(
        CanonicalCacheReadFailureKind kind,
        string message,
        Exception? innerException = null,
        string? rebuildDataLeafName = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        RebuildDataLeafName = rebuildDataLeafName;
    }

    public CanonicalCacheReadFailureKind Kind { get; }

    public bool IsRebuildable => Kind != CanonicalCacheReadFailureKind.UnsafeStorage;

    public string? RebuildDataLeafName { get; }

    public CanonicalCacheReadException WithRebuildDataLeaf(string dataLeafName) =>
        new(Kind, Message, this, dataLeafName);
}
