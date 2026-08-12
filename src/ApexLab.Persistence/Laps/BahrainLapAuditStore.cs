using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Laps;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Laps;

internal enum BahrainLapAuditStoreStage
{
    WriteDocument = 1,
    FlushDocument = 2,
    PublishDocument = 3,
    VerifyDocument = 4,
}

public enum BahrainLapAuditStoreFailureKind
{
    Missing = 1,
    AlreadyExists = 2,
    InvalidDocument = 3,
    UnsafeStorage = 4,
    PublicationFailed = 5,
}

public sealed class BahrainLapAuditStoreException : Exception
{
    public BahrainLapAuditStoreException(
        BahrainLapAuditStoreFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BahrainLapAuditStoreFailureKind Kind { get; }
}

[SupportedOSPlatform("windows")]
public static class BahrainLapAuditStore
{
    public static Task PrepareAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        BahrainLapAuditDocument document,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(
            paths,
            captureId,
            document,
            observer: null,
            cancellationToken);

    internal static async Task PrepareAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        BahrainLapAuditDocument document,
        Action<BahrainLapAuditStoreStage>? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        await PublishAsync(
                paths,
                document,
                CreateLeafName(captureId),
                CreateStagingLeafName(captureId),
                observer,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task CompleteAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        BahrainLapAuditDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        return PublishAsync(
            paths,
            document,
            CreateCompletedLeafName(captureId),
            CreateStagingLeafName(captureId),
            observer: null,
            cancellationToken);
    }

    private static async Task PublishAsync(
        ApplicationPaths paths,
        BahrainLapAuditDocument document,
        string finalLeaf,
        string stagingLeaf,
        Action<BahrainLapAuditStoreStage>? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = BahrainLapAuditJson.Serialize(document);
        WindowsLapAuditDirectory? directory = null;
        FileStream? stream = null;
        var deleteOwned = false;
        Exception? primary = null;
        try
        {
            try
            {
                directory = WindowsLapAuditDirectory.Open(paths);
            }
            catch (Exception exception) when (IsUnsafeStorageFailure(exception))
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.UnsafeStorage,
                    exception);
            }

            FileStream? existing;
            try
            {
                existing = directory.TryOpenExistingReadOnly(finalLeaf);
            }
            catch (Exception exception) when (IsUnsafeStorageFailure(exception))
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.UnsafeStorage,
                    exception);
            }

            if (existing is not null)
            {
                await existing.DisposeAsync().ConfigureAwait(false);
                throw Failure(BahrainLapAuditStoreFailureKind.AlreadyExists);
            }

            try
            {
                stream = directory.CreateNewFile(stagingLeaf);
            }
            catch (Exception exception) when (IsUnsafeStorageFailure(exception))
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.UnsafeStorage,
                    exception);
            }

            deleteOwned = true;
            observer?.Invoke(BahrainLapAuditStoreStage.WriteDocument);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            observer?.Invoke(BahrainLapAuditStoreStage.FlushDocument);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            observer?.Invoke(BahrainLapAuditStoreStage.VerifyDocument);
            await VerifyStagedAsync(stream, bytes, cancellationToken)
                .ConfigureAwait(false);
            observer?.Invoke(BahrainLapAuditStoreStage.PublishDocument);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                directory.RenameOpenFile(stream.SafeFileHandle, finalLeaf);
            }
            catch (IOException exception)
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.PublicationFailed,
                    exception);
            }

            deleteOwned = false;
            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        var cleanupFailures = new List<Exception>();
        if (stream is not null)
        {
            if (deleteOwned && directory is not null)
            {
                try
                {
                    directory.DeleteOpenFile(stream.SafeFileHandle);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }
        else if (deleteOwned && directory is not null)
        {
            try
            {
                var published = directory.TryOpenExistingForDeletion(finalLeaf);
                if (published is not null)
                {
                    directory.DeleteOpenFile(published.SafeFileHandle);
                    await published.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (directory is not null)
        {
            try
            {
                directory.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        ThrowFailures(primary, cleanupFailures);
    }

    public static Task<BahrainLapAuditDocument> OpenAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        return OpenByLeafAsync(
            paths,
            CreateLeafName(captureId),
            cancellationToken);
    }

    public static Task<BahrainLapAuditDocument> OpenCompletedAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        return OpenByLeafAsync(
            paths,
            CreateCompletedLeafName(captureId),
            cancellationToken);
    }

    public static async Task<BahrainLapAuditDocument> OpenForEvaluationAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await OpenCompletedAsync(
                    paths,
                    captureId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BahrainLapAuditStoreException exception)
            when (exception.Kind == BahrainLapAuditStoreFailureKind.Missing)
        {
            return await OpenAsync(paths, captureId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<BahrainLapAuditDocument> OpenByLeafAsync(
        ApplicationPaths paths,
        string leafName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsLapAuditDirectory? directory = null;
        FileStream? stream = null;
        Exception? primary = null;
        BahrainLapAuditDocument? result = null;
        try
        {
            try
            {
                directory = WindowsLapAuditDirectory.Open(paths);
                stream = directory.TryOpenExistingReadOnly(leafName);
            }
            catch (Exception exception) when (IsUnsafeStorageFailure(exception))
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.UnsafeStorage,
                    exception);
            }

            if (stream is null)
            {
                throw Failure(BahrainLapAuditStoreFailureKind.Missing);
            }

            if (stream.Length is < 3 or > BahrainLapAuditJson.MaximumLengthBytes)
            {
                throw Failure(BahrainLapAuditStoreFailureKind.InvalidDocument);
            }

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                result = BahrainLapAuditJson.Deserialize(bytes);
            }
            catch (InvalidDataException exception)
            {
                throw Failure(
                    BahrainLapAuditStoreFailureKind.InvalidDocument,
                    exception);
            }
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        var cleanupFailures = new List<Exception>();
        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (directory is not null)
        {
            try
            {
                directory.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        ThrowFailures(primary, cleanupFailures);
        return result!;
    }

    internal static string CreateLeafName(RawEvidenceCaptureId captureId)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        return $"{captureId.Value}.bahrain-lap-audit.json";
    }

    internal static string CreateCompletedLeafName(
        RawEvidenceCaptureId captureId)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        return $"{captureId.Value}.bahrain-lap-audit.completed.json";
    }

    private static string CreateStagingLeafName(RawEvidenceCaptureId captureId) =>
        $"{captureId.Value}.{Guid.NewGuid():N}.bahrain-lap-audit.json.partial";

    private static async Task VerifyStagedAsync(
        FileStream staging,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        staging.Position = 0;
        if (staging.Length != expected.Length)
        {
            throw Failure(BahrainLapAuditStoreFailureKind.PublicationFailed);
        }

        var actual = new byte[expected.Length];
        await staging.ReadExactlyAsync(actual, cancellationToken)
            .ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw Failure(BahrainLapAuditStoreFailureKind.PublicationFailed);
        }
    }

    private static BahrainLapAuditStoreException Failure(
        BahrainLapAuditStoreFailureKind kind,
        Exception? innerException = null) =>
        new(
            kind,
            "The local Bahrain lap audit could not be accessed safely.",
            innerException);

    private static bool IsUnsafeStorageFailure(Exception exception) =>
        exception is IOException or Win32Exception or UnauthorizedAccessException;

    private static void ThrowFailures(
        Exception? primary,
        IReadOnlyCollection<Exception> cleanupFailures)
    {
        if (primary is not null && cleanupFailures.Count != 0)
        {
            throw new AggregateException([primary, .. cleanupFailures]);
        }

        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures.Single()).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(cleanupFailures);
        }
    }
}
