using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IvarOffload.Core.IO;

/// <summary>Win32 declarations used for the operations .NET does not expose directly.</summary>
internal static unsafe partial class Native
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    internal const uint DELETE = 0x00010000;

    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint FILE_SHARE_DELETE = 0x4;

    internal const uint OPEN_EXISTING = 3;

    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    internal const uint MOVEFILE_WRITE_THROUGH = 0x8;

    internal const int ERROR_INVALID_FUNCTION = 1;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_DRIVE = 15;
    internal const int ERROR_NOT_SAME_DEVICE = 17;
    internal const int ERROR_WRITE_PROTECT = 19;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_CRC = 23;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_LOCK_VIOLATION = 33;
    internal const int ERROR_HANDLE_DISK_FULL = 39;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_BAD_NETPATH = 53;
    internal const int ERROR_DEV_NOT_EXIST = 55;
    internal const int ERROR_NETNAME_DELETED = 64;
    internal const int ERROR_BAD_NET_NAME = 67;
    internal const int ERROR_FILE_EXISTS = 80;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_DISK_FULL = 112;
    internal const int ERROR_SEM_TIMEOUT = 121;
    internal const int ERROR_ALREADY_EXISTS = 183;
    internal const int ERROR_FILE_TOO_LARGE = 223;
    internal const int ERROR_NO_SUCH_DEVICE = 433;
    internal const int ERROR_FILE_INVALID = 1006;
    internal const int ERROR_IO_DEVICE = 1117;
    internal const int ERROR_DEVICE_NOT_CONNECTED = 1167;

    internal const uint DRIVE_REMOVABLE = 2;
    internal const uint DRIVE_FIXED = 3;
    internal const uint DRIVE_REMOTE = 4;
    internal const uint DRIVE_CDROM = 5;
    internal const uint DRIVE_RAMDISK = 6;

    internal const uint FILE_READ_ONLY_VOLUME = 0x00080000;

    internal const int FileBasicInfo = 0;
    internal const int FileStandardInfo = 1;
    internal const int FileDispositionInfo = 4;
    internal const int FileIdInfo = 18;
    internal const int FileDispositionInfoEx = 21;

    internal const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    internal const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    internal const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileTime(SafeFileHandle file, nint creationTime, ref long lastAccessTime, nint lastWriteTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string rootPathName);

    /// <summary>What a drive letter ("W:") stands for: "\??\C:\Shoots" for a subst drive, "\Device\HarddiskVolume3" for a volume.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint QueryDosDevice(string deviceName, char* targetPath, uint max);

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetThreadExecutionState(uint flags);
}
