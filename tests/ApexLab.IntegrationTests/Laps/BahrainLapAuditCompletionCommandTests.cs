using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApexLab.Application.Laps;
using ApexLab.Persistence.Laps;
using ApexLab.Replay.LapAudit;

namespace ApexLab.IntegrationTests.Laps;

[TestClass]
public sealed class BahrainLapAuditCompletionCommandTests
{
    [TestMethod]
    public async Task PrivateStandardInputProducesOnlyAClosedAggregateReport()
    {
        BahrainLapAuditManualInputs? observed = null;

        var result = await BahrainLapAuditCompletionCommand.ExecuteAsync(
            ValidArguments(),
            new StringReader(ValidPrivateInput()),
            TestContext.CancellationToken,
            (_, _, inputs, _) =>
            {
                observed = inputs;
                return Task.FromResult(
                    BahrainLapAuditCompletionExecutionResult.Completed(7, 2));
            });

        Assert.AreEqual(BahrainLapAuditCompletionExitCode.Success, result.ExitCode);
        Assert.IsNotNull(observed);
        Assert.AreEqual("synthetic-private-vehicle", observed.PlayerVehicle);
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion", "schemaId", "status", "lapAuditId",
                "includedCount", "excludedCount", "pendingCount",
                "minimumRequiredCount", "allCandidatesAudited",
                "includedContextsMatch", "baselineDisposition",
                "privateDataExcluded", "nextAction",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("completed", root.GetProperty("status").GetString());
        Assert.AreEqual(7, root.GetProperty("includedCount").GetInt32());
        Assert.AreEqual(2, root.GetProperty("excludedCount").GetInt32());
        Assert.IsTrue(root.GetProperty("allCandidatesAudited").GetBoolean());
        Assert.AreEqual(
            "ready",
            root.GetProperty("baselineDisposition").GetString());
        Assert.IsTrue(root.GetProperty("privateDataExcluded").GetBoolean());
        Assert.AreEqual(
            "validateBahrainLapAudit",
            root.GetProperty("nextAction").GetString());
        AssertPrivateValuesExcluded(result.Json);
    }

