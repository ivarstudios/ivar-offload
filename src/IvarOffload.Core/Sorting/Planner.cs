using System.Globalization;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;

namespace IvarOffload.Core.Sorting;

public enum TransferMethod
{
    /// <summary>Same volume: the file is renamed into place; its data is never read back or rewritten.</summary>
    Rename,
    /// <summary>Different volumes: copy, verify from disk, then delete the original.</summary>
    Copy,
}

public enum MessageLevel { Info, Warning, Error }

public sealed record PlanMessage(MessageLevel Level, string Text);

public sealed record TypeSummary(string Extension, string Classification, bool Moves, int Files, long Bytes);

/// <param name="Folder">Relative folder, or "(source folder)".</param>
/// <param name="Split">
/// Files move out of this folder while other files that may belong to them stay behind (not system files, and not the
/// other side's photos or videos). See the "folders will be split" warning.
/// </param>
public sealed record FolderSummary(string Folder, int MovingFiles, long MovingBytes, int StayingFiles, long StayingBytes, bool Split);

/// <summary>Everything the preview shows, and exactly what a confirmed job will do.</summary>
public sealed class MovePlan
{
    private (int Files, long Bytes)? _transfer;

    public required ScanResult Scan { get; init; }
    public required string SourceRoot { get; init; }
    public required string TargetRoot { get; init; }
    public required MoveMode Mode { get; init; }
    public TransferMethod Method { get; init; }
    /// <summary>Hash every file (always true for copies, where it is required to prove the copy).</summary>
    public bool VerifyChecksums { get; init; }
    public VolumeInfo? SourceVolume { get; init; }
    public VolumeInfo? TargetVolume { get; init; }
    /// <summary>What the job will work through, in order. Includes <see cref="IdenticalConflicts"/>, which it skips.</summary>
    public required IReadOnlyList<SourceFile> ToMove { get; init; }
    /// <summary>Everything else, including <see cref="HeldBack"/>.</summary>
    public required IReadOnlyList<SourceFile> Staying { get; init; }
    /// <summary>
    /// Files that would move but already exist in the target. They will be skipped, never overwritten. Always a part of
    /// <see cref="ToMove"/>: the same files as <see cref="IdenticalConflicts"/>. A DIFFERENT file with the same name keeps
    /// its file out of the job altogether (<see cref="DifferentConflicts"/>).
    /// </summary>
    public required IReadOnlyList<SourceFile> Conflicts { get; init; }
    /// <summary>
    /// In <see cref="ToMove"/>, but a file with the same name, size and date is already in the target: the job compares
    /// them and skips the file (never overwrites it).
    /// </summary>
    public IReadOnlyList<SourceFile> IdenticalConflicts { get; init; } = [];
    /// <summary>
    /// A DIFFERENT file with the same name is already in the target (probably another card with restarted numbering).
    /// They are in <see cref="Staying"/>, with <see cref="FileNote.DifferentInTarget"/>.
    /// </summary>
    public IReadOnlyList<SourceFile> DifferentConflicts { get; init; } = [];
    /// <summary>
    /// Files of the moving side kept in the source because their group (clip, card structure, mission) contains a
    /// <see cref="DifferentConflicts"/> file: the clashing files themselves plus their companions (<see cref="FileNote.HeldWithGroup"/>).
    /// </summary>
    public IReadOnlyList<SourceFile> HeldBack { get; init; } = [];
    /// <summary>
    /// Photos or videos of the moving side that stay in the source anyway, so the source is not done when they are left:
    /// online-only placeholders that are not downloaded (<see cref="FileNote.OnlineOnly"/>), links (<see cref="FileNote.Link"/>)
    /// and media inside editing or processing projects (<see cref="FileNote.InProject"/>). They are in <see cref="Staying"/>;
    /// <see cref="SourceFile.NaturalSide"/> is the moving side.
    /// </summary>
    public IReadOnlyList<SourceFile> MovingSideStaying { get; init; } = [];
    /// <summary>
    /// When the source was picked inside a video card structure: the folder that holds the whole card ("Use this folder").
    /// Null when that folder would be refused itself (for example the Windows drive).
    /// </summary>
    public string? SuggestedSource { get; init; }
    /// <summary>The drive ("F: SONY_A") when the source is on a memory card; the plan then has the memory-card error.</summary>
    public string? MemoryCard { get; init; }
    public required IReadOnlyList<PlanMessage> Messages { get; init; }
    public required IReadOnlyList<TypeSummary> ByType { get; init; }
    public required IReadOnlyList<FolderSummary> ByFolder { get; init; }

