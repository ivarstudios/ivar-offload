using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>
/// What sorts of a folder planned to move and left behind (a job ended early, or a file was skipped or failed). Whether
/// a file moves can depend on the files next to it: the last frames of a CinemaDNG clip whose other frames already moved
/// look like photos, a sidecar whose photo moved looks unmatched. A fresh look at the source alone would let such files
/// stay and call the result finished, so the jobs' own logs decide: those files stay listed as left behind until they
/// have moved, and "Move the remaining" (or a new preview of the folder, in the app or on the command line) takes them
/// along.
/// </summary>
public static class Leftovers
{
    /// <summary>
    /// The plan with what earlier sorts of its folder into its target (in its mode) planned and left behind added, where
    /// a fresh look at those files alone would let them stay (the rest of a clip whose other frames already moved). What
    /// every preview of a sort runs, in the app and on the command line.
    /// </summary>
    public static MovePlan WithEarlierSorts(MovePlan plan) => plan.HasErrors ? plan
        : Include(plan, EarlierSorts(plan.SourceRoot, plan.TargetRoot, plan.Mode), InTarget(plan.TargetRoot));

    /// <summary>
    /// The files the given sorts planned to move and did not, that are still in the source unchanged (same size and
    /// date), with the item that planned them and its job; the first job given wins. Files whose identical copy is
    /// already in the target are left out (nothing is missing).
    /// </summary>
    public static List<(SourceFile File, JobItem Item, JobState Job)> StillThere(MovePlan plan, IEnumerable<JobState> jobs)
    {
        var files = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile f in plan.Scan.Files) files.TryAdd(f.RelativePath, f);
        var identical = new HashSet<SourceFile>(plan.IdenticalConflicts);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(SourceFile, JobItem, JobState)>();
        foreach (JobState job in jobs.Where(j => j.Header.Kind == JobKind.Sort && j.Header.Mode == plan.Mode))
            foreach (JobItem item in job.StillInSource)
                if (files.TryGetValue(item.Rel, out SourceFile? f) && !identical.Contains(f) && IsSameFile(f, item) && seen.Add(item.Rel))
                    result.Add((f, item, job));
        return result;
    }

    /// <summary>
    /// The plan with what the given sorts left behind added to the files that move, where a fresh look at them alone lets
    /// them stay. Never a file that has a reason to stay now (online-only, a link, inside a project, a name clash in the
    /// target), nor one whose name is already taken in the target (<paramref name="inTarget"/>: never overwritten, so it
    /// could not move anyway). The same plan when nothing is added or when it cannot run.
    /// </summary>
    public static MovePlan Include(MovePlan plan, IEnumerable<JobState> jobs, Func<string, bool> inTarget)
    {
        if (plan.HasErrors) return plan;
        var moving = new HashSet<SourceFile>(plan.ToMove);
        var add = StillThere(plan, jobs)
            .Where(x => !moving.Contains(x.File) && !MustStay(x.File) && !inTarget(x.File.RelativePath)).ToList();
        if (add.Count == 0) return plan;

        // New entries, so the scan's own files keep what the classifier said about them.
        MediaSide side = MediaRules.SideFor(plan.Mode);
        List<SourceFile> added = add.Select(x => new SourceFile
        {
            RelativePath = x.File.RelativePath,
            Size = x.File.Size,
            CreationTime = x.File.CreationTime,
            LastWriteTime = x.File.LastWriteTime,
            Attributes = x.File.Attributes,
            Side = side,
            Role = x.File.Role,
            Reason = $"{(x.Item.Why.Length > 0 ? x.Item.Why : "planned to move")} - left behind by an earlier sort of this folder "
                + $"({Format.JobDate(x.Job.Header.Created)}), moves now",
            GroupKey = x.Item.Group.Length > 0 ? x.Item.Group : x.File.GroupKey,
        }).ToList();
        var taken = new HashSet<SourceFile>(add.Select(x => x.File));
        List<string> folders = added.Select(f => f.Directory.Length > 0 ? f.Directory : "(source folder)").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var message = new PlanMessage(MessageLevel.Info,
            $"{Format.Count(added.Count, "file")} that an earlier sort of this folder planned to move and left behind "
            + $"{(added.Count == 1 ? "moves" : "move")} now, although on {(added.Count == 1 ? "its" : "their")} own {(added.Count == 1 ? "it" : "they")} would stay "
            + $"(for example the last frames of a clip whose other frames already moved): {string.Join(", ", folders.Take(3))}{(folders.Count > 3 ? ", ..." : "")}.");

        // The totals and the messages that depend on what moves are worked out again, so the preview never says the
        // added files stay while Confirm and the move list take them.
        var replaced = add.Select((x, i) => (x.File, New: added[i])).ToDictionary(p => p.File, p => p.New);
        List<SourceFile> files = plan.Scan.Files.Select(f => replaced.TryGetValue(f, out SourceFile? now) ? now : f).ToList();
        List<SourceFile> toMove = [.. plan.ToMove, .. added];
        Summary summary = Summarize(files, new HashSet<SourceFile>(toMove), plan.Mode, plan.Messages.Append(message));
        return new MovePlan
        {
            Scan = plan.Scan,
            SourceRoot = plan.SourceRoot,
            TargetRoot = plan.TargetRoot,
            Mode = plan.Mode,
            Method = plan.Method,
            VerifyChecksums = plan.VerifyChecksums,
            SourceVolume = plan.SourceVolume,
            TargetVolume = plan.TargetVolume,
            ToMove = toMove,
            Staying = plan.Staying.Where(f => !taken.Contains(f)).ToList(),
            Conflicts = plan.Conflicts,
            IdenticalConflicts = plan.IdenticalConflicts,
            DifferentConflicts = plan.DifferentConflicts,
            HeldBack = plan.HeldBack,
            // Online-only, links and project media: never added (see MustStay), so they are the same files.
            MovingSideStaying = plan.MovingSideStaying,
            SuggestedSource = plan.SuggestedSource,
            MemoryCard = plan.MemoryCard,
            Messages = summary.Messages,
            ByType = summary.ByType,
            ByFolder = summary.ByFolder,
        };
    }

    /// <summary>The per-type and per-folder totals, and the messages, of a plan whose files that move changed.</summary>
    public sealed record Summary(IReadOnlyList<TypeSummary> ByType, IReadOnlyList<FolderSummary> ByFolder, IReadOnlyList<PlanMessage> Messages);

    /// <summary>
    /// The "By type" and "By folder" totals of <paramref name="files"/> where <paramref name="moving"/> move, and
    /// <paramref name="messages"/> with the ones that depend on what moves (folders that will be split, audio recordings
    /// with no matching photo) worked out again, all as the Planner words them (a test holds the two together).
    /// </summary>
    public static Summary Summarize(IReadOnlyList<SourceFile> files, IReadOnlySet<SourceFile> moving, MoveMode mode, IEnumerable<PlanMessage> messages)
    {
        Dictionary<string, List<SourceFile>> split = SplitFolders(files, moving);

        var byType = files.GroupBy(f => (Ext: f.Extension.Length == 0 ? "(none)" : f.Extension, Kind: Describe(f), Moves: moving.Contains(f)))
            .Select(g => new TypeSummary(g.Key.Ext, g.Key.Kind, g.Key.Moves, g.Count(), g.Sum(f => f.Size)))
            .OrderByDescending(t => t.Moves).ThenByDescending(t => t.Bytes).ToList();

        var byFolder = new List<FolderSummary>();
        foreach (IGrouping<string, SourceFile> g in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            var moved = g.Where(moving.Contains).ToList();
            var stays = g.Where(f => !moving.Contains(f)).ToList();
            if (moved.Count == 0 && !stays.Any(f => f.Note != FileNote.SystemFile)) continue;
            byFolder.Add(new FolderSummary(Display(g.Key), moved.Count, moved.Sum(f => f.Size), stays.Count, stays.Sum(f => f.Size), split.ContainsKey(g.Key)));
        }

        // Stays in place when the text did not change; a message that is new now goes with the others of its level.
        PlanMessage? splitMessage = SplitMessage(split, mode);
        // An added file is a new entry with no note: it is not counted among the recordings that stay.
        PlanMessage? audioMessage = AudioMessage(files.Where(f => f.Note == FileNote.UnmatchedAudio).ToList(), mode);
        var result = new List<PlanMessage>();
        bool splitShown = false, audioShown = false;
        foreach (PlanMessage m in messages)
        {
            if (IsSplitMessage(m))
            {
                if (splitMessage is not null && !splitShown) result.Add(splitMessage);
                splitShown = true;
            }
            else if (IsAudioMessage(m))
            {
                if (audioMessage is not null && !audioShown) result.Add(audioMessage);
                audioShown = true;
            }
            else result.Add(m);
        }
        if (splitMessage is not null && !splitShown) result.Add(splitMessage);
        if (audioMessage is not null && !audioShown) result.Add(audioMessage);

        return new Summary(byType, byFolder.OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).ToList(),
            result.OrderByDescending(m => m.Level).ToList());
    }

    // ---- As Planner.Summaries words them ----------------------------------------------------------------------------

    private const string SplitMarker = " will be split - files next to your ";
    private const string AudioMarker = " with no matching photo ";

    /// <summary>The Planner's warning about folders that will be split (the app keeps it among the few it always shows).</summary>
    public static bool IsSplitMessage(PlanMessage m) => m.Level == MessageLevel.Warning && m.Text.Contains(SplitMarker, StringComparison.Ordinal);

    private static bool IsAudioMessage(PlanMessage m) =>
        m.Level == MessageLevel.Info && m.Text.Contains(" audio recording(s) (", StringComparison.Ordinal) && m.Text.Contains(AudioMarker, StringComparison.Ordinal);

    /// <summary>Folders where files move while others stay that may belong to them (see the Planner's "will be split").</summary>
    private static Dictionary<string, List<SourceFile>> SplitFolders(IReadOnlyList<SourceFile> files, IReadOnlySet<SourceFile> moving)
    {
        var split = new Dictionary<string, List<SourceFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, SourceFile> folder in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            if (!folder.Any(moving.Contains)) continue;
            var left = folder.Where(f => !moving.Contains(f) && f.Side == MediaSide.Neutral
                && f.Note is not (FileNote.SystemFile or FileNote.LeftoverTemp or FileNote.Link or FileNote.OnlineOnly)
                && !f.Name.StartsWith("._", StringComparison.Ordinal)).ToList();
            if (left.Count > 0) split[folder.Key] = left;
        }
        return split;
    }

    private static PlanMessage? SplitMessage(Dictionary<string, List<SourceFile>> split, MoveMode mode)
    {
        if (split.Count == 0) return null;
        var worst = split.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return new PlanMessage(MessageLevel.Warning,
            $"{split.Count:N0} folder(s){SplitMarker}{Planner.Word(mode)} stay behind (e.g. "
            + string.Join("; ", worst.Take(3).Select(p => $"{Display(p.Key)}: {ExtensionCounts(p.Value, 4)}"))
            + (split.Count > 3 ? "; ..." : "") + "). They may belong to the files that move - check them before deleting or formatting the source.");
    }

    /// <summary>Audio recordings with no matching photo: they go with the videos, or stay as video sound.</summary>
    private static PlanMessage? AudioMessage(List<SourceFile> audio, MoveMode mode) => audio.Count == 0 ? null
        : new PlanMessage(MessageLevel.Info,
            $"{audio.Count:N0} audio recording(s) ({Format.Bytes(audio.Sum(f => f.Size))}){AudioMarker}"
            + (mode == MoveMode.Videos ? "go with the videos" : "count as video sound and stay") + ": " + FolderExamples(audio, 3) + ".");

    private static string Describe(SourceFile f) => f.Note switch
    {
        FileNote.UnknownType => "Unrecognized",
        FileNote.Ambiguous => "Companion (ambiguous)",
        FileNote.SystemFile => "System file",
        FileNote.Link => "Link",
        FileNote.OnlineOnly => "Online-only",
        FileNote.LeftoverTemp => "Unfinished copy",
        FileNote.UnmatchedAudio => "Audio recording",
        FileNote.FollowsByName => "Unrecognized, follows video",
        FileNote.LivePhoto => "Live Photo clip",
        FileNote.InProject => "Inside a project",
        FileNote.DifferentInTarget => "Name taken in target (different)",
        FileNote.HeldWithGroup => "Held with its clip",
        _ => (f.Side, f.Role) switch
        {
            (MediaSide.Video, FileRole.Primary) => "Video",
            (MediaSide.Photo, FileRole.Primary) => "Photo",
            (MediaSide.Video, _) => "Video companion",
            (MediaSide.Photo, _) => "Photo companion",
            (_, FileRole.Sidecar) => "Companion (no match)",
            _ => "Not photo/video",
        },
    };

    private static string Display(string folder) => folder.Length == 0 ? "(source folder)" : folder;

    private static string ExtensionCounts(List<SourceFile> files, int max)
    {
        var groups = files.GroupBy(f => f.Extension.Length == 0 ? "(no extension)" : f.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        return string.Join(", ", groups.Take(max).Select(g => $"{g.Key} x{g.Count():N0}")) + (groups.Count > max ? ", ..." : "");
    }

    private static string FolderExamples(List<SourceFile> files, int max)
    {
        var folders = files.Select(f => f.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(", ", folders.Take(max).Select(Display)) + (folders.Count > max ? $" and {folders.Count - max:N0} more" : "");
    }

    /// <summary>Whether a file of that relative path (or a folder of that name) is already in the target.</summary>
    public static Func<string, bool> InTarget(string targetRoot) => rel =>
    {
        try
        {
            return targetRoot.Length > 0 && Path.Exists(Path.Join(targetRoot, rel));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // unknown: not added
        }
    };

    /// <summary>
    /// Ended sorts from <paramref name="source"/> into <paramref name="target"/> in this mode that left files behind and
    /// were not undone (their logs are in the target), newest first. <paramref name="except"/>: a job log to leave out.
    /// A log that cannot be read is left out. Unfinished sorts are not asked: they are resumed or ended first.
    /// </summary>
    public static List<JobState> EarlierSorts(string source, string target, MoveMode mode, string? except = null)
    {
        var result = new List<JobState>();
        if (source.Length == 0 || target.Length == 0) return result;
        try
        {
            foreach (string journal in JobPaths.FindJournals(target))
            {
                if (except is not null && JobPaths.SamePath(journal, except)) continue;
                if (JournalReader.TryReadHeader(journal) is not { Kind: JobKind.Sort } header || header.Mode != mode || !JobPaths.SamePath(header.Source, source))
                    continue;
                try
                {
                    JobState state = JournalReader.Read(journal);
                    if (state.IsEnded && state.UndoneBy is null && state.StillInSourceCount > 0) result.Add(state);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return result.OrderByDescending(j => j.Header.Created ?? "", StringComparer.Ordinal).ToList();
    }

    /// <summary>The file in the source is the one the job planned: same size and last-write time.</summary>
    private static bool IsSameFile(SourceFile f, JobItem item) =>
        f.Size == item.Size && f.LastWriteTime == (item.SeenLastWriteTime ?? item.LastWriteTime);

    /// <summary>A reason to stay that may not have existed when the file was planned.</summary>
    private static bool MustStay(SourceFile f) =>
        f.Note is FileNote.OnlineOnly or FileNote.Link or FileNote.InProject or FileNote.DifferentInTarget or FileNote.HeldWithGroup or FileNote.LeftoverTemp;
}
