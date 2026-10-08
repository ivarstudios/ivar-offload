using System.Text.RegularExpressions;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Decides for every file whether it belongs to the photo side, the video side, or stays where it is.
/// Companion files (sidecars) follow the photo/video they belong to. Some folders are treated as one unit:
/// video card structures, image-sequence clips, mapping missions and editing projects.
/// </summary>
public static partial class Classifier
{
    private const FileAttributes OnlineOnlyAttributes = FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    public static IReadOnlyList<MediaUnit> Classify(IReadOnlyList<SourceFile> files) => Classify(files, null, null);

    /// <summary>Classifies every file and returns the folders that were treated as one unit.</summary>
    /// <param name="folders">Every scanned folder (relative paths). Null: derived from the files' folders.</param>
    /// <param name="sourceName">Name of the source folder itself, for rules that look at a folder's own name.</param>
    /// <param name="isMovieFrame">
    /// Reads a DNG file and tells whether it is a CinemaDNG movie frame (see <see cref="CinemaDng.IsMovieFrame"/>).
    /// Null: nothing is read, and a run of DNG frames only counts as a clip when a sound file named after it is next to it.
    /// </param>
    /// <param name="isLivePhotoClip">
    /// Reads a short MOV/MP4 with a photo's name and tells whether it is an Apple Live Photo clip (see
    /// <see cref="LivePhotoClip.IsLivePhotoClip"/>). Null: nothing is read, and such a clip is treated as a video.
    /// </param>
    public static IReadOnlyList<MediaUnit> Classify(IReadOnlyList<SourceFile> files, IEnumerable<string>? folders, string? sourceName,
        Func<SourceFile, bool>? isMovieFrame = null, Func<SourceFile, bool>? isLivePhotoClip = null)
    {
        var tree = new FolderTree(files, folders, sourceName ?? "", isMovieFrame);

        // Pass 1: everything that can be decided from the file itself and its folder.
        var deferred = new List<SourceFile>();
        var unitOf = new Dictionary<SourceFile, Unit>();
        foreach (SourceFile f in files)
        {
            if (ClassifyAlone(f, tree, out Unit? unit))
                deferred.Add(f);
            else if (f.Role == FileRole.Primary)
                tree[f.Directory].AddPrimary(f);
            if (unit is not null) unitOf[f] = unit;
        }

        // Pass 2: an Apple Live Photo keeps its short clip with the photo. The name alone proves nothing (Canon bodies and
        // iPhones both count IMG_0001, ...), so the clip itself must say it is a Live Photo; any other clip is a video.
        foreach (FolderIndex index in tree.Folders)
        {
            foreach (SourceFile clip in index.Primaries.Where(p => p.Side == MediaSide.Video && !unitOf.ContainsKey(p)
                         && MediaRules.LivePhotoClipExtensions.Contains(p.Extension)).ToList())
            {
                SourceFile? still = index.PrimariesWithStem(Stem(clip.Name)).FirstOrDefault(IsLivePhotoStill);
                if (still is null) continue;
                if (clip.Size > MediaRules.LivePhotoMaxClipBytes)
                    Set(clip, MediaSide.Video, FileRole.Primary,
                        $"video ({ExtLabel(clip)}) - same name as the photo {still.Name}, but too large for a Live Photo clip", FileNote.LivePhotoTooLarge);
                else if (isLivePhotoClip?.Invoke(clip) == true)
                {
                    Set(clip, MediaSide.Photo, FileRole.Primary, $"Live Photo motion clip of {still.Name} - goes with the photo", FileNote.LivePhoto);
                    clip.GroupKey = still.GroupKey;
                }
                else
                    Set(clip, MediaSide.Video, FileRole.Primary,
                        $"video ({ExtLabel(clip)}) - same name as the photo {still.Name}, but not a Live Photo clip (no Apple Live Photo tag in it)",
                        FileNote.NotLivePhoto);
            }
        }

        // Pass 3: an unrecognized file with the same name as a video next to it is part of that video (N-RAW, 360 masters).
        foreach (SourceFile f in files.Where(f => f.Note == FileNote.UnknownType))
        {
            var owners = tree[f.Directory].PrimariesWithStem(Stem(f.Name)).ToList();
            SourceFile? video = owners.FirstOrDefault(o => o.Side == MediaSide.Video);
            if (video is null || owners.Any(o => o.Side == MediaSide.Photo)) continue;
            Set(f, MediaSide.Video, FileRole.Sidecar, $"unrecognized type with the same name as video {video.Name} - goes with the videos", FileNote.FollowsByName);
            f.GroupKey = video.GroupKey;
        }

        // Pass 4: companions follow their photo/video, then recorder take files their recordings; macOS "._" files last,
        // since they may shadow a companion.
        foreach (SourceFile f in deferred.Where(f => !IsAppleDouble(f.Name) && !IsRecorderProject(f))) ResolveSidecar(f, tree);
        foreach (SourceFile f in deferred.Where(IsRecorderProject)) ResolveRecorderProject(f, tree);
        foreach (SourceFile f in deferred.Where(f => IsAppleDouble(f.Name))) ResolveAppleDouble(f, tree);

        foreach (SourceFile f in files) f.Classified = (f.Note, f.Reason);
        return Units(unitOf);
    }

