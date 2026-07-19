using System.Text;
using ApexLab.Application.Configuration;
using ApexLab.Persistence.Configuration;

namespace ApexLab.Persistence.Tests.Configuration;

[TestClass]
public sealed class JsonSettingsStoreTests
{
    [TestMethod]
    public async Task LoadAsync_WhenSettingsDoNotExist_ReturnsDefaultsWithoutWriting()
    {
        using var directory = new TemporaryDirectory(create: false);
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(defaults, loaded);
        Assert.IsFalse(Directory.Exists(directory.Path));
    }

    [TestMethod]
    public async Task SaveAndLoadAsync_RoundTripsValidatedOptions()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var configured = defaults with
        {
            UdpPort = 20_778,
            LiveSnapshotRateHz = 12,
            RawChunkDuration = TimeSpan.FromSeconds(7),
            StorageQuotaBytes = 4_000_000_000,
        };
        using var store = new JsonSettingsStore(defaults);

        await store.SaveAsync(configured, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(configured, loaded);
        Assert.IsEmpty(ApexLabOptionsValidator.Validate(loaded));
    }

    [TestMethod]
    public async Task SaveAsync_WritesStableUtf8JsonWithoutByteOrderMark()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path) with
        {
            BindAddress = "127.0.0.1",
            UdpPort = 31_337,
            LiveSnapshotRateHz = 10,
        };
        using var store = new JsonSettingsStore(options);

        await store.SaveAsync(options, CancellationToken.None);
        var firstBytes = await File.ReadAllBytesAsync(store.SettingsFilePath);
        await store.SaveAsync(options, CancellationToken.None);
        var secondBytes = await File.ReadAllBytesAsync(store.SettingsFilePath);

        CollectionAssert.AreEqual(firstBytes, secondBytes);
        Assert.IsFalse(firstBytes.Length >= 3
            && firstBytes[0] == 0xEF
            && firstBytes[1] == 0xBB
            && firstBytes[2] == 0xBF);
        var json = new UTF8Encoding(false, true).GetString(firstBytes);
        StringAssert.Contains(json, "\"udpPort\": 31337");
        StringAssert.Contains(json, "\"rawChunkDuration\": \"00:00:05\"");
    }

    [TestMethod]
    public async Task SaveAsync_WhenCancelledBeforeCommit_PreservesPreviousFileAndRemovesSiblingTemp()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var initialStore = new JsonSettingsStore(defaults);
        await initialStore.SaveAsync(defaults, CancellationToken.None);
        var previousBytes = await File.ReadAllBytesAsync(initialStore.SettingsFilePath);
        using var cancellation = new CancellationTokenSource();
        string? observedTempPath = null;
        string? observedTargetPath = null;
        using var interruptedStore = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = (tempPath, targetPath, _) =>
                {
                    observedTempPath = tempPath;
                    observedTargetPath = targetPath;
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
            });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => interruptedStore.SaveAsync(defaults with { UdpPort = 30_001 }, cancellation.Token));

        Assert.IsNotNull(observedTempPath);
        Assert.IsNotNull(observedTargetPath);
        Assert.AreEqual(Path.GetDirectoryName(observedTargetPath), Path.GetDirectoryName(observedTempPath));
        CollectionAssert.AreEqual(previousBytes, await File.ReadAllBytesAsync(initialStore.SettingsFilePath));
        Assert.IsFalse(File.Exists(observedTempPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    public async Task SaveAsync_WhenCommitPreparationFails_PreservesPreviousFileAndPropagatesFailure()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var initialStore = new JsonSettingsStore(defaults);
        await initialStore.SaveAsync(defaults, CancellationToken.None);
        var previousBytes = await File.ReadAllBytesAsync(initialStore.SettingsFilePath);
        using var failingStore = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = (_, _, _) => throw new IOException("Simulated storage failure."),
            });

        var exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => failingStore.SaveAsync(defaults with { UdpPort = 30_002 }, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Simulated storage failure");
        CollectionAssert.AreEqual(previousBytes, await File.ReadAllBytesAsync(initialStore.SettingsFilePath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    [DataRow("{\"udpPort\":")]
    [DataRow("not-json")]
    [DataRow("{\"dataRootPath\":\"relative\",\"udpPort\":20777}")]
    public async Task LoadAsync_WhenSettingsAreInvalid_PreservesEvidenceBeforeReturningDefaults(string invalidJson)
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);
        await File.WriteAllTextAsync(store.SettingsFilePath, invalidJson, new UTF8Encoding(false));

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(defaults, loaded);
        Assert.IsFalse(File.Exists(store.SettingsFilePath));
        var preservedFiles = Directory.GetFiles(directory.Path, "settings.invalid-*.json");
        Assert.HasCount(1, preservedFiles);
        Assert.AreEqual(invalidJson, await File.ReadAllTextAsync(preservedFiles[0]));
    }

    [TestMethod]
    public async Task LoadAsync_WhenInvalidSettingsRepeat_UsesCollisionSafeDiagnosticNames()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);

        await File.WriteAllTextAsync(store.SettingsFilePath, "first-invalid");
        _ = await store.LoadAsync(CancellationToken.None);
        await File.WriteAllTextAsync(store.SettingsFilePath, "second-invalid");
        _ = await store.LoadAsync(CancellationToken.None);

        var preservedFiles = Directory.GetFiles(directory.Path, "settings.invalid-*.json");
        Assert.HasCount(2, preservedFiles);
        Assert.AreEqual(2, preservedFiles.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
        var preservedContents = await Task.WhenAll(
            preservedFiles.Select(path => File.ReadAllTextAsync(path)));
        CollectionAssert.AreEquivalent(new[] { "first-invalid", "second-invalid" }, preservedContents);
    }

    [TestMethod]
    public async Task LoadAsync_WhenCancelled_DoesNotMoveSettingsToDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);
        await store.SaveAsync(defaults, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.LoadAsync(cancellation.Token));

        Assert.IsTrue(File.Exists(store.SettingsFilePath));
        Assert.IsEmpty(Directory.GetFiles(directory.Path, "settings.invalid-*.json"));
    }

    [TestMethod]
    public async Task LoadAsync_WhenSettingsPathIsADirectory_PropagatesAccessFailureWithoutFallback()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);
        Directory.CreateDirectory(store.SettingsFilePath);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => store.LoadAsync(CancellationToken.None));

        Assert.IsTrue(Directory.Exists(store.SettingsFilePath));
        Assert.IsEmpty(Directory.GetFiles(directory.Path, "settings.invalid-*.json"));
    }

    [TestMethod]
    public async Task SaveAsync_ConcurrentCallsAreSerializedAndLeaveNoTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        using var store = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = async (_, _, _) =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 1)
                    {
                        firstEntered.SetResult();
                        await releaseFirst.Task;
                    }
                },
            });

        var firstSave = store.SaveAsync(defaults with { UdpPort = 30_003 }, CancellationToken.None);
        await firstEntered.Task;
        var secondOptions = defaults with { UdpPort = 30_004 };
        var secondSave = store.SaveAsync(secondOptions, CancellationToken.None);

        Assert.AreEqual(1, Volatile.Read(ref callbackCount));
        Assert.IsFalse(secondSave.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(firstSave, secondSave);

        Assert.AreEqual(2, callbackCount);
        Assert.AreEqual(secondOptions, await store.LoadAsync(CancellationToken.None));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    public void Constructor_UsesFixedContainedFilenameAndRejectsInvalidDefaults()
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonSettingsStore(CreateOptions(directory.Path));

        Assert.AreEqual(Path.Combine(Path.GetFullPath(directory.Path), "settings.json"), store.SettingsFilePath);
        Assert.AreEqual(
            Path.GetFullPath(directory.Path),
            Path.GetDirectoryName(Path.GetFullPath(store.SettingsFilePath)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new JsonSettingsStore(new ApexLabOptions("relative-root")));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsInvalidOptionsWithoutChangingExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using var store = new JsonSettingsStore(defaults);
        await store.SaveAsync(defaults, CancellationToken.None);
        var previousBytes = await File.ReadAllBytesAsync(store.SettingsFilePath);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => store.SaveAsync(defaults with { UdpPort = 0 }, CancellationToken.None));

        CollectionAssert.AreEqual(previousBytes, await File.ReadAllBytesAsync(store.SettingsFilePath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    public async Task CompletedOperations_DoNotRetainFileHandles()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using (var store = new JsonSettingsStore(defaults))
        {
            await store.SaveAsync(defaults, CancellationToken.None);
            _ = await store.LoadAsync(CancellationToken.None);
        }

        var movedPath = $"{directory.Path}-moved";
        Directory.Move(directory.Path, movedPath);
        Directory.Delete(movedPath, recursive: true);

        Assert.IsFalse(Directory.Exists(movedPath));
        directory.MarkDeleted();
    }

    [TestMethod]
    public async Task Dispose_DuringActiveSave_WaitsForCompletionAndRejectsLaterOperations()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = async (_, _, _) =>
                {
                    saveEntered.SetResult();
                    await releaseSave.Task;
                },
                AfterAdmissionClosed = disposalStarted.SetResult,
            });

        var saveTask = store.SaveAsync(defaults with { UdpPort = 30_005 }, CancellationToken.None);
        await saveEntered.Task;
        var disposeTask = Task.Run(store.Dispose);
        await disposalStarted.Task;

        Assert.IsFalse(disposeTask.IsCompleted);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => store.LoadAsync(CancellationToken.None));
        releaseSave.SetResult();
        await saveTask;
        await disposeTask;

        AssertNoTemporaryFiles(directory.Path);
        store.Dispose();
    }

    [TestMethod]
    public async Task Dispose_DuringActiveLoad_WaitsForCompletionAndReleasesFileHandle()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        using (var initialStore = new JsonSettingsStore(defaults))
        {
            await initialStore.SaveAsync(defaults, CancellationToken.None);
        }

        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                AfterLoadOpenedAsync = async (_, _) =>
                {
                    loadEntered.SetResult();
                    await releaseLoad.Task;
                },
                AfterAdmissionClosed = disposalStarted.SetResult,
            });

        var loadTask = store.LoadAsync(CancellationToken.None);
        await loadEntered.Task;
        var disposeTask = Task.Run(store.Dispose);
        await disposalStarted.Task;

        Assert.IsFalse(disposeTask.IsCompleted);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => store.SaveAsync(defaults, CancellationToken.None));
        releaseLoad.SetResult();
        Assert.AreEqual(defaults, await loadTask);
        await disposeTask;

        var movedPath = $"{directory.Path}-disposed-load";
        Directory.Move(directory.Path, movedPath);
        Directory.Delete(movedPath, recursive: true);
        directory.MarkDeleted();
    }

    [TestMethod]
    public async Task Dispose_WithQueuedSave_WaitsForEveryAdmittedOperation()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var store = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = async (_, _, _) =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 1)
                    {
                        firstEntered.SetResult();
                        await releaseFirst.Task;
                    }
                    else
                    {
                        secondEntered.SetResult();
                        await releaseSecond.Task;
                    }
                },
                AfterAdmissionClosed = disposalStarted.SetResult,
            });

        var firstSave = store.SaveAsync(defaults with { UdpPort = 30_006 }, CancellationToken.None);
        await firstEntered.Task;
        var finalOptions = defaults with { UdpPort = 30_007 };
        var queuedSave = store.SaveAsync(finalOptions, CancellationToken.None);
        var disposeTask = Task.Run(store.Dispose);
        await disposalStarted.Task;

        Assert.IsFalse(disposeTask.IsCompleted);
        releaseFirst.SetResult();
        await secondEntered.Task;
        Assert.IsFalse(disposeTask.IsCompleted);
        releaseSecond.SetResult();
        await Task.WhenAll(firstSave, queuedSave);
        await disposeTask;

        using var verificationStore = new JsonSettingsStore(defaults);
        Assert.AreEqual(finalOptions, await verificationStore.LoadAsync(CancellationToken.None));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    public async Task Dispose_ConcurrentCallersBothWaitAndCompleteSafely()
    {
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothDisposersWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalWaitCount = 0;
        var store = new JsonSettingsStore(
            defaults,
            new JsonSettingsStoreHooks
            {
                BeforeCommitAsync = async (_, _, _) =>
                {
                    saveEntered.SetResult();
                    await releaseSave.Task;
                },
                BeforeDisposalDrainWait = () =>
                {
                    if (Interlocked.Increment(ref disposalWaitCount) == 2)
                    {
                        bothDisposersWaiting.SetResult();
                    }
                },
            });

        var saveTask = store.SaveAsync(defaults with { UdpPort = 30_010 }, CancellationToken.None);
        await saveEntered.Task;
        var firstDispose = Task.Run(store.Dispose);
        var secondDispose = Task.Run(store.Dispose);
        await bothDisposersWaiting.Task;

        Assert.IsFalse(firstDispose.IsCompleted);
        Assert.IsFalse(secondDispose.IsCompleted);
        releaseSave.SetResult();
        await saveTask;
        await Task.WhenAll(firstDispose, secondDispose);

        store.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => store.LoadAsync(CancellationToken.None));
        AssertNoTemporaryFiles(directory.Path);
    }

    [TestMethod]
    public async Task SaveAsync_OnWindowsAtomicallyReplacesFileVisibleThroughDeleteSharing()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "ApexLab desktop persistence targets Windows.");
        using var directory = new TemporaryDirectory();
        var defaults = CreateOptions(directory.Path);
        var oldOptions = defaults with { UdpPort = 30_008 };
        var newOptions = defaults with { UdpPort = 30_009 };
        using var store = new JsonSettingsStore(defaults);
        await store.SaveAsync(newOptions, CancellationToken.None);
        var expectedNewBytes = await File.ReadAllBytesAsync(store.SettingsFilePath);
        await store.SaveAsync(oldOptions, CancellationToken.None);
        var expectedOldBytes = await File.ReadAllBytesAsync(store.SettingsFilePath);
        await using var retainedOldHandle = new FileStream(
            store.SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await store.SaveAsync(newOptions, CancellationToken.None);

        var bytesThroughRetainedHandle = await ReadAllBytesAsync(retainedOldHandle);
        var bytesThroughNewPath = await File.ReadAllBytesAsync(store.SettingsFilePath);
        CollectionAssert.AreEqual(expectedOldBytes, bytesThroughRetainedHandle);
        CollectionAssert.AreEqual(expectedNewBytes, bytesThroughNewPath);
        Assert.AreEqual(newOptions, await store.LoadAsync(CancellationToken.None));
    }

    private static ApexLabOptions CreateOptions(string dataRootPath)
    {
        return new ApexLabOptions(dataRootPath);
    }

    private static void AssertNoTemporaryFiles(string directoryPath)
    {
        Assert.IsEmpty(Directory.GetFiles(directoryPath, ".settings.json.*.tmp"));
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private bool _deleted;

        public TemporaryDirectory(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"apexlab-settings-tests-{Guid.NewGuid():N}");

            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void MarkDeleted()
        {
            _deleted = true;
        }

        public void Dispose()
        {
            if (!_deleted && Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
