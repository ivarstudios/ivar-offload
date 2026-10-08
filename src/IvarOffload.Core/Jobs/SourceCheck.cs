using System.Text.RegularExpressions;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>A file that should have moved and is still in the source after a job.</summary>
/// <param name="CountAs">The video (or photo) it counts as (<see cref="MediaRules.CountKey"/>); null for a companion file.</param>
/// <param name="Reason">Why, in a few plain words; files with the same reason are counted together.</param>
/// <param name="Detail">Why this file in particular (the full message for a failed file).</param>
/// <param name="Status">"failed", "skipped", "not moved", "copied, original kept", ...</param>
/// <param name="FromRescan">Found by the fresh scan of the source, not in the job's own record (see <see cref="SourceCheck"/>).</param>
public sealed record LeftFile(string Rel, long Size, string? CountAs, string Reason, string Detail, string Status, bool FromRescan = false)
{
    public bool IsPrimary => CountAs is not null;
}

/// <summary>
/// What is still in the source after a job: the job's own record (every planned file that did not move, with its
/// reason) combined with a fresh scan of the source (anything else that should move, such as files added after the
/// preview, files held back by a name clash in the target or videos that are only online) and with what earlier sorts
/// of the same folder left behind (<see cref="Leftovers"/>), so a fresh look at a file can never hide it. The app's
/// result, the command line's exit code and the job's summary all go by it.
/// </summary>
public sealed class SourceCheck
{
    public const string ClashReason = "a DIFFERENT file with the same name is already in the target";
    public const string NotInThisSort = "not part of this sort (new since the preview?) - it can move now";
    /// <summary>A video (or photo) that is only a cloud placeholder: it cannot be read, so it did not move.</summary>
    public const string OnlineOnlyReason = "online-only (not downloaded), so not moved";
    /// <summary>A video (or photo) that is a link (shortcut): links are never followed, so it did not move.</summary>
    public const string LinkReason = "a link (shortcut) - not followed, so not moved; what it points to may need moving by hand";
    /// <summary>A video (or photo) inside an editing or processing project: it stays so the project keeps its media.</summary>
    public const string InProjectReason = "inside an editing/processing project - stays so the project keeps its media";
    /// <summary>A file an earlier sort of the same folder planned to move and did not (it was ended early, or the file was skipped).</summary>
    public const string EarlierJobReason = "left in the source by an earlier sort of this folder";
    /// <summary>The status of a file the preview kept in the source (as the job's reports call it).</summary>
    public const string HeldBackStatus = "held back";

    public IReadOnlyList<LeftFile> Left { get; init; } = [];
    /// <summary>
    /// Planned files that disappeared from the source (something else removed them) before they were moved: they are
    /// not in the source and not in the target (unless a copy was kept there under another name, see
    /// <see cref="KeptCopies"/>). Never part of <see cref="Left"/>.
    /// </summary>
    public IReadOnlyList<LeftFile> Missing { get; init; } = [];
    /// <summary>
    /// Copies the job kept in the target under another name ("DCIM\A.MOV.verified-copy") for files that did not move:
    /// they may be all that is left of a file, so they are named on the result card.
    /// </summary>
    public IReadOnlyList<string> KeptCopies { get; init; } = [];
    /// <summary>Files a new job could move right now (the fresh plan can run).</summary>
    public int MovableNow { get; init; }
    /// <summary>Unrecognized files that stay in the source (from the fresh scan).</summary>
    public int Unrecognized { get; init; }
    /// <summary>
    /// Other staying files that need a look: matching both a photo and a video, online-only ones of the other side,
    /// links (shortcuts) named like a file that moves.
    /// </summary>
    public int OtherLooks { get; init; }
    /// <summary>Online-only videos (or photos) and their companions. They are in <see cref="Left"/>.</summary>
    public int OnlineOnly { get; init; }
    /// <summary>
    /// The source looks like a memory card or camera drive ("F: SONY_A"): whatever moved, it still holds files that may
    /// exist nowhere else, so the result is never green. Null otherwise.
    /// </summary>
    public string? MemoryCard { get; init; }
    /// <summary>Why the source was not scanned again, in plain words; null when it was (or when no scan is needed).</summary>
    public string? NotChecked { get; init; }
    /// <summary>Folders the fresh scan could not read ("DCIM\101MEDIA: Access is denied."): what is in them was not checked.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>The whole source was scanned again, and every folder of it could be read.</summary>
    public bool IsChecked => NotChecked is null && Unreadable.Count == 0;
    public int LeftPrimaries => Primaries(Left);
    public long LeftBytes => Left.Sum(f => f.Size);
    /// <summary>What the fresh scan found that is not in the job's own record (see <see cref="LeftFile.FromRescan"/>).</summary>
    public IEnumerable<LeftFile> FoundAgain => Left.Where(f => f.FromRescan);