    [TestMethod]
    public async Task ValidUnderMinimumAuditIsPersistedButReportedAsAbstained()
    {
        var result = await BahrainLapAuditCompletionCommand.ExecuteAsync(
            ValidArguments(),
            new StringReader(ValidPrivateInput()),
            TestContext.CancellationToken,
            static (_, _, _, _) => Task.FromResult(
                BahrainLapAuditCompletionExecutionResult.Abstained(4, 2)));

        Assert.AreEqual(BahrainLapAuditCompletionExitCode.Abstained, result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        Assert.AreEqual(
            "abstained",
            document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "abstained",
            document.RootElement.GetProperty("baselineDisposition").GetString());
        Assert.AreEqual(
            "captureMoreLaps",
            document.RootElement.GetProperty("nextAction").GetString());
        AssertPrivateValuesExcluded(result.Json);
    }

    [TestMethod]
    public async Task ArgumentsAndPrivateInputAreExactBoundedAndFullyConfirmed()
    {
        foreach (var arguments in new[]
                 {
                     new[] { "complete-bahrain-lap-audit" },
                     ValidArguments()[..^1],
                     ValidArguments().Concat(new[] { "--extra" }).ToArray(),
                 })
        {
            var result = await ExecuteNeverAsync(arguments, ValidPrivateInput());
            AssertFailure(
                result,
                BahrainLapAuditCompletionExitCode.InvalidArguments,
                "invalidArguments");
        }

        var interactive = await BahrainLapAuditCompletionCommand.ExecuteAsync(
            ValidArguments(),
            new ExplodingTextReader(),
            privateInputRedirected: false,
            cancellationToken: TestContext.CancellationToken,
            evaluator: static (_, _, _, _) => throw new AssertFailedException(
                "The evaluator must not run."));
        AssertFailure(
            interactive,
            BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
            "invalidPrivateInput");

        foreach (var input in new[]
                 {
                     "{}",
                     ValidPrivateInput().Replace(
                         "\"contextCrossCheckPassed\":true",
                         "\"contextCrossCheckPassed\":false",
                         StringComparison.Ordinal),
                     ValidPrivateInput()[..^1] + ",\"extra\":true}",
                     new string('x', 2_049),
                 })
        {
            var result = await ExecuteNeverAsync(ValidArguments(), input);
            AssertFailure(
                result,
                BahrainLapAuditCompletionExitCode.InvalidPrivateInput,
                "invalidPrivateInput");
            AssertPrivateValuesExcluded(result.Json);
        }
    }

    [TestMethod]
    public async Task CompletionFailuresAreBoundedAndNeverLeakPrivateInput()
    {
        var individualReview = await BahrainLapAuditCompletionCommand.ExecuteAsync(
            ValidArguments(),
            new StringReader(ValidPrivateInput()),
            TestContext.CancellationToken,
            static (_, _, _, _) => throw new BahrainLapAuditCompletionException(
                BahrainLapAuditCompletionFailureKind.RequiresIndividualReview,
                "C:\\private\\synthetic-private-vehicle deadbeef"));
        AssertFailure(
            individualReview,
            BahrainLapAuditCompletionExitCode.RequiresIndividualReview,
            "requiresIndividualReview");

        var duplicate = await BahrainLapAuditCompletionCommand.ExecuteAsync(
            ValidArguments(),
            new StringReader(ValidPrivateInput()),
            TestContext.CancellationToken,
            static (_, _, _, _) => throw new BahrainLapAuditStoreException(
                BahrainLapAuditStoreFailureKind.AlreadyExists,
                "C:\\private\\synthetic-private-vehicle deadbeef"));
        AssertFailure(
            duplicate,
            BahrainLapAuditCompletionExitCode.AlreadyCompleted,
            "alreadyCompleted");
        AssertPrivateValuesExcluded(individualReview.Json);
        AssertPrivateValuesExcluded(duplicate.Json);
    }

    [TestMethod]
    public async Task ProductionProcessAcceptsBomMarkedUtf8StandardInput()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-completion-stdin-{Guid.NewGuid():N}");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "ApexLab.Replay.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
                     {
                         "complete-bahrain-lap-audit",
                         "--data-root",
                         dataRoot,
                         "--capture-id",
                         "00112233445546778899aabbccddeeff",
                         "--context-stdin",
                         "--confirm-every-eligible-candidate",
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Replay process did not start.");
            var input = Encoding.UTF8.GetPreamble()
                .Concat(new UTF8Encoding(false).GetBytes(ValidPrivateInput()))
                .ToArray();
            await process.StandardInput.BaseStream.WriteAsync(
                input,
                TestContext.CancellationToken);
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            await process.WaitForExitAsync(TestContext.CancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            using var report = JsonDocument.Parse(output);
            Assert.AreEqual(42, process.ExitCode, error);
            Assert.AreEqual(
                "invalidEvidence",
                report.RootElement.GetProperty("status").GetString(),
                error);
            AssertPrivateValuesExcluded(output);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProductionProcessSanitizesInvalidUtf8StandardInput()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-completion-invalid-stdin-{Guid.NewGuid():N}");
        try
        {
            var start = ProductionProcessStart(dataRoot);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Replay process did not start.");
            await process.StandardInput.BaseStream.WriteAsync(
                new byte[] { (byte)'{', 0xff, (byte)'}' },
                TestContext.CancellationToken);
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            await process.WaitForExitAsync(TestContext.CancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            using var report = JsonDocument.Parse(output);
            Assert.AreEqual(41, process.ExitCode);
            Assert.AreEqual(
                "invalidPrivateInput",
                report.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(string.Empty, error);
            AssertPrivateValuesExcluded(output);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private Task<BahrainLapAuditCompletionCommandResult> ExecuteNeverAsync(
        IReadOnlyList<string> arguments,
        string input) =>
        BahrainLapAuditCompletionCommand.ExecuteAsync(
            arguments,
            new StringReader(input),
            TestContext.CancellationToken,
            static (_, _, _, _) => throw new AssertFailedException(
                "The evaluator must not run."));

    private static string[] ValidArguments() =>
    [
        "complete-bahrain-lap-audit",
        "--data-root",
        Path.Combine(Path.GetTempPath(), "apexlab-lap-audit-completion"),
        "--capture-id",
        "00112233445546778899aabbccddeeff",
        "--context-stdin",
        "--confirm-every-eligible-candidate",
    ];

    private static ProcessStartInfo ProductionProcessStart(string dataRoot)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "ApexLab.Replay.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "complete-bahrain-lap-audit",
                     "--data-root",
                     dataRoot,
                     "--capture-id",
                     "00112233445546778899aabbccddeeff",
                     "--context-stdin",
                     "--confirm-every-eligible-candidate",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static string ValidPrivateInput() =>
        """
        {"gameBuild":"synthetic-private-build","playerVehicle":"synthetic-private-vehicle","controllerProfile":"synthetic-private-controller","setupDescriptor":"synthetic-private-setup","tyreCompound":"synthetic-private-tyre","evidenceIntegrityPassed":true,"trackAndModeVisuallyConfirmed":true,"setupUnchanged":true,"contextCrossCheckPassed":true}
        """;

    private static void AssertFailure(
        BahrainLapAuditCompletionCommandResult result,
        BahrainLapAuditCompletionExitCode exitCode,
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
        foreach (var sentinel in new[]
                 {
                     "captureId", "sha256", "path", "candidateId", "lapTime",
                     "setupDescriptor", "controllerProfile", "playerVehicle",
                     "00112233445546778899aabbccddeeff", "deadbeef",
                     "synthetic-private",
                 })
        {
            Assert.IsFalse(
                json.Contains(sentinel, StringComparison.OrdinalIgnoreCase),
                sentinel);
        }
    }

    private sealed class ExplodingTextReader : TextReader
    {
        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Private input must not be read.");
    }
}
