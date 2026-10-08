using System.Runtime.InteropServices;

namespace IvarOffload.Core.IO;

/// <summary>Win32 declarations used by the preview's checks: sync detection, drive facts and known folders.</summary>
internal static unsafe partial class Native
{
    /// <summary>CF_SYNC_ROOT_INFO_PROVIDER: fills a CF_SYNC_ROOT_PROVIDER_INFO (status, then the provider name).</summary>
    internal const int CF_SYNC_ROOT_INFO_PROVIDER = 2;

    /// <summary>Offset of ProviderName (WCHAR[256]) in CF_SYNC_ROOT_PROVIDER_INFO, after the ULONG ProviderStatus.</summary>
    internal const int CF_PROVIDER_NAME_OFFSET = 4;
    internal const int CF_MAX_PROVIDER_NAME_LENGTH = 255;

    internal static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Asks the Windows Cloud Files API which sync provider (OneDrive, iCloud, Box, ...) owns a path. Returns an HRESULT.</summary>
    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CfGetSyncRootInfoByPath(string filePath, int infoClass, void* infoBuffer, uint infoBufferLength, out uint returnedLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    /// <summary>GetFinalPathNameByHandle flag: the path with a drive letter, or \\?\UNC\server\share for a network folder.</summary>
    internal const uint VOLUME_NAME_DOS = 0;

    /// <summary>Where an open file or folder really is: junctions, links, subst and mapped drive letters and short names resolved.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, char* path, uint pathLength, uint flags);
}
