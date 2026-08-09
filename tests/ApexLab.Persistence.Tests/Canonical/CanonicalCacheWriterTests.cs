using System.Runtime.Versioning;
using System.Text;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Tests.Canonical;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class CanonicalCacheWriterTests
{
    [TestMethod]
    public async Task FinalizePublishesContentAddressedDataThenAuthoritativeManifest()
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        var observedStages = new List<CanonicalCacheWriterStage>();
        await using var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            request,
            stage => observedStages.Add(stage),
            TestContext.CancellationToken);

        foreach (var record in Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        var completion = await writer.FinalizeAsync(TestContext.CancellationToken);

        Assert.AreEqual(3L, completion.RecordCount);
        Assert.AreEqual(1L, completion.ObservationCount);
        Assert.AreEqual(1L, completion.ExclusionCount);
        Assert.AreEqual(1L, completion.GapCount);
        Assert.AreEqual(3L, completion.FirstSourceSequence);
        Assert.AreEqual(4L, completion.LastSourceSequence);
        Assert.IsLessThan(
            observedStages.IndexOf(CanonicalCacheWriterStage.PublishManifest),
            observedStages.IndexOf(CanonicalCacheWriterStage.PublishData));
        Assert.AreEqual(
            CanonicalCacheWriterStage.PublishManifest,
            observedStages[^1]);

        var dataLeaf = $"{request.Identity.IdentitySha256}."
            + $"{completion.DataSha256}.apxcan";
        var manifestLeaf = CanonicalCacheManifest.CreateManifestLeafName(
            request.Identity);
        Assert.IsTrue(File.Exists(Path.Combine(
            temporary.Paths.DerivedCacheDirectory,
            dataLeaf)));
        Assert.IsTrue(File.Exists(Path.Combine(
            temporary.Paths.DerivedCacheDirectory,
            manifestLeaf)));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.partial"));

        var manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(temporary.Paths.DerivedCacheDirectory, manifestLeaf),
            TestContext.CancellationToken);
        Assert.AreEqual(
            completion,
            CanonicalCacheManifest.Deserialize(manifestBytes).Completion);
    }

    [TestMethod]
    public async Task IndependentBuildsHaveExactDataManifestAndChecksums()
    {
        using var firstRoot = TemporaryRoot.Create();
        using var secondRoot = TemporaryRoot.Create();

        var first = await BuildAsync(firstRoot.Paths, Request());
        var second = await BuildAsync(secondRoot.Paths, Request());

        Assert.AreEqual(first.Completion, second.Completion);
        CollectionAssert.AreEqual(first.Data, second.Data);
        CollectionAssert.AreEqual(first.Manifest, second.Manifest);
    }

    [TestMethod]
    public async Task IdentityVersionChangesOnlyTheCanonicalChecksumAndNames()
    {
        using var firstRoot = TemporaryRoot.Create();
        using var secondRoot = TemporaryRoot.Create();
        var firstRequest = Request();
        var secondRequest = new CanonicalCacheRequest(
            new CanonicalReplayIdentity(
                firstRequest.Identity.SourceEvidenceSha256,
                firstRequest.Identity.ProtocolId,
                firstRequest.Identity.ContractId,
                "f125-v3-minimal-decoder-v2",
                firstRequest.Identity.CanonicalSchemaId),
            firstRequest.SourceStopwatchFrequency);

        var first = await BuildAsync(firstRoot.Paths, firstRequest);
        var second = await BuildAsync(secondRoot.Paths, secondRequest);

        CollectionAssert.AreEqual(first.Data, second.Data);
        Assert.AreEqual(
            first.Completion.DataSha256,
            second.Completion.DataSha256);
        Assert.AreNotEqual(
            first.Completion.CanonicalSha256,
            second.Completion.CanonicalSha256);
        Assert.AreNotEqual(
            Directory.GetFiles(firstRoot.Paths.DerivedCacheDirectory)
                .Select(Path.GetFileName)
                .Order()
                .ToArray()[0],
            Directory.GetFiles(secondRoot.Paths.DerivedCacheDirectory)
                .Select(Path.GetFileName)
                .Order()
                .ToArray()[0]);
    }

    [TestMethod]
    public async Task RejectsDisorderedRecordsAndNeverPublishes()
    {
        using var temporary = TemporaryRoot.Create();
        var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            Request(),
            observer: null,
            TestContext.CancellationToken);
        await writer.WriteAsync(
            CanonicalRecord.Observation(1, 1, Motion()),
            TestContext.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteAsync(
                CanonicalRecord.Observation(3, 2, Motion()),
                TestContext.CancellationToken));
        await writer.DisposeAsync();

        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory));
    }

    [TestMethod]
    public async Task CancellationAndLifecycleMisuseLeaveNoStagingFiles()
    {
        using var temporary = TemporaryRoot.Create();
        var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            Request(),
            observer: null,
            TestContext.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync(Records()[0], cancellation.Token));
        await writer.DisposeAsync();
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await writer.WriteAsync(Records()[0], TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task FinalizeIsSingleUseAndWriteAfterFinalizeFails()
    {
        using var temporary = TemporaryRoot.Create();
        await using var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            Request(),
            observer: null,
            TestContext.CancellationToken);
        foreach (var record in Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        await writer.FinalizeAsync(TestContext.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.FinalizeAsync(TestContext.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteAsync(Records()[0], TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ManifestFailureLeavesOnlyInertFinalDataAndCleansStaging()
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            request,
            stage =>
            {
                if (stage == CanonicalCacheWriterStage.PublishManifest)
                {
                    Assert.HasCount(1, Directory.GetFiles(
                        temporary.Paths.DerivedCacheDirectory,
                        "*.apxcan"));
                    Assert.IsFalse(File.Exists(Path.Combine(
                        temporary.Paths.DerivedCacheDirectory,
                        CanonicalCacheManifest.CreateManifestLeafName(
                            request.Identity))));
                    throw new IOException("injected manifest publication failure");
                }
            },
            TestContext.CancellationToken);
        foreach (var record in Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        await Assert.ThrowsAsync<IOException>(() =>
            writer.FinalizeAsync(TestContext.CancellationToken));
        await writer.DisposeAsync();

        Assert.HasCount(1, Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.apxcan"));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.json"));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.partial"));
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, true)]
    [DataRow(5, true)]
    [DataRow(6, true)]
    public async Task InjectedLifecycleFailureCleansOnlyOwnedStaging(
        int failureStageValue,
        bool finalDataExpected)
    {
        using var temporary = TemporaryRoot.Create();
        var failureStage = (CanonicalCacheWriterStage)failureStageValue;
        var writer = await CanonicalCacheWriter.CreateAsync(
            temporary.Paths,
            Request(),
            stage =>
            {
                if (stage == failureStage)
                {
                    throw new IOException("injected writer lifecycle failure");
                }
            },
            TestContext.CancellationToken);

        if (failureStage == CanonicalCacheWriterStage.WriteRecord)
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await writer.WriteAsync(
                    Records()[0],
                    TestContext.CancellationToken));
        }
        else
        {
            foreach (var record in Records())
            {
                await writer.WriteAsync(record, TestContext.CancellationToken);
            }

            await Assert.ThrowsAsync<IOException>(() =>
                writer.FinalizeAsync(TestContext.CancellationToken));
        }

        await writer.DisposeAsync();
        Assert.HasCount(
            finalDataExpected ? 1 : 0,
            Directory.GetFiles(
                temporary.Paths.DerivedCacheDirectory,
                "*.apxcan"));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.json"));
        Assert.IsEmpty(Directory.GetFiles(
            temporary.Paths.DerivedCacheDirectory,
            "*.partial"));
    }

    [TestMethod]
    public async Task OccupiedContentAddressedNameIsNeverOverwritten()
    {
        using var sourceRoot = TemporaryRoot.Create();
        using var targetRoot = TemporaryRoot.Create();
        var built = await BuildAsync(sourceRoot.Paths, Request());
        Directory.CreateDirectory(targetRoot.Paths.DerivedCacheDirectory);
        var dataLeaf = $"{Request().Identity.IdentitySha256}."
            + $"{built.Completion.DataSha256}.apxcan";
        var occupiedPath = Path.Combine(
            targetRoot.Paths.DerivedCacheDirectory,
            dataLeaf);
        await File.WriteAllTextAsync(
            occupiedPath,
            "occupied",
            Encoding.UTF8,
            TestContext.CancellationToken);

        var writer = await CanonicalCacheWriter.CreateAsync(
            targetRoot.Paths,
            Request(),
            observer: null,
            TestContext.CancellationToken);
        foreach (var record in Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        await Assert.ThrowsAsync<IOException>(() =>
            writer.FinalizeAsync(TestContext.CancellationToken));
        await writer.DisposeAsync();
        Assert.AreEqual(
            "occupied",
            await File.ReadAllTextAsync(
                occupiedPath,
                Encoding.UTF8,
                TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(Path.Combine(
            targetRoot.Paths.DerivedCacheDirectory,
            CanonicalCacheManifest.CreateManifestLeafName(Request().Identity))));
        Assert.IsEmpty(Directory.GetFiles(
            targetRoot.Paths.DerivedCacheDirectory,
            "*.partial"));
    }

    public TestContext TestContext { get; set; } = null!;

    private async Task<BuildOutput> BuildAsync(
        ApplicationPaths paths,
        CanonicalCacheRequest request)
    {
        await using var writer = await CanonicalCacheWriter.CreateAsync(
            paths,
            request,
            observer: null,
            TestContext.CancellationToken);
        foreach (var record in Records())
        {
            await writer.WriteAsync(record, TestContext.CancellationToken);
        }

        var completion = await writer.FinalizeAsync(TestContext.CancellationToken);
        var data = await File.ReadAllBytesAsync(
            Path.Combine(
                paths.DerivedCacheDirectory,
                $"{request.Identity.IdentitySha256}.{completion.DataSha256}.apxcan"),
            TestContext.CancellationToken);
        var manifest = await File.ReadAllBytesAsync(
            Path.Combine(
                paths.DerivedCacheDirectory,
                CanonicalCacheManifest.CreateManifestLeafName(request.Identity)),
            TestContext.CancellationToken);
        return new(completion, data, manifest);
    }

    private static CanonicalRecord[] Records() =>
    [
        CanonicalRecord.Gap(
            1,
            2,
            CanonicalGapReason.UnretainedOrMissingSourceRange),
        CanonicalRecord.Observation(3, 100, Motion()),
        CanonicalRecord.Exclusion(
            4,
            7,
            CanonicalExclusionReason.CompatibleFamilyOutsideSlice),
    ];

    private static CanonicalPacket Motion() =>
        CanonicalPacket.CreateMotion(
            new CanonicalPacketHeader(1F, 2, 3, 0, byte.MaxValue),
            new CanonicalMotionPacket(4F, 5F, 6F));

    private static CanonicalCacheRequest Request() =>
        new(
            new CanonicalReplayIdentity(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "ea-f1-25-v3",
                "apexlab-bahrain-tt-slice-v1",
                "f125-v3-minimal-decoder-v1",
                "apexlab-canonical-sample-v1"),
            10_000_000);

    private sealed record BuildOutput(
        CanonicalCacheCompletion Completion,
        byte[] Data,
        byte[] Manifest);

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-canonical-writer-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
