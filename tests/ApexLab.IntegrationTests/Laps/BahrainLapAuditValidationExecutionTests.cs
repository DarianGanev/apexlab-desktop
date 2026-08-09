using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Protocols.F125.Canonical;
using ApexLab.Replay.LapAudit;

namespace ApexLab.IntegrationTests.Laps;

[TestClass]
[DoNotParallelize]
public sealed class BahrainLapAuditValidationExecutionTests
{
    [TestMethod]
    public async Task IndependentReplaysAgreeAndOwnedCachesAreDeleted()
    {
        using var evidence = RawEvidenceBahrainLapAuditTests.TemporaryRoot.Create(
            "lap-audit-validation-execution");
        var captureId = RawEvidenceCaptureId.Parse(
            "2123456789ab4def8123456789abcdef");
        await RawEvidenceBahrainLapAuditTests.CreateEvidenceAsync(
            evidence.Paths,
            captureId);
        var template = await RawEvidenceBahrainLapAudit.PrepareAsync(
            evidence.Paths,
            captureId,
            new F125BahrainCanonicalProjector(),
            TestContext.CancellationToken);
        await RawEvidenceBahrainLapAuditTests.ReplaceAuditAsync(
            evidence.Paths,
            RawEvidenceBahrainLapAuditTests.Complete(template, 5));
        var roots = new Queue<ApplicationPaths>(
        [
            TemporaryCachePaths(),
            TemporaryCachePaths(),
        ]);
        var owned = roots.Select(paths => paths.RootDirectory).ToArray();

        var result = await BahrainLapAuditValidationExecution.ExecuteAsync(
            evidence.Paths,
            captureId,
            () => roots.Dequeue(),
            TestContext.CancellationToken);

        Assert.AreEqual("PASS", result.Conclusion);
        Assert.AreEqual(BaselineDisposition.Ready, result.Disposition);
        Assert.IsTrue(result.DeterministicSelection);
        Assert.AreEqual(5, result.IncludedCount);
        Assert.AreEqual(2, result.ExcludedCount);
        Assert.IsTrue(owned.All(root => !Directory.Exists(root)));
        RawEvidenceBahrainLapAuditTests.DeleteAllEvidence(evidence.Paths);
    }

    [TestMethod]
    public async Task CancellationDeletesEveryClaimedTemporaryRoot()
    {
        using var evidence = RawEvidenceBahrainLapAuditTests.TemporaryRoot.Create(
            "lap-audit-validation-cancel");
        var captureId = RawEvidenceCaptureId.Parse(
            "3123456789ab4def8123456789abcdef");
        await RawEvidenceBahrainLapAuditTests.CreateEvidenceAsync(
            evidence.Paths,
            captureId);
        var created = new List<string>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            BahrainLapAuditValidationExecution.ExecuteAsync(
                evidence.Paths,
                captureId,
                () =>
                {
                    var paths = TemporaryCachePaths();
                    created.Add(paths.RootDirectory);
                    return paths;
                },
                canceled.Token));

        Assert.IsTrue(created.All(root => !Directory.Exists(root)));
        RawEvidenceBahrainLapAuditTests.DeleteAllEvidence(evidence.Paths);
    }

    public TestContext TestContext { get; set; } = null!;

    private static ApplicationPaths TemporaryCachePaths() =>
        ApplicationPaths.FromRoot(Path.Combine(
            Path.GetTempPath(),
            $"apexlab-lap-audit-validation-{Guid.NewGuid():N}"));
}
