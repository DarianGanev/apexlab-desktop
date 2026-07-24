using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ApexLab.Application.Capture;
using ApexLab.Application.Storage;

namespace ApexLab.Persistence.Raw;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRawEvidenceDirectory : IDisposable
{
    private readonly string _rootPath;
    private readonly string _capturesPath;
    private readonly SafeFileHandle _rootHandle;
    private readonly SafeFileHandle _capturesHandle;
    private int _disposed;

    private WindowsRawEvidenceDirectory(
        string rootPath,
        string capturesPath,
        SafeFileHandle rootHandle,
        SafeFileHandle capturesHandle)
    {
        _rootPath = rootPath;
        _capturesPath = capturesPath;
        _rootHandle = rootHandle;
        _capturesHandle = capturesHandle;
    }

    public static WindowsRawEvidenceDirectory Open(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Raw evidence v1 requires Windows handle safety.");
        }

        Directory.CreateDirectory(paths.RootDirectory);
        SafeFileHandle? rootHandle = null;
        SafeFileHandle? capturesHandle = null;
        try
        {
            rootHandle = OpenDirectory(paths.RootDirectory);
            ValidateDirectory(rootHandle, paths.RootDirectory);

            Directory.CreateDirectory(paths.RawCapturesDirectory);
            capturesHandle = OpenDirectory(paths.RawCapturesDirectory);
            ValidateDirectory(
                capturesHandle,
                paths.RawCapturesDirectory);

            return new WindowsRawEvidenceDirectory(
                paths.RootDirectory,
                paths.RawCapturesDirectory,
                rootHandle,
                capturesHandle);
        }
        catch
        {
            capturesHandle?.Dispose();
            rootHandle?.Dispose();
            throw;
        }
    }

    public FileStream CreateNewFile(string leafName)
    {
        ThrowIfDisposed();
        ValidateLeafName(leafName);
        var expectedPath = Path.Combine(_capturesPath, leafName);
        var handle = WindowsRawEvidenceNative.CreateFile(
            expectedPath,
            WindowsRawEvidenceNative.GenericRead
            | WindowsRawEvidenceNative.GenericWrite
            | WindowsRawEvidenceNative.DeleteAccess,
            WindowsRawEvidenceNative.ShareRead,
            IntPtr.Zero,
            WindowsRawEvidenceNative.CreateNew,
            WindowsRawEvidenceNative.FileAttributeNormal
            | WindowsRawEvidenceNative.FileFlagOpenReparsePoint
            | WindowsRawEvidenceNative.FileFlagOverlapped,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"create {leafName}");

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
        ThrowIfDisposed();
        ValidateLeafName(leafName);
        var expectedPath = Path.Combine(_capturesPath, leafName);
        var handle = WindowsRawEvidenceNative.CreateFile(
            expectedPath,
            WindowsRawEvidenceNative.GenericRead,
            WindowsRawEvidenceNative.ShareRead,
            IntPtr.Zero,
            WindowsRawEvidenceNative.OpenExisting,
            WindowsRawEvidenceNative.FileAttributeNormal
            | WindowsRawEvidenceNative.FileFlagOpenReparsePoint
            | WindowsRawEvidenceNative.FileFlagOverlapped,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open {leafName}");

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

    public FileStream? TryOpenExistingReadOnly(string leafName)
    {
        ThrowIfDisposed();
        ValidateLeafName(leafName);
        var expectedPath = Path.Combine(_capturesPath, leafName);
        var handle = WindowsRawEvidenceNative.CreateFile(
            expectedPath,
            WindowsRawEvidenceNative.GenericRead,
            WindowsRawEvidenceNative.ShareRead,
            IntPtr.Zero,
            WindowsRawEvidenceNative.OpenExisting,
            WindowsRawEvidenceNative.FileAttributeNormal
            | WindowsRawEvidenceNative.FileFlagOpenReparsePoint
            | WindowsRawEvidenceNative.FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is WindowsRawEvidenceNative.ErrorFileNotFound
                or WindowsRawEvidenceNative.ErrorPathNotFound)
            {
                return null;
            }

            throw NewWin32Exception(
                $"open {leafName}",
                error);
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

    public RawEvidenceFileIdentity GetIdentity(
        SafeFileHandle fileHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        var info = Query<WindowsRawEvidenceNative.FileIdInfo>(
            fileHandle,
            WindowsRawEvidenceNative.FileIdInfoClass);
        return new RawEvidenceFileIdentity(
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
        ValidateLeafName(finalLeafName);
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "Raw evidence v1 handle rename requires win-x64.");
        }

        var finalPath = Path.GetFullPath(
            Path.Combine(_capturesPath, finalLeafName));
        var fileNameBytes = checked(finalPath.Length * sizeof(char));
        const int fileNameOffset = 20;
        var bufferLength = checked(
            fileNameOffset + fileNameBytes + sizeof(char));
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

            if (!WindowsRawEvidenceNative.SetFileInformationByHandle(
                    fileHandle,
                    WindowsRawEvidenceNative.FileRenameInfoClass,
                    buffer,
                    checked((uint)bufferLength)))
            {
                throw NewWin32Exception(
                    $"rename to {finalLeafName}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        RequireExpectedPath(
            fileHandle,
            finalPath);
    }

    public void DeleteOpenFile(SafeFileHandle fileHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fileHandle);
        var disposition = new WindowsRawEvidenceNative.FileDispositionInfo
        {
            DeleteFile = 1,
        };
        var size = Marshal.SizeOf<
            WindowsRawEvidenceNative.FileDispositionInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(
                disposition,
                buffer,
                fDeleteOld: false);
            if (!WindowsRawEvidenceNative.SetFileInformationByHandle(
                    fileHandle,
                    WindowsRawEvidenceNative.FileDispositionInfoClass,
                    buffer,
                    checked((uint)size)))
            {
                throw NewWin32Exception(
                    "mark raw evidence for deletion");
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

        _capturesHandle.Dispose();
        _rootHandle.Dispose();
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = WindowsRawEvidenceNative.CreateFile(
            path,
            WindowsRawEvidenceNative.GenericRead
            | WindowsRawEvidenceNative.FileListDirectory
            | WindowsRawEvidenceNative.FileReadAttributes,
            WindowsRawEvidenceNative.ShareRead
            | WindowsRawEvidenceNative.ShareWrite,
            IntPtr.Zero,
            WindowsRawEvidenceNative.OpenExisting,
            WindowsRawEvidenceNative.FileFlagBackupSemantics
            | WindowsRawEvidenceNative.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open directory {Path.GetFileName(path)}");
        return handle;
    }

    private static void ValidateDirectory(
        SafeFileHandle handle,
        string expectedPath)
    {
        var attributes = Query<WindowsRawEvidenceNative.FileAttributeTagInfo>(
            handle,
            WindowsRawEvidenceNative.FileAttributeTagInfoClass);
        var standard = Query<WindowsRawEvidenceNative.FileStandardInfo>(
            handle,
            WindowsRawEvidenceNative.FileStandardInfoClass);
        if ((attributes.FileAttributes
             & WindowsRawEvidenceNative.FileAttributeReparsePoint) != 0
            || standard.Directory == 0)
        {
            throw new IOException(
                "A raw evidence directory cannot be a reparse point.");
        }

        RequireExpectedPath(handle, expectedPath);
    }

    private static void ValidateRegularSingleLinkFile(
        SafeFileHandle handle,
        string expectedPath)
    {
        var attributes = Query<WindowsRawEvidenceNative.FileAttributeTagInfo>(
            handle,
            WindowsRawEvidenceNative.FileAttributeTagInfoClass);
        var standard = Query<WindowsRawEvidenceNative.FileStandardInfo>(
            handle,
            WindowsRawEvidenceNative.FileStandardInfoClass);
        if ((attributes.FileAttributes
             & WindowsRawEvidenceNative.FileAttributeReparsePoint) != 0
            || standard.Directory != 0
            || standard.NumberOfLinks != 1)
        {
            throw new IOException(
                "A raw evidence file must be a regular single-link file.");
        }

        RequireExpectedPath(handle, expectedPath);
    }

    private static void RequireExpectedPath(
        SafeFileHandle handle,
        string expectedPath)
    {
        var actual = GetNormalizedPathCore(handle);
        var expected = Path.GetFullPath(expectedPath);
        if (!string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "A raw evidence handle resolved outside its generated path.");
        }
    }

    private static string GetNormalizedPathCore(SafeFileHandle handle)
    {
        const int capacity = 32_768;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            var length = WindowsRawEvidenceNative.GetFinalPathNameByHandle(
                handle,
                buffer,
                capacity,
                flags: 0);
            if (length == 0)
            {
                throw NewWin32Exception("resolve final handle path");
            }

            if (length >= capacity)
            {
                throw new PathTooLongException(
                    "A raw evidence handle path exceeded the v1 bound.");
            }

            var nativePath = Marshal.PtrToStringUni(
                buffer,
                checked((int)length))
                ?? throw new IOException(
                    "Windows returned no final handle path.");
            var dosPath = nativePath.StartsWith(
                @"\\?\",
                StringComparison.Ordinal)
                ? nativePath[4..]
                : nativePath;
            return Path.GetFullPath(dosPath);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static T Query<T>(
        SafeFileHandle handle,
        int informationClass)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!WindowsRawEvidenceNative.GetFileInformationByHandleEx(
                    handle,
                    informationClass,
                    buffer,
                    checked((uint)size)))
            {
                throw NewWin32Exception("query file handle");
            }

            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateLeafName(string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName)
            || leafName.Length > 255
            || leafName != leafName.Trim()
            || leafName.Length <= 32
            || !RawEvidenceCaptureId.TryParse(
                leafName[..32],
                out _)
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

    private static Win32Exception NewWin32Exception(string operation)
    {
        return NewWin32Exception(
            operation,
            Marshal.GetLastWin32Error());
    }

    private static Win32Exception NewWin32Exception(
        string operation,
        int error)
    {
        return new Win32Exception(
            error,
            $"Windows could not {operation} (error {error}).");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}

internal readonly record struct RawEvidenceFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

[SupportedOSPlatform("windows")]
internal static partial class WindowsRawEvidenceNative
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint DeleteAccess = 0x00010000;
    public const uint FileListDirectory = 0x00000001;
    public const uint FileReadAttributes = 0x00000080;
    public const uint ShareRead = 0x00000001;
    public const uint ShareWrite = 0x00000002;
    public const uint CreateNew = 1;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x00000080;
    public const uint FileAttributeReparsePoint = 0x00000400;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const int FileStandardInfoClass = 1;
    public const int FileAttributeTagInfoClass = 9;
    public const int FileIdInfoClass = 18;
    public const int FileRenameInfoClass = 3;
    public const int FileDispositionInfoClass = 4;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        IntPtr filePath,
        int filePathLength,
        uint flags);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileDispositionInfo
    {
        public byte DeleteFile;
    }
}
