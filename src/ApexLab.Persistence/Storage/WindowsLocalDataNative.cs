using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ApexLab.Persistence.Storage;

[SupportedOSPlatform("windows")]
internal static partial class WindowsLocalDataNative
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