    /// <summary>Card structures, image-sequence clips, mapping missions and projects found in the source.</summary>
    public IReadOnlyList<MediaUnit> Units => Scan.Units;
    public long BytesToMove => ToMove.Sum(f => f.Size);
    public long BytesStaying => Staying.Sum(f => f.Size);
    /// <summary>Files the job will actually move: <see cref="ToMove"/> without the identical copies it skips.</summary>
    public int FilesToTransfer => Transfer.Files;
    public long BytesToTransfer => Transfer.Bytes;
    public bool HasErrors => Messages.Any(m => m.Level == MessageLevel.Error);
    public bool CanRun => !HasErrors && FilesToTransfer > 0;

    private (int Files, long Bytes) Transfer => _transfer ??= Count();

    private (int, long) Count()
    {
        var skipped = new HashSet<SourceFile>(Conflicts);
        var moving = ToMove.Where(f => !skipped.Contains(f)).ToList();
        return (moving.Count, moving.Sum(f => f.Size));
    }
}

public static partial class Planner
{
    /// <summary>Headroom kept free on the target drive when copying.</summary>
    public const long FreeSpaceMargin = 256L * 1024 * 1024;

    /// <summary>FAT and exFAT store modification times coarsely (FAT: 2 seconds), so an identical copy may differ by that much.</summary>
    private const long FatTimeTolerance = 2 * TimeSpan.TicksPerSecond;

    public static MovePlan Build(ScanResult scan, string targetRoot, MoveMode mode, bool verifyChecksums) =>
        Build(scan, targetRoot, mode, verifyChecksums, PlanEnvironment.Default);

