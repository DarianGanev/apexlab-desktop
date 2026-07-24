using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class RawEvidenceLimitsTests
{
    [TestMethod]
    public void DefaultsMatchTheReviewedV1Bounds()
    {
        var limits = new RawEvidenceLimits();

        Assert.AreEqual(TimeSpan.FromMinutes(15), limits.MaximumDuration);
        Assert.AreEqual(536_870_912L, limits.MaximumFileBytes);
        Assert.AreEqual(1_073_741_824L, limits.MinimumFreeSpaceBytes);
        Assert.AreEqual(
            UdpDatagramLimits.MaximumPayloadLength,
            limits.MaximumPayloadBytes);
    }

    [TestMethod]
    public void AcceptsEveryInclusiveBoundary()
    {
        var minimum = new RawEvidenceLimits(
            TimeSpan.FromTicks(1),
            RawEvidenceLimits.MinimumFileBytes,
            minimumFreeSpaceBytes: 0,
            maximumPayloadBytes: 1);
        var maximum = new RawEvidenceLimits(
            RawEvidenceLimits.AbsoluteMaximumDuration,
            RawEvidenceLimits.AbsoluteMaximumFileBytes,
            long.MaxValue,
            UdpDatagramLimits.MaximumPayloadLength);

        Assert.AreEqual(TimeSpan.FromTicks(1), minimum.MaximumDuration);
        Assert.AreEqual(RawEvidenceLimits.MinimumFileBytes, minimum.MaximumFileBytes);
        Assert.AreEqual(long.MaxValue, maximum.MinimumFreeSpaceBytes);
    }

    [TestMethod]
    public void RejectsValuesOutsideTheReviewedV1Bounds()
    {
        Action[] invalid =
        [
            () => new RawEvidenceLimits(TimeSpan.Zero),
            () => new RawEvidenceLimits(TimeSpan.FromTicks(-1)),
            () => new RawEvidenceLimits(
                RawEvidenceLimits.AbsoluteMaximumDuration + TimeSpan.FromTicks(1)),
            () => new RawEvidenceLimits(
                maximumFileBytes: RawEvidenceLimits.MinimumFileBytes - 1),
            () => new RawEvidenceLimits(
                maximumFileBytes: RawEvidenceLimits.AbsoluteMaximumFileBytes + 1),
            () => new RawEvidenceLimits(minimumFreeSpaceBytes: -1),
            () => new RawEvidenceLimits(maximumPayloadBytes: 0),
            () => new RawEvidenceLimits(
                maximumPayloadBytes: UdpDatagramLimits.MaximumPayloadLength + 1),
        ];

        foreach (var construct in invalid)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(construct);
        }
    }
}
