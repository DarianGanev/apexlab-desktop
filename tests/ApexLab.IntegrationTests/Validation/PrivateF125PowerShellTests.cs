using System.Diagnostics;
using System.Text.Json;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Probe;

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
            new ProbeSequenceReport(0, 0, 0, 0),
            new ProbeHeaderReport(1, 0, 0, 0, 0, 0),
            new ProbePlayerIndexReport(3, 3, null, null, 3)));

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
