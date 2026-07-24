using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Replay.Replay;

namespace ApexLab.IntegrationTests.Replay;

[TestClass]
public sealed class ReplayCommandTests
{
    [TestMethod]
    public void ParsesRequiredIdentityAndBoundedTimingOptions()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "apexlab-replay-arguments");

        var success = ReplayArguments.TryParse(
            [
                "replay",
                "--data-root", root,
                "--capture-id",
                "00112233445546778899aabbccddeeff",
                "--timing", "recorded",
                "--speed-permille", "2500",
            ],
            out var parsed);

        Assert.IsTrue(success);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(root, parsed.DataRoot);
        Assert.AreEqual(
            RawReplayTimingMode.Recorded,
            parsed.TimingMode);
        Assert.AreEqual(2_500, parsed.SpeedPermille);
    }

    [TestMethod]
    public void RejectsMissingDuplicateAndOutOfRangeArguments()
    {
        string[][] invalid =
        [
            [],
            ["replay"],
            ["replay", "--data-root", "C:\\data"],
            [
                "replay",
                "--data-root", "C:\\data",
                "--capture-id", "invalid",
            ],
            [
                "replay",
                "--data-root", "C:\\data",
                "--data-root", "D:\\data",
                "--capture-id",
                "00112233445546778899aabbccddeeff",
            ],
            [
                "replay",
                "--data-root", "C:\\data",
                "--capture-id",
                "00112233445546778899aabbccddeeff",
                "--speed-permille", "99",
            ],
        ];

        foreach (var arguments in invalid)
        {
            Assert.IsFalse(
                ReplayArguments.TryParse(arguments, out var parsed),
                string.Join(' ', arguments));
            Assert.IsNull(parsed);
        }
    }

    [TestMethod]
    public async Task ReplaysVerifiedEvidenceWithoutDisclosingPrivateIdentifiers()
    {
        using var temporary = TemporaryRoot.Create();
        var protocolId =
            RawEvidenceProtocolId.Parse(F125Protocol.Id);
        RawEvidenceCompletion completion;
        await using (var writer = await RawEvidenceWriter.CreateAsync(
                         temporary.Paths,
                         protocolId,
                         new RawEvidenceLimits(
                             minimumFreeSpaceBytes: 0),
                         TestContext.CancellationToken))
        {
            completion = await writer.FinalizeAsync(
                TestContext.CancellationToken);
        }

        var result = await ReplayCommand.ExecuteAsync(
            [
                "replay",
                "--data-root", temporary.Paths.RootDirectory,
                "--capture-id", completion.CaptureId.Value,
            ],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ReplayExitCode.Success, result.ExitCode);
        Assert.AreEqual(
            "replayed",
            document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            0L,
            document.RootElement.GetProperty("recordCount").GetInt64());
        Assert.AreEqual(
            0L,
            document.RootElement
                .GetProperty("source")
                .GetProperty("sourceEnqueued")
                .GetInt64());
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
    public async Task MissingCaptureReturnsPrivacySafeInvalidEvidence()
    {
        using var temporary = TemporaryRoot.Create();
        var captureId =
            "00112233445546778899aabbccddeeff";

        var result = await ReplayCommand.ExecuteAsync(
            [
                "replay",
                "--data-root", temporary.Paths.RootDirectory,
                "--capture-id", captureId,
            ],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(
            ReplayExitCode.InvalidEvidence,
            result.ExitCode);
        Assert.AreEqual(
            "invalidEvidence",
            document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(
            captureId,
            result.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            temporary.Paths.RootDirectory,
            result.Json,
            StringComparison.OrdinalIgnoreCase);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path)
        {
            Paths = ApplicationPaths.FromRoot(path);
        }

        public ApplicationPaths Paths { get; }

        public static TemporaryRoot Create() =>
            new(Path.Combine(
                Path.GetTempPath(),
                $"apexlab-replay-command-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Paths.RootDirectory))
            {
                Directory.Delete(Paths.RootDirectory, recursive: true);
            }
        }
    }
}
