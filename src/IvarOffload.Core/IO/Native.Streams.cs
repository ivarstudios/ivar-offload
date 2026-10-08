using System.Runtime.InteropServices;

namespace IvarOffload.Core.IO;

/// <summary>Win32 declarations for alternate data streams and the extra error codes the job engine reacts to.</summary>
internal static unsafe partial class Native
{
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint CREATE_NEW = 1;

    internal const int ERROR_HANDLE_EOF = 38;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_MEDIA_CHANGED = 1110;
    internal const int ERROR_DEVICE_REMOVED = 1617;

    /// <summary>GetVolumeInformation flag: the file system can store named (alternate) data streams.</summary>
    internal const uint FILE_NAMED_STREAMS = 0x00040000;

    internal const int FindStreamInfoStandard = 0;
    internal static readonly nint INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        public fixed char StreamName[296]; // MAX_PATH + 36
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindFirstStream(string fileName, int infoLevel, WIN32_FIND_STREAM_DATA* findStreamData, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextStream(nint findStream, WIN32_FIND_STREAM_DATA* findStreamData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(nint findFile);
}
