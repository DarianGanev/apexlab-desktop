using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;

namespace ApexLab.Persistence.Tests.Laps;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class BahrainLapAuditStoreTests
{
    [TestMethod]
    public async Task PreparePublishesOnceReopensExactBytesAndReleasesHandles()
    {
        using var temporary = TemporaryRoot.Create();
        var captureId = CaptureId();
        var document = BahrainLapAuditTestData.Document();
        var expected = BahrainLapAuditJson.Serialize(document);

        await BahrainLapAuditStore.PrepareAsync(
            temporary.Paths,
            captureId,
            document,
            observer: null,
            TestContext.CancellationToken);
        var opened = await BahrainLapAuditStore.OpenAsync(
            temporary.Paths,
            captureId,
            TestContext.CancellationToken);

        Assert.AreEqual(document.CanonicalSha256, opened.CanonicalSha256);
        var files = Directory.GetFiles(temporary.Paths.LapAuditsDirectory);
        Assert.HasCount(1, files);
        CollectionAssert.AreEqual(
            expected,
            await File.ReadAllBytesAsync(files[0], TestContext.CancellationToken));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.LapAuditsDirectory,
            "*.partial"));
        File.Delete(files[0]);
        Directory.Delete(temporary.Paths.LapAuditsDirectory);
    }

    [TestMethod]
    public async Task ExistingAuditIsNeverOverwritten()
    {
        using var temporary = TemporaryRoot.Create();
        var captureId = CaptureId();
        var original = BahrainLapAuditTestData.Document();
        await BahrainLapAuditStore.PrepareAsync(
            temporary.Paths,
            captureId,
            original,
            observer: null,
            TestContext.CancellationToken);
        var path = Directory.GetFiles(temporary.Paths.LapAuditsDirectory).Single();
        var originalBytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            BahrainLapAuditStore.PrepareAsync(
                temporary.Paths,
                captureId,
                BahrainLapAuditTestData.Document(LapAuditDecision.Included),
                observer: null,
                TestContext.CancellationToken));

        Assert.AreEqual(BahrainLapAuditStoreFailureKind.AlreadyExists, exception.Kind);
        CollectionAssert.AreEqual(
            originalBytes,
            await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.LapAuditsDirectory,
            "*.partial"));
    }

    [TestMethod]
    public async Task FaultAndCancellationDeleteOnlyOwnedStagingFile()
    {
        foreach (var stage in Enum.GetValues<BahrainLapAuditStoreStage>())
        {
            using var temporary = TemporaryRoot.Create();
            await Assert.ThrowsExactlyAsync<ForcedStoreException>(() =>
                BahrainLapAuditStore.PrepareAsync(
                    temporary.Paths,
                    CaptureId(),
                    BahrainLapAuditTestData.Document(),
                    observed =>
                    {
                        if (observed == stage)
                        {
                            throw new ForcedStoreException();
                        }
                    },
                    TestContext.CancellationToken));
            AssertNoAuditFiles(temporary.Paths);
        }

        using var canceledRoot = TemporaryRoot.Create();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            BahrainLapAuditStore.PrepareAsync(
                canceledRoot.Paths,
                CaptureId(),
                BahrainLapAuditTestData.Document(),
                observer: null,
                canceled.Token));
        Assert.IsFalse(Directory.Exists(canceledRoot.Paths.LapAuditsDirectory));
    }

    [TestMethod]
    public async Task MissingMalformedHardLinkedAndReparseStorageFailClosed()
    {
        using var missing = TemporaryRoot.Create();
        var missingError = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            BahrainLapAuditStore.OpenAsync(
                missing.Paths,
                CaptureId(),
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditStoreFailureKind.Missing, missingError.Kind);

        using var malformed = TemporaryRoot.Create();
        Directory.CreateDirectory(malformed.Paths.LapAuditsDirectory);
        var malformedId = CaptureId();
        var malformedPath = Path.Combine(
            malformed.Paths.LapAuditsDirectory,
            BahrainLapAuditStore.CreateLeafName(malformedId));
        await File.WriteAllTextAsync(
            malformedPath,
            "{}\n",
            TestContext.CancellationToken);
        var malformedError = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            BahrainLapAuditStore.OpenAsync(
                malformed.Paths,
                malformedId,
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditStoreFailureKind.InvalidDocument, malformedError.Kind);

        using var hardLinked = TemporaryRoot.Create();
        var hardLinkId = CaptureId();
        Directory.CreateDirectory(hardLinked.Paths.LapAuditsDirectory);
        var hardLinkPath = Path.Combine(
            hardLinked.Paths.LapAuditsDirectory,
            BahrainLapAuditStore.CreateLeafName(hardLinkId));
        var outside = Path.Combine(hardLinked.Paths.RootDirectory, "outside.json");
        await File.WriteAllBytesAsync(
            outside,
            BahrainLapAuditJson.Serialize(BahrainLapAuditTestData.Document()),
            TestContext.CancellationToken);
        Assert.IsTrue(CreateHardLink(hardLinkPath, outside, IntPtr.Zero));
        var hardLinkError = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            BahrainLapAuditStore.OpenAsync(
                hardLinked.Paths,
                hardLinkId,
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditStoreFailureKind.UnsafeStorage, hardLinkError.Kind);

        using var reparse = TemporaryRoot.Create();
        var external = Path.Combine(reparse.Paths.RootDirectory, "external");
        Directory.CreateDirectory(external);
        CreateJunction(reparse.Paths.LapAuditsDirectory, external);
        var reparseError = await Assert.ThrowsExactlyAsync<BahrainLapAuditStoreException>(() =>
            BahrainLapAuditStore.OpenAsync(
                reparse.Paths,
                CaptureId(),
                TestContext.CancellationToken));
        Assert.AreEqual(BahrainLapAuditStoreFailureKind.UnsafeStorage, reparseError.Kind);
        Directory.Delete(reparse.Paths.LapAuditsDirectory);
    }

    public TestContext TestContext { get; set; }

    private static RawEvidenceCaptureId CaptureId() =>
        RawEvidenceCaptureId.Parse("0123456789ab4def8123456789abcdef");

    private static void AssertNoAuditFiles(ApplicationPaths paths)
    {
        if (Directory.Exists(paths.LapAuditsDirectory))
        {
            Assert.IsEmpty(Directory.GetFiles(paths.LapAuditsDirectory));
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junctionPath);
        start.ArgumentList.Add(targetPath);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Junction helper did not start.");
        if (!process.WaitForExit(milliseconds: 5_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Junction helper timed out.");
        }

        Assert.AreEqual(
            0,
            process.ExitCode,
            process.StandardError.ReadToEnd());
    }

    private sealed class ForcedStoreException : Exception;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-lap-audit-store-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
