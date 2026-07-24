using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;

namespace ApexLab.Persistence.Tests.Raw;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsRawEvidenceDirectoryTests
{
    [TestMethod]
    public async Task CreateAndHandleRenamePreserveTheOpenedFileIdentity()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        using var directory = WindowsRawEvidenceDirectory.Open(
            temporary.Paths);
        const string stagingName =
            "00112233445546778899aabbccddeeff.apxraw.partial";
        const string finalName =
            "00112233445546778899aabbccddeeff.apxraw";

        await using (var stream = directory.CreateNewFile(stagingName))
        {
            await stream.WriteAsync(
                "evidence"u8.ToArray(),
                TestContext.CancellationToken);
            await stream.FlushAsync(TestContext.CancellationToken);
            var before = directory.GetIdentity(stream.SafeFileHandle);

            directory.RenameOpenFile(
                stream.SafeFileHandle,
                finalName);

            var after = directory.GetIdentity(stream.SafeFileHandle);
            Assert.AreEqual(before, after);
            Assert.AreEqual(
                Path.Combine(
                    temporary.Paths.RawCapturesDirectory,
                    finalName),
                directory.GetNormalizedPath(stream.SafeFileHandle),
                ignoreCase: true);
        }

        Assert.IsFalse(File.Exists(Path.Combine(
            temporary.Paths.RawCapturesDirectory,
            stagingName)));
        Assert.AreEqual(
            "evidence",
            await File.ReadAllTextAsync(
                Path.Combine(
                    temporary.Paths.RawCapturesDirectory,
                    finalName),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public void CreateNewAndRenameNeverOverwriteExistingNames()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        using var directory = WindowsRawEvidenceDirectory.Open(
            temporary.Paths);
        const string stagingName =
            "00112233445546778899aabbccddeeff.apxraw.partial";
        const string finalName =
            "00112233445546778899aabbccddeeff.apxraw";
        using var first = directory.CreateNewFile(stagingName);

        Assert.ThrowsExactly<Win32Exception>(
            () => directory.CreateNewFile(stagingName));
        File.WriteAllText(
            Path.Combine(
                temporary.Paths.RawCapturesDirectory,
                finalName),
            "occupied",
            Encoding.UTF8);

        Assert.ThrowsExactly<Win32Exception>(
            () => directory.RenameOpenFile(
                first.SafeFileHandle,
                finalName));
        Assert.IsTrue(File.Exists(Path.Combine(
            temporary.Paths.RawCapturesDirectory,
            stagingName)));
        Assert.AreEqual(
            "occupied",
            File.ReadAllText(Path.Combine(
                temporary.Paths.RawCapturesDirectory,
                finalName)));
    }

    [TestMethod]
    public void RetainedHandlesPreventApplicationRootReplacement()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        using var directory = WindowsRawEvidenceDirectory.Open(
            temporary.Paths);
        var movedPath = $"{temporary.Paths.RootDirectory}-moved";

        Assert.ThrowsExactly<IOException>(
            () => Directory.Move(
                temporary.Paths.RootDirectory,
                movedPath));
        Assert.IsTrue(Directory.Exists(
            temporary.Paths.RawCapturesDirectory));
        Assert.IsFalse(Directory.Exists(movedPath));
    }

    [TestMethod]
    public void RejectsUnsafeLeafNamesBeforeNativeOpen()
    {
        using var temporary = TemporaryEvidenceRoot.Create();
        using var directory = WindowsRawEvidenceDirectory.Open(
            temporary.Paths);
        string[] unsafeNames =
        [
            "",
            ".",
            "..",
            "child/file.apxraw",
            @"child\file.apxraw",
            "file.apxraw:stream",
            " file.apxraw",
            "file.apxraw ",
        ];

        foreach (var name in unsafeNames)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => directory.CreateNewFile(name),
                name);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class TemporaryEvidenceRoot : IDisposable
    {
        private TemporaryEvidenceRoot(string path)
        {
            Paths = ApplicationPaths.FromRoot(path);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryEvidenceRoot Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"apexlab-raw-directory-{Guid.NewGuid():N}");
            return new TemporaryEvidenceRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
