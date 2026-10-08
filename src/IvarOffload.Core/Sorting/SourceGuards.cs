using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IvarOffload.Core.IO;
using Microsoft.Win32.SafeHandles;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Checks on the chosen source folder: a memory card or camera drive is flagged (Sort is meant for card backups, but a
/// camera drive can be sorted on purpose), a system or profile folder is refused, and so is a folder files cannot be
/// removed from. The decisions are pure functions of the facts passed in.
/// </summary>
public static partial class SourceGuards
{
    /// <summary>The one wording used wherever a memory-card-like source is flagged. {0}: the drive, e.g. "F: SONY_A".</summary>
    public const string MemoryCardWarningFormat =
        "This looks like a memory card or camera drive ({0}). Sort moves files around on it. " +
        "Back the card up to another drive first (the Backup tab) and sort the backup.";

    public const string CannotRemoveError = "Files can't be moved out of this folder (read-only drive or no permission).";

    /// <summary>
    /// Folders cameras create at the root of a memory card. DCIM, PRIVATE and AVCHD only count together with the camera
    /// folders they hold (see <see cref="CardSignsAtRoot"/>), so a personal "Private" folder is never taken for a card.
    /// </summary>
    public static readonly FrozenSet<string> CardRootFolders = new[]
    {
        "DCIM", "PRIVATE", "AVCHD", "BPAV", "XDROOT", "CLIPS001", "MP_ROOT",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string MemoryCardWarning(string drive) => string.Format(MemoryCardWarningFormat, drive);

    /// <summary>"F: SONY_A", or "F:" when the drive has no label.</summary>
    public static string DriveName(VolumeInfo volume) => volume.Label.Length > 0 ? $"{volume.DisplayName} {volume.Label}" : volume.DisplayName;

    /// <summary>
    /// The memory-card warning for <paramref name="folder"/>, or null when it does not look like a memory card or camera
    /// drive. For the GUI, to ask before sorting it. Never throws.
    /// </summary>
    public static string? MemoryCardNote(string folder)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            VolumeInfo volume = VolumeInfo.Of(full);
            return CheckMemoryCard(full, volume, CardSignsAtRoot(volume.Root, DriveFacts.SubfolderNames, DriveFacts.FileNames))?.Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// What at the root of a volume shows that it is a camera card, in name order: card folders ("DCIM" holding a camera
    /// folder, "PRIVATE" holding M4ROOT or AVCHD, a P2 or Canon XF "CONTENTS", "A001_0101XY.RDM", an ARRI reel folder
    /// such as "A016R1K4") and the first video file recorded straight to the root (Blackmagic cameras and recorders do
    /// that). Empty when there is none.
    /// </summary>
    public static IReadOnlyList<string> CardSignsAtRoot(string volumeRoot,
        Func<string, IReadOnlyCollection<string>> subfolders, Func<string, IReadOnlyCollection<string>> files)
    {
        var signs = subfolders(volumeRoot).Where(n => IsCardFolder(n, Path.Join(volumeRoot, n), subfolders, files))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var clips = files(volumeRoot).Where(n => MediaRules.VideoExtensions.Contains(Path.GetExtension(n)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        signs.AddRange(clips.Where(IsBlackmagicClip).Take(1));
        signs.AddRange(clips.Where(n => !IsBlackmagicClip(n)).Take(1));
        return signs;
    }

    private static bool IsCardFolder(string name, string path, Func<string, IReadOnlyCollection<string>> subfolders,
        Func<string, IReadOnlyCollection<string>> files) => name.ToUpperInvariant() switch
    {
        "DCIM" => subfolders(path).Count > 0, // 100MSDCF, 100CANON, DJI_001, Camera01, ...
        "PRIVATE" => subfolders(path).Any(MediaRules.VideoStructureFolders.ContainsKey),
        "AVCHD" => subfolders(path).Any(c => c.Equals("BDMV", StringComparison.OrdinalIgnoreCase)),
        "CONTENTS" => CardStructures.Describe(name, subfolders(path), _ => false) is not null,
        _ when CardRootFolders.Contains(name) => true, // BPAV, XDROOT, CLIPS001, MP_ROOT: names only cameras use
        _ when name.EndsWith(".RDM", StringComparison.OrdinalIgnoreCase) => true,
        // ARRI: a reel folder "A016R1K4" at the root holds the clips "A016C001_120126_R1K4.mxf" (or a folder per clip).
        _ => ArriReel().IsMatch(name) && subfolders(path).Concat(files(path))
            .Any(n => n.StartsWith(name[..4] + "C", StringComparison.OrdinalIgnoreCase)),
    };

    private static bool IsBlackmagicClip(string name) => Path.GetExtension(name).Equals(".braw", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A source on a memory card is flagged (a Warning; the GUI asks before sorting it). The volume root shows a card (<see cref="CardSignsAtRoot"/>) and the volume
    /// is removable, or the source is the volume root, or the source is inside one of the card folders. Video files at
    /// the root only count on removable media or when the source is the root (Blackmagic .braw), or both (other videos).
    /// Cameras only write FAT, exFAT or UDF, so an NTFS or ReFS drive is never a card.
    /// </summary>
    internal static PlanMessage? CheckMemoryCard(string source, VolumeInfo volume, IReadOnlyList<string> signs)
    {
        source = VolumeInfo.Unsubst(source); // a subst drive letter (W: for F:\) is judged by the folder it stands for, as the volume is
        if (signs.Count == 0 || volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            || volume.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            return null;
        bool removable = volume.IsRemovable;
        bool isRoot = Same(source, volume.Root);
        bool card = signs.Any(sign =>
            !MediaRules.VideoExtensions.Contains(Path.GetExtension(sign))
                ? removable || isRoot || Planner.IsInside(source, Path.Join(volume.Root, sign))
                : IsBlackmagicClip(sign) ? removable || isRoot : removable && isRoot);
        return card ? new PlanMessage(MessageLevel.Warning, MemoryCardWarning(DriveName(volume))) : null;
    }

    [GeneratedRegex(@"^[A-Z]\d{3}R[0-9A-Z]{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex ArriReel();

    /// <summary>Refuses system and profile folders; warns about drive roots and personal folders like Pictures.</summary>
    internal static PlanMessage? CheckBroad(string source, VolumeInfo? volume, SystemFolders folders, MoveMode mode)
    {
        // A subst drive letter is judged by the folder it stands for too: W:\ is the Windows drive after "subst W: C:\".
        string real = VolumeInfo.Unsubst(source);
        if (!Same(real, source) && CheckBroad(real, volume, folders, mode) is { } forReal)
            return forReal;
        string? tooBroad =
            Same(source, folders.SystemDriveRoot) ? $"the whole Windows drive ({source.TrimEnd('\\')})"
            : Same(source, folders.UserProfile) ? "your user folder"
            : In(folders.UserProfile, source) ? "a folder that holds the user folders"
            : folders.System.Any(s => In(source, s)) ? "a Windows or program folder"
            : In(source, folders.AppData) && !In(source, folders.Temp) ? "an app data folder (AppData)"
            : null;
        if (tooBroad is not null)
            return new PlanMessage(MessageLevel.Error,
                $"This folder is too broad to sort: {tooBroad}. Choose the folder that holds your card backups.");

        string? broad =
            volume is not null && Same(source, volume.Root) ? $"the whole drive {volume.DisplayNameWithLabel}"
            : folders.Personal.FirstOrDefault(p => Same(source, p.Path)) is { Path.Length: > 0 } personal ? $"your {personal.Name} folder"
            : null;
        return broad is null ? null : new PlanMessage(MessageLevel.Warning,
            $"You picked {broad}. Sorting it moves every {Planner.Word(mode)[..^1]} in it, not just your card backups - choose the folder with the card backups.");
    }

    /// <summary>
    /// A source inside an application library (Apple Photos, Final Cut, Lightroom previews, ...) is refused: moving media
    /// out of it breaks the library. A source inside an editing or processing project (a folder above it holds the
    /// project file, or is a processing folder such as OpenDroneMap's) gets a warning, because the project may use these files.
    /// </summary>
    internal static PlanMessage? CheckLibraryOrProject(string source, Func<string, IReadOnlyCollection<string>> subfolders,
        Func<string, IReadOnlyCollection<string>> files)
    {
        for (string? p = source; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Path.GetFileName(p) is { Length: > 0 } name
                && (MediaRules.ApplicationLibraryKind(name) ?? (MediaRules.IsLuminarCatalogFolder(name, files(p)) ? MediaRules.LuminarCatalogKind : null)) is { } kind)
                return new PlanMessage(MessageLevel.Error,
                    $"This folder is inside an application library: {kind} ({p}). Moving files out of it would break the library - choose the folder with the card backups instead.");

        // A project file in the source folder itself is handled by the classifier: everything then stays.
        for (string? p = Path.GetDirectoryName(source); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            string? marker = files(p).Concat(subfolders(p)) // a project can be a folder (a package) on Windows
                .Where(n => MediaRules.ProjectMarkers.ContainsKey(Path.GetExtension(n)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            string folder = p;
            (string Description, string Evidence)? project = marker is not null
                ? (MediaRules.ProjectMarkers[Path.GetExtension(marker)], marker)
                : ProcessingProjects.Describe(relative => subfolders(Path.Join(folder, relative)),
                    relative => files(Path.Join(folder, Path.GetDirectoryName(relative))).Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase));
            if (project is { } found)
                return new PlanMessage(MessageLevel.Warning,
                    $"This folder is inside an editing/processing project: {found.Description} ({found.Evidence} in {p}). "
                    + "If the project uses these files, it will show them as missing after the move.");
        }
        return null;
    }

    private static bool Same(string a, string b) =>
        a.Length > 0 && b.Length > 0 && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it; false when either is unknown.</summary>
    private static bool In(string path, string folder) => path.Length > 0 && folder.Length > 0 && Planner.IsInside(path, folder);
}

/// <summary>System and personal folders of the current user, normalized. Empty strings for folders that do not exist.</summary>
internal sealed record SystemFolders(
    string SystemDriveRoot,
    string UserProfile,
    string AppData,
    string Temp,
    IReadOnlyList<string> System,
    IReadOnlyList<(string Name, string Path)> Personal)
{
    public static SystemFolders Current { get; } = Load();

    private static SystemFolders Load()
    {
        string profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return new SystemFolders(
            Normalize(Path.GetPathRoot(Environment.SystemDirectory) ?? ""),
            profile,
            profile.Length > 0 ? Path.Join(profile, "AppData") : "",
            Normalize(Path.GetTempPath()),
            new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            }.Select(f => Normalize(Environment.GetFolderPath(f))).Where(p => p.Length > 0).ToList(),
            new (string, string)[]
            {
                ("Pictures", Normalize(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))),
                ("Videos", Normalize(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))),
                ("Desktop", Normalize(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))),
                ("Documents", Normalize(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))),
                ("Downloads", Normalize(DriveFacts.DownloadsFolder())),
            }.Where(p => p.Item2.Length > 0).ToList());
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }
}

/// <summary>Read-only facts about drives and files that the preview needs. None of these change anything on disk.</summary>
internal static class DriveFacts
{
    /// <summary>Allocation unit of the drive, in bytes; 0 when unknown.</summary>
    public static long ClusterSize(string root) =>
        GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _) ? (long)sectorsPerCluster * bytesPerSector : 0;

    /// <summary>
    /// Opens the file with delete access and closes it again without deleting anything. True: it could be removed;
    /// false: read-only drive or no permission; null: could not tell (for example, another program has it open).
    /// </summary>
    public static bool? ProbeDelete(string path)
    {
        using SafeFileHandle h = CreateFile(SafeFile.ToExtendedPath(path), DELETE | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (!h.IsInvalid) return true;
        return Marshal.GetLastPInvokeError() is ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT ? false : null;
    }

    public static IReadOnlyCollection<string> SubfolderNames(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateDirectories().Select(d => d.Name).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyCollection<string> FileNames(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Select(f => f.Name).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, 0, out nint path) != 0) return "";
            try
            {
                return Marshal.PtrToStringUni(path) ?? "";
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "";
        }
    }
}

/// <summary>What the Planner looks up about drives, folders and earlier jobs. Tests replace parts of it.</summary>
internal sealed class PlanEnvironment
{
    public static PlanEnvironment Default { get; } = new();

    public Func<string, VolumeInfo> VolumeOf { get; init; } = VolumeInfo.Of;
    public Func<string, long> ClusterSizeOf { get; init; } = DriveFacts.ClusterSize;
    public Func<string, bool?> CanDelete { get; init; } = DriveFacts.ProbeDelete;
    public Func<string, IReadOnlyCollection<string>> SubfolderNames { get; init; } = DriveFacts.SubfolderNames;
    public Func<string, IReadOnlyCollection<string>> FileNames { get; init; } = DriveFacts.FileNames;
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, string?> SyncOf { get; init; } = SyncDetector.Describe;
    public Func<string, IEnumerable<string>> JournalsIn { get; init; } = Jobs.JobPaths.FindJournals;
    /// <summary>Where a folder really is (links and drive-letter aliases resolved); see <see cref="SafeFile.RealPath"/>.</summary>
    public Func<string, string> RealPathOf { get; init; } = SafeFile.RealPath;
    public SystemFolders? Folders { get; init; }

    /// <summary>
    /// Test use only (tools/Test-Gui.ps1): IVAROFFLOAD_TEST_CARD = a drive name such as "F: TEST_CARD" flags every
    /// source as a memory card with that name, so the question asked before sorting a card can be tested without one.
    /// It only adds the warning; the plan is otherwise unchanged.
    /// </summary>
    public string? TestCard { get; init; } = Environment.GetEnvironmentVariable(TestCardVariable) is { Length: > 0 } card ? card : null;

    public const string TestCardVariable = "IVAROFFLOAD_TEST_CARD";

    public SystemFolders SystemFolders => Folders ?? SystemFolders.Current;
}
