using System.Net;
using ApexLab.Application.Configuration;

namespace ApexLab.Application.Tests.Configuration;

[TestClass]
public sealed class ApexLabOptionsTests
{
    private static readonly string ValidDataRoot = Path.Combine(
        Path.GetTempPath(),
        "apexlab-options-tests",
        "data");

    [TestMethod]
    public void Constructor_UsesSafeCaptureDefaults()
    {
        var options = new ApexLabOptions(ValidDataRoot);

        Assert.AreEqual(IPAddress.Loopback.ToString(), options.BindAddress);
        Assert.AreEqual(20777, options.UdpPort);
        Assert.IsFalse(options.AllowLan);
        Assert.AreEqual(8, options.LiveSnapshotRateHz);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.RawChunkDuration);
        Assert.IsGreaterThan(0L, options.StorageQuotaBytes);
    }

    [TestMethod]
    public void WithExpression_ConfiguresQuotaWithoutMutatingOriginal()
    {
        var defaults = new ApexLabOptions(ValidDataRoot);
        const long configuredQuota = 5L * 1024 * 1024 * 1024;

        var configured = defaults with { StorageQuotaBytes = configuredQuota };

        Assert.AreEqual(configuredQuota, configured.StorageQuotaBytes);
        Assert.AreNotEqual(configuredQuota, defaults.StorageQuotaBytes);
    }

    [TestMethod]
    public void Validate_AcceptsDefaultsAndInclusiveSnapshotRateBounds()
    {
        var defaults = new ApexLabOptions(ValidDataRoot);
        var minimumRate = defaults with { LiveSnapshotRateHz = 1 };
        var maximumRate = defaults with { LiveSnapshotRateHz = 20 };

        Assert.IsEmpty(ApexLabOptionsValidator.Validate(defaults));
        Assert.IsEmpty(ApexLabOptionsValidator.Validate(minimumRate));
        Assert.IsEmpty(ApexLabOptionsValidator.Validate(maximumRate));
    }

    [TestMethod]
    public void Validate_ReportsEachInvalidField()
    {
        var options = new ApexLabOptions("relative-data-root")
        {
            BindAddress = "not-an-ip-address",
            UdpPort = 0,
            LiveSnapshotRateHz = 21,
            RawChunkDuration = TimeSpan.Zero,
            StorageQuotaBytes = 0,
        };

        var failures = ApexLabOptionsValidator.Validate(options);
        var fields = failures.Select(failure => failure.FieldName).ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(ApexLabOptions.BindAddress),
                nameof(ApexLabOptions.UdpPort),
                nameof(ApexLabOptions.LiveSnapshotRateHz),
                nameof(ApexLabOptions.RawChunkDuration),
                nameof(ApexLabOptions.DataRootPath),
                nameof(ApexLabOptions.StorageQuotaBytes),
            },
            fields);
        Assert.IsTrue(failures.All(failure => !string.IsNullOrWhiteSpace(failure.Message)));
    }

    [TestMethod]
    public void Validate_RejectsOutOfRangeUdpPorts()
    {
        var defaults = new ApexLabOptions(ValidDataRoot);

        AssertSingleFailure(defaults with { UdpPort = -1 }, nameof(ApexLabOptions.UdpPort));
        AssertSingleFailure(defaults with { UdpPort = 65_536 }, nameof(ApexLabOptions.UdpPort));
    }

    [TestMethod]
    public void Validate_AcceptsInclusiveUdpPortBoundaries()
    {
        var defaults = new ApexLabOptions(ValidDataRoot);

        Assert.IsEmpty(ApexLabOptionsValidator.Validate(defaults with { UdpPort = 1 }));
        Assert.IsEmpty(ApexLabOptionsValidator.Validate(defaults with { UdpPort = 65_535 }));
    }

    [TestMethod]
    public void Validate_RejectsSnapshotRateBelowMinimum()
    {
        var options = new ApexLabOptions(ValidDataRoot) { LiveSnapshotRateHz = 0 };

        AssertSingleFailure(options, nameof(ApexLabOptions.LiveSnapshotRateHz));
    }

    [TestMethod]
    public void Validate_RejectsNegativeRawChunkDuration()
    {
        var options = new ApexLabOptions(ValidDataRoot)
        {
            RawChunkDuration = TimeSpan.FromTicks(-1),
        };

        AssertSingleFailure(options, nameof(ApexLabOptions.RawChunkDuration));
    }

    [TestMethod]
    public void Validate_RejectsNegativeStorageQuota()
    {
        var options = new ApexLabOptions(ValidDataRoot) { StorageQuotaBytes = -1 };

        AssertSingleFailure(options, nameof(ApexLabOptions.StorageQuotaBytes));
    }

    [TestMethod]
    public void Validate_RejectsEmptyDataRoot()
    {
        var options = new ApexLabOptions(string.Empty);

        AssertSingleFailure(options, nameof(ApexLabOptions.DataRootPath));
    }

    [TestMethod]
    public void Validate_RejectsRootOnlyDataPath()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);
        var options = new ApexLabOptions(root);

        AssertSingleFailure(options, nameof(ApexLabOptions.DataRootPath));
    }

    [TestMethod]
    public void Validate_RejectsWindowsReservedAndInvalidFilenameComponents()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "ApexLab desktop path policy is Windows-specific.");

        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        foreach (var component in WindowsPathComponentCases.UnsafeComponents())
        {
            var invalidPath = Path.Combine(root, "safe", component, "apexlab-data");
            var options = new ApexLabOptions(invalidPath);

            AssertSingleFailure(options, nameof(ApexLabOptions.DataRootPath));
        }
    }

    [TestMethod]
    public void Validate_AcceptsRepresentativeWindowsDirectoryNames()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "ApexLab desktop path policy is Windows-specific.");

        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        foreach (var component in WindowsPathComponentCases.AcceptedComponents())
        {
            var validPath = Path.Combine(root, "safe", component, "apexlab-data");

            Assert.IsEmpty(ApexLabOptionsValidator.Validate(new ApexLabOptions(validPath)));
        }
    }

    [TestMethod]
    public void Validate_RejectsNonLoopbackAddressWhenLanIsDisabled()
    {
        var options = new ApexLabOptions(ValidDataRoot)
        {
            BindAddress = "192.0.2.10",
            AllowLan = false,
        };

        AssertSingleFailure(options, nameof(ApexLabOptions.BindAddress));
    }

    [TestMethod]
    public void Validate_AcceptsNonLoopbackAddressWhenLanIsEnabled()
    {
        var options = new ApexLabOptions(ValidDataRoot)
        {
            BindAddress = "192.0.2.10",
            AllowLan = true,
        };

        Assert.IsEmpty(ApexLabOptionsValidator.Validate(options));
    }

    [TestMethod]
    public void Validate_RejectsTraversalProneDataRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);
        var options = new ApexLabOptions(Path.Combine(root, "safe", "..", "data"));

        AssertSingleFailure(options, nameof(ApexLabOptions.DataRootPath));
    }

    private static void AssertSingleFailure(ApexLabOptions options, string expectedFieldName)
    {
        var failures = ApexLabOptionsValidator.Validate(options);

        Assert.HasCount(1, failures);
        Assert.AreEqual(expectedFieldName, failures[0].FieldName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failures[0].Message));
    }
}
