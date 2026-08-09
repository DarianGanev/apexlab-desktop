using System.Buffers.Binary;
using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.CanonicalValidation;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Canonical;

[TestClass]
[DoNotParallelize]
public sealed class CanonicalReplayValidationExecutionTests
{
    [TestMethod]
    public async Task TwoForcedBuildsAndMutationPassThenDeleteTemporaryCaches()
    {
        using var evidence = TemporaryEvidenceRoot.Create();
        var completion = await CreateEvidenceAsync(evidence.Paths);
        var temporaryPaths = new Queue<ApplicationPaths>(
        [
            TemporaryCachePaths(),
            TemporaryCachePaths(),
        ]);
        var allRoots = temporaryPaths
            .Select(paths => paths.RootDirectory)
            .ToArray();

        var result = await CanonicalReplayValidationExecution.ExecuteAsync(
            evidence.Paths,
            completion.CaptureId,
            () => temporaryPaths.Dequeue(),
            TestContext.CancellationToken);

        Assert.IsTrue(result.DeterministicReplay);
        Assert.IsTrue(result.ExplicitGapPolicy);
        Assert.IsTrue(result.StaleCacheRejected);
        Assert.IsTrue(result.PrivateDataExcluded);
        Assert.AreEqual("ea-f1-25-v3", result.ProtocolId);
        Assert.IsTrue(allRoots.All(path => !Directory.Exists(path)));
        DeleteRawEvidence(evidence.Paths, completion.CaptureId);
    }

    [TestMethod]
    public async Task CancellationStillDeletesEveryCreatedTemporaryCache()
    {
        using var evidence = TemporaryEvidenceRoot.Create();
        var completion = await CreateEvidenceAsync(evidence.Paths);
        var created = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CanonicalReplayValidationExecution.ExecuteAsync(
                evidence.Paths,
                completion.CaptureId,
                () =>
                {
                    var paths = TemporaryCachePaths();
                    created.Add(paths.RootDirectory);
                    return paths;
                },
                cancellation.Token));

        Assert.IsTrue(created.All(path => !Directory.Exists(path)));
        DeleteRawEvidence(evidence.Paths, completion.CaptureId);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        var receivedAt = new DateTimeOffset(
            2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        await writer.WriteAsync(DatagramEnvelope.CopyFrom(
            2,
            100,
            receivedAt,
            new DatagramSender(IPAddress.Loopback, 20_777),
            Packet(0, 1_349)));
        await writer.WriteAsync(DatagramEnvelope.CopyFrom(
            4,
            200,
            receivedAt.AddMilliseconds(1),
            new DatagramSender(IPAddress.Loopback, 20_777),
            Packet(7, 1_239)));
        return await writer.FinalizeAsync();
    }

    private static byte[] Packet(byte packetId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = packetId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(15),
            unchecked((uint)BitConverter.SingleToInt32Bits(12.5F)));
        packet[27] = 0;
        packet[28] = byte.MaxValue;
        return packet;
    }

    private static ApplicationPaths TemporaryCachePaths() =>
        ApplicationPaths.FromRoot(Path.Combine(
            Path.GetTempPath(),
            $"apexlab-canonical-validation-{Guid.NewGuid():N}"));

    private static void DeleteRawEvidence(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId)
    {
        var prefix = Path.Combine(paths.RawCapturesDirectory, captureId.Value);
        File.Delete(prefix + ".apxraw");
        File.Delete(prefix + ".apxraw.json");
    }

    private sealed class TemporaryEvidenceRoot : IDisposable
    {
        private TemporaryEvidenceRoot(string path) => Paths = ApplicationPaths.FromRoot(path);

        public ApplicationPaths Paths { get; }

        public static TemporaryEvidenceRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-canonical-evidence-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
