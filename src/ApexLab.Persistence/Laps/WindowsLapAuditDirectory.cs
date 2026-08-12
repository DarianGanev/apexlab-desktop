using System.ComponentModel;
using System.Runtime.Versioning;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Storage;
using Microsoft.Win32.SafeHandles;

namespace ApexLab.Persistence.Laps;

[SupportedOSPlatform("windows")]
internal sealed class WindowsLapAuditDirectory : IDisposable
{
    private readonly WindowsLocalDataDirectory _directory;

    private WindowsLapAuditDirectory(WindowsLocalDataDirectory directory) =>
        _directory = directory;

    public static WindowsLapAuditDirectory Open(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(WindowsLocalDataDirectory.Open(
            paths.RootDirectory,
            Path.GetFileName(paths.LapAuditsDirectory)));
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

    public FileStream? TryOpenExistingForDeletion(string leafName)
    {
        ValidateLeafName(leafName);
        return _directory.TryOpenExistingForDeletion(leafName);
    }

    public void RenameOpenFile(SafeFileHandle handle, string finalLeafName)
    {
        ValidateLeafName(finalLeafName);
        try
        {
            _directory.RenameOpenFile(handle, finalLeafName);
        }
        catch (Win32Exception exception)
        {
            throw new IOException(
                "A lap audit document could not be published.",
                exception);
        }
    }

    public void DeleteOpenFile(SafeFileHandle handle) =>
        _directory.DeleteOpenFile(handle);

    public void Dispose() => _directory.Dispose();

    private static void ValidateLeafName(string leafName)
    {
        var parts = leafName.Split('.');
        var valid = parts switch
        {
            [var capture, "bahrain-lap-audit", "json"] => IsCaptureId(capture),
            [var capture, "bahrain-lap-audit", "completed", "json"] =>
                IsCaptureId(capture),
            [var capture, var staging, "bahrain-lap-audit", "json", "partial"] =>
                IsCaptureId(capture) && IsStagingId(staging),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "A generated lap audit leaf name is required.",
                nameof(leafName));
        }
    }

    private static bool IsCaptureId(string value) =>
        RawEvidenceCaptureId.TryParse(value, out _);

    private static bool IsStagingId(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}
