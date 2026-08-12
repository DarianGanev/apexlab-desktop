using System.Text.Json;
using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Replay.LapAudit;

namespace ApexLab.IntegrationTests.Laps;

[TestClass]
public sealed class BahrainLapAuditPrepareCommandTests
{
    [TestMethod]
    public async Task SuccessUsesClosedPrivateSchema()
    {
        var result = await BahrainLapAuditPrepareCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => Task.FromResult(Template(candidateCount: 7)));

        Assert.AreEqual(BahrainLapAuditPrepareExitCode.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion", "schemaId", "status", "lapAuditId",
                "candidateCount", "privateDataExcluded", "nextAction",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("bahrain-lap-audit-prepare-v1", root.GetProperty("schemaId").GetString());
        Assert.AreEqual("prepared", root.GetProperty("status").GetString());
        Assert.AreEqual(BahrainLapAuditContract.AuditId, root.GetProperty("lapAuditId").GetString());
        Assert.AreEqual(7, root.GetProperty("candidateCount").GetInt32());
        Assert.IsTrue(root.GetProperty("privateDataExcluded").GetBoolean());
        Assert.AreEqual("completePrivateManualAudit", root.GetProperty("nextAction").GetString());
        AssertPrivateValuesExcluded(result.Json);
    }

    [TestMethod]
    public async Task FewerThanFiveCompleteCandidatesRequestsMoreEvidence()
    {
        var result = await BahrainLapAuditPrepareCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => Task.FromResult(Template(candidateCount: 3)));

        using var document = JsonDocument.Parse(result.Json);
        Assert.AreEqual(
            "captureMoreLaps",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    [TestMethod]
    public async Task ExactArgumentsAndBoundedFailuresAreEnforced()
    {
        foreach (var invalid in new[]
                 {
                     new[] { "prepare-bahrain-lap-audit" },
                     new[]
                     {
                         "prepare-bahrain-lap-audit", "--data-root", "relative",
                         "--capture-id", "00112233445546778899aabbccddeeff",
                     },
                     ValidArguments().Concat(new[] { "--extra", "value" }).ToArray(),
                 })
        {
            var result = await BahrainLapAuditPrepareCommand.ExecuteAsync(
                invalid,
                TestContext.CancellationToken);
            AssertFailure(
                result,
                BahrainLapAuditPrepareExitCode.InvalidArguments,
                "invalidArguments");
        }

        var evidence = await BahrainLapAuditPrepareCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => throw new IOException(
                "C:\\private\\capture-secret.apxraw deadbeef"));
        AssertFailure(
            evidence,
            BahrainLapAuditPrepareExitCode.InvalidEvidence,
            "invalidEvidence");
        AssertPrivateValuesExcluded(evidence.Json);

        var existing = await BahrainLapAuditPrepareCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => throw new BahrainLapAuditStoreException(
                BahrainLapAuditStoreFailureKind.AlreadyExists,
                "private path"));
        AssertFailure(
            existing,
            BahrainLapAuditPrepareExitCode.AlreadyExists,
            "alreadyExists");

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var interrupted = await BahrainLapAuditPrepareCommand.ExecuteAsync(
            ValidArguments(),
            canceled.Token,
            static (_, _, token) =>
                Task.FromCanceled<BahrainLapAuditDocument>(token));
        AssertFailure(
            interrupted,
            BahrainLapAuditPrepareExitCode.Interrupted,
            "interrupted");
    }

    public TestContext TestContext { get; set; } = null!;

    private static string[] ValidArguments() =>
    [
        "prepare-bahrain-lap-audit",
        "--data-root",
        Path.Combine(Path.GetTempPath(), "apexlab-lap-audit-command"),
        "--capture-id",
        "00112233445546778899aabbccddeeff",
    ];

    private static BahrainLapAuditDocument Template(int candidateCount)
    {
        var entries = Enumerable.Range(0, candidateCount).Select(index =>
            new BahrainLapAuditEntry(
                new LapCandidateId($"{index:x64}"),
                LapBoundary.Complete(
                    index + 1L,
                    index + 2L,
                    (byte)(index + 1),
                    checked((uint)(90_000 + index))),
                LapEvidenceFlags.None,
                context: null));
        return new(
            1,
            BahrainLapAuditContract.AuditId,
            new string('a', 64),
            new string('b', 64),
            referenceContext: null,
            BahrainLapAuditManualInputs.Empty(),
            entries);
    }

    private static void AssertFailure(
        BahrainLapAuditPrepareCommandResult result,
        BahrainLapAuditPrepareExitCode exitCode,
        string status)
    {
        Assert.AreEqual(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        CollectionAssert.AreEqual(
            new[] { "schemaVersion", "schemaId", "status" },
            document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(status, document.RootElement.GetProperty("status").GetString());
    }

    private static void AssertPrivateValuesExcluded(string json)
    {
        string[] forbidden =
        [
            "captureId", "sha256", "path", "session", "lapTime", "setup",
            "00112233445546778899aabbccddeeff", "deadbeef", "capture-secret",
        ];
        foreach (var value in forbidden)
        {
            Assert.IsFalse(json.Contains(value, StringComparison.OrdinalIgnoreCase), value);
        }
    }
}
