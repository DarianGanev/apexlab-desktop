using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Probe;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Validation;

[TestClass]
public sealed class PrivateF125PowerShellTests
{
    [TestMethod]
    public async Task DerivesFeasibleRateGateBoundsWithoutPrintingThem()
    {
        using var lower = await InvokeModuleAsync(
            "$value = Get-ApexLabRateGatePlan -Peak 70; $value | ConvertTo-Json -Compress");
        using var upper = await InvokeModuleAsync(
            "$value = Get-ApexLabRateGatePlan -Peak 37878; $value | ConvertTo-Json -Compress");

        Assert.AreEqual(0, lower.ExitCode, lower.StandardError);
        Assert.AreEqual(0, upper.ExitCode, upper.StandardError);
        using var lowerJson = JsonDocument.Parse(lower.StandardOutput);
        using var upperJson = JsonDocument.Parse(upper.StandardOutput);
        Assert.AreEqual(
            140,
            lowerJson.RootElement.GetProperty("MinimumRate").GetInt32());
        Assert.AreEqual(
            154,
            lowerJson.RootElement.GetProperty("TargetRate").GetInt32());
        Assert.AreEqual(
            10_000,
            lowerJson.RootElement.GetProperty("DatagramCount").GetInt32());
        Assert.AreEqual(
            75_756,
            upperJson.RootElement.GetProperty("MinimumRate").GetInt32());
        Assert.AreEqual(
            83_332,
            upperJson.RootElement.GetProperty("TargetRate").GetInt32());
        Assert.AreEqual(
            4_999_920,
            upperJson.RootElement.GetProperty("DatagramCount").GetInt32());
    }

    [TestMethod]
    public async Task SelectsExactlyOneNewCanonicalFinalizedManifest()
    {
        const string prior = "00112233445546778899aabbccddeeff";
        const string added = "1122334455664778899aabbccddeeff0";
        using var success = await InvokeModuleAsync(
            "$value = Select-ApexLabNewCaptureId "
            + $"-Before @('{prior}.apxraw.json') "
            + $"-After @('{prior}.apxraw.json','{added}.apxraw.json'); "
            + "$value");

        Assert.AreEqual(0, success.ExitCode, success.StandardError);
        Assert.AreEqual(added, success.StandardOutput.Trim());

        string[] rejectedScripts =
        [
            $"Select-ApexLabNewCaptureId -Before @('{prior}.apxraw.json') -After @('{prior}.apxraw.json')",
            $"Select-ApexLabNewCaptureId -Before @() -After @('{prior}.apxraw.json','{added}.apxraw.json')",
            "Select-ApexLabNewCaptureId -Before @() -After @('invalid.apxraw.json')",
        ];
        foreach (var script in rejectedScripts)
        {
            using var rejected = await InvokeModuleAsync(script);
            Assert.AreNotEqual(0, rejected.ExitCode, script);
            Assert.AreEqual(string.Empty, rejected.StandardOutput);
        }
    }

    [TestMethod]
    public async Task RejectsUnsafeGameBuildTextAndRateBounds()
    {
        string[] rejectedScripts =
        [
            "Assert-ApexLabGameBuild -GameBuild ''",
            "Assert-ApexLabGameBuild -GameBuild 'folder/build'",
            "Assert-ApexLabGameBuild -GameBuild 'folder\\build'",
            "Assert-ApexLabGameBuild -GameBuild ('x' * 81)",
            "Get-ApexLabRateGatePlan -Peak 0",
            "Get-ApexLabRateGatePlan -Peak 37879",
        ];

        foreach (var script in rejectedScripts)
        {
            using var rejected = await InvokeModuleAsync(script);
            Assert.AreNotEqual(0, rejected.ExitCode, script);
            Assert.AreEqual(string.Empty, rejected.StandardOutput);
        }
    }

