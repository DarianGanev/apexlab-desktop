using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApexLab.Application.Configuration;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Configuration;
using ApexLab.Persistence.Database;
using ApexLabIdentity = ApexLab.Application.Identity.ApplicationIdentity;

namespace ApexLab.App.CommandLine;

public enum SmokeTestExitCode
{
    Success = 0,
    InvalidArguments = 2,
    InitializationFailure = 3,
    Timeout = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<SmokeTestCompletionState>))]
public enum SmokeTestCompletionState
{
    Completed,
}

public sealed record SmokeTestResult(
    string Product,
    string Version,
    int SchemaVersion,
    bool SettingsValid,
    SmokeTestCompletionState CompletionState);

public sealed class SmokeTestRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeSpan _timeout;
    private readonly SmokeTestRunnerHooks _hooks;

    public SmokeTestRunner()
        : this(TimeSpan.FromSeconds(30))
    {
    }

    public SmokeTestRunner(TimeSpan timeout)
        : this(timeout, SmokeTestRunnerHooks.None)
    {
    }

    internal SmokeTestRunner(TimeSpan timeout, SmokeTestRunnerHooks hooks)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Smoke-test timeout must be positive.");
        }

        ArgumentNullException.ThrowIfNull(hooks);
        _timeout = timeout;
        _hooks = hooks;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var parsed = StartupArguments.Parse(arguments);
        if (!parsed.IsSuccess || parsed.Value!.Mode != StartupMode.SmokeTest)
        {
            return (int)SmokeTestExitCode.InvalidArguments;
        }

        using var timeoutCancellation = new CancellationTokenSource(_timeout);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            await _hooks.BeforeInitializationAsync(executionCancellation.Token).ConfigureAwait(false);
            await InitializeAsync(parsed.Value, executionCancellation.Token).ConfigureAwait(false);
            return (int)SmokeTestExitCode.Success;
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (int)SmokeTestExitCode.Timeout;
        }
        catch (Exception exception) when (
            timeoutCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && ContainsCancellation(exception))
        {
            return (int)SmokeTestExitCode.Timeout;
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested && ContainsCancellation(exception))
        {
            throw new OperationCanceledException(
                "Smoke-test execution was cancelled.",
                exception,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (int)SmokeTestExitCode.InitializationFailure;
        }
    }

    private static async Task InitializeAsync(
        StartupArguments arguments,
        CancellationToken cancellationToken)
    {
        var dataRoot = arguments.DataRoot!;
        var resultFile = arguments.ResultFile!;
        var paths = ApplicationPaths.FromRoot(dataRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(paths.RootDirectory) || File.Exists(paths.RootDirectory))
        {
            throw new IOException("Smoke-test data root must not already exist.");
        }

        if (File.Exists(resultFile) || Directory.Exists(resultFile))
        {
            throw new IOException("Smoke-test result path must not already exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.RootDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var defaults = new ApexLabOptions(paths.RootDirectory);
            using (var settingsStore = new JsonSettingsStore(defaults))
            {
                await settingsStore.SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
                var loaded = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (ApexLabOptionsValidator.Validate(loaded).Count != 0)
                {
                    throw new InvalidOperationException("Smoke-test settings validation failed.");
                }
            }

            var migrator = new DatabaseMigrator(new SqliteConnectionFactory(paths.DatabaseFile));
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new SmokeTestResult(
                ApexLabIdentity.ProductName,
                ApexLabIdentity.InformationalVersion,
                DatabaseSchema.CurrentVersion,
                SettingsValid: true,
                SmokeTestCompletionState.Completed);
            await WriteResultAtomicallyAsync(resultFile, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException)
        {
            try
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Smoke-test initialization and owned-root cleanup both failed.",
                    primaryException,
                    cleanupException);
            }

            throw;
        }
    }

    private static async Task WriteResultAtomicallyAsync(
        string resultFile,
        SmokeTestResult result,
        CancellationToken cancellationToken)
    {
        var resultDirectory = Path.GetDirectoryName(resultFile)!;
        Directory.CreateDirectory(resultDirectory);
        var temporaryFile = Path.Combine(
            resultDirectory,
            $".{Path.GetFileName(resultFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    result,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryFile, resultFile, overwrite: false);
        }
        catch (Exception primaryException)
        {
            try
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Smoke-test result write and temporary-file cleanup both failed.",
                    primaryException,
                    cleanupException);
            }

            throw;
        }
    }

    private static bool ContainsCancellation(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        return exception is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(ContainsCancellation);
    }
}

internal sealed class SmokeTestRunnerHooks
{
    internal static SmokeTestRunnerHooks None { get; } = new();

    internal Func<CancellationToken, ValueTask> BeforeInitializationAsync { get; init; } =
        static _ => ValueTask.CompletedTask;
}