    /// <summary>"2 folders could not be read (DCIM\101MEDIA, Private)", for the result card.</summary>
    public string UnreadableText
    {
        get
        {
            var names = Unreadable.Select(p => p.Split(": ", 2)[0]).Select(n => n == "(source folder)" ? "the source folder itself" : n).ToList();
            return $"{Format.Count(names.Count, "folder")} could not be read ({string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", ..." : "")})";
        }
    }

    public static int Primaries(IEnumerable<LeftFile> files) =>
        files.Select(f => f.CountAs).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// A sort job's check: its unmoved files, what a fresh plan of the source would still move, online-only files of
    /// the kind that moves, and what the <paramref name="earlier"/> sorts of this folder left that is still there.
    /// <paramref name="rescan"/> is null when the source could not be scanned (<paramref name="notChecked"/> says why).
    /// <paramref name="memoryCard"/>: the source was flagged as a memory card before the job (a fresh plan says so too).
    /// </summary>
    public static SourceCheck ForSort(JobState job, MovePlan? rescan, string? notChecked = null, IReadOnlyList<JobState>? earlier = null,
        string? memoryCard = null)
    {
        MoveMode mode = job.Header.Mode;
        List<LeftFile> left = FromJob(job);
        if (rescan is null)
            return new SourceCheck
            {
                Left = left, Missing = MissingFrom(job), KeptCopies = KeptCopiesOf(job),
                NotChecked = notChecked ?? "the source was not scanned again", MemoryCard = memoryCard,
            };

        var seen = new HashSet<string>(left.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
        var leftBefore = Leftovers.StillThere(rescan, earlier ?? []).ToDictionary(x => x.File.RelativePath, StringComparer.OrdinalIgnoreCase);
        var identical = new HashSet<SourceFile>(rescan.IdenticalConflicts);
        foreach (SourceFile f in rescan.ToMove.Where(f => !identical.Contains(f)).Concat(rescan.HeldBack))
        {
            if (!seen.Add(f.RelativePath)) continue;
            string? countAs = MediaRules.CountKey(f.RelativePath, mode);
            if (f.Note is FileNote.DifferentInTarget or FileNote.HeldWithGroup)
                left.Add(new LeftFile(f.RelativePath, f.Size, countAs, ClashReason, f.Reason, HeldBackStatus, FromRescan: true));
            else if (leftBefore.TryGetValue(f.RelativePath, out var before))
                left.Add(new LeftFile(f.RelativePath, f.Size, countAs, EarlierJobReason + " - it can move now",
                    Earlier(before.Item, before.Job) + " - it can move now", "not moved", FromRescan: true));
            else
                left.Add(new LeftFile(f.RelativePath, f.Size, countAs, NotInThisSort, NotInThisSort, "not in this sort", FromRescan: true));
        }

        // A cloud placeholder is never read, so a video (or photo) that is only online stays behind although it still
        // has to move: it keeps the result amber. Its companions (an online-only .SRT) are named by their type.
        // Media inside an editing project stays by design (the preview says so), and a link is not the media itself:
        // neither blocks a green result; links are mentioned among the files worth a look.
        var online = rescan.MovingSideStaying.Where(f => f.Note == FileNote.OnlineOnly)
            .Concat(rescan.Staying.Where(f => f.Note == FileNote.OnlineOnly && MediaRules.CountKey(f.RelativePath, mode) is null
                && MediaRules.IsMovingType(f.RelativePath, mode)))
            .ToHashSet();
        var listed = new HashSet<SourceFile>();
        foreach (SourceFile f in online)
            if (listed.Add(f) && seen.Add(f.RelativePath))
                left.Add(new LeftFile(f.RelativePath, f.Size, MediaRules.CountKey(f.RelativePath, mode), OnlineOnlyReason,
                    f.Reason is { Length: > 0 } why ? why : OnlineOnlyReason, "stays", FromRescan: true));

        // What an earlier sort left and must stay for now (inside a project by now, for example) is still left behind.
        foreach ((SourceFile f, JobItem item, JobState from) in leftBefore.Values)
            if (seen.Add(f.RelativePath))
                left.Add(new LeftFile(f.RelativePath, f.Size, MediaRules.CountKey(f.RelativePath, mode), EarlierJobReason,
                    $"{Earlier(item, from)}; now: {f.Reason}", "not moved", FromRescan: true));

        return new SourceCheck
        {
            Left = left,
            Missing = MissingFrom(job),
            KeptCopies = KeptCopiesOf(job),
            MovableNow = rescan.CanRun ? rescan.FilesToTransfer : 0,
            Unrecognized = rescan.Staying.Count(f => f.Note == FileNote.UnknownType),
            // A link named like a companion of a video is not followed either: what it points to may need moving by hand.
            OtherLooks = rescan.Staying.Count(f => !listed.Contains(f) && (f.Note is FileNote.Ambiguous or FileNote.OnlineOnly
                || f.Note == FileNote.Link && MediaRules.IsMovingType(f.RelativePath, mode))),
            OnlineOnly = online.Count,
            MemoryCard = rescan.MemoryCard ?? memoryCard,
            Unreadable = rescan.Scan.Problems,
        };
    }

    /// <summary>
    /// The job's own record only. For an undo this is the whole answer: the files it did not move back are in its log,
    /// and anything else in the sorted folder was never part of it.
    /// </summary>
    public static SourceCheck ForJob(JobState job, string? notChecked = null, string? memoryCard = null) =>
        new() { Left = FromJob(job), Missing = MissingFrom(job), KeptCopies = KeptCopiesOf(job), NotChecked = notChecked, MemoryCard = job.IsUndo ? null : memoryCard };

    /// <summary>
    /// The check after a sort ended: scans its source again (without its target, when that is inside the source), adds
    /// back what this job and earlier sorts of the folder planned but did not move (<see cref="Leftovers"/>), and returns
    /// the check together with that plan, which "Move the remaining" runs. The plan is null when the source could not be
    /// scanned. <paramref name="memoryCard"/>: the source was flagged as a memory card before the job.
    /// </summary>
    public static (SourceCheck Check, MovePlan? Plan) Rescan(JobState job, IProgress<ScanProgress>? progress, CancellationToken ct, string? memoryCard = null)
    {
        JobHeader h = job.Header;
        try
        {
            if (!Directory.Exists(h.Source)) return (ForSort(job, null, "the source folder can't be reached", memoryCard: memoryCard), null);
            List<JobState> earlier = Leftovers.EarlierSorts(h.Source, h.Target, h.Mode, except: job.JournalPath);
            MovePlan fresh = Planner.Build(Scanner.Scan(h.Source, progress, ct, h.Target), h.Target, h.Mode, h.Verify);
            MovePlan plan = Leftovers.Include(fresh, [job, .. earlier], Leftovers.InTarget(fresh.TargetRoot));
            return (ForSort(job, plan, earlier: earlier, memoryCard: memoryCard), plan);
        }
        catch (OperationCanceledException)
        {
            return (ForSort(job, null, "the check was stopped", memoryCard: memoryCard), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ForSort(job, null, e.Message, memoryCard: memoryCard), null);
        }
    }

    /// <summary>
    /// Writes what the check of an ended sort found beyond the job's own record into the job's log (the record
    /// "rescan"), and brings the job's summary and manifest up to date, so they and the command line's <c>status</c> say
    /// the same as the result: files added to the source after the preview, online-only files, what earlier sorts left,
    /// and whether the source could be checked at all. Best effort: the job's own record stays right without it.
    /// </summary>
    public static void Record(JobState job, SourceCheck check)
    {
        if (job.IsUndo || job.IsBackup || !job.IsEnded) return;
        try
        {
            using JournalWriter writer = JournalWriter.OpenForAppend(job.JournalPath, out JobState now);
            RescanRecord rescan = new(JournalRecord.Now(), check.NotChecked, check.Unreadable,
                check.FoundAgain.Select(f => new RescanFile(f.Rel, f.Size, f.Reason)).ToList(), check.MovableNow);
            writer.Write(new JournalRecord
            {
                Type = "rescan",
                At = rescan.At,
                Error = rescan.NotChecked,
                Count = rescan.MovableNow,
                Left = rescan.Left.Count > 0 ? rescan.Left.ToList() : null,
                Unreadable = rescan.Unreadable.Count > 0 ? rescan.Unreadable.ToList() : null,
            });
            now.Rescan = rescan;
            JobReports.TryWrite(now);
        }
        catch (Exception e) when (e is JournalException or IOException or UnauthorizedAccessException)
        {
            // Open elsewhere, or the drive went away: the result still says it; the summary is only not updated.
        }
    }

    /// <summary>
    /// The job's planned files that did not move, and the files its preview held back (a DIFFERENT file with the same
    /// name is in the target, or they belong with one): both are still in the source.
    /// </summary>
    private static List<LeftFile> FromJob(JobState job) =>
        job.StillInSource.Select(i => new LeftFile(i.Rel, i.Size, MediaRules.CountKey(i.Rel, job.Header.Mode), LeftReasons.Reason(i),
                LeftReasons.Detail(i), LeftReasons.StatusText(i, job.IsUndo)))
            .Concat(job.HeldBack.Select(h => new LeftFile(h.Rel, h.Size, MediaRules.CountKey(h.Rel, job.Header.Mode), ClashReason,
                h.Why.Length > 0 ? h.Why : ClashReason, HeldBackStatus)))
            .ToList();

    /// <summary>The job's planned files that disappeared from the source before they were moved (see <see cref="Missing"/>).</summary>
    private static List<LeftFile> MissingFrom(JobState job) =>
        job.Missing.Select(i => new LeftFile(i.Rel, i.Size, MediaRules.CountKey(i.Rel, job.Header.Mode), LeftReasons.MissingReason(i, job.IsUndo),
            i.Failed && i.Note is { Length: > 0 } note ? note : LeftReasons.MissingReason(i, job.IsUndo), LeftReasons.StatusText(i, job.IsUndo))).ToList();

    /// <summary>Copies kept in the target under another name for files that did not move (relative to the target).</summary>
    private static List<string> KeptCopiesOf(JobState job) =>
        job.Items.Where(i => i.SetAside is not null && i.Stage != ItemStage.Done).Select(i => i.SetAside!).ToList();

    /// <summary>"not moved - the job was ended early (sort of 26 Sep 15:42)".</summary>
    private static string Earlier(JobItem item, JobState job) => $"{LeftReasons.Reason(item)} (sort of {Format.JobDate(job.Header.Created)})";
}

/// <summary>Why a planned file did not move, in plain words: for the result, the list of what is left, and the reports.</summary>
public static partial class LeftReasons
{
    /// <summary>Why a planned file did not move, in a few plain words (files with the same words are counted together).</summary>
    public static string Reason(JobItem item)
    {
        string? note = item.Note;
        if (item.IsMissing) return MissingReason(item, undo: false);
        if (note == FailReasons.OriginalNotChecked)
            return "verified copy is in the target; the original could not be checked (the source was not there when the job was ended), so it was kept";
        if (FailReasons.IsMadeBy(note, FailReasons.UnfinishedCopyKept))
            return $"not moved - the job was ended while it was being copied and the source was not there; the unfinished copy was kept in the target ({JobPaths.UnverifiedCopySuffix})";
        if (FailReasons.IsMadeBy(note, FailReasons.VerifiedCopyKeptNotChecked))
            return "not moved - the job was ended while the source was not there, and a file with its name is in the target; its verified copy was kept "
                + $"in the target ({JobPaths.VerifiedCopySuffix})";
        if (FailReasons.IsMadeBy(note, FailReasons.DamagedCopyKeptNotChecked))
            return "not moved - the job was ended while the source was not there, and its copy does not match the checksum; the copy was kept in the "
                + $"target ({JobPaths.DamagedCopySuffix})";
        if (item.Failed && item.Stage == ItemStage.Placed) return "verified copy is in the target; original kept";
        if (item.Failed)
        {
            if (FailReasons.IsWaiting(note)) return "waiting for another file of its clip, which could not be moved yet";
            if (FailReasons.IsDeviceError(note)) return "the drive stopped responding while it was being moved";
            return "could not be moved: " + Plain(note ?? "unknown error");
        }
        if (item.Stage == ItemStage.Skipped)
            return note switch
            {
                SkipReasons.DifferentInTarget => SourceCheck.ClashReason,
                SkipReasons.SameNameAndSizeInTarget => "a file with the same name and size, but another date, is already in the target",
                SkipReasons.FolderInTarget => "a folder with the same name is in the target",
                SkipReasons.AppearedDuringMove or SkipReasons.AppearedDuringCopy => "a file with the same name appeared in the target during the move",
                SkipReasons.ChangedAfterPreview => "changed after the preview",
                SkipReasons.ChangedDuringMove => "changed while it was being moved",
                SkipReasons.SourceGone => "no longer in the source (removed by something else)",
                SkipReasons.Closed => "not moved - the job was ended early",
                SkipReasons.ChangedSinceSorted => "changed since the sort - left where it is",
                SkipReasons.NotInSortedFolder => "no longer in the sorted folder (moved, renamed or deleted since the sort)",
                SkipReasons.AlreadyBackDifferent or SkipReasons.AlreadyBackSameSize => "a different file is now in its original place - both kept",
                _ when SkipReasons.IsKeptWithGroup(note) => "kept with another file of its clip, which was not moved",
                _ => note ?? "skipped",
            };
        return item.Stage == ItemStage.Pending ? "not moved yet" : "interrupted - Resume finishes it";
    }

