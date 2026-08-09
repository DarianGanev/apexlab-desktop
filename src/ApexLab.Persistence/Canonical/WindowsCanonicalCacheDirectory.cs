using System.ComponentModel;
using System.Runtime.Versioning;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Storage;
using Microsoft.Win32.SafeHandles;

namespace ApexLab.Persistence.Canonical;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCanonicalCacheDirectory : IDisposable
{
    private readonly WindowsLocalDataDirectory _directory;

    private WindowsCanonicalCacheDirectory(WindowsLocalDataDirectory directory) =>
        _directory = directory;

    public static WindowsCanonicalCacheDirectory Open(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(WindowsLocalDataDirectory.Open(
            paths.RootDirectory,
            Path.GetFileName(paths.DerivedCacheDirectory)));
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
                "A canonical cache leaf could not be published.",
                exception);
        }
    }

    public void DeleteOpenFile(SafeFileHandle handle) =>
        _directory.DeleteOpenFile(handle);

    public void Dispose() => _directory.Dispose();

    private static void ValidateLeafName(string leafName)
    {
        if (leafName is not { Length: >= 72 and <= 144 })
        {
            throw InvalidLeaf();
        }

        var parts = leafName.Split('.');
        var valid = parts switch
        {
            [var identity, "apxcan", "json"] => IsSha256(identity),
            [var identity, var dataHash, "apxcan"] =>
                IsSha256(identity) && IsSha256(dataHash),
            [var identity, var staging, "apxcan", "partial"] =>
                IsSha256(identity) && IsStagingId(staging),
            [var identity, var staging, "apxcan", "json", "partial"] =>
                IsSha256(identity) && IsStagingId(staging),
            _ => false,
        };
        if (!valid)
        {
            throw InvalidLeaf();
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool IsStagingId(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static ArgumentException InvalidLeaf() =>
        new("A generated canonical cache leaf name is required.", "leafName");
}
