using System.Text.Json;
using ApexLab.Application.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Replay.LapAudit;

namespace ApexLab.IntegrationTests.Laps;

[TestClass]
public sealed class BahrainLapAuditValidationCommandTests
{
    [TestMethod]
    public async Task PassAndAbstainUseSameClosedAggregateSchema()
    {
        var passed = await ExecuteAsync(
            BahrainLapAuditValidationExecutionResult.Passed(5, 2));
        var abstained = await ExecuteAsync(
            BahrainLapAuditValidationExecutionResult.Abstained(4, 3));

        Assert.AreEqual(BahrainLapAuditValidationExitCode.Success, passed.ExitCode);
        Assert.AreEqual(BahrainLapAuditValidationExitCode.Abstained, abstained.ExitCode);
        AssertReport(passed.Json, "passed", "PASS", 5, 2);
        AssertReport(abstained.Json, "abstained", "ABSTAIN", 4, 3);
        AssertPrivateValuesExcluded(passed.Json);
        AssertPrivateValuesExcluded(abstained.Json);
    }

    [TestMethod]
    public async Task NondeterminismAndFailuresNeverPassOrLeak()
    {
        var failed = await ExecuteAsync(
            BahrainLapAuditValidationExecutionResult.Failed(5, 2));
        Assert.AreEqual(
            BahrainLapAuditValidationExitCode.ValidationFailed,
            failed.ExitCode);
        AssertReport(failed.Json, "failed", "FAIL", 5, 2);

        var invalidArguments = await BahrainLapAuditValidationCommand.ExecuteAsync(
            ["validate-bahrain-lap-audit"],
            TestContext.CancellationToken);
        AssertFailure(
            invalidArguments,
            BahrainLapAuditValidationExitCode.InvalidArguments,
            "invalidArguments");

        var invalidAudit = await BahrainLapAuditValidationCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => throw new BahrainLapAuditException(
                BahrainLapAuditFailureKind.ProvenanceMismatch,
                "C:\\private\\capture-secret.apxraw deadbeef"));
        AssertFailure(
            invalidAudit,
            BahrainLapAuditValidationExitCode.InvalidAudit,
            "invalidAudit");
        AssertPrivateValuesExcluded(invalidAudit.Json);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var interrupted = await BahrainLapAuditValidationCommand.ExecuteAsync(
            ValidArguments(),
            canceled.Token,
            static (_, _, token) =>
                Task.FromCanceled<BahrainLapAuditValidationExecutionResult>(token));
        AssertFailure(
            interrupted,
            BahrainLapAuditValidationExitCode.Interrupted,
            "interrupted");
    }

    public TestContext TestContext { get; set; } = null!;

    private Task<BahrainLapAuditValidationCommandResult> ExecuteAsync(
        BahrainLapAuditValidationExecutionResult execution) =>
        BahrainLapAuditValidationCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            (_, _, _) => Task.FromResult(execution));

    private static string[] ValidArguments() =>
    [
        "validate-bahrain-lap-audit",
        "--data-root",
        Path.Combine(Path.GetTempPath(), "apexlab-lap-audit-validation"),
        "--capture-id",
        "00112233445546778899aabbccddeeff",
    ];

    private static void AssertReport(
        string json,
        string status,
        string conclusion,
        int included,
        int excluded)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion", "schemaId", "status", "lapAuditId",
                "includedCount", "excludedCount", "pendingCount",
                "minimumRequiredCount", "allCandidatesAudited",
                "includedContextsMatch", "provenanceComplete",
                "deterministicSelection", "privateDataExcluded", "conclusion",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(status, root.GetProperty("status").GetString());
        Assert.AreEqual(conclusion, root.GetProperty("conclusion").GetString());
        Assert.AreEqual(included, root.GetProperty("includedCount").GetInt32());
        Assert.AreEqual(excluded, root.GetProperty("excludedCount").GetInt32());
        Assert.AreEqual(5, root.GetProperty("minimumRequiredCount").GetInt32());
        Assert.IsTrue(root.GetProperty("privateDataExcluded").GetBoolean());
    }

    private static void AssertFailure(
        BahrainLapAuditValidationCommandResult result,
        BahrainLapAuditValidationExitCode exitCode,
        string status)
    {
        Assert.AreEqual(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        CollectionAssert.AreEqual(
            new[] { "schemaVersion", "schemaId", "status", "conclusion" },
            document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(status, document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("FAIL", document.RootElement.GetProperty("conclusion").GetString());
    }

    private static void AssertPrivateValuesExcluded(string json)
    {
        using var document = JsonDocument.Parse(json);
        var propertyNames = EnumeratePropertyNames(document.RootElement).ToArray();
        string[] forbiddenProperties =
        [
            "captureId", "sha256", "path", "session", "lapTime", "setup",
            "candidateId", "boundary", "reason", "note", "telemetry",
        ];
        foreach (var property in forbiddenProperties)
        {
            Assert.IsFalse(
                propertyNames.Contains(property, StringComparer.OrdinalIgnoreCase),
                property);
        }

        foreach (var sentinel in new[]
                 {
                     "00112233445546778899aabbccddeeff",
                     "deadbeef",
                     "capture-secret",
                 })
        {
            Assert.IsFalse(
                json.Contains(sentinel, StringComparison.OrdinalIgnoreCase),
                sentinel);
        }
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