    /// <summary>
    /// Classifies what can be decided alone. Returns true when the file is a companion that needs its siblings.
    /// <paramref name="unit"/> is the folder unit the file was classified as part of, if any.
    /// </summary>
    private static bool ClassifyAlone(SourceFile f, FolderTree tree, out Unit? unit)
    {
        string ext = f.Extension;
        string label = ExtLabel(f);
        f.Note = FileNote.None;
        f.NaturalSide = MediaSide.Neutral;
        f.GroupKey = f.RelativePath;
        unit = null;

        FolderFacts facts = tree.Facts(f.Directory);
        if (f.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            f.NaturalSide = NaturalSide(f, facts, tree[f.Directory]);
            return Set(f, MediaSide.Neutral, FileRole.Other, "link / shortcut - not followed, stays", FileNote.Link);
        }
        if ((f.Attributes & OnlineOnlyAttributes) != 0)
        {
            // Never read or moved (that would download it), but a photo or video of the moving side is still reported as left behind.
            f.NaturalSide = NaturalSide(f, facts, tree[f.Directory]);
            return Set(f, MediaSide.Neutral, FileRole.Other,
                f.NaturalSide != MediaSide.Neutral
                    ? $"online-only cloud placeholder ({Word(f.NaturalSide)}, not downloaded) - stays; make it available offline to move it"
                    : MediaRules.PrimarySide(ext) == MediaSide.Video
                        ? "online-only cloud placeholder (a Live Photo clip or a video - it is not downloaded, so it cannot be told) - stays; make it available offline to sort it"
                        : "online-only cloud placeholder - stays",
                FileNote.OnlineOnly);
        }
        if (Jobs.JobPaths.IsTempExtension(ext))
            return Set(f, MediaSide.Neutral, FileRole.Other, "unfinished IVAR Offload copy - stays", FileNote.LeftoverTemp);

        if (facts.Project is { } project)
        {
            unit = project;
            f.GroupKey = project.GroupKey;
            f.NaturalSide = MediaRules.PrimarySide(ext);
            return Set(f, MediaSide.Neutral, FileRole.Other,
                $"inside an editing/processing project ({project.Marker}) - stays so the project keeps its media", FileNote.InProject);
        }

        Unit? structure = facts.Structure;
        if (structure is null && f.Name.Equals(CardStructures.P2LastClipFile, StringComparison.OrdinalIgnoreCase))
            structure = tree.P2ContentsIn(f.Directory);
        if (structure is not null && structure.Includes(f))
        {
            unit = structure;
            f.GroupKey = structure.GroupKey;
            FileRole role = MediaRules.PrimarySide(ext) == MediaSide.Video ? FileRole.Primary : FileRole.Sidecar;
            return Set(f, MediaSide.Video, role, $"part of the {structure.Description} ({tree.FolderName(structure.Folder)})");
        }

        if (facts.Sequence is { } clip && clip.Includes(f))
        {
            unit = clip;
            f.GroupKey = clip.GroupKey;
            FileRole role = MediaRules.SequenceFrameExtensions.Contains(ext) ? FileRole.Primary : FileRole.Sidecar;
            return Set(f, MediaSide.Video, role, $"part of {clip.Description}");
        }

        if (MediaRules.SystemFileNames.Contains(f.Name))
            return Set(f, MediaSide.Neutral, FileRole.Other, "system file - stays", FileNote.SystemFile);
        if (IsAppleDouble(f.Name))
        {
            f.Role = FileRole.Sidecar;
            return true;
        }

        // A mapping mission keeps everything but its videos (and their companions) with the photos.
        if (facts.Mission is { } mission && MediaRules.PrimarySide(ext) != MediaSide.Video && !tree[f.Directory].HasVideoStem(Stem(f.Name)))
        {
            unit = mission;
            f.GroupKey = mission.GroupKey;
            return MediaRules.PhotoExtensions.Contains(ext)
                ? Set(f, MediaSide.Photo, FileRole.Primary, $"photo ({label}), part of a DJI mapping/LiDAR mission ({mission.Marker} found)")
                : Set(f, MediaSide.Photo, FileRole.Sidecar, $"part of a DJI mapping/LiDAR mission ({mission.Marker} found)");
        }

        switch (MediaRules.PrimarySide(ext))
        {
            case MediaSide.Video:
                f.GroupKey = PrimaryKey(f);
                return Set(f, MediaSide.Video, FileRole.Primary, $"video ({label})");
            case MediaSide.Photo when facts.Hyperlapse is { } hyperlapse && hyperlapse.Includes(f):
                // Real stills, so they stay photos, but the frames of one hyperlapse are kept together (the preview warns).
                f.GroupKey = hyperlapse.Folder;
                return Set(f, MediaSide.Photo, FileRole.Primary,
                    $"photo ({label}), a source frame of the DJI hyperlapse {hyperlapse.Name} ({hyperlapse.Frames:N0} frames) - kept with its folder",
                    FileNote.HyperlapseFrame);
            case MediaSide.Photo:
                f.GroupKey = PrimaryKey(f);
                return Set(f, MediaSide.Photo, FileRole.Primary, $"photo ({label})");
        }

        if (MediaRules.IsSurveyData(f.Name))
            return Set(f, MediaSide.Neutral, FileRole.Other, "survey data (GNSS base station or ground control points) - stays", FileNote.SurveyData);
        if (MediaRules.Sidecars.ContainsKey(ext) || MediaRules.RecorderProjectExtensions.Contains(ext))
        {
            f.Role = FileRole.Sidecar;
            return true;
        }
        if (MediaRules.KnownOtherExtensions.Contains(ext))
            return Set(f, MediaSide.Neutral, FileRole.Other, $"not a photo or video ({label}) - stays");
        return Set(f, MediaSide.Neutral, FileRole.Other,
            ext.Length == 0 ? "file without extension - stays" : $"unrecognized type ({label}) - stays", FileNote.UnknownType);
    }

