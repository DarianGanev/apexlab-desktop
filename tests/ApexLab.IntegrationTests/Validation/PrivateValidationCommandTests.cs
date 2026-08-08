using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Probe;
using ApexLab.Replay.Replay;
using ApexLab.Replay.Validation;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Validation;

[TestClass]
public sealed class PrivateValidationCommandTests
{
    private const string CaptureId =
        "00112233445546778899aabbccddeeff";

    [TestMethod]
    public void ParsesExactlyTheRequiredPrivateValidationArguments()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "apexlab-private-validation-root");
        var report = Path.Combine(root, "probe.json");

        var success = PrivateValidationArguments.TryParse(
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report", report,
            ],
            out var parsed);

        Assert.IsTrue(success);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(Path.GetFullPath(root), parsed.DataRoot);
        Assert.AreEqual(CaptureId, parsed.CaptureId.Value);
        Assert.AreEqual(Path.GetFullPath(report), parsed.ProbeReportPath);
    }

    [TestMethod]
    public void RejectsMissingDuplicateRelativeUnknownAndNoncanonicalArguments()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "apexlab-private-validation-root");
        var report = Path.Combine(root, "probe.json");
        string[][] rejected =
        [
            [],
            ["validate"],
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
            ],
            [
                "validate",
                "--data-root", "relative-root",
                "--capture-id", CaptureId,
                "--probe-report", report,
            ],
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report", "relative-report.json",
            ],
            [
                "validate",
                "--data-root", root,
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report", report,
            ],
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId.ToUpperInvariant(),
                "--probe-report", report,
            ],
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report", report,
                "--unknown", "value",
            ],
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report",
            ],
        ];

        foreach (var arguments in rejected)
        {
            Assert.IsFalse(
                PrivateValidationArguments.TryParse(
                    arguments,
                    out var parsed),
                string.Join(' ', arguments));
            Assert.IsNull(parsed);
        }
    }

    [TestMethod]
    public async Task ValidatesEvidenceTwiceAndReturnsOnlySafeConclusions()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths);
        var probePath = Path.Combine(
            temporary.Paths.RootDirectory,
            "private-probe.json");
        await File.WriteAllTextAsync(
            probePath,
            ProbeJson.Serialize(CreateAcceptedProbeReport()),
            TestContext.CancellationToken);

        var result = await PrivateValidationCommand.ExecuteAsync(
            [
                "validate",
                "--data-root", temporary.Paths.RootDirectory,
                "--capture-id", completion.CaptureId.Value,
                "--probe-report", probePath,
            ],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(PrivateValidationExitCode.Success, result.ExitCode);
        string[] expectedProperties =
        [
            "schemaVersion",
            "status",
            "protocolId",
            "manifestIntegrity",
            "deterministicReplay",
            "sequenceGapPreservation",
            "zeroPrivacyExcludedEvidence",
            "probeAssumptions",
        ];
        CollectionAssert.AreEquivalent(
            expectedProperties,
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(
            "validated",
            document.RootElement.GetProperty("status").GetString());
        foreach (var conclusion in expectedProperties.Skip(3))
        {
            Assert.IsTrue(
                document.RootElement.GetProperty(conclusion).GetBoolean(),
                conclusion);
        }

        Assert.DoesNotContain(
            completion.CaptureId.Value,
            result.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            completion.Sha256,
            result.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            temporary.Paths.RootDirectory,
            result.Json,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RoutesValidateThroughTheDiagnosticExecutable()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths);
        var probePath = Path.Combine(
            temporary.Paths.RootDirectory,
            "private-probe.json");
        await File.WriteAllTextAsync(
            probePath,
            ProbeJson.Serialize(CreateAcceptedProbeReport()),
            TestContext.CancellationToken);
        var executable = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ApexLab.Replay",
            "bin",
            "Release",
            "net10.0-windows",
            "ApexLab.Replay.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "validate",
                     "--data-root", temporary.Paths.RootDirectory,
                     "--capture-id", completion.CaptureId.Value,
                     "--probe-report", probePath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.IsNotNull(process);
        var outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        Assert.AreEqual(0, process.ExitCode, error);
        using var document = JsonDocument.Parse(output);
        Assert.AreEqual(
            "validated",
            document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    public async Task RejectsAStableFieldDifferenceBetweenTheTwoReplays()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-private-validation-double-{Guid.NewGuid():N}");
        var reportPath = Path.Combine(root, "probe.json");
        var source = new DatagramSourceCounters(3, 3, 0, 0, 0);
        var classifier = new DatagramClassificationCounters(
            sourceDequeued: 3,
            compatible: 3,
            malformedHeader: 0,
            unsupportedFormat: 0,
            unsupportedYear: 0,
            unknownPacketId: 0,
            unsupportedPacketVersion: 0,
            invalidPacketLength: 0,
            excludedPrivacyPacket: 0,
            unexpectedSender: 0,
            classifierAbandonedOnTermination: 0);
        var first = new ReplayExecutionResult(
            F125Protocol.Id,
            RawReplayTimingMode.Immediate,
            1_000,
            RecordCount: 3,
            new CaptureIngestionCounters(source, classifier),
            SequenceGapCount: 0,
            [new ReplayDescriptorObservation(0, 1, 1_349, 3)]);
        var second = first with { SequenceGapCount = 1 };
        var calls = 0;

        var result = await PrivateValidationCommand.ExecuteAsync(
            [
                "validate",
                "--data-root", root,
                "--capture-id", CaptureId,
                "--probe-report", reportPath,
            ],
            TestContext.CancellationToken,
            (_, _) => Task.FromResult(
                new PrivateProbeEvaluation(
                    F125Protocol.Id,
                    3,
                    new HashSet<PrivateDescriptorShape>
                    {
                        new(0, 1, 1_349),
                    })),
            (_, _, _, _) => Task.FromResult(calls++ == 0 ? first : second));

        Assert.AreEqual(
            PrivateValidationExitCode.NondeterministicReplay,
            result.ExitCode);
        using var document = JsonDocument.Parse(result.Json);
        Assert.AreEqual(
            "nondeterministicReplay",
            document.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task MapsMissingEvidenceWithoutDisclosingItsIdentity()
    {
        using var temporary = TemporaryRoot.Create();
        var probePath = Path.Combine(
            temporary.Paths.RootDirectory,
            "private-probe.json");
        await File.WriteAllTextAsync(
            probePath,
            ProbeJson.Serialize(CreateAcceptedProbeReport()),
            TestContext.CancellationToken);

        var result = await PrivateValidationCommand.ExecuteAsync(
            [
                "validate",
                "--data-root", temporary.Paths.RootDirectory,
                "--capture-id", CaptureId,
                "--probe-report", probePath,
            ],
            TestContext.CancellationToken);

        Assert.AreEqual(
            PrivateValidationExitCode.InvalidEvidence,
            result.ExitCode);
        Assert.DoesNotContain(
            CaptureId,
            result.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            temporary.Paths.RootDirectory,
            result.Json,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RejectsPrivacyExcludedAndUnexpectedSenderEvidence()
    {
        var cases = new[]
        {
            new
            {
                Sender = new DatagramSender(IPAddress.Loopback, 20_777),
                Payload = CreatePacket(packetId: 4, length: 1_284),
            },
            new
            {
                Sender = new DatagramSender(
                    IPAddress.Parse("192.0.2.25"),
                    20_777),
                Payload = CreateMotionPacket(),
            },
        };

        foreach (var item in cases)
        {
            using var temporary = TemporaryRoot.Create();
            var completion = await CreateEvidenceAsync(
                temporary.Paths,
                item.Sender,
                item.Payload);
            var probePath = Path.Combine(
                temporary.Paths.RootDirectory,
                "private-probe.json");
            await File.WriteAllTextAsync(
                probePath,
                ProbeJson.Serialize(CreateAcceptedProbeReport()),
                TestContext.CancellationToken);

            var result = await PrivateValidationCommand.ExecuteAsync(
                [
                    "validate",
                    "--data-root", temporary.Paths.RootDirectory,
                    "--capture-id", completion.CaptureId.Value,
                    "--probe-report", probePath,
                ],
                TestContext.CancellationToken);

            Assert.AreEqual(
                PrivateValidationExitCode.PrivacyFailure,
                result.ExitCode);
            Assert.DoesNotContain(
                completion.CaptureId.Value,
                result.Json,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                completion.Sha256,
                result.Json,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task MapsProbeMismatchUnsupportedProtocolAndCancellationDistinctly()
    {
        using var temporary = TemporaryRoot.Create();
        var completion = await CreateEvidenceAsync(temporary.Paths);
        var probePath = Path.Combine(
            temporary.Paths.RootDirectory,
            "private-probe.json");
        await File.WriteAllTextAsync(
            probePath,
            ProbeJson.Serialize(CreateAcceptedProbeReport() with
            {
                ProtocolId = "other-protocol",
            }),
            TestContext.CancellationToken);
        var arguments = new[]
        {
            "validate",
            "--data-root", temporary.Paths.RootDirectory,
            "--capture-id", completion.CaptureId.Value,
            "--probe-report", probePath,
        };

        var mismatch = await PrivateValidationCommand.ExecuteAsync(
            arguments,
            TestContext.CancellationToken);

        Assert.AreEqual(
            PrivateValidationExitCode.ProbeMismatch,
            mismatch.ExitCode);

        using var unsupportedRoot = TemporaryRoot.Create();
        RawEvidenceCompletion unsupportedCompletion;
        await using (var writer = await RawEvidenceWriter.CreateAsync(
                         unsupportedRoot.Paths,
                         RawEvidenceProtocolId.Parse(
                             "unregistered-synthetic-v1"),
                         new RawEvidenceLimits(minimumFreeSpaceBytes: 0)))
        {
            unsupportedCompletion = await writer.FinalizeAsync();
        }

        var validProbePath = Path.Combine(
            unsupportedRoot.Paths.RootDirectory,
            "private-probe.json");
        await File.WriteAllTextAsync(
            validProbePath,
            ProbeJson.Serialize(CreateAcceptedProbeReport()),
            TestContext.CancellationToken);
        var unsupported = await PrivateValidationCommand.ExecuteAsync(
            [
                "validate",
                "--data-root", unsupportedRoot.Paths.RootDirectory,
                "--capture-id", unsupportedCompletion.CaptureId.Value,
                "--probe-report", validProbePath,
            ],
            TestContext.CancellationToken);

        Assert.AreEqual(
            PrivateValidationExitCode.UnsupportedProtocol,
            unsupported.ExitCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interrupted = await PrivateValidationCommand.ExecuteAsync(
            arguments,
            cancellation.Token);

        Assert.AreEqual(
            PrivateValidationExitCode.Interrupted,
            interrupted.ExitCode);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths)
    {
        return await CreateEvidenceAsync(
            paths,
            new DatagramSender(IPAddress.Loopback, 20_777),
            CreateMotionPacket());
    }

    private static async Task<RawEvidenceCompletion> CreateEvidenceAsync(
        ApplicationPaths paths,
        DatagramSender sender,
        byte[] payload)
    {
        await using var writer = await RawEvidenceWriter.CreateAsync(
            paths,
            RawEvidenceProtocolId.Parse(F125Protocol.Id),
            new RawEvidenceLimits(minimumFreeSpaceBytes: 0));
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
                monotonicTimestamp: 100 + index,
                receivedAt.AddMilliseconds(index),
                sender,
                payload));
        }

        return await writer.FinalizeAsync();
    }

    private static ProbeReport CreateAcceptedProbeReport() =>
        new(
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
            new ProbePlayerIndexReport(3, 3, null, null, 3));

    private static byte[] CreateMotionPacket()
    {
        return CreatePacket(packetId: 0, length: 1_349);
    }

    private static byte[] CreatePacket(byte packetId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            F125Protocol.PacketFormat);
        packet[2] = F125Protocol.GameYear;
        packet[3] = 1;
        packet[4] = 7;
        packet[5] = 1;
        packet[6] = packetId;
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

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path)
        {
            Paths = ApplicationPaths.FromRoot(path);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"apexlab-private-validation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
