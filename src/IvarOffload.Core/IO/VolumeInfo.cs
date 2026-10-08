using System.Runtime.InteropServices;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.IO;

public enum DriveKind { Unknown, Removable, Fixed, Network, Optical, RamDisk }

/// <summary>The volume a path lives on (for paths that do not exist yet: the volume of the nearest existing parent).</summary>
public sealed record VolumeInfo(string Root, uint SerialNumber, string FileSystem, long FreeBytes, long TotalBytes)
{
    /// <summary>Volume label ("SONY_A", "T7 Shield"); empty when the volume has none.</summary>
    public string Label { get; init; } = "";

    public DriveKind Kind { get; init; } = DriveKind.Unknown;

    /// <summary>The raw file system flags from GetVolumeInformation.</summary>
    public uint FileSystemFlags { get; init; }

    /// <summary>A drive Windows reports as removable media (SD slots and most card readers; not USB SSDs).</summary>
    public bool IsRemovable => Kind == DriveKind.Removable;

    /// <summary>The whole volume is read-only (write-protect switch, read-only media or share).</summary>
    public bool IsReadOnly => (FileSystemFlags & FILE_READ_ONLY_VOLUME) != 0;

    /// <summary>FAT12/16/32 or exFAT: no file ids, local-time timestamps, no alternate data streams.</summary>
    public bool IsFatFamily =>
        FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Largest single file the file system can hold; long.MaxValue when it has no practical limit.</summary>
    public long MaxFileSize => FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) ? uint.MaxValue : long.MaxValue;

    /// <summary>NTFS and ReFS keep a file's id when it is renamed, so a move can be proven to be the same file.</summary>
    public bool HasStableFileIds =>
        FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => Root.TrimEnd('\\');

    /// <summary>"F: (SONY_A)" or "F:".</summary>
    public string DisplayNameWithLabel => Label.Length > 0 ? $"{DisplayName} ({Label})" : DisplayName;

    public bool IsSameVolume(VolumeInfo other) =>
        SerialNumber == other.SerialNumber && string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The volume of a path. A path on a subst drive letter (W: for C:\Shoots) is followed to the folder the letter
    /// stands for, so it gets that volume's serial number, file system and root (Windows has no volume of its own for it).
    /// </summary>
    public static unsafe VolumeInfo Of(string path)
    {
        string existing = NearestExistingFolder(Unsubst(Path.GetFullPath(path)))
            ?? throw new DirectoryNotFoundException($"No existing drive or folder found for \"{path}\".");
        char* rootBuffer = stackalloc char[1024];
        if (!GetVolumePathName(existing, rootBuffer, 1024))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Finding the drive", existing);
        string root = new(rootBuffer);

        char* fsBuffer = stackalloc char[64];
        char* labelBuffer = stackalloc char[261];
        string fileSystem = GetVolumeInformation(root, labelBuffer, 261, out uint serial, out _, out uint flags, fsBuffer, 64)
            ? new string(fsBuffer) : "unknown";
        string label = fileSystem == "unknown" ? "" : new string(labelBuffer);
        if (fileSystem == "unknown")
        {
            serial = 0;
            flags = 0;
        }
        GetDiskFreeSpaceEx(root, out ulong free, out ulong total, out _);
        DriveKind kind = GetDriveType(root) switch
        {
            DRIVE_REMOVABLE => DriveKind.Removable,
            DRIVE_FIXED => DriveKind.Fixed,
            DRIVE_REMOTE => DriveKind.Network,
            DRIVE_CDROM => DriveKind.Optical,
            DRIVE_RAMDISK => DriveKind.RamDisk,
            _ => DriveKind.Unknown,
        };
        return new VolumeInfo(root, serial, fileSystem, (long)free, (long)total) { Label = label, Kind = kind, FileSystemFlags = flags };
    }

    public static string? NearestExistingFolder(string fullPath)
    {
        for (string? p = fullPath; p is not null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p)) return p;
        return null;
    }

    /// <summary>
    /// The folder a subst drive letter stands for ("C:\Shoots" for W: after <c>subst W: C:\Shoots</c>, or a network
    /// path); null when the path is not on a subst drive letter (a real drive, a mapped network drive, or no drive).
    /// </summary>
    public static unsafe string? SubstTarget(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return null;
        char* buffer = stackalloc char[1024];
        if (QueryDosDevice(path[..2], buffer, 1024) == 0) return null;
        string device = new(buffer); // the first of the zero-separated names: the one in use
        string? target = device.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + device[8..]
            : device.StartsWith(@"\??\", StringComparison.Ordinal) && device.Length > 4 ? device[4..]
            : null;
        return target is { Length: 2 } && target[1] == ':' ? target + @"\" : target; // "subst S: D:\" stands for "D:", the root of D:
    }

    /// <summary>
    /// A full path with any subst drive letter replaced by the folder it stands for (also a subst of a subst); the path
    /// as it is when it is not on one. Junctions and links are not followed: the path stays on the same drive letter.
    /// </summary>
    public static string Unsubst(string fullPath)
    {
        for (int i = 0; i < 8 && SubstTarget(fullPath) is { } target; i++)
            fullPath = Path.Join(target, fullPath.Length > 3 ? fullPath[3..] : "");
        return fullPath;
    }
}
