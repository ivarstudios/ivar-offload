using System.IO;
using System.Text.RegularExpressions;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>The "needs a look" tile: how many files, and a short breakdown.</summary>
public sealed record Attention(int Files, int Libraries, IReadOnlyList<string> Parts)
{
    public bool Any => Files + Libraries > 0;
    public string CountText => Files > 0 ? RunOutcome.Files(Files) : Libraries > 0 ? RunOutcome.Count(Libraries, "library", "libraries") : "Nothing";
    public string Detail => Parts.Count == 0 ? "No conflicts or unrecognized files" : string.Join(", ", Parts);
}

/// <summary>What the preview says about a plan, in plain words.</summary>
public static partial class PlanFacts
{
    /// <summary>How the Planner's warning about earlier sorts from another folder (or in the other mode) starts.</summary>
    public const string OtherJobsWarningStart = "This target folder already holds files that an earlier sort moved from";

    public const string ClashReason = SourceCheck.ClashReason;

    /// <summary>The files the job will really move (without the identical copies it skips), and how many are photos/videos.</summary>
    public static (int Files, int Primaries, long Bytes) Transfer(MovePlan plan)
    {
        var skipped = new HashSet<SourceFile>(plan.Conflicts);
        var moving = plan.ToMove.Where(f => !skipped.Contains(f)).ToList();
        return (moving.Count, MediaRules.CountPrimaries(moving.Select(f => f.RelativePath), plan.Mode), moving.Sum(f => f.Size));
    }

    /// <summary>"Move 412 videos", "Move 100 videos (400 files)"; after a job, "Move the remaining 17 videos".</summary>
    public static string ConfirmText(MovePlan plan, bool remaining = false)
    {
        (int files, int primaries, _) = Transfer(plan);
        if (files == 0) return $"Move {Planner.Word(plan.Mode)}";
        return (remaining ? "Move the remaining " : "Move ") + RunOutcome.Things(files, primaries, plan.Mode);
    }

    /// <summary>The bold line above Confirm: "Moving videos to: D:\Clients\Smith\Video".</summary>
    public static string Destination(MovePlan plan) => $"The {Planner.Word(plan.Mode)} will move to: {plan.TargetRoot}";

    /// <summary>The target already holds files sorted from another folder or in the other mode (the Planner warns about it).</summary>
    public static bool TargetHoldsOtherJobs(MovePlan plan) =>
        plan.Messages.Any(m => m.Level == MessageLevel.Warning && m.Text.StartsWith(OtherJobsWarningStart, StringComparison.Ordinal));

    /// <summary>A file the user should look at before confirming (counted in the "needs a look" tile).</summary>
    public static bool NeedsLook(SourceFile f, ISet<SourceFile> identical) =>
        identical.Contains(f) || f.NeedsAttention || f.Note is FileNote.InProject or FileNote.LivePhotoTooLarge;

    public static Attention Attention(MovePlan plan)
    {
        var identical = new HashSet<SourceFile>(plan.IdenticalConflicts);
        var all = plan.ToMove.Concat(plan.Staying).ToList();
        int Notes(FileNote note) => all.Count(f => f.Note == note && !identical.Contains(f));
        var parts = new List<string>();
        void Part(int count, string one, string? many = null)
        {
            if (count > 0) parts.Add($"{count:N0} {(count == 1 ? one : many ?? one)}");
        }
        Part(identical.Count, "already in the target folder (skipped)");
        Part(Notes(FileNote.DifferentInTarget), "with a DIFFERENT file of the same name in the target folder");
        Part(Notes(FileNote.HeldWithGroup), "that stays with its clip", "that stay with their clip");
        Part(Notes(FileNote.UnknownType), "of an unknown type (it stays)", "of an unknown type (they stay)");
        Part(Notes(FileNote.FollowsByName), "of an unknown type named like a video (it moves with that video)",
            "of an unknown type named like a video (they move with that video)");
        Part(Notes(FileNote.Ambiguous), "that matches both a photo and a video", "that match both a photo and a video");
        Part(Notes(FileNote.OnlineOnly), "online-only");
        Part(Notes(FileNote.InProject), "inside an editing project");
        Part(Notes(FileNote.LivePhotoTooLarge), "too large for a Live Photo clip (it counts as a video)", "too large for a Live Photo clip (they count as videos)");
        Part(Notes(FileNote.NotLivePhoto), "named like a photo but not a Live Photo clip (it counts as a video)",
            "named like a photo but not a Live Photo clip (they count as videos)");
        int libraries = plan.Scan.SkippedFolders.Count(s => s.LibraryKind is not null);
        if (libraries > 0) parts.Add($"{RunOutcome.Count(libraries, "application library", "application libraries")} not scanned");
        return new Attention(all.Count(f => NeedsLook(f, identical)), libraries, parts);
    }

