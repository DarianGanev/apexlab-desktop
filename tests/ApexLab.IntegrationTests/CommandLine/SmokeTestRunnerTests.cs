using System.Text.Json;
using ApexLab.App.CommandLine;
using ApexLab.Persistence.Database;
using Microsoft.Data.Sqlite;
using ApexLabIdentity = ApexLab.Application.Identity.ApplicationIdentity;

namespace ApexLab.IntegrationTests.CommandLine;

[TestClass]
public sealed class SmokeTestRunnerTests
{
    [TestMethod]
    public async Task ValidArguments_InitializeRealSettingsAndDatabaseAndWriteSuccessResult()
    {
        using var fixture = SmokeTestFixture.Create();
        var subject = new SmokeTestRunner(TimeSpan.FromSeconds(5));

        var exitCode = await subject.RunAsync(
            fixture.Arguments,
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.Success, exitCode);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.DataRoot, "settings.json")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.DataRoot, "apexlab.db")));
        var result = JsonSerializer.Deserialize<SmokeTestResult>(
            await File.ReadAllTextAsync(fixture.ResultFile, TestContext.CancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(result);
        Assert.AreEqual(ApexLabIdentity.ProductName, result.Product);
        Assert.AreEqual(ApexLabIdentity.InformationalVersion, result.Version);
        Assert.AreEqual(DatabaseSchema.CurrentVersion, result.SchemaVersion);
        Assert.IsTrue(result.SettingsValid);
        Assert.AreEqual(SmokeTestCompletionState.Completed, result.CompletionState);

        await using var connection = fixture.CreateDatabaseConnection();
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(
            (long)DatabaseSchema.CurrentVersion,
            await version.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task InvalidArguments_ReturnTwoWithoutCreatingOutput()
    {
        using var fixture = SmokeTestFixture.Create();
        var subject = new SmokeTestRunner(TimeSpan.FromSeconds(5));

        var exitCode = await subject.RunAsync(
            ["--smoke-test", "--data-root", "relative", "--result-file", fixture.ResultFile],
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.InvalidArguments, exitCode);
        Assert.IsFalse(File.Exists(fixture.ResultFile));
        Assert.IsFalse(Directory.Exists(fixture.DataRoot));
    }

    [TestMethod]
    public async Task InitializationFailure_ReturnsThreeAndDoesNotClaimCompletion()
    {
        using var fixture = SmokeTestFixture.Create();
        var subject = new SmokeTestRunner(
            TimeSpan.FromSeconds(5),
            new SmokeTestRunnerHooks
            {
                BeforeInitializationAsync = static _ =>
                    ValueTask.FromException(new IOException("Forced initialization failure.")),
            });

        var exitCode = await subject.RunAsync(
            fixture.Arguments,
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.InitializationFailure, exitCode);
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    [TestMethod]
    public async Task ExistingDataRoot_ReturnsThreeWithoutModifyingCallerData()
    {
        using var fixture = SmokeTestFixture.Create();
        Directory.CreateDirectory(fixture.DataRoot);
        var sentinelPath = Path.Combine(fixture.DataRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinelPath, "caller-owned", TestContext.CancellationToken);
        var subject = new SmokeTestRunner(TimeSpan.FromSeconds(5));

        var exitCode = await subject.RunAsync(
            fixture.Arguments,
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.InitializationFailure, exitCode);
        Assert.AreEqual(
            "caller-owned",
            await File.ReadAllTextAsync(sentinelPath, TestContext.CancellationToken));
        Assert.HasCount(1, Directory.GetFileSystemEntries(fixture.DataRoot));
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    [TestMethod]
    public async Task ExistingResultFile_ReturnsThreeBeforeCreatingDataRoot()
    {
        using var fixture = SmokeTestFixture.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ResultFile)!);
        await File.WriteAllTextAsync(
            fixture.ResultFile,
            "caller-owned",
            TestContext.CancellationToken);
        var subject = new SmokeTestRunner(TimeSpan.FromSeconds(5));

        var exitCode = await subject.RunAsync(
            fixture.Arguments,
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.InitializationFailure, exitCode);
        Assert.AreEqual(
            "caller-owned",
            await File.ReadAllTextAsync(fixture.ResultFile, TestContext.CancellationToken));
        Assert.IsFalse(Directory.Exists(fixture.DataRoot));
    }

    [TestMethod]
    public async Task AlreadyCancelledCaller_LeavesNoOutput()
    {
        using var fixture = SmokeTestFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var subject = new SmokeTestRunner(TimeSpan.FromSeconds(5));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => subject.RunAsync(fixture.Arguments, cancellation.Token));

        Assert.IsFalse(Directory.Exists(fixture.DataRoot));
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    [TestMethod]
    public async Task CallerCancellationAfterRootCreation_RemovesOwnedRoot()
    {
        using var fixture = SmokeTestFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var subject = new SmokeTestRunner(
            TimeSpan.FromSeconds(5),
            new SmokeTestRunnerHooks
            {
                AfterDataRootCreatedAsync = (_, linkedToken) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromCanceled(linkedToken);
                },
            });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => subject.RunAsync(fixture.Arguments, cancellation.Token));

        Assert.IsFalse(Directory.Exists(fixture.DataRoot));
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    [TestMethod]
    public async Task ResultFailure_RemovesOwnedDataAndResultDirectories()
    {
        using var fixture = SmokeTestFixture.Create();
        var resultDirectory = Path.GetDirectoryName(fixture.ResultFile)!;
        var subject = new SmokeTestRunner(
            TimeSpan.FromSeconds(5),
            new SmokeTestRunnerHooks
            {
                AfterResultDirectoryCreatedAsync = static (_, _) =>
                    ValueTask.FromException(new IOException("Forced result failure.")),
            });

        var exitCode = await subject.RunAsync(
            fixture.Arguments,
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.InitializationFailure, exitCode);
        Assert.IsFalse(Directory.Exists(fixture.DataRoot));
        Assert.IsFalse(Directory.Exists(resultDirectory));
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task InitializationTimeout_ReturnsFourAndDoesNotLeaveResult()
    {
        using var fixture = SmokeTestFixture.Create();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subject = new SmokeTestRunner(
            TimeSpan.FromMilliseconds(75),
            new SmokeTestRunnerHooks
            {
                BeforeInitializationAsync = async cancellationToken =>
                {
                    hookEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            });

        var execution = subject.RunAsync(fixture.Arguments, TestContext.CancellationToken);
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        var exitCode = await execution.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.CancellationToken);

        Assert.AreEqual((int)SmokeTestExitCode.Timeout, exitCode);
        Assert.IsFalse(File.Exists(fixture.ResultFile));
    }

    public TestContext TestContext { get; set; }
}

internal sealed class SmokeTestFixture : IDisposable
{
    private readonly string _root;

    private SmokeTestFixture(string root)
    {
        _root = root;
        DataRoot = Path.Combine(root, "data");
        ResultFile = Path.Combine(root, "result", "smoke-result.json");
        Arguments =
        [
            "--smoke-test",
            "--data-root",
            DataRoot,
            "--result-file",
            ResultFile,
        ];
    }

    public string DataRoot { get; }

    public string ResultFile { get; }

    public IReadOnlyList<string> Arguments { get; }

    public static SmokeTestFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apexlab-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new SmokeTestFixture(root);
    }

    public SqliteConnection CreateDatabaseConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataRoot, "apexlab.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
