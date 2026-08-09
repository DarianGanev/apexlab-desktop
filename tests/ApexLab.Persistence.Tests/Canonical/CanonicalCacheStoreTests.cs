using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;

namespace ApexLab.Persistence.Tests.Canonical;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed partial class CanonicalCacheStoreTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task MissingAndIdentityMutatedRequestsAreIndependentMisses(
        int mutationKind)
    {
        using var temporary = CanonicalCacheReaderTests.TemporaryRoot.Create();
        var request = CanonicalCacheReaderTests.Request();
        var store = new CanonicalCacheStore(temporary.Paths);

        Assert.IsNull(await store.TryOpenAsync(
            request,
            TestContext.CancellationToken));
        await BuildAsync(store, request);
        var originalManifest = CanonicalCacheReaderTests.ManifestPath(
            temporary.Paths,
            request.Identity);
        Assert.IsTrue(File.Exists(originalManifest));

        var mutated = MutateIdentity(request, mutationKind);
        Assert.IsNull(await store.TryOpenAsync(
            mutated,
            TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(originalManifest));
    }

    [TestMethod]
    public async Task ExactVerifiedEntryIsReturnedAsAHit()
    {
        using var temporary = CanonicalCacheReaderTests.TemporaryRoot.Create();
        var request = CanonicalCacheReaderTests.Request();
        var store = new CanonicalCacheStore(temporary.Paths);
        var completion = await BuildAsync(store, request);

        await using var entry = await store.TryOpenAsync(
            request,
            TestContext.CancellationToken);

        Assert.IsNotNull(entry);
        Assert.AreEqual(completion, entry.Completion);
    }

    [TestMethod]
    public async Task CorruptEntryIsRemovedAndCanBeRebuiltAtTheSameIdentity()
    {
        using var temporary = CanonicalCacheReaderTests.TemporaryRoot.Create();
        var request = CanonicalCacheReaderTests.Request();
        var store = new CanonicalCacheStore(temporary.Paths);
        var first = await BuildAsync(store, request);
        var dataPath = Path.Combine(
            temporary.Paths.DerivedCacheDirectory,
            $"{request.Identity.IdentitySha256}.{first.DataSha256}.apxcan");
        var bytes = await File.ReadAllBytesAsync(
            dataPath,
            TestContext.CancellationToken);
        bytes[78] ^= 0x01;
        await File.WriteAllBytesAsync(
            dataPath,
            bytes,
            TestContext.CancellationToken);

        Assert.IsNull(await store.TryOpenAsync(
            request,
            TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(dataPath));
        Assert.IsFalse(File.Exists(CanonicalCacheReaderTests.ManifestPath(
            temporary.Paths,
            request.Identity)));

        var rebuilt = await BuildAsync(store, request);
        Assert.AreEqual(first, rebuilt);
        await using var entry = await store.TryOpenAsync(
            request,
            TestContext.CancellationToken);
        Assert.IsNotNull(entry);
    }

    [TestMethod]
    public async Task MalformedManifestIsRemovedWithoutDeletingUnrelatedData()
    {
        using var temporary = CanonicalCacheReaderTests.TemporaryRoot.Create();
        var request = CanonicalCacheReaderTests.Request();
        var store = new CanonicalCacheStore(temporary.Paths);
        var completion = await BuildAsync(store, request);
        var dataPath = Path.Combine(
            temporary.Paths.DerivedCacheDirectory,
            $"{request.Identity.IdentitySha256}.{completion.DataSha256}.apxcan");
        await File.WriteAllTextAsync(
            CanonicalCacheReaderTests.ManifestPath(
                temporary.Paths,
                request.Identity),
            "{}",
            TestContext.CancellationToken);

        Assert.IsNull(await store.TryOpenAsync(
            request,
            TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(dataPath));
        Assert.IsFalse(File.Exists(CanonicalCacheReaderTests.ManifestPath(
            temporary.Paths,
            request.Identity)));

        var rebuilt = await BuildAsync(store, request);
        Assert.AreEqual(completion, rebuilt);
        Assert.HasCount(1, Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.apxcan"));
        await using var entry = await store.TryOpenAsync(
            request,
            TestContext.CancellationToken);
        Assert.IsNotNull(entry);
    }

    [TestMethod]
    public async Task UnsafeHardLinkedManifestRemainsAnOperationalFailure()
    {
        using var temporary = CanonicalCacheReaderTests.TemporaryRoot.Create();
        var request = CanonicalCacheReaderTests.Request();
        Directory.CreateDirectory(temporary.Paths.DerivedCacheDirectory);
        var original = Path.Combine(temporary.Paths.RootDirectory, "outside.json");
        var manifest = CanonicalCacheReaderTests.ManifestPath(
            temporary.Paths,
            request.Identity);
        await File.WriteAllTextAsync(
            original,
            "{}",
            TestContext.CancellationToken);
        Assert.IsTrue(CreateHardLink(manifest, original, IntPtr.Zero));
        var store = new CanonicalCacheStore(temporary.Paths);

        var exception = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            store.TryOpenAsync(request, TestContext.CancellationToken));

        Assert.AreEqual(CanonicalCacheReadFailureKind.UnsafeStorage, exception.Kind);
        Assert.IsTrue(File.Exists(original));
        Assert.IsTrue(File.Exists(manifest));
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

    private async Task<CanonicalCacheCompletion> BuildAsync(
        CanonicalCacheStore store,
        CanonicalCacheRequest request)
    {
        await using var writer = await store.CreateWriterAsync(
            request,
            TestContext.CancellationToken);
        foreach (var record in CanonicalCacheReaderTests.Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        return await writer.FinalizeAsync(TestContext.CancellationToken);
    }

    private static CanonicalCacheRequest MutateIdentity(
        CanonicalCacheRequest request,
        int mutationKind) =>
        new(
            new CanonicalReplayIdentity(
                mutationKind == 0
                    ? new string('c', 64)
                    : request.Identity.SourceEvidenceSha256,
                mutationKind == 1
                    ? "ea-f1-25-v4"
                    : request.Identity.ProtocolId,
                mutationKind == 2
                    ? "apexlab-bahrain-tt-slice-v2"
                    : request.Identity.ContractId,
                mutationKind == 3
                    ? "f125-v3-minimal-decoder-v2"
                    : request.Identity.DecoderId,
                mutationKind == 4
                    ? "apexlab-canonical-sample-v2"
                    : request.Identity.CanonicalSchemaId),
            request.SourceStopwatchFrequency);
}