    [TestMethod]
    public async Task MapsEveryWorkflowStageToAStablePublicExitCode()
    {
        using var result = await InvokeModuleAsync(
            "@('preflight','probe','probeEvaluation','capture',"
            + "'captureSelection','privateValidation','rateGate','safeSummary',"
            + "'cancelled','unexpectedFailure') | ForEach-Object { "
            + "Get-ApexLabPrivateValidationExitCode -Stage $_ } | "
            + "ConvertTo-Json -Compress");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        CollectionAssert.AreEqual(
            new[] { 40, 41, 42, 43, 44, 45, 46, 47, 48, 49 },
            JsonSerializer.Deserialize<int[]>(result.StandardOutput));

        using var rejected = await InvokeModuleAsync(
            "Get-ApexLabPrivateValidationExitCode -Stage 'private-path'");
        Assert.AreNotEqual(0, rejected.ExitCode);
        Assert.AreEqual(string.Empty, rejected.StandardOutput);
    }

    [TestMethod]
    public async Task AcceptsOnlyTheSafeValidatorContractAndBuildsSafeSummary()
    {
        const string validatorJson = """
            {
              "schemaVersion": 1,
              "status": "validated",
              "protocolId": "ea-f1-25-v3",
              "manifestIntegrity": true,
              "deterministicReplay": true,
              "sequenceGapPreservation": true,
              "zeroPrivacyExcludedEvidence": true,
              "probeAssumptions": true
            }
            """;
        var environment = new Dictionary<string, string>
        {
            ["APEXLAB_TEST_JSON"] = validatorJson,
        };
        using var result = await InvokeModuleAsync(
            "$validator = Assert-ApexLabSafeValidatorJson -Json $env:APEXLAB_TEST_JSON; "
            + "$summary = New-ApexLabSafeSummary -GameBuild '1.2.3 test' "
            + "-AdapterId $validator.protocolId -ApplicationVersion '0.1.0' "
            + "-ValidationDate '2026-08-08'; $summary | ConvertTo-Json -Compress",
            environment);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        string[] expectedProperties =
        [
            "schemaVersion", "status", "validationDate", "gameBuild",
            "adapterId", "applicationVersion", "offlineCapture",
            "manifestIntegrity", "deterministicReplay",
            "sequenceGapPreservation", "zeroPrivacyExcludedEvidence",
            "probeAssumptions", "rateGate2x", "conclusion",
        ];
        CollectionAssert.AreEquivalent(
            expectedProperties,
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(
            "passed",
            document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "PASS",
            document.RootElement.GetProperty("conclusion").GetString());
        Assert.DoesNotContain(
            "captureId",
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Rate\":",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RejectsUnsafeValidatorJsonVariants()
    {
        string[] rejected =
        [
            "{}",
            "{\"schemaVersion\":2,\"status\":\"validated\"}",
            "{\"schemaVersion\":1,\"status\":\"validated\",\"protocolId\":\"ea-f1-25-v3\",\"manifestIntegrity\":false,\"deterministicReplay\":true,\"sequenceGapPreservation\":true,\"zeroPrivacyExcludedEvidence\":true,\"probeAssumptions\":true}",
            "{\"schemaVersion\":1,\"status\":\"validated\",\"protocolId\":\"ea-f1-25-v3\",\"manifestIntegrity\":true,\"deterministicReplay\":true,\"sequenceGapPreservation\":true,\"zeroPrivacyExcludedEvidence\":true,\"probeAssumptions\":true,\"captureId\":\"private\"}",
        ];

        foreach (var json in rejected)
        {
            using var result = await InvokeModuleAsync(
                "Assert-ApexLabSafeValidatorJson -Json $env:APEXLAB_TEST_JSON",
                new Dictionary<string, string>
                {
                    ["APEXLAB_TEST_JSON"] = json,
                });
            Assert.AreNotEqual(0, result.ExitCode, json);
            Assert.AreEqual(string.Empty, result.StandardOutput);
        }
    }

    [TestMethod]
    public async Task CapturesChildOutputAndPassesAdversarialArgumentLiterally()
    {
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-process-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var childPath = Path.Combine(temporary, "child.ps1");
        var stdoutPath = Path.Combine(temporary, "stdout.txt");
        var stderrPath = Path.Combine(temporary, "stderr.txt");
        const string argument = "value;$(Set-Content should-not-exist.txt bad)";
        await File.WriteAllTextAsync(
            childPath,
            "param([string] $Value)\n"
            + "[Console]::Out.WriteLine('OUT:' + $Value)\n"
            + "[Console]::Error.WriteLine('PRIVATE-CHILD-SENTINEL')\n"
            + "exit 7\n");
        try
        {
            using var result = await InvokeModuleAsync(
                "$result = Invoke-ApexLabCapturedProcess -FilePath 'powershell.exe' "
                + "-ArgumentList @('-NoProfile','-File',$env:APEXLAB_CHILD,$env:APEXLAB_ARGUMENT) "
                + "-StandardOutputPath $env:APEXLAB_STDOUT -StandardErrorPath $env:APEXLAB_STDERR; "
                + "[pscustomobject]@{ExitCode=$result.ExitCode;Out=[IO.File]::ReadAllText($env:APEXLAB_STDOUT);"
                + "Err=[IO.File]::ReadAllText($env:APEXLAB_STDERR)} | ConvertTo-Json -Compress",
                new Dictionary<string, string>
                {
                    ["APEXLAB_CHILD"] = childPath,
                    ["APEXLAB_ARGUMENT"] = argument,
                    ["APEXLAB_STDOUT"] = stdoutPath,
                    ["APEXLAB_STDERR"] = stderrPath,
                });

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual(
                7,
                document.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.AreEqual(
                JsonValueKind.String,
                document.RootElement.GetProperty("Out").ValueKind,
                result.StandardOutput);
            Assert.AreEqual(
                $"OUT:{argument}\r\n",
                document.RootElement.GetProperty("Out").GetString(),
                result.StandardOutput);
            Assert.AreEqual(
                "PRIVATE-CHILD-SENTINEL\r\n",
                document.RootElement.GetProperty("Err").GetString());
            Assert.IsFalse(File.Exists(Path.Combine(
                FindRepositoryRoot(),
                "should-not-exist.txt")));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [TestMethod]
    public async Task EvaluatesProbeBeforeCaptureAndRejectsUnsafeVariants()
    {
        var acceptedJson = CreateAcceptedProbeJson();
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-probe-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var acceptedPath = Path.Combine(temporary, "accepted.json");
            await File.WriteAllTextAsync(acceptedPath, acceptedJson);
            using var accepted = await InvokeModuleAsync(
                "$value = Read-ApexLabPrivateProbePlan -Path $env:APEXLAB_PROBE; "
                + "$value | ConvertTo-Json -Compress",
                new Dictionary<string, string>
                {
                    ["APEXLAB_PROBE"] = acceptedPath,
                });

            Assert.AreEqual(0, accepted.ExitCode, accepted.StandardError);
            using var document = JsonDocument.Parse(accepted.StandardOutput);
            Assert.AreEqual(
                F125Protocol.Id,
                document.RootElement.GetProperty("ProtocolId").GetString());
            Assert.AreEqual(
                3,
                document.RootElement
                    .GetProperty("MeasuredPeakDatagramsPerSecond")
                    .GetInt32());

            string[] rejectedJson =
            [
                acceptedJson.Replace(
                    "\"socketErrors\": 0",
                    "\"socketErrors\": 1",
                    StringComparison.Ordinal),
                acceptedJson.Replace("1349", "1348", StringComparison.Ordinal),
                acceptedJson.Replace(
                    "\"regressions\": 0",
                    "\"regressions\": 1",
                    StringComparison.Ordinal),
                acceptedJson.Insert(
                    acceptedJson.LastIndexOf('}'),
                    ",\"privateUnexpected\":1"),
            ];
            for (var index = 0; index < rejectedJson.Length; index++)
            {
                var path = Path.Combine(temporary, $"rejected-{index}.json");
                await File.WriteAllTextAsync(path, rejectedJson[index]);
                using var rejected = await InvokeModuleAsync(
                    "Read-ApexLabPrivateProbePlan -Path $env:APEXLAB_PROBE",
                    new Dictionary<string, string>
                    {
                        ["APEXLAB_PROBE"] = path,
                    });
                Assert.AreNotEqual(0, rejected.ExitCode, $"variant {index}");
                Assert.AreEqual(string.Empty, rejected.StandardOutput);
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunsTheGuidedWorkflowInOrderAndAlwaysCleansItsRunDirectory()
    {
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-workflow-order-{Guid.NewGuid():N}");
        var script = WorkflowDependencyScript()
            + "$result = Invoke-ApexLabPrivateF125Validation "
            + "-GameBuild '1.2.3 test' -RepositoryRoot $env:APEXLAB_REPOSITORY "
            + "-Dependencies $dependencies; "
            + "[pscustomobject]@{Stages=$script:stages;Cleaned=$script:cleaned;"
            + "Status=$result.status} | ConvertTo-Json -Compress";
        using var result = await InvokeModuleAsync(
            script,
            new Dictionary<string, string>
            {
                ["APEXLAB_REPOSITORY"] = FindRepositoryRoot(),
                ["APEXLAB_RUN_ROOT"] = temporary,
            });

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        CollectionAssert.AreEqual(
            new[]
            {
                "preflight", "probe", "probeEvaluation", "capture",
                "captureSelection", "privateValidation", "rateGate",
                "safeSummary",
            },
            document.RootElement.GetProperty("Stages")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.IsTrue(document.RootElement.GetProperty("Cleaned").GetBoolean());
        Assert.AreEqual(
            "passed",
            document.RootElement.GetProperty("Status").GetString());
        Assert.IsFalse(Directory.Exists(temporary));
    }

    [TestMethod]
    public async Task FailsClosedAtEveryWorkflowStageAndCleansTemporaryFiles()
    {
        string[] stages =
        [
            "preflight", "probe", "probeEvaluation", "capture",
            "captureSelection", "privateValidation", "rateGate",
            "safeSummary",
        ];
        foreach (var stage in stages)
        {
            var temporary = Path.Combine(
                Path.GetTempPath(),
                $"apexlab-workflow-failure-{Guid.NewGuid():N}");
            var script = WorkflowDependencyScript()
                + "try { Invoke-ApexLabPrivateF125Validation "
                + "-GameBuild '1.2.3 test' -RepositoryRoot $env:APEXLAB_REPOSITORY "
                + "-Dependencies $dependencies; exit 90 } catch { "
                + "[pscustomobject]@{Stage=$_.Exception.Data['ApexLabStage'];"
                + "Code=$_.Exception.Data['ApexLabExitCode'];"
                + "Correction=$_.Exception.Data['ApexLabCorrection'];"
                + "Cleaned=$script:cleaned;Stages=$script:stages} | "
                + "ConvertTo-Json -Compress }";
            using var result = await InvokeModuleAsync(
                script,
                new Dictionary<string, string>
                {
                    ["APEXLAB_REPOSITORY"] = FindRepositoryRoot(),
                    ["APEXLAB_RUN_ROOT"] = temporary,
                    ["APEXLAB_FAIL_STAGE"] = stage,
                });

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual(
                stage,
                document.RootElement.GetProperty("Stage").GetString());
            Assert.IsGreaterThanOrEqualTo(
                40,
                document.RootElement.GetProperty("Code").GetInt32());
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("Correction").GetString()));
            Assert.IsTrue(document.RootElement.GetProperty("Cleaned").GetBoolean());
            var observed = document.RootElement.GetProperty("Stages")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.AreEqual(stage, observed[^1]);
            Assert.IsFalse(Directory.Exists(temporary));
        }
    }

    [TestMethod]
    public async Task PublicValidationScriptExposesOnlyTheBoundedGameBuildInput()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "ValidatePrivateF125.ps1");
        using var result = await InvokeModuleAsync(
            "$command=Get-Command -Name $env:APEXLAB_PUBLIC_SCRIPT; "
            + "$parameter=$command.Parameters['GameBuild']; "
            + "$parameterAttribute=$parameter.Attributes | Where-Object { "
            + "$_ -is [Management.Automation.ParameterAttribute] }; "
            + "[pscustomobject]@{Exists=($null-ne $parameter);"
            + "Mandatory=$parameterAttribute.Mandatory;"
            + "HasDependencies=$command.Parameters.ContainsKey('Dependencies')} | "
            + "ConvertTo-Json -Compress",
            new Dictionary<string, string>
            {
                ["APEXLAB_PUBLIC_SCRIPT"] = scriptPath,
            });

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.IsTrue(document.RootElement.GetProperty("Exists").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("Mandatory").GetBoolean());
        Assert.IsFalse(
            document.RootElement.GetProperty("HasDependencies").GetBoolean());
    }

    [TestMethod]
    public async Task MapsInteractiveCancellationToItsStableExitAndStillCleans()
    {
        string[] stages = ["probe", "capture", "privateValidation", "rateGate"];
        foreach (var stage in stages)
        {
            var temporary = Path.Combine(
                Path.GetTempPath(),
                $"apexlab-workflow-cancel-{Guid.NewGuid():N}");
            var script = WorkflowDependencyScript()
                + "try { Invoke-ApexLabPrivateF125Validation "
                + "-GameBuild '1.2.3 test' -RepositoryRoot $env:APEXLAB_REPOSITORY "
                + "-Dependencies $dependencies; exit 90 } catch { "
                + "[pscustomobject]@{Stage=$_.Exception.Data['ApexLabStage'];"
                + "Code=$_.Exception.Data['ApexLabExitCode'];"
                + "Cleaned=$script:cleaned} | ConvertTo-Json -Compress }";
            using var result = await InvokeModuleAsync(
                script,
                new Dictionary<string, string>
                {
                    ["APEXLAB_REPOSITORY"] = FindRepositoryRoot(),
                    ["APEXLAB_RUN_ROOT"] = temporary,
                    ["APEXLAB_CANCEL_STAGE"] = stage,
                });

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual(
                stage,
                document.RootElement.GetProperty("Stage").GetString());
            Assert.AreEqual(48, document.RootElement.GetProperty("Code").GetInt32());
            Assert.IsTrue(document.RootElement.GetProperty("Cleaned").GetBoolean());
            Assert.IsFalse(Directory.Exists(temporary));
        }
    }

    [TestMethod]
    public async Task SyntheticWorkflowUsesRealEvidenceAndValidatorEndToEnd()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"apexlab synthetic $ (proof) {Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(temporary, "staging evidence");
        var dataRoot = Path.Combine(temporary, "workflow data");
        var diagnosticsRoot = Path.Combine(temporary, "private diagnostics");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(diagnosticsRoot);
        var completion = await CreateSyntheticEvidenceAsync(
            ApplicationPaths.FromRoot(stagingRoot));
        var probePath = Path.Combine(temporary, "accepted probe.json");
        await File.WriteAllTextAsync(probePath, CreateAcceptedProbeJson());
        var fixturePath = Path.Combine(
            repositoryRoot,
            "tests",
            "ApexLab.IntegrationTests",
            "Validation",
            "Fixtures",
            "InvokePrivateF125SyntheticWorkflow.ps1");
        var replayPath = Path.Combine(
            repositoryRoot,
            "tools",
            "ApexLab.Replay",
            "bin",
            "Release",
            "net10.0-windows",
            "ApexLab.Replay.exe");
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            string[] arguments =
            [
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                fixturePath,
                "-RepositoryRoot", repositoryRoot,
                "-StagingDataRoot", stagingRoot,
                "-DataRoot", dataRoot,
                "-ProbeSourcePath", probePath,
                "-ReplayPath", replayPath,
                "-DiagnosticsRoot", diagnosticsRoot,
                "-GameBuild", "1.2.3;$(Set-Content synthetic-pwned.txt bad)",
            ];
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo);
            Assert.IsNotNull(process);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.AreEqual(0, process.ExitCode, stderr);
            Assert.AreEqual(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            CollectionAssert.AreEqual(
                new[]
                {
                    "preflight", "probe", "probeEvaluation", "capture",
                    "captureSelection", "privateValidation", "rateGate",
                    "safeSummary",
                },
                document.RootElement.GetProperty("Stages")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
            Assert.AreEqual(
                "passed",
                document.RootElement.GetProperty("Summary")
                    .GetProperty("status")
                    .GetString());
            Assert.DoesNotContain(completion.CaptureId.Value, stdout);
            Assert.DoesNotContain(completion.Sha256, stdout);
            Assert.DoesNotContain(stagingRoot, stdout);
            Assert.DoesNotContain(dataRoot, stdout);
            Assert.IsFalse(Directory.EnumerateDirectories(
                Path.Combine(dataRoot, "private-validation"),
                "run-*",
                SearchOption.TopDirectoryOnly).Any());
            Assert.IsTrue(File.Exists(Path.Combine(
                dataRoot,
                "private-validation",
                "latest-safe.json")));
            var soakArguments = await File.ReadAllTextAsync(Path.Combine(
                diagnosticsRoot,
                "soak-arguments.json"));
            Assert.Contains("\"DatagramCount\":10000", soakArguments);
            Assert.Contains("\"TargetRate\":7", soakArguments);
            Assert.Contains("\"MeasuredPeak\":3", soakArguments);
            Assert.IsTrue(File.Exists(Path.Combine(
                stagingRoot,
                "captures",
                $"{completion.CaptureId.Value}.apxraw")));
            Assert.IsTrue(File.Exists(Path.Combine(
                stagingRoot,
                "captures",
                $"{completion.CaptureId.Value}.apxraw.json")));
            Assert.IsFalse(File.Exists(Path.Combine(
                repositoryRoot,
                "synthetic-pwned.txt")));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task<PowerShellResult> InvokeModuleAsync(
        string body,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulePath = Path.Combine(
            repositoryRoot,
            "scripts",
            "PrivateF125Validation.psm1");
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-powershell-test-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "$ErrorActionPreference='Stop'\n"
            + "Set-StrictMode -Version Latest\n"
            + "Import-Module -Force -Name $env:APEXLAB_TEST_MODULE\n"
            + body);
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["APEXLAB_TEST_MODULE"] = modulePath;
            if (environment is not null)
            {
                foreach (var pair in environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            using var process = Process.Start(startInfo);
            Assert.IsNotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new PowerShellResult(
                process.ExitCode,
                await stdout,
                await stderr);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ApexLab.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "The ApexLab repository root could not be located.");
    }

    private static string CreateAcceptedProbeJson() =>
        ProbeJson.Serialize(new ProbeReport(
            ProbeAggregator.SchemaVersion,
            "success",
            F125Protocol.Id,
            DurationMilliseconds: 30_000,
            new ProbeSourceReport(3, 3, 0, 0, 0),
            new ProbeClassificationReport(
                SourceDequeued: 3,
                Compatible: 3,
                MalformedHeader: 0,
                UnsupportedFormat: 0,
                UnsupportedYear: 0,
                UnknownPacketId: 0,
                UnsupportedPacketVersion: 0,
                InvalidPacketLength: 0,
                ExcludedPrivacyPacket: 0,
                UnexpectedSender: 0,
                ClassifierAbandonedOnTermination: 0),
            [new ProbePacketShapeReport(0, 1, 1_349, 3)],
            [new ProbeDescriptorReport(0, 1, 1_349, 3)],
            [new ProbeRateBucketReport(0, 3)],
            new ProbeSequenceReport(2, 0, 0, 0),
            new ProbeHeaderReport(1, 0, 0, 0, 0, 0),
            new ProbePlayerIndexReport(3, 3, null, null, 3)));

    private static async Task<RawEvidenceCompletion>
        CreateSyntheticEvidenceAsync(ApplicationPaths paths)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
        var payload = CreateSyntheticMotionPacket();
        var sender = new DatagramSender(IPAddress.Loopback, 20_777);
        var receivedAt = new DateTimeOffset(
            2026,
            8,
            8,
            12,
            0,
            0,
            TimeSpan.Zero);
        long[] sequences = [1, 2, 5];
        for (var index = 0; index < sequences.Length; index++)
        {
            await writer.WriteAsync(DatagramEnvelope.CopyFrom(
                sequences[index],
                100 + index,
                receivedAt.AddMilliseconds(index),
                sender,
                payload));
        }

        return await writer.FinalizeAsync();
    }

    private static byte[] CreateSyntheticMotionPacket()
    {
        var packet = new byte[1_349];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(7),
            0x0123456789ABCDEFUL);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(15),
            unchecked((uint)BitConverter.SingleToInt32Bits(12.5F)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(19),
            0x10203040U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(23),
            0x50607080U);
        packet[27] = 3;
        packet[28] = byte.MaxValue;
        return packet;
    }

    private static string WorkflowDependencyScript() =>
        "$script:stages=[Collections.Generic.List[string]]::new(); "
        + "$script:cleaned=$false; "
        + "function Enter-Stage([string]$Name) { $script:stages.Add($Name); "
        + "if($env:APEXLAB_FAIL_STAGE -ceq $Name){throw 'PRIVATE failure'}; "
        + "if($env:APEXLAB_CANCEL_STAGE -ceq $Name){"
        + "throw [OperationCanceledException]::new()} }; "
        + "$validator='{\"schemaVersion\":1,\"status\":\"validated\","
        + "\"protocolId\":\"ea-f1-25-v3\",\"manifestIntegrity\":true,"
        + "\"deterministicReplay\":true,\"sequenceGapPreservation\":true,"
        + "\"zeroPrivacyExcludedEvidence\":true,\"probeAssumptions\":true}'; "
        + "$dependencies=@{ "
        + "Preflight={param($game,$repo) Enter-Stage 'preflight'; "
        + "[IO.Directory]::CreateDirectory($env:APEXLAB_RUN_ROOT)|Out-Null; "
        + "[pscustomobject]@{RunRoot=$env:APEXLAB_RUN_ROOT;RepositoryHead='abc';"
        + "ApplicationVersion='0.1.0'} }; "
        + "Probe={param($context) Enter-Stage 'probe'; 'probe.json'}; "
        + "ProbeEvaluation={param($path) Enter-Stage 'probeEvaluation'; "
        + "[pscustomobject]@{ProtocolId='ea-f1-25-v3';"
        + "MeasuredPeakDatagramsPerSecond=70} }; "
        + "Capture={param($context) Enter-Stage 'capture'; "
        + "[pscustomobject]@{Before=@();After=@('00112233445546778899aabbccddeeff.apxraw.json')} }; "
        + "CaptureSelection={param($capture) Enter-Stage 'captureSelection'; "
        + "'00112233445546778899aabbccddeeff'}; "
        + "PrivateValidation={param($context,$captureId,$probePath) "
        + "Enter-Stage 'privateValidation'; $validator}; "
        + "RateGate={param($context,$ratePlan) Enter-Stage 'rateGate'}; "
        + "SafeSummary={param($context,$validation,$game) Enter-Stage 'safeSummary'; "
        + "[pscustomobject]@{status='passed'} }; "
        + "Cleanup={param($context) $script:cleaned=$true; "
        + "if(Test-Path -LiteralPath $env:APEXLAB_RUN_ROOT){"
        + "Remove-Item -LiteralPath $env:APEXLAB_RUN_ROOT -Recurse -Force} }; "
        + "Emit={param($message)} }; ";

    private sealed class PowerShellResult : IDisposable
    {
        public PowerShellResult(
            int exitCode,
            string standardOutput,
            string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public int ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }

        public void Dispose()
        {
        }
    }
}
