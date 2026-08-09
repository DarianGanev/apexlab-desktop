using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Storage;
using Microsoft.Win32.SafeHandles;

namespace ApexLab.Persistence.Raw;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRawEvidenceDirectory : IDisposable
{
    private readonly WindowsLocalDataDirectory _directory;

    private WindowsRawEvidenceDirectory(WindowsLocalDataDirectory directory) =>
        _directory = directory;

    public static WindowsRawEvidenceDirectory Open(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(WindowsLocalDataDirectory.Open(
            paths.RootDirectory,
            Path.GetFileName(paths.RawCapturesDirectory)));
    }

    public FileStream CreateNewFile(string leafName)
    {
        ValidateLeafName(leafName);
        return _directory.CreateNewFile(leafName);
    }

    public FileStream OpenExistingReadOnly(string leafName)
    {
        ValidateLeafName(leafName);
        return _directory.OpenExistingReadOnly(leafName);
    }

    public FileStream? TryOpenExistingReadOnly(string leafName)
    {
        ValidateLeafName(leafName);
        return _directory.TryOpenExistingReadOnly(leafName);
    }

    public RawEvidenceFileIdentity GetIdentity(SafeFileHandle fileHandle)
    {
        var identity = _directory.GetIdentity(fileHandle);
        return new(
            identity.VolumeSerialNumber,
            identity.FileIdLow,
            identity.FileIdHigh);
    }

    public string GetNormalizedPath(SafeFileHandle fileHandle) =>
        _directory.GetNormalizedPath(fileHandle);

    public void RenameOpenFile(
        SafeFileHandle fileHandle,
        string finalLeafName)
    {
        ValidateLeafName(finalLeafName);
        _directory.RenameOpenFile(fileHandle, finalLeafName);
    }

    public void DeleteOpenFile(SafeFileHandle fileHandle) =>
        _directory.DeleteOpenFile(fileHandle);

    public void Dispose() => _directory.Dispose();

    private static void ValidateLeafName(string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName)
            || leafName.Length > 255
            || leafName != leafName.Trim()
            || leafName.Length <= 32
            || !RawEvidenceCaptureId.TryParse(leafName[..32], out _)
            || leafName[32..] is not (
                ".apxraw"
                or ".apxraw.partial"
                or ".apxraw.json"
                or ".apxraw.json.partial"))
        {
            throw new ArgumentException(
                "A generated raw evidence leaf name is required.",
                nameof(leafName));
        }
    }
}

internal readonly record struct RawEvidenceFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);
