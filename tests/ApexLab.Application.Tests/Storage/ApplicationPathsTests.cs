using ApexLab.Application.Storage;

namespace ApexLab.Application.Tests.Storage;

[TestClass]
public sealed class ApplicationPathsTests
{
    [TestMethod]
    public void FromLocalApplicationData_CreatesNormalizedPathsBelowSuppliedDirectory()
    {
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-path-contract-{Guid.NewGuid():N}",
            "local-app-data") + Path.DirectorySeparatorChar;

        var paths = ApplicationPaths.FromLocalApplicationData(localApplicationData);

        var expectedRoot = Path.GetFullPath(Path.Combine(localApplicationData, "ApexLab"));
        Assert.AreEqual(expectedRoot, paths.RootDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "apexlab.db"), paths.DatabaseFile);
        Assert.AreEqual(Path.Combine(expectedRoot, "captures"), paths.RawCapturesDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "cache"), paths.DerivedCacheDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "logs"), paths.LogsDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "backups"), paths.BackupsDirectory);
        Assert.AreEqual(Path.Combine(expectedRoot, "import-staging"), paths.ImportStagingDirectory);
    }

    [TestMethod]
    public void Factories_DoNotTouchFileSystem()
    {
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-path-purity-{Guid.NewGuid():N}",
            "local-app-data");
        Assert.IsFalse(Directory.Exists(localApplicationData));

        _ = ApplicationPaths.FromLocalApplicationData(localApplicationData);
        _ = ApplicationPaths.FromRoot(localApplicationData);

        Assert.IsFalse(Directory.Exists(localApplicationData));
    }

    [TestMethod]
    public void Factories_RejectEmptyRelativeAndLocalRootOnlyInputs()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        var invalidPaths = new[]
        {
            string.Empty,
            "   ",
            "relative-path",
            root,
        };

        foreach (var invalidPath in invalidPaths)
        {
            AssertBothFactoriesReject(invalidPath);
        }
    }

    [TestMethod]
    public void Factories_RejectWindowsDeviceNtAndNetworkNamespacePaths()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "ApexLab desktop path policy is Windows-specific.");

        var invalidPaths = new[]
        {
            @"\\?\C:\",
            @"\\?\C:\apexlab-data",
            @"//?/C:/",
            @"//?/C:/apexlab-data",
            @"\\.\C:\",
            @"\\.\C:\apexlab-data",
            @"//./C:/apexlab-data",
            @"\??\C:\",
            @"\??\C:\apexlab-data",
            @"\\??\C:\apexlab-data",
            @"\\?\UNC\server\share\",
            @"\\?\UNC\server\share\apexlab-data",
            @"\\server\share\",
            @"\\server\share\apexlab-data",
        };

        foreach (var invalidPath in invalidPaths)
        {
            AssertBothFactoriesReject(invalidPath);
        }
    }

    [TestMethod]
    public void Factories_RejectMixedSeparatorAndWindowsNormalizedTraversalSegments()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "ApexLab desktop path policy is Windows-specific.");

        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        var invalidPaths = new[]
        {
            $@"{root}safe/../apexlab-data",
            $@"{root}safe\../apexlab-data",
            $@"{root}safe/..\apexlab-data",
            $@"{root}safe\.. \apexlab-data",
            $@"{root}safe\.. .\apexlab-data",
            $@"{root}safe\... \apexlab-data",
        };

        foreach (var invalidPath in invalidPaths)
        {
            AssertBothFactoriesReject(invalidPath);
        }
    }

    [TestMethod]
    public void FromRoot_UsesTheSuppliedNormalizedDataRoot()
    {
        var suppliedRoot = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-data-root-{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;

        var paths = ApplicationPaths.FromRoot(suppliedRoot);

        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedRoot)), paths.RootDirectory);
    }

    private static void AssertBothFactoriesReject(string invalidPath)
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ApplicationPaths.FromLocalApplicationData(invalidPath),
            $"Expected local-application-data factory to reject '{invalidPath}'.");
        Assert.ThrowsExactly<ArgumentException>(
            () => ApplicationPaths.FromRoot(invalidPath),
            $"Expected data-root factory to reject '{invalidPath}'.");
    }
}
