using System.Diagnostics;

namespace IvarOffload.Core.Sorting;

public sealed class ScanResult
{
    public required string SourceRoot { get; init; }
    public required List<SourceFile> Files { get; init; }
    /// <summary>Original times of every scanned folder, keyed by relative path.</summary>
    public required Dictionary<string, FolderTimes> Folders { get; init; }
    public required List<SkippedFolder> SkippedFolders { get; init; }
    /// <summary>Folders that could not be read. Files inside them are not part of the plan.</summary>
    public required List<string> Problems { get; init; }
    /// <summary>Folders classified as one unit: card structures, image-sequence clips, mapping missions, projects.</summary>
    public IReadOnlyList<MediaUnit> Units { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public readonly record struct ScanProgress(int Files, int Folders, string CurrentFolder);

/// <summary>
/// Walks the source folder read-only. Never follows links or junctions, never enters excluded folders or application
/// libraries (Apple Photos, Final Cut, Lightroom previews, ...). The only file content it reads is the header of one
/// frame per possible CinemaDNG clip and the metadata of a short clip named like a photo (is it a Live Photo?), without
/// changing their last-access times.
/// </summary>
public static class Scanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <param name="target">The target folder. When it is inside the source it is left out of the scan.</param>
    public static ScanResult Scan(string sourceRoot, IProgress<ScanProgress>? progress = null, CancellationToken ct = default, string? target = null)
    {
        var sw = Stopwatch.StartNew();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (!System.IO.Directory.Exists(root)) throw new DirectoryNotFoundException($"Source folder not found: {root}");
        string? targetRel = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(target))
                targetRel = Planner.TargetInsideSource(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim().Trim('"'))));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable path: the Planner reports it.
        }

        var files = new List<SourceFile>();
        var folders = new Dictionary<string, FolderTimes>(StringComparer.OrdinalIgnoreCase);
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
            int firstFile = files.Count, firstSkipped = skipped.Count;
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Options))
                {
                    string entryRel = rel.Length == 0 ? entry.Name : rel + "\\" + entry.Name;
                    if (entry is DirectoryInfo dir)
                    {
                        if (targetRel is not null && string.Equals(entryRel, targetRel, StringComparison.OrdinalIgnoreCase))
                            skipped.Add(new SkippedFolder(entryRel, "the target folder"));
                        else if (MediaRules.ExcludedFolders.TryGetValue(dir.Name, out string? why))
                            skipped.Add(new SkippedFolder(entryRel, why));
                        else if (MediaRules.ApplicationLibraryKind(dir.Name) is { } library)
                            skipped.Add(new SkippedFolder(entryRel, "application library - left untouched") { LibraryKind = library });
                        else if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            skipped.Add(new SkippedFolder(entryRel, "link or junction - not followed"));
                        else
                        {
                            folders[entryRel] = new FolderTimes(dir.CreationTimeUtc.ToFileTimeUtc(), dir.LastWriteTimeUtc.ToFileTimeUtc());
                            subfolders.Add(entryRel);
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        files.Add(new SourceFile
                        {
                            RelativePath = entryRel,
                            Size = file.Length,
                            CreationTime = file.CreationTimeUtc.ToFileTimeUtc(),
                            LastWriteTime = file.LastWriteTimeUtc.ToFileTimeUtc(),
                            Attributes = file.Attributes,
                        });
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                problems.Add($"{(rel.Length == 0 ? "(source folder)" : rel)}: {e.Message}");
            }

            // A Luminar catalog is known by the file inside its folder, so the folder is left out once it has been listed.
            // The source folder itself is refused by the Planner instead.
            if (rel.Length > 0 && MediaRules.IsLuminarCatalogFolder(Path.GetFileName(rel), files.Skip(firstFile).Select(f => f.Name)))
            {
                files.RemoveRange(firstFile, files.Count - firstFile);
                folders.Remove(rel);
                foreach (string sub in subfolders) folders.Remove(sub);
                subfolders.Clear();
                skipped.RemoveRange(firstSkipped, skipped.Count - firstSkipped);
                skipped.Add(new SkippedFolder(rel, "application library - left untouched") { LibraryKind = MediaRules.LuminarCatalogKind });
            }

            // Depth-first in name order keeps the plan in a natural, stable order.
            subfolders.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = subfolders.Count - 1; i >= 0; i--) pending.Push(subfolders[i]);

            if (progress is not null && sw.ElapsedMilliseconds - lastReport > 100)
            {
                lastReport = sw.ElapsedMilliseconds;
                progress.Report(new ScanProgress(files.Count, folders.Count, rel));
            }
        }

        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, folders.Keys, Path.GetFileName(root),
            f => CinemaDng.IsMovieFrame(Path.Join(root, f.RelativePath)),
            f => LivePhotoClip.IsLivePhotoClip(Path.Join(root, f.RelativePath)));
        progress?.Report(new ScanProgress(files.Count, folders.Count, ""));
        return new ScanResult
        {
            SourceRoot = root,
            Files = files,
            Folders = folders,
            SkippedFolders = skipped,
            Problems = problems,
            Units = units,
            Duration = sw.Elapsed,
        };
    }
}
