using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ApexLab.Persistence.Storage;

namespace ApexLab.Persistence.Tests.Storage;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed partial class WindowsLocalDataDirectoryTests
{
    [TestMethod]
    public async Task OpensIndependentValidatedChildAndRetainsFileIdentityAcrossRename()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");

        await using var stream = directory.CreateNewFile("entry.data.partial");
        await stream.WriteAsync("canonical"u8.ToArray(), TestContext.CancellationToken);
        await stream.FlushAsync(TestContext.CancellationToken);
        var before = directory.GetIdentity(stream.SafeFileHandle);

        directory.RenameOpenFile(stream.SafeFileHandle, "entry.data");

        Assert.AreEqual(before, directory.GetIdentity(stream.SafeFileHandle));
        Assert.AreEqual(
            Path.Combine(temporary.Path, "canonical-cache", "entry.data"),
            directory.GetNormalizedPath(stream.SafeFileHandle),
            ignoreCase: true);
        Assert.IsFalse(File.Exists(Path.Combine(
            temporary.Path,
            "canonical-cache",
            "entry.data.partial")));
    }

    [TestMethod]
    public void RetainedHandlesBlockRootAndChildReplacement()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");

        Assert.ThrowsExactly<IOException>(() => Directory.Move(
            temporary.Path,
            $"{temporary.Path}-moved"));
        Assert.ThrowsExactly<IOException>(() => Directory.Move(
            Path.Combine(temporary.Path, "canonical-cache"),
            Path.Combine(temporary.Path, "moved-cache")));
    }

    [TestMethod]
    public void CreationAndRenameNeverOverwriteExistingLeaves()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");
        using var staging = directory.CreateNewFile("entry.data.partial");

        Assert.ThrowsExactly<Win32Exception>(() =>
            directory.CreateNewFile("entry.data.partial"));
        File.WriteAllText(
            Path.Combine(temporary.Path, "canonical-cache", "entry.data"),
            "occupied");

        Assert.ThrowsExactly<Win32Exception>(() =>
            directory.RenameOpenFile(staging.SafeFileHandle, "entry.data"));
        Assert.IsTrue(File.Exists(Path.Combine(
            temporary.Path,
            "canonical-cache",
            "entry.data.partial")));
    }

    [TestMethod]
    public void HandleDeletionRemovesOnlyTheOwnedOpenLeaf()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");
        var path = Path.Combine(temporary.Path, "canonical-cache", "staging.data");

        using (var stream = directory.CreateNewFile("staging.data"))
        {
            directory.DeleteOpenFile(stream.SafeFileHandle);
        }

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void RejectsTraversalStreamsDeviceNamesAndUnsafeChildNames()
    {
        using var temporary = TemporaryDirectory.Create();
        string[] unsafeChildren = ["", ".", "..", "a/b", @"a\b", "cache:ads", "CON"];
        foreach (var child in unsafeChildren)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                WindowsLocalDataDirectory.Open(temporary.Path, child));
        }

        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");
        string[] unsafeLeaves =
        [
            "",
            ".",
            "..",
            "child/file",
            @"child\file",
            "entry:stream",
            "entry ",
            "entry.",
            "NUL",
            "COM1.bin",
        ];
        foreach (var leaf in unsafeLeaves)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                directory.CreateNewFile(leaf));
        }
    }

    [TestMethod]
    public void OpenValidatesRegularSingleLinkFiles()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");

        Assert.IsNull(directory.TryOpenExistingReadOnly("missing.data"));
        Assert.ThrowsExactly<Win32Exception>(() =>
            directory.OpenExistingReadOnly("missing.data"));
    }

    [TestMethod]
    public void RejectsAFileWithMoreThanOneHardLink()
    {
        using var temporary = TemporaryDirectory.Create();
        using var directory = WindowsLocalDataDirectory.Open(
            temporary.Path,
            "canonical-cache");
        var original = Path.Combine(temporary.Path, "outside.data");
        var linked = Path.Combine(
            temporary.Path,
            "canonical-cache",
            "linked.data");
        File.WriteAllText(original, "shared");
        Assert.IsTrue(CreateHardLink(linked, original, IntPtr.Zero));

        Assert.ThrowsExactly<IOException>(() =>
            directory.OpenExistingReadOnly("linked.data"));
    }

    public TestContext TestContext { get; set; } = null!;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"apexlab-local-directory-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