    private static void ResolveSidecar(SourceFile f, FolderTree tree)
    {
        SidecarRule rule = MediaRules.Sidecars[f.Extension];
        FolderIndex here = tree[f.Directory];

        // "clip.MOV.xmp", ".LP_Store\clip.MOV.lpmd", "CaptureOne\Settings153\X.CR3.cos": the inner extension names the file it belongs to.
        string inner = Path.GetFileNameWithoutExtension(f.Name);
        if (f.Extension == ".xml" && inner.EndsWith(".aux", StringComparison.OrdinalIgnoreCase))
        {
            inner = inner[..^4]; // GDAL's "ortho.tif.aux.xml" belongs to ortho.tif
            rule = MediaRules.GisAuxXml;
        }
        MediaSide innerSide = MediaRules.PrimarySide(Path.GetExtension(inner));
        if (innerSide != MediaSide.Neutral)
        {
            SourceFile? owner = OwnerFolders(f.Directory).Select(d => tree.Find(d, inner)).FirstOrDefault(o => o is not null);
            if (owner is not null)
                Follow(f, rule, owner);
            else
                Set(f, innerSide, FileRole.Sidecar, $"{rule.Kind} for {inner} (that file is not here) - goes with the {Word(innerSide)}s");
            return;
        }

        List<SourceFile> owners = here.PrimariesWithStem(Stem(f.Name)).ToList();
        bool viaClipXml = false;
        if (owners.Count == 0 && f.Extension == ".xml" && SonyClipXml().Match(Stem(f.Name)) is { Success: true } m)
        {
            owners = here.PrimariesWithStem(m.Groups[1].Value).ToList();
            viaClipXml = owners.Count > 0;
        }
        if (owners.Count == 0 && f.Extension is ".thm" or ".scr" && tree.DjiThumbnailOwner(f) is { } clip)
            owners = [clip];
        if (owners.Count == 0 && f.Extension == ".xml" && ClipFolderVideo(f, here, tree) is { } video)
        {
            // An ARRIRAW clip folder "A001C001_...\A001C001_....0000001.ari" with "A001C001_....xml": the clip's metadata.
            Set(f, MediaSide.Video, FileRole.Sidecar, $"{rule.Kind} for the clip {Stem(f.Name)} - goes with the videos");
            f.GroupKey = video.GroupKey;
            return;
        }

        var sides = owners.Select(o => o.Side).Distinct().ToList();
        if (sides.Count == 1)
        {
            Follow(f, rule, owners.OrderBy(o => o.Side == MediaSide.Video ? 0 : 1).First());
            return;
        }
        if (sides.Count > 1)
        {
            // Telemetry, thumbnails and camera clip XML only exist for videos; Apple edit data only for photos. MOV/MP4 keep
            // their XMP inside the file, so an .xmp next to a raw photo and such a clip holds the raw's edits (two bodies
            // counting the same numbers, e.g. a D850's DSC_0001.NEF and a Z9's DSC_0001.MOV).
            MediaSide prefer = viaClipXml ? MediaSide.Video
                : f.Extension == ".xmp" && owners.Any(o => o.Side == MediaSide.Photo && MediaRules.RawPhotoExtensions.Contains(o.Extension))
                    && owners.Where(o => o.Side == MediaSide.Video).All(o => MediaRules.EmbeddedXmpVideoExtensions.Contains(o.Extension))
                    ? MediaSide.Photo
                : rule.Prefer;
            if (prefer != MediaSide.Neutral)
            {
                SourceFile owner = owners.First(o => o.Side == prefer);
                Follow(f, rule, owner);
                f.Reason += $" (not with the {Word(Other(prefer))} {string.Join(", ", owners.Where(o => o.Side != prefer).Select(o => o.Name))})";
                return;
            }
            Set(f, MediaSide.Neutral, FileRole.Sidecar,
                $"{rule.Kind} matches both a photo and a video ({string.Join(", ", owners.Select(o => o.Name))}) - stays", FileNote.Ambiguous);
            return;
        }
        if (rule.Unmatched != MediaSide.Neutral)
            Set(f, rule.Unmatched, FileRole.Sidecar, $"{rule.UnmatchedReason ?? rule.Kind} - goes with the {Word(rule.Unmatched)}s",
                MediaRules.IsAudio(f.Extension) ? FileNote.UnmatchedAudio : FileNote.None);
        else
            Set(f, MediaSide.Neutral, FileRole.Sidecar, $"{rule.Kind} with no matching photo or video here - stays");
    }

    /// <summary>
    /// A video in <paramref name="f"/>'s folder when <paramref name="f"/> is named after the clip that folder holds: the
    /// folder has videos and no photos, and <paramref name="f"/> has the folder's name or the name its frames are numbered
    /// after ("A001C001_150101_R1AB" for "A001C001_150101_R1AB.0000001.ari").
    /// </summary>
    private static SourceFile? ClipFolderVideo(SourceFile f, FolderIndex here, FolderTree tree)
    {
        if (here.Primaries.Count == 0 || here.Primaries.Any(p => p.Side != MediaSide.Video)) return null;
        string clip = Stem(f.Name);
        return clip.Equals(tree.FolderName(f.Directory), StringComparison.OrdinalIgnoreCase)
            ? here.Primaries[0]
            : here.Primaries.FirstOrDefault(p => FrameNumber(Stem(p.Name), clip) is not null);
    }

