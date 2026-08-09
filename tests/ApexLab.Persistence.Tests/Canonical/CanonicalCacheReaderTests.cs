using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Canonical;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Tests.Canonical;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class CanonicalCacheReaderTests
{
    [TestMethod]
    public async Task OpenFullyVerifiesAndReadsTheOrderedRecordStream()
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        var completion = await BuildAsync(temporary.Paths, request);
        var dataPath = DataPath(temporary.Paths, completion);

        var entry = await CanonicalCacheReader.OpenAsync(
            temporary.Paths,
            request,
            TestContext.CancellationToken);
        Assert.AreEqual(completion, entry.Completion);
        var records = await CollectAsync(
            entry.ReadAllAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(Records(), records);
        Assert.ThrowsExactly<IOException>(() => File.Delete(dataPath));

        await entry.DisposeAsync();
        File.Delete(dataPath);
        Assert.IsFalse(File.Exists(dataPath));
    }

    [TestMethod]
    public async Task MissingManifestHasItsOwnBoundedFailure()
    {
        using var temporary = TemporaryRoot.Create();

        var exception = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                temporary.Paths,
                Request(),
                TestContext.CancellationToken));

        Assert.AreEqual(CanonicalCacheReadFailureKind.Missing, exception.Kind);
    }

    [TestMethod]
    public async Task UnknownOrOversizedManifestIsRejectedBeforeDataOpen()
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        await BuildAsync(temporary.Paths, request);
        var manifestPath = ManifestPath(temporary.Paths, request.Identity);
        var json = await File.ReadAllTextAsync(
            manifestPath,
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            manifestPath,
            json.TrimEnd('\n')[..^1] + ",\"unknown\":1}\n",
            TestContext.CancellationToken);

        var malformed = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                temporary.Paths,
                request,
                TestContext.CancellationToken));
        Assert.AreEqual(
            CanonicalCacheReadFailureKind.MalformedManifest,
            malformed.Kind);

        await File.WriteAllBytesAsync(
            manifestPath,
            new byte[CanonicalCacheManifest.MaximumLengthBytes + 1],
            TestContext.CancellationToken);
        var oversized = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                temporary.Paths,
                request,
                TestContext.CancellationToken));
        Assert.AreEqual(
            CanonicalCacheReadFailureKind.MalformedManifest,
            oversized.Kind);
    }

    [TestMethod]
    public async Task ExactManifestLeafWithAnotherIdentityIsStale()
    {
        using var requestedRoot = TemporaryRoot.Create();
        using var otherRoot = TemporaryRoot.Create();
        var requested = Request();
        var other = MutateIdentity(requested, "f125-v3-minimal-decoder-v2");
        await BuildAsync(requestedRoot.Paths, requested);
        await BuildAsync(otherRoot.Paths, other);
        await File.WriteAllBytesAsync(
            ManifestPath(requestedRoot.Paths, requested.Identity),
            await File.ReadAllBytesAsync(
                ManifestPath(otherRoot.Paths, other.Identity),
                TestContext.CancellationToken),
            TestContext.CancellationToken);

        var exception = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                requestedRoot.Paths,
                requested,
                TestContext.CancellationToken));
        Assert.AreEqual(
            CanonicalCacheReadFailureKind.IdentityMismatch,
            exception.Kind);
    }

    [TestMethod]
    public async Task CorruptDataDigestAndMalformedRecordAreDistinguished()
    {
        using var digestRoot = TemporaryRoot.Create();
        var request = Request();
        var digestCompletion = await BuildAsync(digestRoot.Paths, request);
        var digestPath = DataPath(digestRoot.Paths, digestCompletion);
        var digestBytes = await File.ReadAllBytesAsync(
            digestPath,
            TestContext.CancellationToken);
        const int firstMotionPayloadOffset =
            CanonicalCacheFormat.HeaderLength
            + 22
            + sizeof(uint)
            + 1
            + sizeof(long)
            + sizeof(long)
            + 1
            + 14;
        digestBytes[firstMotionPayloadOffset] ^= 0x01;
        await File.WriteAllBytesAsync(
            digestPath,
            digestBytes,
            TestContext.CancellationToken);

        var integrity = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                digestRoot.Paths,
                request,
                TestContext.CancellationToken));
        Assert.AreEqual(
            CanonicalCacheReadFailureKind.IntegrityMismatch,
            integrity.Kind);

        using var formatRoot = TemporaryRoot.Create();
        var formatCompletion = await BuildAsync(formatRoot.Paths, request);
        var formatPath = DataPath(formatRoot.Paths, formatCompletion);
        var formatBytes = await File.ReadAllBytesAsync(
            formatPath,
            TestContext.CancellationToken);
        formatBytes[CanonicalCacheFormat.HeaderLength + sizeof(uint)] = 99;
        await File.WriteAllBytesAsync(
            formatPath,
            formatBytes,
            TestContext.CancellationToken);

        var malformed = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                formatRoot.Paths,
                request,
                TestContext.CancellationToken));
        Assert.AreEqual(
            CanonicalCacheReadFailureKind.MalformedData,
            malformed.Kind);
    }

    [TestMethod]
    public async Task TruncationTrailingBytesAndHeaderFrequencyAreRejected()
    {
        await AssertDataMutationFails(
            bytes => bytes[..^1],
            CanonicalCacheReadFailureKind.MalformedData);
        await AssertDataMutationFails(
            bytes => [.. bytes, 0xCC],
            CanonicalCacheReadFailureKind.MalformedData);
        await AssertDataMutationFails(
            bytes =>
            {
                bytes[12] ^= 0x01;
                return bytes;
            },
            CanonicalCacheReadFailureKind.MalformedData);
    }

    [TestMethod]
    public async Task PreCanceledOpenReleasesAllHandles()
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        await BuildAsync(temporary.Paths, request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CanonicalCacheReader.OpenAsync(
                temporary.Paths,
                request,
                cancellation.Token));

        Directory.Delete(temporary.Paths.RootDirectory, recursive: true);
        Assert.IsFalse(Directory.Exists(temporary.Paths.RootDirectory));
    }

    public TestContext TestContext { get; set; } = null!;

    private async Task AssertDataMutationFails(
        Func<byte[], byte[]> mutation,
        CanonicalCacheReadFailureKind expectedKind)
    {
        using var temporary = TemporaryRoot.Create();
        var request = Request();
        var completion = await BuildAsync(temporary.Paths, request);
        var path = DataPath(temporary.Paths, completion);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
        await File.WriteAllBytesAsync(
            path,
            mutation(bytes),
            TestContext.CancellationToken);

        var exception = await Assert.ThrowsAsync<CanonicalCacheReadException>(() =>
            CanonicalCacheReader.OpenAsync(
                temporary.Paths,
                request,
                TestContext.CancellationToken));
        Assert.AreEqual(expectedKind, exception.Kind);
    }

    private async Task<CanonicalCacheCompletion> BuildAsync(
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

        return await writer.FinalizeAsync(TestContext.CancellationToken);
    }

    private static async Task<CanonicalRecord[]> CollectAsync(
        IAsyncEnumerable<CanonicalRecord> source)
    {
        var records = new List<CanonicalRecord>();
        await foreach (var record in source)
        {
            records.Add(record);
        }

        return records.ToArray();
    }

    internal static CanonicalRecord[] Records() =>
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

    internal static CanonicalCacheRequest Request() =>
        new(
            new CanonicalReplayIdentity(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "ea-f1-25-v3",
                "apexlab-bahrain-tt-slice-v1",
                "f125-v3-minimal-decoder-v1",
                "apexlab-canonical-sample-v1"),
            10_000_000);

    internal static CanonicalCacheRequest MutateIdentity(
        CanonicalCacheRequest request,
        string decoderId) =>
        new(
            new CanonicalReplayIdentity(
                request.Identity.SourceEvidenceSha256,
                request.Identity.ProtocolId,
                request.Identity.ContractId,
                decoderId,
                request.Identity.CanonicalSchemaId),
            request.SourceStopwatchFrequency);

    internal static string ManifestPath(
        ApplicationPaths paths,
        CanonicalReplayIdentity identity) =>
        Path.Combine(
            paths.DerivedCacheDirectory,
            CanonicalCacheManifest.CreateManifestLeafName(identity));

    private static string DataPath(
        ApplicationPaths paths,
        CanonicalCacheCompletion completion) =>
        Path.Combine(
            paths.DerivedCacheDirectory,
            $"{completion.Identity.IdentitySha256}.{completion.DataSha256}.apxcan");

    private static CanonicalPacket Motion() =>
        CanonicalPacket.CreateMotion(
            new CanonicalPacketHeader(1F, 2, 3, 0, byte.MaxValue),
            new CanonicalMotionPacket(4F, 5F, 6F));

    internal sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-canonical-reader-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
