using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Protocols.F125;
using ApexLab.Protocols.F125.Decoding;
using ApexLab.Replay.BahrainValidation;

namespace ApexLab.IntegrationTests.Replay;

[TestClass]
public sealed class BahrainDecoderValidationCommandTests
{
    private const string CaptureId = "00112233445546778899aabbccddeeff";

    [TestMethod]
    public void ParsesOnlyTheExactAbsoluteIdentityGrammar()
    {
        var root = Path.Combine(Path.GetTempPath(), "apexlab-bahrain-validator");

        var accepted = BahrainDecoderValidationArguments.TryParse(
            [
                "validate-bahrain-decoder",
                "--data-root", root,
                "--capture-id", CaptureId,
            ],
            out var parsed);

        Assert.IsTrue(accepted);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(Path.GetFullPath(root), parsed.DataRoot);
        Assert.AreEqual(CaptureId, parsed.CaptureId.Value);

        string[][] rejected =
        [
            [],
            ["validate-bahrain-decoder"],
            ["validate-bahrain-decoder", "--data-root", "relative", "--capture-id", CaptureId],
            ["validate-bahrain-decoder", "--data-root", root, "--capture-id", "invalid"],
            ["validate-bahrain-decoder", "--capture-id", CaptureId, "--capture-id", CaptureId],
            ["validate-bahrain-decoder", "--data-root", root, "--capture-id", CaptureId, "extra"],
        ];

        foreach (var arguments in rejected)
        {
            Assert.IsFalse(BahrainDecoderValidationArguments.TryParse(arguments, out _));
        }
    }

    [TestMethod]
    public async Task CompleteEvaluationProducesAClosedPrivacySafePassSchema()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), "private-bahrain-root");
        var result = await BahrainDecoderValidationCommand.ExecuteAsync(
            Arguments(privateRoot),
            CancellationToken.None,
            (_, _, _) => Task.FromResult(BahrainDecoderReplayResult.Complete));

        Assert.AreEqual(BahrainDecoderValidationExitCode.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        AssertEvaluationPropertySet(document.RootElement);
        Assert.AreEqual("bahrain-decoder-validation-v1", document.RootElement.GetProperty("schemaId").GetString());
        Assert.AreEqual("passed", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(F125Protocol.Id, document.RootElement.GetProperty("protocolId").GetString());
        Assert.AreEqual("apexlab-bahrain-tt-slice-v1", document.RootElement.GetProperty("contractId").GetString());
        Assert.AreEqual(F125BahrainPacketDecoder.DecoderId, document.RootElement.GetProperty("decoderId").GetString());
        Assert.AreEqual("PASS", document.RootElement.GetProperty("conclusion").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("privateDataExcluded").GetBoolean());
        Assert.DoesNotContain(CaptureId, result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateRoot, result.Json, StringComparison.OrdinalIgnoreCase);
        AssertNoForbiddenKeys(document.RootElement);
    }

    [TestMethod]
    public async Task IncompleteAndRejectedEvaluationsFailWithClosedSchemas()
    {
        var incomplete = await BahrainDecoderValidationCommand.ExecuteAsync(
            Arguments(Path.GetTempPath()),
            CancellationToken.None,
            (_, _, _) => Task.FromResult(
                BahrainDecoderReplayResult.Complete with { EventDecoded = false }));
        var rejected = await BahrainDecoderValidationCommand.ExecuteAsync(
            Arguments(Path.GetTempPath()),
            CancellationToken.None,
            (_, _, _) => Task.FromResult(
                BahrainDecoderReplayResult.Complete with { SelectedPacketsRejected = true }));

        AssertEvaluationFailure(
            incomplete,
            BahrainDecoderValidationExitCode.IncompleteSlice,
            "incompleteSlice");
        using (var document = JsonDocument.Parse(incomplete.Json))
        {
            Assert.IsFalse(document.RootElement.GetProperty("selectedEventDecoded").GetBoolean());
            Assert.IsFalse(document.RootElement.GetProperty("selectedPacketsRejected").GetBoolean());
        }

        AssertEvaluationFailure(
            rejected,
            BahrainDecoderValidationExitCode.SelectedPacketRejected,
            "selectedPacketRejected");
        using (var document = JsonDocument.Parse(rejected.Json))
        {
            Assert.IsTrue(document.RootElement.GetProperty("selectedPacketsRejected").GetBoolean());
        }
    }

    [TestMethod]
    public async Task CancellationAndExceptionsNeverEscapeTheSafeJsonBoundary()
    {
        var privateText = "private exception text";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = await BahrainDecoderValidationCommand.ExecuteAsync(
            Arguments(Path.GetTempPath()),
            cancellation.Token,
            (_, _, token) => Task.FromCanceled<BahrainDecoderReplayResult>(token));
        var unexpected = await BahrainDecoderValidationCommand.ExecuteAsync(
            Arguments(Path.GetTempPath()),
            CancellationToken.None,
            (_, _, _) => throw new InvalidOperationException(privateText));

        AssertFailure(interrupted, BahrainDecoderValidationExitCode.Interrupted, "interrupted");
        AssertFailure(unexpected, BahrainDecoderValidationExitCode.UnexpectedFailure, "unexpectedFailure");
        Assert.DoesNotContain(privateText, unexpected.Json, StringComparison.Ordinal);
    }

    private static string[] Arguments(string root) =>
    [
        "validate-bahrain-decoder",
        "--data-root", Path.GetFullPath(root),
        "--capture-id", CaptureId,
    ];

    private static void AssertFailure(
        BahrainDecoderValidationCommandResult result,
        BahrainDecoderValidationExitCode expectedExitCode,
        string expectedStatus)
    {
        Assert.AreEqual(expectedExitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        AssertPropertySet(document.RootElement, "schemaVersion", "schemaId", "status", "conclusion");
        Assert.AreEqual(expectedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("FAIL", document.RootElement.GetProperty("conclusion").GetString());
        AssertNoForbiddenKeys(document.RootElement);
    }

    private static void AssertEvaluationFailure(
        BahrainDecoderValidationCommandResult result,
        BahrainDecoderValidationExitCode expectedExitCode,
        string expectedStatus)
    {
        Assert.AreEqual(expectedExitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        AssertEvaluationPropertySet(document.RootElement);
        Assert.AreEqual(expectedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("FAIL", document.RootElement.GetProperty("conclusion").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("privateDataExcluded").GetBoolean());
        AssertNoForbiddenKeys(document.RootElement);
    }

    private static void AssertEvaluationPropertySet(JsonElement element)
    {
        AssertPropertySet(
            element,
            "schemaVersion", "schemaId", "status", "protocolId", "contractId",
            "decoderId", "motionDecoded", "sessionDecoded", "lapDecoded",
            "selectedEventDecoded", "carTelemetryDecoded", "selectedPacketsRejected",
            "privateDataExcluded", "conclusion");
    }

    private static void AssertPropertySet(JsonElement element, params string[] expected)
    {
        CollectionAssert.AreEquivalent(
            expected,
            element.EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static void AssertNoForbiddenKeys(JsonElement element)
    {
        string[] forbidden =
        [
            "capture", "path", "hash", "sender", "count", "rate", "time",
            "frame", "sessionUid", "lapTime", "position", "throttle", "brake", "gear",
        ];
        foreach (var property in element.EnumerateObject())
        {
            Assert.IsFalse(
                forbidden.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)),
                property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                AssertNoForbiddenKeys(property.Value);
            }
        }
    }
}
