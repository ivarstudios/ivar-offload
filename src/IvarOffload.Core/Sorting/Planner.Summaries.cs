namespace IvarOffload.Core.Sorting;

/// <summary>The preview's explanations: what was recognized, what needs a look, and the per-type and per-folder totals.</summary>
public static partial class Planner
{
    private const int MaxListedTypes = 8;

    private static void AddSummaries(List<PlanMessage> messages, ScanResult scan, MoveMode mode, HashSet<SourceFile> moving,
        Dictionary<string, List<SourceFile>> split)
    {
        List<SourceFile> files = scan.Files;
        bool videos = mode == MoveMode.Videos;
        MediaSide side = MediaRules.SideFor(mode);

        AddNoteSummary(messages, files, FileNote.UnknownType, MessageLevel.Warning, "file(s) of unrecognized types stay in place");
        var large = files.Where(f => f.Note == FileNote.UnknownType && f.Size >= MediaRules.LargeUnknownBytes).OrderByDescending(f => f.Size).ToList();
        if (large.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{large.Count:N0} unrecognized file(s) of 100 MB or more ({Format.Bytes(large.Sum(f => f.Size))}) stay in place: "
                + Examples(large.Select(f => $"{f.Name} ({Format.Bytes(f.Size)})"), large.Count, 3)
                + ". They can be videos or photos in a format that IVAR Offload does not know. Check them before you delete or format anything."));
        AddNoteSummary(messages, files, FileNote.FollowsByName, MessageLevel.Warning, videos
            ? "file(s) of unrecognized types have the same name as a video next to them and go with it"
            : "file(s) of unrecognized types have the same name as a video next to them and stay with it");
        AddNoteSummary(messages, files, FileNote.Ambiguous, MessageLevel.Warning, "companion file(s) match both a photo and a video and stay in place");
        var online = files.Where(f => f.Note == FileNote.OnlineOnly).ToList();
        if (online.Count > 0)
        {
            int wouldMove = online.Count(f => f.NaturalSide == side);
            // Short clips named like a Live Photo still: Live Photo clips (photo side) or videos, which only their content tells.
            int undecided = online.Count(f => f.NaturalSide == MediaSide.Neutral && MediaRules.PrimarySide(f.Extension) == MediaSide.Video);
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{online.Count:N0} online-only cloud file(s) are not downloaded and stay in place ({Format.Bytes(online.Sum(f => f.Size))}): {TypeList(online)}."
                + (wouldMove == 0 ? "" : $" When these files are on this device, {Count(wouldMove, mode)} of them will move.")
                + (undecided == 0 ? "" : $" {undecided:N0} short clip(s) have the same name as a photo next to them. "
                    + "IVAR Offload can identify them as Live Photo clips or videos only after you download them.")
                + (wouldMove + undecided == 0 ? "" : " To sort them, make the folder available offline (right-click > Always keep on this device). "
                    + "When the download is complete, check again.")));
        }
        AddNoteSummary(messages, files, FileNote.Link, MessageLevel.Info, "link(s) or shortcut(s) stay in place, and the sort does not follow them");
        AddNoteSummary(messages, files, FileNote.LeftoverTemp, MessageLevel.Warning,
            "unfinished temporary file(s) from an earlier IVAR Offload (or IVAR Ingest, Ingest Sorter) job are in the source folder");

        var audio = files.Where(f => f.Note == FileNote.UnmatchedAudio).ToList();
        if (audio.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{audio.Count:N0} audio recording(s) ({Format.Bytes(audio.Sum(f => f.Size))}) have no photo of the same name, so they "
                + (videos ? "go with the videos" : "stay with the videos as video sound") + ": " + FolderExamples(audio, 3) + "."));

        var live = files.Where(f => f.Note == FileNote.LivePhoto).ToList();
        if (live.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{live.Count:N0} iPhone Live Photo clip(s) ({Format.Bytes(live.Sum(f => f.Size))}) belong to their photos and "
                + (videos ? "stay with them: " : "go with them: ") + Examples(live.Select(f => f.RelativePath), live.Count, 3) + "."));
        var sameName = files.Where(f => f.Note == FileNote.NotLivePhoto).ToList();
        if (sameName.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{sameName.Count:N0} video(s) have the same name as a photo next to them, but they are not Live Photo clips (no Apple Live Photo tag). "
                + $"IVAR Offload treats them as videos, so they {(videos ? "move" : "stay")}: "
                + Examples(sameName.Select(f => $"{f.RelativePath} ({Format.Bytes(f.Size)})"), sameName.Count, 3)
                + ". Probably another camera used the same file numbers. Check that they are videos."));
        var notLive = files.Where(f => f.Note == FileNote.LivePhotoTooLarge).ToList();
        if (notLive.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{notLive.Count:N0} video(s) have the same name as a photo, but they are too large for Live Photo clips. IVAR Offload treats them as videos: "
                + Examples(notLive.Select(f => $"{f.RelativePath} ({Format.Bytes(f.Size)})"), notLive.Count, 3) + "."));

        AddUnitSummaries(messages, scan, mode);

        var runs = SplitFrameRuns(files);
        if (runs.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{runs.Count:N0} sound file(s) have numbered frames with their name next to them, but IVAR Offload does not treat these frames as a clip "
                + "(too few frames, missing frames, or no CinemaDNG tags). The sound goes with the videos, and the frames go with the photos: "
                + Examples(runs.Select(r => $"{r.Sound.RelativePath} ({Files(r.Frames.Count)})"), runs.Count, 3)
                + ". If they are one clip, keep them together manually."));

        // By the classifier's note: a frame held back by a name clash in the target is still a hyperlapse frame.
        var hyperlapses = files.Where(f => (f.Classified?.Note ?? f.Note) == FileNote.HyperlapseFrame).GroupBy(f => f.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Frames: g.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderBy(h => h.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        if (hyperlapses.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{hyperlapses.Count:N0} DJI hyperlapse folder(s) hold the still frames of a hyperlapse: "
                + Examples(hyperlapses.Select(h => $"{Display(h.Folder)} ({h.Frames:N0} frames)"), hyperlapses.Count, 3)
                + (videos
                    ? ". They are photos and stay together with the photos, but the finished hyperlapse video moves with the videos. "
                    : ". They are photos and move together with the photos, but the finished hyperlapse video stays with the videos. ")
                + "If you edit the hyperlapse from its frames, move the folder manually."));

        var survey = files.Where(f => f.Note == FileNote.SurveyData).ToList();
        if (!videos && survey.Count > 0 && moving.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                "Survey data outside the mapping mission folders stays in the source folder: "
                + Examples(survey.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{Display(g.Key)} ({string.Join(", ", g.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase).Take(3))}{(g.Count() > 3 ? ", ..." : "")})"),
                    survey.Select(f => f.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count(), 3)
                + ". If you process PPK or use ground control points, copy it next to the photos."));

        var catalogs = files.Where(f => MediaRules.CatalogExtensions.Contains(f.Extension)).Select(f => f.Name)
            .Concat(scan.SkippedFolders.Where(s => s.LibraryKind is "Capture One catalog" or MediaRules.LuminarCatalogKind).Select(s => Path.GetFileName(s.RelativePath)))
            .ToList();
        if (catalogs.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"The source folder holds a photo catalog or session ({Examples(catalogs, catalogs.Count, 3)}). After the move, the catalog will show "
                + "the photos and videos that you imported into it as missing. Relink them to their new place in the target folder."));

        if (scan.SkippedFolders.Any(s => s.RelativePath.Split('\\')[^1].Equals("ascmhl", StringComparison.OrdinalIgnoreCase))
            || files.Any(f => f.Extension == ".mhl"))
            messages.Add(new PlanMessage(MessageLevel.Warning,
                "The source folder has checksum manifests from your backup tool (ASC MHL). After the sort, these manifests will show the moved files as missing. "
                + $"The IVAR Offload receipt in {Jobs.JobPaths.LogFolderName} records where each file went."));

        var libraries = scan.SkippedFolders.Where(s => s.LibraryKind is not null).ToList();
        if (libraries.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"The sort does not scan or change {libraries.Count:N0} application librar{(libraries.Count == 1 ? "y" : "ies")}: "
                + Examples(libraries.Select(s => $"{s.RelativePath} ({s.LibraryKind})"), libraries.Count, 4) + "."));

        if (split.Count > 0)
        {
            var worst = split.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"The sort will split {split.Count:N0} folder(s): files next to your {Word(mode)} stay in the source folder (for example "
                + string.Join("; ", worst.Take(3).Select(p => $"{Display(p.Key)}: {ExtensionCounts(p.Value, 4)}"))
                + (split.Count > 3 ? "; ..." : "") + "). They can belong to the files that move. Check them before you delete the source folder or format its drive."));
        }

        foreach (string problem in scan.Problems)
            messages.Add(new PlanMessage(MessageLevel.Warning, $"The scan could not read the folder {problem}"));
        if (scan.SkippedFolders.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info, "The scan skipped these folders: " + string.Join(", ",
                scan.SkippedFolders.Take(8).Select(s => $"{s.RelativePath} ({s.Reason})")) + (scan.SkippedFolders.Count > 8 ? ", ..." : ".")));
    }

    private static void AddUnitSummaries(List<PlanMessage> messages, ScanResult scan, MoveMode mode)
    {
        bool videos = mode == MoveMode.Videos;
        var structures = scan.Units.Where(u => u.Kind == UnitKind.CardStructure).ToList();
        if (structures.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                (videos ? "Video card structures move as a whole: " : "Video card structures stay whole: ")
                + Examples(structures.Select(u => $"{Display(u.Folder)} ({u.Description}, {Files(u.Files)})"), structures.Count, 4) + "."));

        var clips = scan.Units.Where(u => u.Kind == UnitKind.ImageSequence).ToList();
        if (clips.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{clips.Count:N0} image-sequence clip(s) ({Format.Bytes(clips.Sum(u => u.Bytes))}) are videos, not photos: "
                + Examples(clips.Select(u => $"{u.Description} in {Display(u.Folder)}"), clips.Count, 3)
                + (videos ? ". They go with the videos." : ". They stay with the videos.")));
        var gaps = clips.Where(u => u.MissingFrames > 0).ToList();
        if (gaps.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{gaps.Count:N0} CinemaDNG clip(s) have missing frames, but each one still stays together as one clip: "
                + Examples(gaps.Select(u => $"{Display(u.Folder)} ({u.MissingFrames:N0} missing)"), gaps.Count, 3)
                + ". Check the card or its backup for the missing frames."));

        var missions = scan.Units.Where(u => u.Kind == UnitKind.Mission).ToList();
        if (missions.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"{missions.Count:N0} mapping/LiDAR mission(s) stay whole "
                + (videos ? "(they stay with the photos): " : "(images, timestamps, RINEX/PPK and LiDAR files move together): ")
                + Examples(missions.Select(u => Display(u.Folder)), missions.Count, 3) + "."));

        var projects = scan.Units.Where(u => u.Kind == UnitKind.Project).ToList();
        int inProjects = scan.Files.Count(f => f.Note == FileNote.InProject && f.NaturalSide == MediaRules.SideFor(mode));
        string staying = inProjects == 0 ? "" : $" {Count(inProjects, mode)} in them stay{(inProjects == 1 ? "s" : "")} in the source folder.";
        if (projects.FirstOrDefault(u => u.Folder.Length == 0) is { } whole)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"The source folder is itself an editing/processing project: {whole.Description}. Nothing in it will move. "
                + "Choose the folder with the card backups instead." + staying));
        else if (projects.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{projects.Count:N0} editing/processing project folder(s) are in the source folder: "
                + Examples(projects.Select(u => $"{u.Description} in {u.Folder}"), projects.Count, 3)
                + ". Everything in them stays, so that the projects keep their media." + staying));
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    private static void AddNoteSummary(List<PlanMessage> messages, List<SourceFile> files, FileNote note, MessageLevel level, string text)
    {
        var matching = files.Where(f => f.Note == note).ToList();
        if (matching.Count == 0) return;
        messages.Add(new PlanMessage(level, $"{matching.Count:N0} {text} ({Format.Bytes(matching.Sum(f => f.Size))}): {TypeList(matching)}."));
    }

    /// <summary>".nev x12 (192 GB), .xml x40 (2.1 MB), ..." ordered by size, so big unknown media is never cut off.</summary>
    private static string TypeList(List<SourceFile> files)
    {
        var groups = files.GroupBy(f => f.Extension.Length == 0 ? "(no extension)" : f.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Ext: g.Key, Count: g.Count(), Bytes: g.Sum(f => f.Size)))
            .OrderByDescending(g => g.Bytes).ThenByDescending(g => g.Count).ThenBy(g => g.Ext, StringComparer.Ordinal).ToList();
        string list = string.Join(", ", groups.Take(MaxListedTypes).Select(g => $"{g.Ext} x{g.Count:N0} ({Format.Bytes(g.Bytes)})"));
        return groups.Count > MaxListedTypes ? $"{list} and {groups.Count - MaxListedTypes:N0} more types" : list;
    }

    private static string ExtensionCounts(List<SourceFile> files, int max)
    {
        var groups = files.GroupBy(f => f.Extension.Length == 0 ? "(no extension)" : f.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        return string.Join(", ", groups.Take(max).Select(g => $"{g.Key} x{g.Count():N0}")) + (groups.Count > max ? ", ..." : "");
    }

    private static string FolderExamples(List<SourceFile> files, int max)
    {
        var folders = files.Select(f => f.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Examples(folders.Select(Display), folders.Count, max);
    }

    private static string Examples(IEnumerable<string> items, int count, int max) =>
        string.Join(", ", items.Take(max)) + (count > max ? $" and {count - max:N0} more" : "");

    private static string Display(string folder) => folder.Length == 0 ? "(source folder)" : folder;

    /// <summary>
    /// Folders where files move while others stay that may belong to them: not system files, not links or online-only
    /// files (they have their own messages), not macOS "._" files, and not the other side's photos or videos.
    /// </summary>
    private static Dictionary<string, List<SourceFile>> FindSplitFolders(List<SourceFile> files, HashSet<SourceFile> moving)
    {
        var split = new Dictionary<string, List<SourceFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, SourceFile> folder in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            if (!folder.Any(moving.Contains)) continue;
            var left = folder.Where(f => !moving.Contains(f) && IsLeftBehind(f)).ToList();
            if (left.Count > 0) split[folder.Key] = left;
        }
        // A sound file and the numbered frames named after it that did not become a clip: whichever of them stays is left behind.
        foreach ((SourceFile sound, List<SourceFile> frames) in SplitFrameRuns(files))
        {
            var left = moving.Contains(sound) ? frames.Where(f => !moving.Contains(f)).ToList()
                : frames.Any(moving.Contains) ? [sound] : [];
            if (left.Count == 0) continue;
            if (!split.TryGetValue(sound.Directory, out List<SourceFile>? list)) split[sound.Directory] = list = [];
            list.AddRange(left.Where(f => !list.Contains(f)));
        }
        return split;
    }

    /// <summary>
    /// Sound files next to numbered frames named after them ("C0003.wav" with "C0003_000001.dng", ...) where the frames
    /// did not become a clip (too few, gaps, no CinemaDNG tags): the sound and the frames end up on different sides.
    /// </summary>
    private static List<(SourceFile Sound, List<SourceFile> Frames)> SplitFrameRuns(List<SourceFile> files)
    {
        var result = new List<(SourceFile, List<SourceFile>)>();
        foreach (IGrouping<string, SourceFile> folder in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            var frames = folder.Where(f => MediaRules.SequenceFrameExtensions.Contains(f.Extension) && f.Side != MediaSide.Video).ToList();
            if (frames.Count == 0) continue;
            foreach (SourceFile sound in folder.Where(f => MediaRules.IsAudio(f.Extension) && f.Side == MediaSide.Video))
            {
                var run = frames.Where(f => Classifier.IsFrameOf(f, Path.GetFileNameWithoutExtension(sound.Name))).ToList();
                if (run.Count > 0) result.Add((sound, run));
            }
        }
        return result;
    }

    private static bool IsLeftBehind(SourceFile f) =>
        f.Side == MediaSide.Neutral
        && f.Note is not (FileNote.SystemFile or FileNote.LeftoverTemp or FileNote.Link or FileNote.OnlineOnly)
        && !f.Name.StartsWith("._", StringComparison.Ordinal);

    private static List<TypeSummary> SummarizeTypes(List<SourceFile> files, HashSet<SourceFile> moving) =>
        files.GroupBy(f => (Ext: f.Extension.Length == 0 ? "(none)" : f.Extension, Kind: Describe(f.Side, f.Role, f.Note), Moves: moving.Contains(f)))
            .Select(g => new TypeSummary(g.Key.Ext, g.Key.Kind, g.Key.Moves, g.Count(), g.Sum(f => f.Size)))
            .OrderByDescending(t => t.Moves).ThenByDescending(t => t.Bytes).ToList();

    private static string Describe(MediaSide side, FileRole role, FileNote note) => note switch
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
        FileNote.DifferentInTarget => "Name taken in target folder (different)",
        FileNote.HeldWithGroup => "Kept with its clip",
        FileNote.NotLivePhoto => "Video (a photo's name)",
        FileNote.SurveyData => "Survey data",
        FileNote.HyperlapseFrame => "Hyperlapse frame",
        _ => (side, role) switch
        {
            (MediaSide.Video, FileRole.Primary) => "Video",
            (MediaSide.Photo, FileRole.Primary) => "Photo",
            (MediaSide.Video, _) => "Video companion",
            (MediaSide.Photo, _) => "Photo companion",
            (_, FileRole.Sidecar) => "Companion (no match)",
            _ => "Not photo/video",
        },
    };

    /// <summary>Folders with files that move, plus folders where something other than system files stays (so nothing is hidden).</summary>
    private static List<FolderSummary> SummarizeFolders(List<SourceFile> files, HashSet<SourceFile> moving, Dictionary<string, List<SourceFile>> split)
    {
        var result = new List<FolderSummary>();
        foreach (IGrouping<string, SourceFile> g in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            var moved = g.Where(moving.Contains).ToList();
            var stays = g.Where(f => !moving.Contains(f)).ToList();
            if (moved.Count == 0 && !stays.Any(f => f.Note != FileNote.SystemFile)) continue;
            result.Add(new FolderSummary(Display(g.Key), moved.Count, moved.Sum(f => f.Size), stays.Count, stays.Sum(f => f.Size), split.ContainsKey(g.Key)));
        }
        return result.OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
