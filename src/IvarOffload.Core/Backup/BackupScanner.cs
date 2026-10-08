using System.Collections.Frozen;
using System.Diagnostics;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>A file the backup leaves out, and why (a link, or a cloud placeholder that is not downloaded).</summary>
public sealed record LeftOutFile(string RelativePath, long Size, long LastWriteTime, string Why);

public sealed class BackupScan
{
    public required string SourceRoot { get; init; }
    /// <summary>Every file that is copied, in name order (depth first).</summary>
    public required List<SourceFile> Files { get; init; }
    /// <summary>Every folder below the source root (empty ones too) with its original times, keyed by relative path.</summary>
    public required Dictionary<string, FolderTimes> Folders { get; init; }
    /// <summary>Files that are not copied (links, online-only placeholders). They keep a backup from being complete.</summary>
    public required List<LeftOutFile> LeftOut { get; init; }
    /// <summary>Folders that are not copied: operating-system clutter (never counted against the backup) and links.</summary>
    public required List<SkippedFolder> SkippedFolders { get; init; }
    /// <summary>Folders that could not be read. Their files are not part of the backup, so it cannot be complete.</summary>
    public required List<string> Problems { get; init; }
    public TimeSpan Duration { get; init; }

    public long Bytes => Files.Sum(f => f.Size);
}

/// <summary>
/// Walks a card (or any folder) read-only for a backup. Unlike the sort scan it classifies nothing: every file is copied,
/// hidden and system files and camera metadata included. Only operating-system clutter is left out (<see cref="OsClutter"/>),
/// links are never followed, and cloud placeholders that are not downloaded are never read (that would download them).
/// No file content is read.
/// </summary>
public static class BackupScanner
{
    /// <summary>Folders Windows and macOS keep on every drive; they are not part of what a camera recorded.</summary>
    public static readonly FrozenDictionary<string, string> OsClutter = new Dictionary<string, string>
    {
        ["System Volume Information"] = "Windows system folder",
        ["$RECYCLE.BIN"] = "Recycle Bin",
        [".Trashes"] = "macOS trash",
        [".Spotlight-V100"] = "macOS index",
        [".fseventsd"] = "macOS events",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private const FileAttributes OnlineOnlyAttributes = FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    public static BackupScan Scan(string sourceRoot, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (Path.GetPathRoot(root) is { } driveRoot && root.Length < driveRoot.Length) root = driveRoot; // "F:" -> "F:\"
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The folder {root} does not exist.");

        var files = new List<SourceFile>();
        var folders = new Dictionary<string, FolderTimes>(StringComparer.OrdinalIgnoreCase);
        var leftOut = new List<LeftOutFile>();
        var skipped = new List<SkippedFolder>();
        var problems = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        long lastReport = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string rel = pending.Pop();
            string full = rel.Length == 0 ? root : Path.Join(root, rel);
            var subfolders = new List<string>();
            var here = new List<SourceFile>();
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Options))
                {
                    string entryRel = rel.Length == 0 ? entry.Name : rel + "\\" + entry.Name;
                    if (entry is DirectoryInfo dir)
                    {
                        if (OsClutter.TryGetValue(dir.Name, out string? why))
                            skipped.Add(new SkippedFolder(entryRel, why));
                        else if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            skipped.Add(new SkippedFolder(entryRel, "link or junction - not followed, not copied"));
                        else
                        {
                            folders[entryRel] = new FolderTimes(dir.CreationTimeUtc.ToFileTimeUtc(), dir.LastWriteTimeUtc.ToFileTimeUtc());
                            subfolders.Add(entryRel);
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        long mtime = file.LastWriteTimeUtc.ToFileTimeUtc();
                        if ((file.Attributes & OnlineOnlyAttributes) != 0)
                            leftOut.Add(new LeftOutFile(entryRel, file.Length, mtime,
                                "online-only cloud placeholder (not downloaded) - not copied. To copy a placeholder, make it available offline. Then back up again."));
                        else if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            leftOut.Add(new LeftOutFile(entryRel, file.Length, mtime, "link / shortcut - not followed, not copied"));
                        else
                            here.Add(new SourceFile
                            {
                                RelativePath = entryRel,
                                Size = file.Length,
                                CreationTime = file.CreationTimeUtc.ToFileTimeUtc(),
                                LastWriteTime = mtime,
                                Attributes = file.Attributes,
                            });
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                problems.Add($"{(rel.Length == 0 ? "(the card's top folder)" : rel)}: {e.Message}");
            }
            files.AddRange(here.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase));

            // Depth-first in name order: the files of a folder, then its subfolders.
            subfolders.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = subfolders.Count - 1; i >= 0; i--) pending.Push(subfolders[i]);

            if (progress is not null && sw.ElapsedMilliseconds - lastReport > 100)
            {
                lastReport = sw.ElapsedMilliseconds;
                progress.Report(new ScanProgress(files.Count, folders.Count, rel));
            }
        }
        progress?.Report(new ScanProgress(files.Count, folders.Count, ""));
        return new BackupScan
        {
            SourceRoot = root,
            Files = files,
            Folders = folders,
            LeftOut = leftOut,
            SkippedFolders = skipped,
            Problems = problems,
            Duration = sw.Elapsed,
        };
    }
}
