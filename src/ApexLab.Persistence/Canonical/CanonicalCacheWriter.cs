using System.Runtime.Versioning;
using ApexLab.Application.Canonical;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Canonical;

internal enum CanonicalCacheWriterStage
{
    WriteRecord = 1,
    FlushData = 2,
    PublishData = 3,
    WriteManifest = 4,
    FlushManifest = 5,
    PublishManifest = 6,
}

[SupportedOSPlatform("windows")]
internal sealed class CanonicalCacheWriter : ICanonicalCacheWriter
{
    private readonly CanonicalCacheRequest _request;
    private readonly WindowsCanonicalCacheDirectory _directory;
    private readonly Action<CanonicalCacheWriterStage>? _observer;
    private readonly string _dataStagingLeaf;
    private FileStream? _dataStream;
    private FileStream? _manifestStream;
    private string? _manifestStagingLeaf;
    private long _nextSourceSequence = 1;
    private bool _sequenceExhausted;
    private long _recordCount;
    private long _observationCount;
    private long _exclusionCount;
    private long _gapCount;
    private long? _firstSourceSequence;
    private long? _lastSourceSequence;
    private bool _dataIsStaging = true;
    private bool _manifestIsStaging;
    private bool _faulted;
    private bool _finalized;
    private int _disposed;

    private CanonicalCacheWriter(
        CanonicalCacheRequest request,
        WindowsCanonicalCacheDirectory directory,
        Action<CanonicalCacheWriterStage>? observer,
        string dataStagingLeaf,
        FileStream dataStream)
    {
        _request = request;
        _directory = directory;
        _observer = observer;
        _dataStagingLeaf = dataStagingLeaf;
        _dataStream = dataStream;
    }

