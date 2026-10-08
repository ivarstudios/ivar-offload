namespace IvarOffload.Core.Sorting;

public enum FileRole { Primary, Sidecar, Other }

/// <summary>Something the user should look at in the preview, or a rule worth naming. New values are only ever added at the end.</summary>
public enum FileNote
{
    None,
    UnknownType,
    Ambiguous,
    Link,
    OnlineOnly,
    SystemFile,
    LeftoverTemp,
    /// <summary>Audio with no photo or video of the same name: recorder / dual-system sound, so it goes with the videos.</summary>
    UnmatchedAudio,
    /// <summary>An unrecognized type that has the same name as a video next to it, so it goes with that video.</summary>
    FollowsByName,
    /// <summary>A short iPhone Live Photo clip that belongs to its photo.</summary>
    LivePhoto,
    /// <summary>A video with the same name as a photo, but too large to be a Live Photo clip: treated as a video.</summary>
    LivePhotoTooLarge,
    /// <summary>Inside an editing or processing project folder: stays so the project keeps its media.</summary>
    InProject,
    /// <summary>A DIFFERENT file with the same name is already in the target: stays in the source.</summary>
    DifferentInTarget,
    /// <summary>Stays because another file of its clip (or card structure) clashes with a different file in the target.</summary>
    HeldWithGroup,
    /// <summary>
    /// A short video with the same name as a photo, but without Apple's Live Photo tag: a real video (another camera that
    /// also counts IMG_0001, ...), treated as a video.
    /// </summary>
    NotLivePhoto,
    /// <summary>GNSS base-station data or a ground control point list outside a mapping mission folder: stays.</summary>
    SurveyData,
    /// <summary>A source frame of a DJI hyperlapse: a photo, kept together with the other frames of its hyperlapse.</summary>
    HyperlapseFrame,
}

/// <summary>Plain words and "needs a look" flags for <see cref="FileNote"/>, shared by the GUI, the CLI and the reports.</summary>
public static class FileNotes
{
    public static string Describe(FileNote note) => note switch
    {
        FileNote.UnknownType => "Unrecognized type",
        FileNote.Ambiguous => "Matches both a photo and a video",
        FileNote.Link => "Link - not followed",
        FileNote.OnlineOnly => "Online-only file",
        FileNote.SystemFile => "System file",
        FileNote.LeftoverTemp => "Unfinished temporary copy",
        FileNote.UnmatchedAudio => "Audio recording - goes with the videos",
        FileNote.FollowsByName => "Unrecognized type - follows the video with its name",
        FileNote.LivePhoto => "Live Photo clip",
        FileNote.LivePhotoTooLarge => "Video - too large for a Live Photo clip",
        FileNote.InProject => "Inside an editing/processing project",
        FileNote.DifferentInTarget => "DIFFERENT file with the same name in the target folder - stays",
        FileNote.HeldWithGroup => "Stays with its clip (name clash in the target folder)",
        FileNote.NotLivePhoto => "Video - same name as a photo, but not a Live Photo clip",
        FileNote.SurveyData => "Survey data (GNSS base station or ground control points)",
        FileNote.HyperlapseFrame => "Hyperlapse frame",
        _ => "",
    };

    /// <summary>Notes the user should check before confirming (the "needs a look" count).</summary>
    public static bool NeedsAttention(FileNote note) =>
        note is FileNote.UnknownType or FileNote.Ambiguous or FileNote.OnlineOnly or FileNote.FollowsByName
            or FileNote.DifferentInTarget or FileNote.HeldWithGroup or FileNote.NotLivePhoto;
}

/// <summary>A file found under the source folder, with its classification.</summary>
public sealed class SourceFile
{
    public required string RelativePath { get; init; }
    public required long Size { get; init; }
    /// <summary>UTC FILETIME ticks.</summary>
    public required long CreationTime { get; init; }
    /// <summary>UTC FILETIME ticks.</summary>
    public required long LastWriteTime { get; init; }
    public required FileAttributes Attributes { get; init; }

    public MediaSide Side { get; set; }
    public FileRole Role { get; set; }
    public FileNote Note { get; set; }
    /// <summary>Why the file was classified this way, in words shown to the user.</summary>
    public string Reason { get; set; } = "";
    /// <summary>
    /// Files that must stay together (a clip with its proxies, telemetry and sidecars, or a whole card structure).
    /// Compared case-insensitively. Empty for files that belong to no group.
    /// </summary>
    public string GroupKey { get; set; } = "";
    /// <summary>
    /// For a photo or video that stays although its type would move it (an online-only placeholder that is not
    /// downloaded, a link, a file inside an editing project): the side it belongs to. Neutral for every other file, and
    /// for an online-only or linked short clip named like a Live Photo still, which may belong to either side.
    /// </summary>
    public MediaSide NaturalSide { get; set; }

    /// <summary>The classifier's note and reason, so a plan built again from the same scan starts from them.</summary>
    internal (FileNote Note, string Reason)? Classified { get; set; }

    public string Name => Path.GetFileName(RelativePath);
    public string Directory => Path.GetDirectoryName(RelativePath) ?? "";
    public string Extension => Path.GetExtension(RelativePath).ToLowerInvariant();
    public DateTime LastWriteUtc => DateTime.FromFileTimeUtc(LastWriteTime);
    public bool NeedsAttention => FileNotes.NeedsAttention(Note);

    /// <summary>Undoes what an earlier plan added (for example "stays with its clip").</summary>
    internal void RestoreClassification()
    {
        if (Classified is { } c) (Note, Reason) = c;
    }

    public override string ToString() => $"{RelativePath} [{Side}/{Role}] {Reason}";
}

/// <summary>Original timestamps of a source folder (UTC FILETIME ticks), used to date the mirrored target folder.</summary>
public readonly record struct FolderTimes(long CreationTime, long LastWriteTime);

public sealed record SkippedFolder(string RelativePath, string Reason)
{
    /// <summary>The kind of application library ("Apple Photos library") when the folder is one; null for other skipped folders.</summary>
    public string? LibraryKind { get; init; }
}

public enum UnitKind
{
    /// <summary>A video card structure (Sony M4ROOT, P2 CONTENTS, ...): everything in it moves as one unit with the videos.</summary>
    CardStructure,
    /// <summary>A CinemaDNG or other image-sequence clip folder: one video.</summary>
    ImageSequence,
    /// <summary>A DJI mapping or LiDAR mission folder: kept whole with the photos.</summary>
    Mission,
    /// <summary>An editing or processing project folder: everything in it stays.</summary>
    Project,
}

/// <summary>A folder the classifier treated as one unit instead of file by file.</summary>
/// <param name="Folder">Relative path of the unit's folder ("" for the source folder itself).</param>
/// <param name="Description">Plain words, e.g. "Sony XAVC card structure" or "CinemaDNG clip A001_C003 (240 frames)".</param>
public sealed record MediaUnit(UnitKind Kind, string Folder, string Description, int Files, long Bytes)
{
    /// <summary>For an image-sequence clip: frame numbers missing between its first and last frame.</summary>
    public int MissingFrames { get; init; }
}
