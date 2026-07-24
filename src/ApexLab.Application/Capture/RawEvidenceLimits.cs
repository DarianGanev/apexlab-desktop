using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Capture;

public sealed class RawEvidenceLimits
{
    public const long MinimumFileBytes = 136;
    public const long AbsoluteMaximumFileBytes = 536_870_912;
    public const long DefaultMinimumFreeSpaceBytes = 1_073_741_824;
    public static readonly TimeSpan AbsoluteMaximumDuration =
        TimeSpan.FromMinutes(15);

    public RawEvidenceLimits(
        TimeSpan? maximumDuration = null,
        long maximumFileBytes = AbsoluteMaximumFileBytes,
        long minimumFreeSpaceBytes = DefaultMinimumFreeSpaceBytes,
        int maximumPayloadBytes = UdpDatagramLimits.MaximumPayloadLength)
    {
        var resolvedDuration =
            maximumDuration ?? AbsoluteMaximumDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            resolvedDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            resolvedDuration,
            AbsoluteMaximumDuration);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumFileBytes,
            MinimumFileBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumFileBytes,
            AbsoluteMaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(
            minimumFreeSpaceBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumPayloadBytes,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumPayloadBytes,
            UdpDatagramLimits.MaximumPayloadLength);

        MaximumDuration = resolvedDuration;
        MaximumFileBytes = maximumFileBytes;
        MinimumFreeSpaceBytes = minimumFreeSpaceBytes;
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    public TimeSpan MaximumDuration { get; }

    public long MaximumFileBytes { get; }

    public long MinimumFreeSpaceBytes { get; }

    public int MaximumPayloadBytes { get; }
}