    public static async Task<CanonicalCacheWriter> CreateAsync(
        ApplicationPaths paths,
        CanonicalCacheRequest request,
        Action<CanonicalCacheWriterStage>? observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsCanonicalCacheDirectory? directory = null;
        FileStream? stream = null;
        try
        {
            directory = WindowsCanonicalCacheDirectory.Open(paths);
            var stagingLeaf = CreateStagingLeaf(
                request.Identity,
                manifest: false);
            stream = directory.CreateNewFile(stagingLeaf);
            await stream.WriteAsync(
                    CanonicalCacheFormat.SerializeHeader(
                        request.SourceStopwatchFrequency),
                    cancellationToken)
                .ConfigureAwait(false);
            return new(request, directory, observer, stagingLeaf, stream);
        }
        catch
        {
            if (stream is not null)
            {
                TryDelete(directory, stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            directory?.Dispose();
            throw;
        }
    }

    public async ValueTask WriteAsync(
        CanonicalRecord record,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailableForWrite();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOrder(record);
        if (_recordCount >= CanonicalCacheFormat.MaximumRecordCount)
        {
            throw new InvalidOperationException(
                "The canonical cache record limit was reached.");
        }

        try
        {
            _observer?.Invoke(CanonicalCacheWriterStage.WriteRecord);
            var bytes = CanonicalCacheFormat.SerializeRecord(record);
            if (checked(_dataStream!.Length + bytes.Length
                        + CanonicalCacheFormat.FooterLength)
                > CanonicalCacheFormat.MaximumDataLength)
            {
                throw new InvalidOperationException(
                    "The canonical cache data limit was reached.");
            }

            await _dataStream.WriteAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
            Account(record);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async Task<CanonicalCacheCompletion> FinalizeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailableForFinalize();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var footer = new CanonicalCacheFooter(
                _recordCount,
                _observationCount,
                _exclusionCount,
                _gapCount,
                _firstSourceSequence,
                _lastSourceSequence);
            await _dataStream!.WriteAsync(
                    CanonicalCacheFormat.SerializeFooter(footer),
                    cancellationToken)
                .ConfigureAwait(false);
            _observer?.Invoke(CanonicalCacheWriterStage.FlushData);
            await FlushDurablyAsync(_dataStream, cancellationToken)
                .ConfigureAwait(false);

            var dataLength = _dataStream.Length;
            var hashes = await CanonicalCacheHash.CalculateAsync(
                    _dataStream,
                    _request.Identity,
                    dataLength,
                    cancellationToken)
                .ConfigureAwait(false);
            var completion = new CanonicalCacheCompletion(
                _request.Identity,
                _request.SourceStopwatchFrequency,
                dataLength,
                hashes.DataSha256,
                hashes.CanonicalSha256,
                _recordCount,
                _observationCount,
                _exclusionCount,
                _gapCount,
                _firstSourceSequence,
                _lastSourceSequence);

            var dataLeaf = CanonicalCacheManifest.FromCompletion(completion)
                .DataLeafName;
            _observer?.Invoke(CanonicalCacheWriterStage.PublishData);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishDataAsync(dataLeaf, completion, cancellationToken)
                .ConfigureAwait(false);

            var manifest = CanonicalCacheManifest.FromCompletion(completion);
            var manifestBytes = manifest.Serialize();
            _manifestStagingLeaf = CreateStagingLeaf(
                _request.Identity,
                manifest: true);
            _manifestStream = _directory.CreateNewFile(_manifestStagingLeaf);
            _manifestIsStaging = true;
            _observer?.Invoke(CanonicalCacheWriterStage.WriteManifest);
            await _manifestStream.WriteAsync(manifestBytes, cancellationToken)
                .ConfigureAwait(false);
            _observer?.Invoke(CanonicalCacheWriterStage.FlushManifest);
            await FlushDurablyAsync(_manifestStream, cancellationToken)
                .ConfigureAwait(false);

            _observer?.Invoke(CanonicalCacheWriterStage.PublishManifest);
            cancellationToken.ThrowIfCancellationRequested();
            _directory.RenameOpenFile(
                _manifestStream.SafeFileHandle,
                CanonicalCacheManifest.CreateManifestLeafName(_request.Identity));
            _manifestIsStaging = false;

            await _manifestStream.DisposeAsync().ConfigureAwait(false);
            _manifestStream = null;
            if (_dataStream is not null)
            {
                await _dataStream.DisposeAsync().ConfigureAwait(false);
                _dataStream = null;
            }

            await VerifyPublishedAsync(
                    manifest,
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _finalized = true;
            return completion;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failures = new List<Exception>();
        await CleanupStreamAsync(
                _manifestStream,
                _manifestIsStaging,
                failures)
            .ConfigureAwait(false);
        await CleanupStreamAsync(_dataStream, _dataIsStaging, failures)
            .ConfigureAwait(false);
        try
        {
            _directory.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private void ValidateOrder(CanonicalRecord record)
    {
        if (_sequenceExhausted)
        {
            throw new InvalidOperationException(
                "No record can follow the maximum source sequence.");
        }

        switch (record.Kind)
        {
            case CanonicalRecordKind.Gap
                when record.FirstMissingSequence == _nextSourceSequence
                     && record.LastMissingSequence is { } gapLast:
                AdvanceAfter(gapLast);
                break;
            case CanonicalRecordKind.Observation or CanonicalRecordKind.Exclusion
                when record.SourceSequence == _nextSourceSequence:
                AdvanceAfter(record.SourceSequence!.Value);
                break;
            default:
                throw new InvalidOperationException(
                    "Canonical cache records must cover source order exactly.");
        }
    }

    private void AdvanceAfter(long sequence)
    {
        if (sequence == long.MaxValue)
        {
            _sequenceExhausted = true;
        }
        else
        {
            _nextSourceSequence = sequence + 1;
        }
    }

    private void Account(CanonicalRecord record)
    {
        _recordCount++;
        switch (record.Kind)
        {
            case CanonicalRecordKind.Observation:
                _observationCount++;
                AccountSourceSequence(record.SourceSequence!.Value);
                break;
            case CanonicalRecordKind.Exclusion:
                _exclusionCount++;
                AccountSourceSequence(record.SourceSequence!.Value);
                break;
            case CanonicalRecordKind.Gap:
                _gapCount++;
                break;
        }
    }

    private void AccountSourceSequence(long sequence)
    {
        _firstSourceSequence ??= sequence;
        _lastSourceSequence = sequence;
    }

    private async Task VerifyPublishedAsync(
        CanonicalCacheManifest manifest,
        byte[] expectedManifest,
        CancellationToken cancellationToken)
    {
        await using (var manifestStream = _directory.OpenExistingReadOnly(
                         CanonicalCacheManifest.CreateManifestLeafName(
                             _request.Identity)))
        {
            if (manifestStream.Length != expectedManifest.Length)
            {
                throw new IOException(
                    "The published canonical manifest could not be verified.");
            }

            var actual = new byte[expectedManifest.Length];
            await manifestStream.ReadExactlyAsync(actual, cancellationToken)
                .ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expectedManifest))
            {
                throw new IOException(
                    "The published canonical manifest could not be verified.");
            }
        }

        await using var dataStream = _directory.OpenExistingReadOnly(
            manifest.DataLeafName);
        var hashes = await CanonicalCacheHash.CalculateAsync(
                dataStream,
                _request.Identity,
                manifest.Completion.DataLengthBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (dataStream.Length != manifest.Completion.DataLengthBytes
            || !StringComparer.Ordinal.Equals(
                hashes.DataSha256,
                manifest.Completion.DataSha256)
            || !StringComparer.Ordinal.Equals(
                hashes.CanonicalSha256,
                manifest.Completion.CanonicalSha256))
        {
            throw new IOException(
                "The published canonical data could not be verified.");
        }
    }

    private async Task PublishDataAsync(
        string dataLeaf,
        CanonicalCacheCompletion completion,
        CancellationToken cancellationToken)
    {
        try
        {
            _directory.RenameOpenFile(_dataStream!.SafeFileHandle, dataLeaf);
            _dataIsStaging = false;
            return;
        }
        catch (IOException publicationFailure)
        {
            await using var existing = _directory.TryOpenExistingReadOnly(dataLeaf);
            if (existing is null
                || existing.Length != completion.DataLengthBytes)
            {
                throw new IOException(
                    "An occupied canonical data leaf did not match the finalized bytes.",
                    publicationFailure);
            }

            var hashes = await CanonicalCacheHash.CalculateAsync(
                    existing,
                    _request.Identity,
                    completion.DataLengthBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(
                    hashes.DataSha256,
                    completion.DataSha256)
                || !StringComparer.Ordinal.Equals(
                    hashes.CanonicalSha256,
                    completion.CanonicalSha256))
            {
                throw new IOException(
                    "An occupied canonical data leaf did not match the finalized bytes.",
                    publicationFailure);
            }

            _directory.DeleteOpenFile(_dataStream!.SafeFileHandle);
            _dataIsStaging = false;
            await _dataStream.DisposeAsync().ConfigureAwait(false);
            _dataStream = null;
        }
    }

    private static async Task FlushDurablyAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task CleanupStreamAsync(
        FileStream? stream,
        bool delete,
        List<Exception> failures)
    {
        if (stream is null)
        {
            return;
        }

        if (delete)
        {
            try
            {
                _directory.DeleteOpenFile(stream.SafeFileHandle);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void TryDelete(
        WindowsCanonicalCacheDirectory? directory,
        FileStream stream)
    {
        try
        {
            directory?.DeleteOpenFile(stream.SafeFileHandle);
        }
        catch
        {
            // Preserve the creation failure. The owned handle still closes below.
        }
    }

    private static string CreateStagingLeaf(
        CanonicalReplayIdentity identity,
        bool manifest) =>
        $"{identity.IdentitySha256}.{Guid.NewGuid():N}.apxcan"
        + (manifest ? ".json.partial" : ".partial");

    private void ThrowIfUnavailableForWrite()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_faulted || _finalized)
        {
            throw new InvalidOperationException(
                "The canonical cache writer is no longer writable.");
        }
    }

    private void ThrowIfUnavailableForFinalize()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_faulted || _finalized)
        {
            throw new InvalidOperationException(
                "The canonical cache writer cannot be finalized again.");
        }
    }

}
