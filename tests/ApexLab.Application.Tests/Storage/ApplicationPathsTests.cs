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
    public void FromLocalApplicationData_DoesNotTouchFileSystem()
    {
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-path-purity-{Guid.NewGuid():N}",
            "local-app-data");
        Assert.IsFalse(Directory.Exists(localApplicationData));

        _ = ApplicationPaths.FromLocalApplicationData(localApplicationData);

        Assert.IsFalse(Directory.Exists(localApplicationData));
    }

    [TestMethod]
    public void FromLocalApplicationData_RejectsUnsafeInputs()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        var traversalPath = Path.Combine(root, "safe", "..", "local-app-data");
        var invalidPaths = new[]
        {
            string.Empty,
            "   ",
            "relative-path",
            root,
            traversalPath,
        };

        foreach (var invalidPath in invalidPaths)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => ApplicationPaths.FromLocalApplicationData(invalidPath),
                $"Expected '{invalidPath}' to be rejected.");
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
}