    /// <summary>
    /// A recorder take file follows the recordings in its folder when they all go the same way; else it stays. macOS "._"
    /// files and recordings that stay only because they cannot be moved (online-only, links) do not count.
    /// </summary>
    private static void ResolveRecorderProject(SourceFile f, FolderTree tree)
    {
        const string kind = "field recorder take file";
        var recordings = tree[f.Directory].Files.Where(r => MediaRules.IsAudio(r.Extension) && !IsAppleDouble(r.Name)
            && r.Note is not (FileNote.OnlineOnly or FileNote.Link)).ToList();
        var sides = recordings.Select(r => r.Side).Distinct().ToList();
        if (recordings.Count == 0)
            Set(f, MediaSide.Neutral, FileRole.Sidecar, $"{kind} with no recordings next to it - stays");
        else if (sides is [var side] && side != MediaSide.Neutral)
            Set(f, side, FileRole.Sidecar,
                $"{kind} for the recordings next to it ({recordings[0].Name}{(recordings.Count > 1 ? ", ..." : "")})" + Suffix(side));
        else
            Set(f, MediaSide.Neutral, FileRole.Sidecar, $"{kind} - the recordings next to it do not all go the same way - stays", FileNote.Ambiguous);
    }

    private static bool IsRecorderProject(SourceFile f) => MediaRules.RecorderProjectExtensions.Contains(f.Extension);

    private static void ResolveAppleDouble(SourceFile f, FolderTree tree)
    {
        SourceFile? owner = tree.Find(f.Directory, f.Name[2..]);
        if (owner is null)
            Set(f, MediaSide.Neutral, FileRole.Sidecar, "macOS resource file with no matching file - stays");
        else
        {
            Set(f, owner.Side, FileRole.Sidecar, $"macOS resource file for {owner.Name}" + Suffix(owner.Side));
            f.GroupKey = owner.GroupKey;
        }
    }

    private static void Follow(SourceFile f, SidecarRule rule, SourceFile owner)
    {
        Set(f, owner.Side, FileRole.Sidecar, $"{rule.Kind} for {owner.Name}" + Suffix(owner.Side));
        f.GroupKey = owner.GroupKey;
    }

    private static List<MediaUnit> Units(Dictionary<SourceFile, Unit> unitOf) =>
        unitOf.GroupBy(p => p.Value)
            .Select(g => new MediaUnit(g.Key.Kind, g.Key.Folder,
                g.Key.Marker.Length > 0 ? $"{g.Key.Description} ({g.Key.Marker})" : g.Key.Description, g.Count(), g.Sum(p => p.Key.Size))
            {
                MissingFrames = g.Key.MissingFrames,
            })
            .OrderBy(u => u.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Kind).ToList();

    /// <summary>Folders where the owner of a "name.EXT.sidecar" file may be: its own, and the media folder above a sidecar store.</summary>
    private static IEnumerable<string> OwnerFolders(string directory)
    {
        yield return directory;
        if (MediaRules.SidecarStoreFolders.Contains(Path.GetFileName(directory)))
            yield return Path.GetDirectoryName(directory) ?? "";
        // Capture One keeps settings and caches in CaptureOne\Settings123 and CaptureOne\Cache\... next to the photos.
        string[] segments = directory.Split('\\');
        int c1 = Array.FindLastIndex(segments, s => s.Equals("CaptureOne", StringComparison.OrdinalIgnoreCase));
        if (c1 >= 0) yield return string.Join('\\', segments[..c1]);
    }

    /// <summary>
    /// The side a photo or video that stays anyway (online-only, a link) belongs to: its card structure's or clip's side,
    /// else its type's. Neutral for companions and other files, and for a short clip named like a Live Photo still next to
    /// it: it may be the photo's Live Photo clip or a video, and only reading it (downloading it) would tell.
    /// </summary>
    private static MediaSide NaturalSide(SourceFile f, FolderFacts facts, FolderIndex folder) =>
        facts.Structure is { } structure && structure.Includes(f) || facts.Sequence is { } clip && clip.Includes(f) ? MediaSide.Video
        : MayBeLivePhotoClip(f, folder) ? MediaSide.Neutral
        : MediaRules.PrimarySide(f.Extension);

    /// <summary>
    /// Whether <paramref name="f"/> is a clip that could be a Live Photo clip by its name, type and size (a still of the
    /// same name is in its folder, on disk or online-only); only its content can tell for sure.
    /// </summary>
    private static bool MayBeLivePhotoClip(SourceFile f, FolderIndex folder) =>
        MediaRules.LivePhotoClipExtensions.Contains(f.Extension) && f.Size <= MediaRules.LivePhotoMaxClipBytes
        && folder.Files.Any(s => s != f && Stem(s.Name).Equals(Stem(f.Name), StringComparison.OrdinalIgnoreCase)
            && MediaRules.LivePhotoExtensions.Contains(s.Extension)
            && (s.Extension is ".heic" or ".heif" || s.Name.StartsWith(MediaRules.LivePhotoJpegPrefix, StringComparison.OrdinalIgnoreCase)));

