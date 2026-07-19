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

    private readonly ApexLabOptions _defaults;
    private readonly JsonSettingsStoreHooks _hooks;
    private readonly object _lifetimeLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TaskCompletionSource _operationsDrained = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposalCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admittedOperationCount;
    private bool _admissionClosed;
    private bool _disposalStarted;

    public JsonSettingsStore(ApexLabOptions defaults)
        : this(defaults, JsonSettingsStoreHooks.None)
    {
    }

    internal JsonSettingsStore(
        ApexLabOptions defaults,
        JsonSettingsStoreHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(hooks);

        ThrowIfInvalid(defaults, nameof(defaults));
        var paths = ApplicationPaths.FromRoot(defaults.DataRootPath);

        _defaults = defaults;
        _hooks = hooks;
        SettingsFilePath = Path.Combine(paths.RootDirectory, SettingsFileName);
    }

    public string SettingsFilePath { get; }

    public async Task<ApexLabOptions> LoadAsync(CancellationToken cancellationToken)
    {
        AdmitOperation();
        var gateEntered = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
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
                await _hooks.AfterLoadOpenedAsync(
                    SettingsFilePath,
                    cancellationToken).ConfigureAwait(false);
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
            try
            {
                if (gateEntered)
                {
                    _operationGate.Release();
                }
            }
            finally
            {
                CompleteOperation();
            }
        }
    }

    public async Task SaveAsync(ApexLabOptions options, CancellationToken cancellationToken)
    {
        AdmitOperation();
        var gateEntered = false;
        string? temporaryFilePath = null;
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            ThrowIfInvalid(options, nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
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

            await _hooks.BeforeCommitAsync(
                temporaryFilePath,
                SettingsFilePath,
                cancellationToken).ConfigureAwait(false);
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
            try
            {
                if (temporaryFilePath is not null && File.Exists(temporaryFilePath))
                {
                    File.Delete(temporaryFilePath);
                }
            }
            finally
            {
                try
                {
                    if (gateEntered)
                    {
                        _operationGate.Release();
                    }
                }
                finally
                {
                    CompleteOperation();
                }
            }
        }
    }

    public void Dispose()
    {
        bool notifyAdmissionClosed;
        lock (_lifetimeLock)
        {
            notifyAdmissionClosed = !_admissionClosed;
            _admissionClosed = true;
            if (_admittedOperationCount == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }

        if (notifyAdmissionClosed)
        {
            _hooks.AfterAdmissionClosed();
        }

        _operationsDrained.Task.GetAwaiter().GetResult();

        bool ownsResourceDisposal;
        lock (_lifetimeLock)
        {
            ownsResourceDisposal = !_disposalStarted;
            _disposalStarted = true;
        }

        if (ownsResourceDisposal)
        {
            try
            {
                _operationGate.Dispose();
                _disposalCompleted.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposalCompleted.TrySetException(exception);
            }
        }

        _disposalCompleted.Task.GetAwaiter().GetResult();
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

    private void AdmitOperation()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_admissionClosed, this);
            _admittedOperationCount++;
        }
    }

    private void CompleteOperation()
    {
        lock (_lifetimeLock)
        {
            _admittedOperationCount--;
            if (_admissionClosed && _admittedOperationCount == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private sealed class InvalidSettingsFileException : Exception;
}

internal sealed class JsonSettingsStoreHooks
{
    internal static JsonSettingsStoreHooks None { get; } = new();

    internal Func<string, CancellationToken, ValueTask> AfterLoadOpenedAsync { get; init; } =
        static (_, _) => ValueTask.CompletedTask;

    internal Func<string, string, CancellationToken, ValueTask> BeforeCommitAsync { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    internal Action AfterAdmissionClosed { get; init; } = static () => { };
}
