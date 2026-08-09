using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ApexLab.Application.Storage;
using Microsoft.Win32.SafeHandles;

namespace ApexLab.Persistence.Storage;

[SupportedOSPlatform("windows")]
internal sealed class WindowsLocalDataDirectory : IDisposable
{
    private readonly string _childPath;
    private readonly SafeFileHandle _rootHandle;
    private readonly SafeFileHandle _childHandle;
    private int _disposed;

    private WindowsLocalDataDirectory(
        string childPath,
        SafeFileHandle rootHandle,
        SafeFileHandle childHandle)
    {
        _childPath = childPath;
        _rootHandle = rootHandle;
        _childHandle = childHandle;
    }

    public static WindowsLocalDataDirectory Open(
        string rootPath,
        string childDirectoryName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Local handle-safe storage requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ValidateLeafName(childDirectoryName, nameof(childDirectoryName));
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException(
                "An absolute local root path is required.",
                nameof(rootPath));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath));
        var childPath = Path.Combine(normalizedRoot, childDirectoryName);
        Directory.CreateDirectory(normalizedRoot);

        SafeFileHandle? rootHandle = null;
        SafeFileHandle? childHandle = null;
        try
        {
            rootHandle = OpenDirectory(normalizedRoot);
            ValidateDirectory(rootHandle, normalizedRoot);
            Directory.CreateDirectory(childPath);
            childHandle = OpenDirectory(childPath);
            ValidateDirectory(childHandle, childPath);
            return new(childPath, rootHandle, childHandle);
        }
        catch
        {
            childHandle?.Dispose();
            rootHandle?.Dispose();
            throw;
        }
    }

    public FileStream CreateNewFile(string leafName)
    {
        ThrowIfDisposed();
        ValidateLeafName(leafName, nameof(leafName));
        var expectedPath = Path.Combine(_childPath, leafName);
        var handle = WindowsLocalDataNative.CreateFile(
            expectedPath,
            WindowsLocalDataNative.GenericRead
            | WindowsLocalDataNative.GenericWrite
            | WindowsLocalDataNative.DeleteAccess,
            WindowsLocalDataNative.ShareRead,
            IntPtr.Zero,
            WindowsLocalDataNative.CreateNew,
            WindowsLocalDataNative.FileAttributeNormal
            | WindowsLocalDataNative.FileFlagOpenReparsePoint
            | WindowsLocalDataNative.FileFlagOverlapped,
            IntPtr.Zero);
        ThrowIfInvalid(handle, "create local data leaf");

        try
        {
            ValidateRegularSingleLinkFile(handle, expectedPath);
            return new FileStream(
                handle,
                FileAccess.ReadWrite,
                bufferSize: 4_096,
                isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public FileStream OpenExistingReadOnly(string leafName)
    {
        var stream = TryOpenExistingReadOnlyCore(leafName, allowMissing: false);
        return stream!;
    }

    public FileStream? TryOpenExistingReadOnly(string leafName) =>
        TryOpenExistingReadOnlyCore(leafName, allowMissing: true);

    public LocalDataFileIdentity GetIdentity(SafeFileHandle fileHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        var info = Query<WindowsLocalDataNative.FileIdInfo>(
            fileHandle,
            WindowsLocalDataNative.FileIdInfoClass);
        return new(
            info.VolumeSerialNumber,
            info.FileIdLow,
            info.FileIdHigh);
    }

    public string GetNormalizedPath(SafeFileHandle fileHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        return GetNormalizedPathCore(fileHandle);
    }

    public void RenameOpenFile(
        SafeFileHandle fileHandle,
        string finalLeafName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        ValidateLeafName(finalLeafName, nameof(finalLeafName));
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "Handle-based local rename requires win-x64.");
        }

        var finalPath = Path.GetFullPath(Path.Combine(_childPath, finalLeafName));
        var fileNameBytes = checked(finalPath.Length * sizeof(char));
        const int fileNameOffset = 20;
        var bufferLength = checked(fileNameOffset + fileNameBytes + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            for (var offset = 0; offset < bufferLength; offset++)
            {
                Marshal.WriteByte(buffer, offset, 0);
            }

            Marshal.WriteInt32(buffer, 16, fileNameBytes);
            Marshal.Copy(
                finalPath.ToCharArray(),
                0,
                IntPtr.Add(buffer, fileNameOffset),
                finalPath.Length);

            if (!WindowsLocalDataNative.SetFileInformationByHandle(
                    fileHandle,
                    WindowsLocalDataNative.FileRenameInfoClass,
                    buffer,
                    checked((uint)bufferLength)))
            {
                throw NewWin32Exception("rename local data leaf");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        RequireExpectedPath(fileHandle, finalPath);
    }

    public void DeleteOpenFile(SafeFileHandle fileHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        var disposition = new WindowsLocalDataNative.FileDispositionInfo
        {
            DeleteFile = 1,
        };
        var size = Marshal.SizeOf<WindowsLocalDataNative.FileDispositionInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(disposition, buffer, fDeleteOld: false);
            if (!WindowsLocalDataNative.SetFileInformationByHandle(
                    fileHandle,
                    WindowsLocalDataNative.FileDispositionInfoClass,
                    buffer,
                    checked((uint)size)))
            {
                throw NewWin32Exception("mark local data leaf for deletion");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _childHandle.Dispose();
        _rootHandle.Dispose();
    }

    private FileStream? TryOpenExistingReadOnlyCore(
        string leafName,
        bool allowMissing)
    {
        ThrowIfDisposed();
        ValidateLeafName(leafName, nameof(leafName));
        var expectedPath = Path.Combine(_childPath, leafName);
        var handle = WindowsLocalDataNative.CreateFile(
            expectedPath,
            WindowsLocalDataNative.GenericRead,
            WindowsLocalDataNative.ShareRead,
            IntPtr.Zero,
            WindowsLocalDataNative.OpenExisting,
            WindowsLocalDataNative.FileAttributeNormal
            | WindowsLocalDataNative.FileFlagOpenReparsePoint
            | WindowsLocalDataNative.FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (allowMissing
                && error is WindowsLocalDataNative.ErrorFileNotFound
                    or WindowsLocalDataNative.ErrorPathNotFound)
            {
                return null;
            }

            throw NewWin32Exception("open local data leaf", error);
        }

        try
        {
            ValidateRegularSingleLinkFile(handle, expectedPath);
            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 4_096,
                isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = WindowsLocalDataNative.CreateFile(
            path,
            WindowsLocalDataNative.GenericRead
            | WindowsLocalDataNative.FileListDirectory
            | WindowsLocalDataNative.FileReadAttributes,
            WindowsLocalDataNative.ShareRead | WindowsLocalDataNative.ShareWrite,
            IntPtr.Zero,
            WindowsLocalDataNative.OpenExisting,
            WindowsLocalDataNative.FileFlagBackupSemantics
            | WindowsLocalDataNative.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, "open local data directory");
        return handle;
    }

    private static void ValidateDirectory(
        SafeFileHandle handle,
        string expectedPath)
    {
        var attributes = Query<WindowsLocalDataNative.FileAttributeTagInfo>(
            handle,
            WindowsLocalDataNative.FileAttributeTagInfoClass);
        var standard = Query<WindowsLocalDataNative.FileStandardInfo>(
            handle,
            WindowsLocalDataNative.FileStandardInfoClass);
        if ((attributes.FileAttributes
             & WindowsLocalDataNative.FileAttributeReparsePoint) != 0
            || standard.Directory == 0)
        {
            throw new IOException(
                "A local data directory cannot be a reparse point.");
        }

        RequireExpectedPath(handle, expectedPath);
    }

    private static void ValidateRegularSingleLinkFile(
        SafeFileHandle handle,
        string expectedPath)
    {
        var attributes = Query<WindowsLocalDataNative.FileAttributeTagInfo>(
            handle,
            WindowsLocalDataNative.FileAttributeTagInfoClass);
        var standard = Query<WindowsLocalDataNative.FileStandardInfo>(
            handle,
            WindowsLocalDataNative.FileStandardInfoClass);
        if ((attributes.FileAttributes
             & WindowsLocalDataNative.FileAttributeReparsePoint) != 0
            || standard.Directory != 0
            || standard.NumberOfLinks != 1)
        {
            throw new IOException(
                "A local data leaf must be a regular single-link file.");
        }

        RequireExpectedPath(handle, expectedPath);
    }

    private static void RequireExpectedPath(
        SafeFileHandle handle,
        string expectedPath)
    {
        var actual = GetNormalizedPathCore(handle);
        var expected = Path.GetFullPath(expectedPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "A local data handle resolved outside its validated path.");
        }
    }

    private static string GetNormalizedPathCore(SafeFileHandle handle)
    {
        const int capacity = 32_768;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            var length = WindowsLocalDataNative.GetFinalPathNameByHandle(
                handle,
                buffer,
                capacity,
                flags: 0);
            if (length == 0)
            {
                throw NewWin32Exception("resolve final local data path");
            }

            if (length >= capacity)
            {
                throw new PathTooLongException(
                    "A local data handle path exceeded the supported bound.");
            }

            var nativePath = Marshal.PtrToStringUni(buffer, checked((int)length))
                ?? throw new IOException("Windows returned no final handle path.");
            var dosPath = nativePath.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? nativePath[4..]
                : nativePath;
            return Path.GetFullPath(dosPath);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static T Query<T>(SafeFileHandle handle, int informationClass)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!WindowsLocalDataNative.GetFileInformationByHandleEx(
                    handle,
                    informationClass,
                    buffer,
                    checked((uint)size)))
            {
                throw NewWin32Exception("query local data handle");
            }

            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateLeafName(string leafName, string parameterName)
    {
        if (leafName is not { Length: >= 1 and <= 255 }
            || leafName is "." or ".."
            || !WindowsPathSegment.IsSafe(leafName))
        {
            throw new ArgumentException(
                "A safe local data leaf name is required.",
                parameterName);
        }
    }

    private static void ThrowIfInvalid(
        SafeFileHandle handle,
        string operation)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NewWin32Exception(operation, error);
        }
    }

    private static Win32Exception NewWin32Exception(string operation) =>
        NewWin32Exception(operation, Marshal.GetLastWin32Error());

    private static Win32Exception NewWin32Exception(
        string operation,
        int error) =>
        new(error, $"Windows could not {operation} (error {error}).");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

internal readonly record struct LocalDataFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);