    internal static MovePlan Build(ScanResult scan, string targetRoot, MoveMode mode, bool verifyChecksums, PlanEnvironment env)
    {
        var messages = new List<PlanMessage>();
        string source = scan.SourceRoot;
        MediaSide side = MediaRules.SideFor(mode);
        foreach (SourceFile f in scan.Files) f.RestoreClassification();
        var toMove = scan.Files.Where(f => f.Side == side).ToList();

        VolumeInfo? sourceVolume = null, targetVolume = null;
        try
        {
            sourceVolume = env.VolumeOf(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            messages.Add(new PlanMessage(MessageLevel.Error, $"Cannot read the source drive: {e.Message}"));
        }
        string? suggestedSource = CheckSource(source, sourceVolume, mode, env, messages, out string? memoryCard);
        bool sourceRefused = messages.Any(m => m.Level == MessageLevel.Error);

        var identical = new List<SourceFile>();
        var different = new List<SourceFile>();
        var held = new List<SourceFile>();
        TransferMethod method = TransferMethod.Rename;

        string? targetError = ValidateTarget(source, targetRoot, out string target, env.RealPathOf);
        if (targetError is null && TargetInsideSource(source, target, env.RealPathOf) is { } inside)
        {
            if (scan.Files.Any(f => IsInside(f.RelativePath, inside)))
                targetError = "The target folder is inside the source folder, but it was scanned as part of the source. Scan again.";
            else
                messages.Add(new PlanMessage(MessageLevel.Info,
                    $"The target folder is inside the source folder ({inside}). It is left out of the scan, so nothing in it is sorted again."));
        }
        if (targetError is not null)
            messages.Add(new PlanMessage(MessageLevel.Error, targetError));
        else
        {
            try
            {
                targetVolume = env.VolumeOf(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                messages.Add(new PlanMessage(MessageLevel.Error, $"Cannot read the target drive: {e.Message}"));
            }

            FindConflicts(toMove, target, targetVolume, identical, different);
            held = HoldBackGroups(toMove, different);
            identical.RemoveAll(new HashSet<SourceFile>(held).Contains);

            if (sourceVolume is not null && targetVolume is not null)
            {
                method = sourceVolume.IsSameVolume(targetVolume) ? TransferMethod.Rename : TransferMethod.Copy;
                if (method == TransferMethod.Rename)
                    messages.Add(new PlanMessage(MessageLevel.Info,
                        $"Same drive ({sourceVolume.DisplayName}, {sourceVolume.FileSystem}): files are moved by renaming them. No file data is copied or rewritten."
                        + (verifyChecksums ? " A SHA-256 checksum of every file is recorded in the log before it moves." : "")));
                else
                    messages.Add(new PlanMessage(MessageLevel.Info,
                        $"Different drives ({sourceVolume.DisplayName} to {targetVolume.DisplayName}): every file is copied, read back from disk and compared by SHA-256, and only then is the original deleted."));
                var identicalSet = new HashSet<SourceFile>(identical);
                messages.AddRange(CheckTarget(targetVolume, env.ClusterSizeOf(targetVolume.Root),
                    toMove.Where(f => !identicalSet.Contains(f)).ToList(), copying: method == TransferMethod.Copy));
            }
            AddEarlierJobs(target, source, mode, env, messages);
        }

        if (!sourceRefused && sourceVolume is not null && toMove.Count > 0)
            CheckCanRemove(source, sourceVolume, toMove, identical, env, messages);

        var movingSideStaying = scan.Files.Where(f => f.Side == MediaSide.Neutral && f.NaturalSide == side).ToList();
        if (toMove.Count == 0 && held.Count == 0)
            messages.Add(movingSideStaying.Count == 0
                ? new PlanMessage(MessageLevel.Info, $"No {Word(mode)} found in the source folder - nothing to move.")
                : new PlanMessage(MessageLevel.Warning, $"No {Word(mode)} can be moved - nothing to move. "
                    + $"Still in the source: {Count(movingSideStaying.Count, mode)} ({StayReasons(movingSideStaying)})."));
        if (identical.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{identical.Count:N0} {(identical.Count == 1 ? "file is" : "files are")} already in the target (same name, size and date) and will be skipped."));
        if (different.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{different.Count:N0} file(s) have the same name as DIFFERENT files already in the target (probably another card with restarted numbering). "
                + $"They stay in the source together with their companions ({held.Count:N0} files in all). Sort each card into its own folder."));

        string? sourceSync = env.SyncOf(source);
        if (sourceSync is not null)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"The source folder is synchronized by {sourceSync}. Moving files out of it looks like a deletion to the sync tool, so they will also disappear from the other devices that share this folder."));
        if (targetError is null)
        {
            string? targetSync = env.SyncOf(target);
            if (targetSync is not null && targetSync != sourceSync)
                messages.Add(new PlanMessage(MessageLevel.Info, $"The target folder is synchronized by {targetSync}; moved files will be synced from there."));
        }

        var moving = new HashSet<SourceFile>(toMove);
        Dictionary<string, List<SourceFile>> split = FindSplitFolders(scan.Files, moving);
        AddSummaries(messages, scan, mode, moving, split);

        return new MovePlan
        {
            Scan = scan,
            SourceRoot = source,
            TargetRoot = target,
            Mode = mode,
            Method = method,
            VerifyChecksums = verifyChecksums || method == TransferMethod.Copy,
            SourceVolume = sourceVolume,
            TargetVolume = targetVolume,
            ToMove = toMove,
            Staying = scan.Files.Where(f => !moving.Contains(f)).ToList(),
            Conflicts = identical,
            IdenticalConflicts = identical,
            DifferentConflicts = different,
            HeldBack = held,
            MovingSideStaying = movingSideStaying,
            SuggestedSource = suggestedSource,
            MemoryCard = memoryCard,
            Messages = messages.OrderByDescending(m => m.Level).ToList(),
            ByType = SummarizeTypes(scan.Files, moving),
            ByFolder = SummarizeFolders(scan.Files, moving, split),
        };
    }