    private static bool IsLivePhotoStill(SourceFile p) =>
        p.Side == MediaSide.Photo && MediaRules.LivePhotoExtensions.Contains(p.Extension)
        && (p.Extension is ".heic" or ".heif" || p.Name.StartsWith(MediaRules.LivePhotoJpegPrefix, StringComparison.OrdinalIgnoreCase));

    private static string PrimaryKey(SourceFile f) => Path.Join(f.Directory, Stem(f.Name));

    private static string Suffix(MediaSide side) => side == MediaSide.Neutral ? " - stays" : $" - goes with the {Word(side)}s";

    private static string Word(MediaSide side) => side == MediaSide.Video ? "video" : "photo";

    private static MediaSide Other(MediaSide side) => side == MediaSide.Video ? MediaSide.Photo : MediaSide.Video;

    private static bool IsAppleDouble(string name) => name.StartsWith("._", StringComparison.Ordinal) && name.Length > 2;

    private static string Stem(string name) => Path.GetFileNameWithoutExtension(name);

    private static string ExtLabel(SourceFile f) => f.Extension.Length == 0 ? "no extension" : f.Extension.ToUpperInvariant();

    private static bool Set(SourceFile f, MediaSide side, FileRole role, string reason, FileNote note = FileNote.None)
    {
        f.Side = side;
        f.Role = role;
        f.Reason = reason;
        f.Note = note;
        return false;
    }

    [GeneratedRegex(@"^(.+)M\d\d$", RegexOptions.IgnoreCase)]
    private static partial Regex SonyClipXml();

    /// <summary>"&lt;name&gt;&lt;separator&gt;&lt;frame number&gt;": the frame part of an image-sequence file name.</summary>
    [GeneratedRegex(@"^[_.\- ]?(\d{5,9})$")]
    private static partial Regex FrameSuffix();

    /// <summary>The name part of "&lt;name&gt;&lt;separator&gt;&lt;frame number&gt;" (the frame part as in <see cref="FrameSuffix"/>).</summary>
    [GeneratedRegex(@"^(.+?)[_.\- ]?\d{5,9}$")]
    private static partial Regex FramePrefix();