    /// <summary>
    /// Why a planned file that disappeared from the source (<see cref="JobItem.IsMissing"/>) did not move, and what is
    /// left of it: nothing, or a copy kept in the target under another name. In an undo, "the source" is the sorted folder.
    /// </summary>
    public static string MissingReason(JobItem item, bool undo)
    {
        string from = MissingFromText(undo);
        string target = undo ? "the original folder" : "the target";
        string? note = item.Note;
        return note switch
        {
            FailReasons.InNeitherPlace => $"{from} - the move was interrupted, and it is not in {target} either",
            FailReasons.CopyAndOriginalGone => $"{from} - its copy was interrupted, and no copy is in {target}",
            FailReasons.PlacedCopyDamagedOriginalGone => $"{from} - its copy in {target} does not match the checksum (kept - please check it)",
            _ when FailReasons.IsMadeBy(note, FailReasons.OriginalGoneDuringCopy) =>
                $"{from} while it was being copied - the unchecked copy was kept in {target} ({JobPaths.UnverifiedCopySuffix})",
            _ when FailReasons.IsMadeBy(note, FailReasons.ChangedCopyKept) =>
                $"{from} while it was being copied, and it had changed since the sort - its copy was kept in {target} ({JobPaths.UnverifiedCopySuffix})",
            _ when FailReasons.IsMadeBy(note, FailReasons.VerifiedCopyKept) =>
                $"{from} - its verified copy was kept in {target} under another name ({JobPaths.VerifiedCopySuffix}), because a file with its name is there",
            _ when FailReasons.IsMadeBy(note, FailReasons.DamagedCopyKept) =>
                $"{from} - its copy does not match the checksum and was kept in {target} ({JobPaths.DamagedCopySuffix})",
            _ when item.SetAside is not null => $"{from} - a copy was kept in {target} under another name",
            _ => $"{from} - not in {target} either",
        };
    }

