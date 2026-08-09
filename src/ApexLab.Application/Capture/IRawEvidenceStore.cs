using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Capture;

public interface IRawEvidenceStore : IAsyncDisposable
{
    RawEvidenceCaptureId CaptureId { get; }

    RawEvidenceProtocolId ProtocolId { get; }

    RawEvidenceLimits Limits { get; }

    ValueTask WriteAsync(
        DatagramEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<RawEvidenceCompletion> FinalizeAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RawEvidenceCompletion
{
    public RawEvidenceCompletion(
        RawEvidenceCaptureId captureId,
        RawEvidenceProtocolId protocolId,
        long recordCount,
        long dataLengthBytes,
        string sha256,
        DateTimeOffset finalizedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        ArgumentNullException.ThrowIfNull(protocolId);

        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            dataLengthBytes,
            RawEvidenceLimits.MinimumFileBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            dataLengthBytes,
            RawEvidenceLimits.AbsoluteMaximumFileBytes);
        if (sha256 is null
            || sha256.Length != 64
            || sha256.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 value is required.",
                nameof(sha256));
        }

        if (finalizedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Finalization time must be expressed as UTC.",
                nameof(finalizedAtUtc));
        }

        CaptureId = captureId;
        ProtocolId = protocolId;
        RecordCount = recordCount;
        DataLengthBytes = dataLengthBytes;
        Sha256 = sha256;
        FinalizedAtUtc = finalizedAtUtc;
    }

    public RawEvidenceCaptureId CaptureId { get; }

    public RawEvidenceProtocolId ProtocolId { get; }

    public long RecordCount { get; }

    public long DataLengthBytes { get; }

    public string Sha256 { get; }

    public DateTimeOffset FinalizedAtUtc { get; }

}