    /// <summary>DJI names hyperlapse source frames and their folders HYPERLAPSE_0001, HYPERLAPSE_0002, ...</summary>
    [GeneratedRegex(@"^HYPERLAPSE_\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex HyperlapseName();

    [GeneratedRegex(@"^(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex FolderNumber();

    /// <summary>The frame number when <paramref name="stem"/> is "&lt;clip&gt;&lt;separator&gt;&lt;5-9 digits&gt;", else null.</summary>
    private static long? FrameNumber(string stem, string clip) =>
        stem.Length > clip.Length && stem.StartsWith(clip, StringComparison.OrdinalIgnoreCase)
            && FrameSuffix().Match(stem[clip.Length..]) is { Success: true } m ? long.Parse(m.Groups[1].Value) : null;

    private static bool IsFrame(SourceFile f, string clip) =>
        MediaRules.SequenceFrameExtensions.Contains(f.Extension) && FrameNumber(Stem(f.Name), clip) is not null;

    /// <summary>Whether <paramref name="f"/> is a numbered frame (DNG/DPX/EXR) named "&lt;clip&gt;&lt;separator&gt;&lt;frame number&gt;".</summary>
    internal static bool IsFrameOf(SourceFile f, string clip) => IsFrame(f, clip);

    // ---- Folder facts ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A folder treated as one unit. <see cref="Marker"/> is the file (or folder) that gave it away (project or mission);
    /// <see cref="Clips"/> are the frame name prefixes of an image-sequence clip, <see cref="MissingFrames"/> the frame
    /// numbers missing in them.
    /// </summary>
    private sealed record Unit(UnitKind Kind, string Folder, string Description, string Marker = "", IReadOnlyList<string>? Clips = null,
        int MissingFrames = 0, IReadOnlySet<string>? Members = null)
    {
        /// <summary>
        /// The group its files move in together: the folder, or "*" (never a file or folder name) for the source folder
        /// itself, since an empty group key means no group.
        /// </summary>
        public string GroupKey => Folder.Length > 0 ? Folder : "*";

        /// <summary>
        /// Whether a file in the unit's folder tree belongs to it. Photos that are not frames of an image-sequence clip keep
        /// their own side, and so does every file next to the folders of a Sony card copied without its wrapper except the
        /// card's index files (<see cref="CardStructures.SonyCardRootFiles"/>). When
        /// <see cref="Members"/> is set (a clip found by its CinemaDNG tags alone, in a folder that may hold other things),
        /// only those files and videos join it besides the frames; everything else is classified on its own.
        /// </summary>
        public bool Includes(SourceFile f)
        {
            // Next to the folders of a Sony card copied without its wrapper, only the card's own index files belong to it:
            // other files there (a loose clip from another camera, a photo and its .xmp) keep their own rules.
            if (Kind == UnitKind.CardStructure && Description == CardStructures.UnwrappedSonyDescription
                && f.Directory.Equals(Folder, StringComparison.OrdinalIgnoreCase))
                return CardStructures.SonyCardRootFiles.Contains(f.Name);
            MediaSide side = MediaRules.PrimarySide(f.Extension);
            if (side != MediaSide.Photo) return Members is null || side == MediaSide.Video || Members.Contains(f.Name);
            return Kind == UnitKind.ImageSequence ? Clips?.Any(c => IsFrame(f, c)) == true : true;
        }
    }

    private sealed record FolderFacts(Unit? Project, Unit? Structure, Unit? Sequence, Unit? Mission, Hyperlapse? Hyperlapse);

    /// <summary>
    /// A folder of DJI hyperlapse source frames: its HYPERLAPSE_0001.JPG, ... frames (or every photo, in a folder named
    /// HYPERLAPSE_0005) are photos that are kept together.
    /// </summary>
    private sealed record Hyperlapse(string Folder, string Name, bool NamedFolder, int Frames)
    {
        public bool Includes(SourceFile f) => NamedFolder || HyperlapseName().IsMatch(Stem(f.Name));
    }

    private sealed class FolderIndex
    {
        public readonly Dictionary<string, SourceFile> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<SourceFile> Files = [];
        public readonly List<SourceFile> Primaries = [];
        private readonly Dictionary<string, List<SourceFile>> _byStem = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string>? _videoStems;

        public void Add(SourceFile f)
        {
            ByName[f.Name] = f;
            Files.Add(f);
        }

        public void AddPrimary(SourceFile f)
        {
            Primaries.Add(f);
            string stem = Stem(f.Name);
            if (!_byStem.TryGetValue(stem, out List<SourceFile>? list)) _byStem[stem] = list = [];
            list.Add(f);
        }

        public IEnumerable<SourceFile> PrimariesWithStem(string stem) =>
            _byStem.TryGetValue(stem, out List<SourceFile>? list) ? list : [];

        /// <summary>Whether a video type file with this name stem is in the folder (judged by extension alone).</summary>
        public bool HasVideoStem(string stem)
        {
            _videoStems ??= Files.Where(f => MediaRules.VideoExtensions.Contains(f.Extension))
                .Select(f => Stem(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _videoStems.Contains(stem);
        }
    }

    /// <summary>The scanned folders with their files, and what each folder turns out to be.</summary>
    private sealed class FolderTree
    {
        private static readonly FolderIndex Empty = new();
        private readonly Dictionary<string, FolderIndex> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FolderFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Unit?> _structureAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Unit?> _projectAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<SourceFile, bool> _movieFrame = [];
        private readonly string _sourceName;
        private readonly Func<SourceFile, bool>? _isMovieFrame;

        public FolderTree(IReadOnlyList<SourceFile> files, IEnumerable<string>? folders, string sourceName, Func<SourceFile, bool>? isMovieFrame)
        {
            _sourceName = sourceName;
            _isMovieFrame = isMovieFrame;
            foreach (SourceFile f in files)
            {
                if (!_index.TryGetValue(f.Directory, out FolderIndex? index))
                    _index[f.Directory] = index = new FolderIndex();
                index.Add(f);
                AddFolder(f.Directory);
            }
            if (folders is not null)
                foreach (string folder in folders) AddFolder(folder);
        }

        public IEnumerable<FolderIndex> Folders => _index.Values;

        public FolderIndex this[string directory] => _index.TryGetValue(directory, out FolderIndex? index) ? index : Empty;

        public SourceFile? Find(string directory, string name) => this[directory].ByName.GetValueOrDefault(name);

        public FolderFacts Facts(string directory)
        {
            if (_facts.TryGetValue(directory, out FolderFacts? facts)) return facts;
            facts = new FolderFacts(FindProject(directory), FindStructure(directory), FindSequence(directory), FindMission(directory),
                FindHyperlapse(directory));
            _facts[directory] = facts;
            return facts;
        }

        /// <summary>The P2 CONTENTS structure next to a file in <paramref name="directory"/> (for LASTCLIP.TXT).</summary>
        public Unit? P2ContentsIn(string directory)
        {
            string contents = Path.Join(directory, "CONTENTS");
            return StructureAt(contents) is { Description: "Panasonic P2 card structure" } p2 ? p2 : null;
        }

        /// <summary>DJI keeps THM/SCR files in MISC\THM\100, apart from the clip in DCIM\100MEDIA: finds that clip.</summary>
        public SourceFile? DjiThumbnailOwner(SourceFile f)
        {
            string[] segments = f.Directory.Split('\\');
            if (segments.Length < 3 || !segments[^2].Equals("THM", StringComparison.OrdinalIgnoreCase)
                || !segments[^3].Equals("MISC", StringComparison.OrdinalIgnoreCase)
                || FolderNumber().Match(segments[^1]) is not { Success: true } number)
                return null;
            string dcim = Path.Join(string.Join('\\', segments[..^3]), "DCIM");
            string stem = Stem(f.Name);
            return ChildFolders(dcim)
                .Where(c => c.StartsWith(number.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => this[Path.Join(dcim, c)].PrimariesWithStem(stem))
                .FirstOrDefault(p => p.Side == MediaSide.Video);
        }

        private void AddFolder(string folder)
        {
            for (string? d = folder; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                string parent = Path.GetDirectoryName(d) ?? "";
                if (!_children.TryGetValue(parent, out HashSet<string>? set))
                    _children[parent] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!set.Add(Path.GetFileName(d))) break; // this folder and its parents are known already
            }
        }

        private IReadOnlyCollection<string> ChildFolders(string folder) =>
            _children.TryGetValue(folder, out HashSet<string>? set) ? set : [];

        private string NameOf(string directory) => directory.Length == 0 ? _sourceName : Path.GetFileName(directory);

        /// <summary>The folder's own name (the source folder's for the source folder itself).</summary>
        public string FolderName(string directory) => NameOf(directory);

        /// <summary>
        /// The outermost folder at or above <paramref name="directory"/> that directly holds a project file, or that is a
        /// processing project by its layout (<see cref="ProcessingProjects"/>).
        /// </summary>
        private Unit? FindProject(string directory)
        {
            foreach (string folder in SelfAndParents(directory, includeRoot: true).Reverse())
                if (ProjectAt(folder) is { } unit)
                    return unit;
            return null;
        }

        private Unit? ProjectAt(string folder)
        {
            if (_projectAt.TryGetValue(folder, out Unit? unit)) return unit;
            string? marker = this[folder].Files.Select(f => f.Name)
                .Concat(ChildFolders(folder)) // a project can be a folder (a package) on Windows
                .Where(n => MediaRules.ProjectMarkers.ContainsKey(Path.GetExtension(n)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (marker is not null)
                unit = new Unit(UnitKind.Project, folder, MediaRules.ProjectMarkers[Path.GetExtension(marker)], marker);
            else if (ProcessingProjects.Describe(relative => ChildFolders(Path.Join(folder, relative)),
                         relative => Find(Path.Join(folder, Path.GetDirectoryName(relative)), Path.GetFileName(relative)) is not null) is { } layout)
                unit = new Unit(UnitKind.Project, folder, layout.Description, layout.Evidence);
            _projectAt[folder] = unit;
            return unit;
        }

        /// <summary>
        /// The outermost video card structure at or above <paramref name="directory"/> that the folder belongs to (see
        /// <see cref="CardStructures.Owns"/>). The source folder itself is judged only by what it holds (its name is not
        /// looked at), so it can only be a Sony card copied without its wrapper; a source picked inside a structure (M4ROOT,
        /// CONTENTS, ...) is refused by the Planner.
        /// </summary>
        private Unit? FindStructure(string directory)
        {
            foreach (string folder in SelfAndParents(directory, includeRoot: true).Reverse())
                if (StructureAt(folder) is { } unit
                    && (folder.Length == directory.Length
                        || CardStructures.Owns(unit.Description, (folder.Length == 0 ? directory : directory[(folder.Length + 1)..]).Split('\\')[0])))
                    return unit;
            return null;
        }

        private Unit? StructureAt(string folder)
        {
            if (_structureAt.TryGetValue(folder, out Unit? unit)) return unit;
            string? what = CardStructures.Describe(Path.GetFileName(folder), ChildFolders(folder), n => this[folder].ByName.ContainsKey(n));
            unit = what is null ? null : new Unit(UnitKind.CardStructure, folder, what);
            _structureAt[folder] = unit;
            return unit;
        }

        /// <summary>
        /// A folder of at least 10 frames (DNG/DPX/EXR) numbered without gaps and named after the folder (or after a sound
        /// file next to them) is one clip. The name rule keeps photogrammetry runs (DJI_0001..0900.DNG in 100MEDIA) on the
        /// photo side. DNG photos can still be named like that (a sequence rename into a folder of the same name, a phone's
        /// date folder), so a DNG clip also needs its sound file named after it, or CinemaDNG movie tags in a frame.
        /// Frames with CinemaDNG movie tags are a clip even when that rule does not hold (<see cref="TaggedSequence"/>).
        /// </summary>
        private Unit? FindSequence(string directory)
        {
            var frames = this[directory].Files.Where(f => MediaRules.SequenceFrameExtensions.Contains(f.Extension)).ToList();
            if (frames.Count == 0) return null;
            return NamedSequence(directory, frames) ?? TaggedSequence(directory, frames);
        }

        private Unit? NamedSequence(string directory, List<SourceFile> frames)
        {
            if (frames.Count < MediaRules.SequenceMinimumFrames) return null;
            FolderIndex index = this[directory];
            var sounds = index.Files.Where(f => MediaRules.IsAudio(f.Extension)).Select(f => Stem(f.Name)).ToList();
            var candidates = sounds.Prepend(NameOf(directory)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => (Name: n, Frames: frames.Where(f => IsFrame(f, n)).ToList()))
                .Where(c => c.Frames.Count >= MediaRules.SequenceMinimumFrames)
                .OrderByDescending(c => c.Frames.Count);
            foreach ((string name, List<SourceFile> clip) in candidates)
            {
                var numbers = clip.Select(f => FrameNumber(Stem(f.Name), name)!.Value).Distinct().ToList();
                if (numbers.Count != clip.Count || numbers.Max() - numbers.Min() + 1 != clip.Count) continue; // gaps: not one recording
                bool dng = clip.Any(f => f.Extension == ".dng");
                if (dng && !sounds.Contains(name, StringComparer.OrdinalIgnoreCase) && !HasMovieTags(clip)) continue;
                return new Unit(UnitKind.ImageSequence, directory,
                    $"{(dng ? "CinemaDNG clip" : "image-sequence clip")} {name} ({clip.Count:N0} frames)", Clips: [name]);
            }
            return null;
        }

        /// <summary>
        /// CinemaDNG frames say themselves that they belong to a movie. Every run of DNG frames whose first frame carries
        /// the movie tags is a clip, whatever its length, gaps or name (a renamed clip folder, a drone camera without
        /// sound, a lost frame), as long as fewer frames are missing than are there; missing frames are counted so the
        /// preview can warn. Untagged runs keep the stricter rule in <see cref="NamedSequence"/>, which is what keeps
        /// photogrammetry and renamed photo DNGs on the photo side.
        /// Such a clip may sit in a folder with other things (frame grabs next to stills and their edits), so it takes in
        /// only its frames, its sound (named after it, or the folder's only sound file) and companions named after it;
        /// other files, such as a photo's .xmp, are classified on their own.
        /// </summary>
        private Unit? TaggedSequence(string directory, List<SourceFile> frames)
        {
            if (_isMovieFrame is null) return null;
            var runs = frames.Where(f => f.Extension == ".dng")
                .Select(f => (File: f, Match: FramePrefix().Match(Stem(f.Name))))
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups[1].Value, x => x.File, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= MediaRules.TaggedSequenceMinimumFrames)
                .Select(g => (Name: g.Key, Frames: g.ToList()))
                .Where(r => MissingFrames(r.Name, r.Frames) < r.Frames.Count) // frame grabs far apart are stills
                .Where(r => HasMovieTags(r.Frames))
                .OrderByDescending(r => r.Frames.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (runs.Count == 0) return null;

            var clips = runs.Select(r => r.Name).ToList();
            FolderIndex index = this[directory];
            var photoStems = index.Files
                .Where(f => MediaRules.PrimarySide(f.Extension) == MediaSide.Photo && !clips.Any(c => IsFrame(f, c)))
                .Select(f => Stem(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int sounds = index.Files.Count(f => MediaRules.IsAudio(f.Extension));
            var members = index.Files
                .Where(f => !photoStems.Contains(Stem(f.Name))
                    && (MediaRules.IsAudio(f.Extension) && (sounds == 1 || clips.Contains(Stem(f.Name), StringComparer.OrdinalIgnoreCase))
                        || MediaRules.Sidecars.ContainsKey(f.Extension) && clips.Contains(Stem(f.Name), StringComparer.OrdinalIgnoreCase)))
                .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            int count = runs.Sum(r => r.Frames.Count);
            int missing = runs.Sum(r => MissingFrames(r.Name, r.Frames));
            string description = $"CinemaDNG clip{(runs.Count == 1 ? "" : "s")} {string.Join(", ", clips)} "
                + $"({count:N0} frames{(missing > 0 ? $", {missing:N0} missing" : "")})";
            return new Unit(UnitKind.ImageSequence, directory, description, Clips: clips, MissingFrames: missing, Members: members);
        }

        /// <summary>Frame numbers between the first and last frame of a run that are not there.</summary>
        private static int MissingFrames(string clip, List<SourceFile> frames)
        {
            var numbers = frames.Select(f => FrameNumber(Stem(f.Name), clip)).OfType<long>().Distinct().ToList();
            return numbers.Count == 0 ? 0 : (int)Math.Min(int.MaxValue, numbers.Max() - numbers.Min() + 1 - numbers.Count);
        }

        /// <summary>Reads one frame that is on the disk (not an online-only placeholder) for the CinemaDNG movie tags.</summary>
        private bool HasMovieTags(List<SourceFile> clip)
        {
            if (_isMovieFrame is null
                || clip.Where(f => f.Extension == ".dng" && (f.Attributes & (OnlineOnlyAttributes | FileAttributes.ReparsePoint)) == 0)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is not { } frame)
                return false;
            if (!_movieFrame.TryGetValue(frame, out bool tagged))
                _movieFrame[frame] = tagged = _isMovieFrame(frame); // read each frame at most once
            return tagged;
        }

        /// <summary>A folder of DJI hyperlapse source frames (see <see cref="Hyperlapse"/>).</summary>
        private Hyperlapse? FindHyperlapse(string directory)
        {
            string name = NameOf(directory);
            bool namedFolder = HyperlapseName().IsMatch(name);
            var frames = this[directory].Files.Where(f => MediaRules.PhotoExtensions.Contains(f.Extension)
                    && (namedFolder || HyperlapseName().IsMatch(Stem(f.Name))))
                .Select(f => Stem(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            // Two frames at least: one photo called HYPERLAPSE_0001 alone is just a photo.
            return frames >= 2 ? new Hyperlapse(directory, name.Length > 0 ? name : "(source folder)", namedFolder, frames) : null;
        }

        /// <summary>A folder with photos and a DJI timestamp file (.MRK), or with DJI LiDAR raw data, is a mapping mission.</summary>
        private Unit? FindMission(string directory)
        {
            FolderIndex index = this[directory];
            if (index.Files.Count == 0) return null;
            string? mrk = index.Files.FirstOrDefault(f => f.Extension == ".mrk" && !f.Name.Equals("AUTPRINT.MRK", StringComparison.OrdinalIgnoreCase))?.Name;
            if (mrk is not null && index.Files.Any(f => MediaRules.PhotoExtensions.Contains(f.Extension)))
                return new Unit(UnitKind.Mission, directory, "DJI mapping mission", mrk);
            string? lidar = index.Files.FirstOrDefault(f => MediaRules.LidarExtensions.Contains(f.Extension))?.Name;
            if (lidar is not null && index.Files.Any(f => MediaRules.LidarCompanionExtensions.Contains(f.Extension)))
                return new Unit(UnitKind.Mission, directory, "DJI LiDAR mission", lidar);
            return null;
        }

        /// <summary>The folder and its parents, innermost first.</summary>
        private static IEnumerable<string> SelfAndParents(string directory, bool includeRoot)
        {
            for (string? d = directory; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) yield return d;
            if (includeRoot) yield return "";
        }
    }
}
