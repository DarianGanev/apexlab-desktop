using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Replay.CanonicalValidation;

namespace ApexLab.IntegrationTests.Canonical;

[TestClass]
[DoNotParallelize]
public sealed class CanonicalReplayValidationCommandTests
{
    [TestMethod]
    public async Task SuccessUsesTheClosedCommitSafeSchema()
    {
        var result = await CanonicalReplayValidationCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => Task.FromResult(
                CanonicalReplayValidationExecutionResult.Passed(
                    "ea-f1-25-v3",
                    "apexlab-bahrain-tt-slice-v1",
                    "f125-v3-minimal-decoder-v1",
                    "apexlab-canonical-sample-v1")));

        Assert.AreEqual(CanonicalReplayValidationExitCode.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion",
                "schemaId",
                "status",
                "protocolId",
                "contractId",
                "decoderId",
                "canonicalSchemaId",
                "deterministicReplay",
                "explicitGapPolicy",
                "staleCacheRejected",
                "privateDataExcluded",
                "conclusion",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("canonical-replay-validation-v1", root.GetProperty("schemaId").GetString());
        Assert.AreEqual("passed", root.GetProperty("status").GetString());
        Assert.IsTrue(root.GetProperty("deterministicReplay").GetBoolean());
        Assert.IsTrue(root.GetProperty("explicitGapPolicy").GetBoolean());
        Assert.IsTrue(root.GetProperty("staleCacheRejected").GetBoolean());
        Assert.IsTrue(root.GetProperty("privateDataExcluded").GetBoolean());
        Assert.AreEqual("PASS", root.GetProperty("conclusion").GetString());
        AssertPrivateValuesExcluded(result.Json);
    }

    [TestMethod]
    public async Task InvalidArgumentsFailureAndCancellationAreBounded()
    {
        var invalid = await CanonicalReplayValidationCommand.ExecuteAsync(
            ["validate-canonical-replay"],
            TestContext.CancellationToken);
        Assert.AreEqual(
            CanonicalReplayValidationExitCode.InvalidArguments,
            invalid.ExitCode);
        AssertFailure(invalid.Json, "invalidArguments");

        var failure = await CanonicalReplayValidationCommand.ExecuteAsync(
            ValidArguments(),
            TestContext.CancellationToken,
            static (_, _, _) => throw new IOException(
                "C:\\private\\capture-secret.apxraw deadbeef"));
        Assert.AreEqual(
            CanonicalReplayValidationExitCode.InvalidEvidence,
            failure.ExitCode);
        AssertFailure(failure.Json, "invalidEvidence");
        AssertPrivateValuesExcluded(failure.Json);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interrupted = await CanonicalReplayValidationCommand.ExecuteAsync(
            ValidArguments(),
            cancellation.Token,
            static (_, _, token) => Task.FromCanceled<
                CanonicalReplayValidationExecutionResult>(token));
        Assert.AreEqual(
            CanonicalReplayValidationExitCode.Interrupted,
            interrupted.ExitCode);
        AssertFailure(interrupted.Json, "interrupted");
    }

    public TestContext TestContext { get; set; } = null!;

    private static string[] ValidArguments() =>
    [
        "validate-canonical-replay",
        "--data-root",
        Path.Combine(Path.GetTempPath(), "apexlab-command-test"),
        "--capture-id",
        "00112233445546778899aabbccddeeff",
    ];

    private static void AssertFailure(string json, string expectedStatus)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[] { "schemaVersion", "schemaId", "status", "conclusion" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(expectedStatus, root.GetProperty("status").GetString());
        Assert.AreEqual("FAIL", root.GetProperty("conclusion").GetString());
    }

    private static void AssertPrivateValuesExcluded(string json)
    {
        string[] forbidden =
        [
            "captureId",
            "sha256",
            "path",
            "sender",
            "recordCount",
            "packetId",
            "session",
            "frame",
            "00112233445546778899aabbccddeeff",
            "deadbeef",
            "capture-secret",
        ];
        foreach (var value in forbidden)
        {
            Assert.IsFalse(
                json.Contains(value, StringComparison.OrdinalIgnoreCase),
                value);
        }
    }
}
