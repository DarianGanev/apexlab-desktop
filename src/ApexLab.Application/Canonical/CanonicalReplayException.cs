using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Canonical;

public enum CanonicalReplayFailureKind
{
    Unspecified = 0,
    IdentityMismatch = 1,
    InvalidCacheCompletion = 2,
    ProjectionRejected = 3,
    DuplicateOrReorderedSequence = 4,
    UnexpectedProjection = 5,
}

public sealed class CanonicalReplayException : Exception
{
    public CanonicalReplayException(
        CanonicalReplayFailureKind kind,
        string message,
        CanonicalProjectionReason? projectionReason = null)
        : base(message)
    {
        if (!Enum.IsDefined(kind)
            || kind == CanonicalReplayFailureKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ProjectionReason = projectionReason;
    }

    public CanonicalReplayFailureKind Kind { get; }

    public CanonicalProjectionReason? ProjectionReason { get; }
}