    /// <summary>Returns an error message, or null when the target is usable. Outputs the normalized target path.</summary>
    public static string? ValidateTarget(string source, string targetRoot, out string target) =>
        ValidateTarget(source, targetRoot, out target, SafeFile.RealPath);

    internal static string? ValidateTarget(string source, string targetRoot, out string target, Func<string, string> realPath)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(targetRoot)) return "Choose a target folder.";
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"The target folder path is not valid: {e.Message}";
        }
        if (Path.GetPathRoot(target) is not { Length: > 0 } root || !Directory.Exists(root))
            return $"The drive for the target folder ({Path.GetPathRoot(target)}) does not exist.";
        if (File.Exists(target)) return "The target path is a file, not a folder.";
        // Also compared where the paths really lead: a junction, a subst or mapped drive letter can name the same folder.
        string realSource = realPath(source), realTarget = realPath(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || string.Equals(realSource, realTarget, StringComparison.OrdinalIgnoreCase))
            return "The target folder must be different from the source folder.";
        // A target inside the source is fine (e.g. a breakout folder on a camera drive): the scan leaves it out.
        if (IsInside(source, target) || IsInside(realSource, realTarget)) return "The source folder cannot be inside the target folder.";
        return null;
    }

    /// <summary>
    /// For a target inside the source: its path relative to the source ("Video\Breakout"), else null. Decided where the
    /// paths really lead, so a target named through a junction, a subst or mapped drive letter (Z:\Card\Video for
    /// \\NAS\share\Card\Video) or a short name is still recognized as the folder inside the source.
    /// </summary>
    public static string? TargetInsideSource(string source, string target) => TargetInsideSource(source, target, SafeFile.RealPath);

    internal static string? TargetInsideSource(string source, string target, Func<string, string> realPath)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(source)) return null;
        string realSource = realPath(source), realTarget = realPath(target);
        return IsInside(realTarget, realSource) && !string.Equals(realSource.TrimEnd('\\'), realTarget.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(realSource, realTarget)
            : null;
    }

    public static bool IsInside(string path, string folder) =>
        (path.TrimEnd('\\') + "\\").StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static string Word(MoveMode mode) => mode == MoveMode.Videos ? "videos" : "photos";

    /// <summary>"1 video", "3 photos".</summary>
    private static string Count(int count, MoveMode mode) => count == 1 ? $"1 {Word(mode)[..^1]}" : $"{count:N0} {Word(mode)}";

    /// <summary>"2 online-only (not downloaded), 1 inside an editing/processing project".</summary>
    private static string StayReasons(List<SourceFile> files) => string.Join(", ", files.GroupBy(f => f.Note).OrderByDescending(g => g.Count())
        .Select(g => $"{g.Count():N0} " + g.Key switch
        {
            FileNote.OnlineOnly => "online-only (not downloaded)",
            FileNote.Link => (g.Count() == 1 ? "is a link" : "are links"),
            FileNote.InProject => "inside an editing/processing project",
            _ => FileNotes.Describe(g.Key).ToLowerInvariant(),
        }));

    /// <summary>
    /// Memory card, too-broad folder, application library or project, or a folder inside a video card structure.
    /// Returns the folder to suggest instead (only one that would not be refused itself).
    /// </summary>
    private static string? CheckSource(string source, VolumeInfo? volume, MoveMode mode, PlanEnvironment env, List<PlanMessage> messages,
        out string? memoryCard)
    {
        memoryCard = null;
        IReadOnlyList<string> cardSigns = volume is null ? [] : SourceGuards.CardSignsAtRoot(volume.Root, env.SubfolderNames, env.FileNames);
        if (volume is not null && SourceGuards.CheckMemoryCard(source, volume, cardSigns) is { } card)
        {
            // Flagged, not refused: a camera drive (e.g. an SSD a camera recorded to) can be sorted on purpose.
            messages.Add(card);
            memoryCard = SourceGuards.DriveName(volume);
        }
        else if (env.TestCard is { } testCard)
        {
            messages.Add(new PlanMessage(MessageLevel.Warning, SourceGuards.MemoryCardWarning(testCard)));
            memoryCard = testCard;
        }
        if (SourceGuards.CheckBroad(source, volume, env.SystemFolders, mode) is { } broad)
            messages.Add(broad);
        if (SourceGuards.CheckLibraryOrProject(source, env.SubfolderNames, env.FileNames) is { } enclosing)
            messages.Add(enclosing);

        if (CardStructures.FindEnclosing(source, env.SubfolderNames, env.FileExists) is not { } structure) return null;
        string? suggested = structure.SuggestedFolder;
        if (SourceGuards.CheckBroad(suggested, volume, env.SystemFolders, mode) is { Level: MessageLevel.Error })
            suggested = null;
        messages.Add(new PlanMessage(MessageLevel.Error,
            $"You picked a folder inside a video card structure (...\\{Path.GetFileName(structure.StructureRoot)}). Choose the folder that holds the whole card instead"
            + (suggested is null ? " (copy the card into a folder of its own first)." : $": {suggested}.")));
        return suggested;
    }

    /// <summary>A read-only drive or a folder without delete permission is found now, not after every file was copied.</summary>
    private static void CheckCanRemove(string source, VolumeInfo volume, List<SourceFile> toMove, List<SourceFile> identical,
        PlanEnvironment env, List<PlanMessage> messages)
    {
        bool? canRemove = false;
        if (!volume.IsReadOnly)
        {
            var skipped = new HashSet<SourceFile>(identical);
            SourceFile probe = toMove.FirstOrDefault(f => !skipped.Contains(f) && !f.Attributes.HasFlag(FileAttributes.ReadOnly)) ?? toMove[0];
            try
            {
                canRemove = env.CanDelete(Path.Join(source, probe.RelativePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                canRemove = null;
            }
        }
        if (canRemove == false)
            messages.Add(new PlanMessage(MessageLevel.Error, SourceGuards.CannotRemoveError));
    }

    /// <summary>Read-only target, files too large for a FAT32 target, and free space (with each file's cluster allowance).</summary>
    internal static List<PlanMessage> CheckTarget(VolumeInfo target, long clusterSize, IReadOnlyCollection<SourceFile> toCopy, bool copying)
    {
        var messages = new List<PlanMessage>();
        if (target.IsReadOnly)
            messages.Add(new PlanMessage(MessageLevel.Error,
                $"The target drive {target.DisplayNameWithLabel} is read-only. Choose a folder on a drive that can be written to."));
        if (!copying) return messages;

        var tooLarge = toCopy.Where(f => f.Size > target.MaxFileSize).OrderByDescending(f => f.Size).ToList();
        if (tooLarge.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Error,
                $"The target drive {target.DisplayNameWithLabel} is {target.FileSystem}, which cannot store files of 4 GB or more: "
                + $"{tooLarge.Count:N0} file(s) (e.g. {tooLarge[0].Name}, {Format.Bytes(tooLarge[0].Size)}). Choose an NTFS or exFAT drive."));

        long needed = toCopy.Sum(f => Allocated(f.Size, clusterSize));
        if (needed + FreeSpaceMargin > target.FreeBytes)
            messages.Add(new PlanMessage(MessageLevel.Error,
                $"Not enough free space on {target.DisplayName}: {Format.Bytes(needed)} needed, {Format.Bytes(target.FreeBytes)} available."));
        return messages;
    }

    /// <summary>Space a file takes on a drive: its size rounded up to whole clusters.</summary>
    private static long Allocated(long size, long clusterSize) =>
        clusterSize <= 0 ? size : (size + clusterSize - 1) / clusterSize * clusterSize;

    /// <summary>Same size and modification time as the file already in the target: identical. Anything else: different.</summary>
    private static void FindConflicts(List<SourceFile> toMove, string target, VolumeInfo? targetVolume, List<SourceFile> identical, List<SourceFile> different)
    {
        if (!Directory.Exists(target)) return;
        bool coarseTimes = targetVolume?.IsFatFamily == true;
        foreach (SourceFile f in toMove)
        {
            FileSnapshot? existing;
            try
            {
                existing = SafeFile.TrySnapshot(Path.Join(target, f.RelativePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                different.Add(f); // something is there that cannot even be read: never treat it as a copy
                continue;
            }
            if (existing is null) continue;
            bool same = !existing.IsDirectory && existing.Size == f.Size
                && (existing.LastWriteTime == f.LastWriteTime || coarseTimes && Math.Abs(existing.LastWriteTime - f.LastWriteTime) <= FatTimeTolerance);
            (same ? identical : different).Add(f);
        }
    }

    /// <summary>
    /// A group (clip with its companions, card structure, mission) that contains a file clashing with a DIFFERENT file in
    /// the target stays in the source as a whole, so card B's proxies never land next to card A's clip.
    /// </summary>
    private static List<SourceFile> HoldBackGroups(List<SourceFile> toMove, List<SourceFile> different)
    {
        if (different.Count == 0) return [];
        var clashIn = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile d in different) clashIn.TryAdd(GroupOf(d), d);
        var clashing = new HashSet<SourceFile>(different);
        var held = toMove.Where(f => clashIn.ContainsKey(GroupOf(f))).ToList();
        foreach (SourceFile f in held)
        {
            if (clashing.Contains(f))
            {
                f.Note = FileNote.DifferentInTarget;
                f.Reason = "a DIFFERENT file with the same name is already in the target - stays";
            }
            else
            {
                f.Note = FileNote.HeldWithGroup;
                f.Reason = $"stays with {clashIn[GroupOf(f)].Name}: a DIFFERENT file with that name is already in the target";
            }
        }
        var heldSet = new HashSet<SourceFile>(held);
        toMove.RemoveAll(heldSet.Contains);
        return held;
    }

    private static string GroupOf(SourceFile f) => f.GroupKey.Length > 0 ? f.GroupKey : f.RelativePath;

    /// <summary>Earlier jobs in the same target: a warning when they came from another folder or used the other mode.</summary>
    private static void AddEarlierJobs(string target, string source, MoveMode mode, PlanEnvironment env, List<PlanMessage> messages)
    {
        var headers = new List<JobHeader>();
        try
        {
            foreach (string journal in env.JournalsIn(target))
            {
                try
                {
                    if (JournalReader.TryReadHeader(journal) is { Mode: MoveMode.Videos or MoveMode.Photos } header)
                        headers.Add(header);
                }
                catch (Exception)
                {
                    // A log this version cannot read (damaged, or written by a newer version): it only adds a hint to the
                    // preview, so it must never stop one.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // the log folder cannot be read; nothing to say about earlier jobs
        }
        if (headers.Count == 0) return;

        var others = headers.Where(h => !SameFolder(h.Source, source) || h.Mode != mode)
            .OrderByDescending(h => h.Created, StringComparer.Ordinal).ToList();
        if (others.Count > 0)
        {
            JobHeader latest = others[0];
            int more = others.Count - 1;
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"This target already holds files sorted from {latest.Source}{On(latest.Created)} ({Word(latest.Mode)})"
                + (more > 0 ? $", and from {more:N0} other earlier sort{(more == 1 ? "" : "s")}." : ".")));
        }
        else
        {
            string? created = headers.Select(h => h.Created).Max(StringComparer.Ordinal);
            messages.Add(new PlanMessage(MessageLevel.Info, $"This continues an earlier sort of this folder{On(created)}."));
        }
    }

    private static bool SameFolder(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>" on 25 Sep" (with the year when it is not this year), or "" when the date is unknown.</summary>
    private static string On(string? created)
    {
        if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset when)) return "";
        return " on " + when.ToString(when.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