    /// <summary>"missing from the source (removed by something else)".</summary>
    public static string MissingFromText(bool undo) => $"missing from {(undo ? "the sorted folder" : "the source")} (removed by something else)";

    /// <summary>
    /// The Status column of the list of what a job left: the job's reports' status (<see cref="JobReports.Status"/>) in
    /// plain words.
    /// </summary>
    public static string StatusText(JobItem item, bool undo) => JobReports.Status(item) switch
    {
        "missing" => undo ? "missing from the sorted folder" : "missing from the source",
        "copied, original kept" when item.Note == FailReasons.OriginalNotChecked => "copied, original not checked",
        var status => status,
    };

    /// <summary>Why this file did not move: the full message of a failed file, else its reason.</summary>
    public static string Detail(JobItem item) =>
        item.Failed && item.Note is { Length: > 0 } note
            ? (item.Stage == ItemStage.Placed && note != FailReasons.OriginalNotChecked ? "verified copy is in the target; original kept - " : "") + note
            : Reason(item);

    /// <summary>A failure message without file paths or error numbers, so the same problem on many files reads as one reason.</summary>
    public static string Plain(string message)
    {
        string text = Win32Message().Match(message) is { Success: true } m ? m.Groups[1].Value : message;
        return QuotedPath().Replace(text, "").Trim();
    }

    [GeneratedRegex("""^.* failed for ".*": (.*?)(?: \(error \d+\))?$""")]
    private static partial Regex Win32Message();

    [GeneratedRegex("""\s*['"][^'"]*[\\/][^'"]*['"]""")]
    private static partial Regex QuotedPath();
}