    /// <summary>
    /// The target suggested for a source: the sibling folder "Card1-Video" / "Card1-Photos", or for a drive root (for
    /// example a camera SSD) a folder inside it, "E:\Video" / "E:\Photos". Null when there is no source yet.
    /// </summary>
    public static string? SuggestTarget(string source, MoveMode mode)
    {
        string s = source.Trim().Trim('"');
        if (s.Length == 0) return null;
        string name = mode == MoveMode.Videos ? "Video" : "Photos";
        string? root;
        try
        {
            root = Path.GetPathRoot(s);
        }
        catch (ArgumentException)
        {
            return null;
        }
        string folder = s.TrimEnd('\\', '/');
        if (root is { Length: > 0 } && (folder.Length == 0 || string.Equals(folder, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            return Path.Join(root.TrimEnd('\\', '/') + "\\", name);
        return Path.GetFileName(folder).Length == 0 ? null : $"{folder}-{name}";
    }

    // ---- A memory card or camera drive ------------------------------------------------------------------------------

    /// <summary>The source is flagged as a memory card or camera drive and the target is on it too: nothing leaves it.</summary>
    public static bool DestinationOnCard(MovePlan plan) => plan.MemoryCard is not null && !plan.HasErrors && plan.Method == TransferMethod.Rename;

    /// <summary>
    /// The memory-card warning, shown next to Confirm (always in view, however short the window) instead of among the
    /// messages that may be scrolled away. Null when the source is not flagged.
    /// </summary>
    public static string? CardLine(MovePlan plan) => plan.MemoryCard is not { } card ? null
        : SourceGuards.MemoryCardWarning(card) + (DestinationOnCard(plan) ? " The target folder is on it too, so nothing leaves the card." : "");

    /// <summary>The preview's messages, without the memory-card warning (<see cref="CardLine"/> shows it).</summary>
    public static IReadOnlyList<PlanMessage> ShownMessages(MovePlan plan) => plan.MemoryCard is { } card
        ? plan.Messages.Where(m => m.Text != SourceGuards.MemoryCardWarning(card)).ToList()
        : plan.Messages;

    /// <summary>Why the destination line above Confirm is amber (its tooltip); null when it is not.</summary>
    public static string? DestinationWarning(MovePlan plan) =>
        DestinationOnCard(plan) ? "The target folder is on the memory card or camera drive itself: nothing leaves it."
        : TargetHoldsOtherJobs(plan) ? "This target folder already contains files from a different sort. For details, see the messages above."
        : null;

    /// <summary>
    /// Empty folders of a memory card worth offering for removal: not the camera's own folders (DCIM\100MSDCF,
    /// PRIVATE\M4ROOT, AVCHD, CONTENTS, RED .RDM and .RDC, ARRI reels such as A016R1K4, ...), which the camera expects to
    /// find. <paramref name="folders"/> are relative to <paramref name="source"/>, which may be a folder on the card (for
    /// example F:\DCIM) or hold a card's folders (a camera drive sorted from a subfolder): each is judged both by its path
    /// from the source and by its path from the drive's root, and left out when either looks like the camera's.
    /// </summary>
    public static List<string> WithoutCardFolders(IEnumerable<string> folders, string source) =>
        folders.Where(rel => !IsCardPath(rel) && !IsCardPath(FromVolumeRoot(source, rel))).ToList();

    private static bool IsCardPath(string path)
    {
        string[] parts = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || SourceGuards.CardRootFolders.Contains(parts[0]) || parts.Any(IsCameraFolder);
    }

    /// <summary>A folder name only cameras use inside a card structure (whatever folder holds it).</summary>
    private static bool IsCameraFolder(string name) =>
        MediaRules.VideoStructureFolders.ContainsKey(name)
        || name.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".RDM", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".RDC", StringComparison.OrdinalIgnoreCase)
        || ArriReel().IsMatch(name);

    /// <summary>"DCIM\100MSDCF" for the folder "100MSDCF" of the source F:\DCIM; the folder itself when that can't be worked out.</summary>
    private static string FromVolumeRoot(string source, string rel)
    {
        try
        {
            string full = Path.GetFullPath(Path.Join(source, rel));
            return Path.GetPathRoot(full) is { Length: > 0 } root ? Path.GetRelativePath(root, full) : rel;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return rel;
        }
    }

    /// <summary>An ARRI reel folder ("A016R1K4"), as <see cref="SourceGuards"/> recognizes one at a card's root.</summary>
    [GeneratedRegex(@"^[A-Z]\d{3}R[0-9A-Z]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArriReel();

    /// <summary>The Type column of the file lists.</summary>
    public static string Kind(SourceFile f) => (f.Side, f.Role) switch
    {
        (MediaSide.Video, FileRole.Primary) => "Video",
        (MediaSide.Photo, FileRole.Primary) => "Photo",
        (MediaSide.Video, _) => "Video companion",
        (MediaSide.Photo, _) => "Photo companion",
        _ => "Other",
    };

    /// <summary>The Type column for a file known only by its path (after a job).</summary>
    public static string Kind(bool primary, MoveMode mode) =>
        (mode == MoveMode.Videos ? "Video" : "Photo") + (primary ? "" : " companion");
}
