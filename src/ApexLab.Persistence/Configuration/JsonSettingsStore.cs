using System.Text.Json;
using ApexLab.Application.Configuration;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Func<string, string, CancellationToken, ValueTask> _beforeCommit;
    private readonly ApexLabOptions _defaults;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;

    public JsonSettingsStore(ApexLabOptions defaults)
        : this(defaults, static (_, _, _) => ValueTask.CompletedTask)
    {
    }

    internal JsonSettingsStore(
        ApexLabOptions defaults,
        Func<string, string, CancellationToken, ValueTask> beforeCommit)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(beforeCommit);

        ThrowIfInvalid(defaults, nameof(defaults));
        var paths = ApplicationPaths.FromRoot(defaults.DataRootPath);

        _defaults = defaults;
        _beforeCommit = beforeCommit;
        SettingsFilePath = Path.Combine(paths.RootDirectory, SettingsFileName);
    }

    public string SettingsFilePath { get; }

    public async Task<ApexLabOptions> LoadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(SettingsFilePath))
            {
                return _defaults;
            }

            try
            {
                await using var stream = new FileStream(
                    SettingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var options = await JsonSerializer.DeserializeAsync<ApexLabOptions>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (options is null)
                {
                    throw new InvalidSettingsFileException();
                }

                var failures = ApexLabOptionsValidator.Validate(options);
                if (failures.Count != 0)
                {
                    throw new InvalidSettingsFileException();
                }

                return options;
            }
            catch (Exception exception) when (exception is JsonException or InvalidSettingsFileException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreserveInvalidSettings();
                return _defaults;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SaveAsync(ApexLabOptions options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfInvalid(options, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryFilePath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settingsDirectory = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(settingsDirectory);
            temporaryFilePath = Path.Combine(
                settingsDirectory,
                $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    options,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await _beforeCommit(temporaryFilePath, SettingsFilePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(SettingsFilePath))
            {
                File.Replace(temporaryFilePath, SettingsFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryFilePath, SettingsFilePath);
            }

            temporaryFilePath = null;
        }
        finally
        {
            if (temporaryFilePath is not null && File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationGate.Dispose();
        }
    }

    private static void ThrowIfInvalid(ApexLabOptions options, string parameterName)
    {
        var failures = ApexLabOptionsValidator.Validate(options);
        if (failures.Count == 0)
        {
            return;
        }

        var message = string.Join(
            " ",
            failures.Select(failure => $"{failure.FieldName}: {failure.Message}"));
        throw new ArgumentException(message, parameterName);
    }

    private void PreserveInvalidSettings()
    {
        var settingsDirectory = Path.GetDirectoryName(SettingsFilePath)!;
        var diagnosticFileName = $"settings.invalid-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json";
        var diagnosticFilePath = Path.Combine(settingsDirectory, diagnosticFileName);
        File.Move(SettingsFilePath, diagnosticFilePath);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class InvalidSettingsFileException : Exception;
}
