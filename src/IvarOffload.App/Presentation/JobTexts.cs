using System.IO;
using IvarOffload.Core;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>Why a job log can't be found where the job expects it (see <see cref="JobTexts.WhyLogMissing"/>).</summary>
public enum MissingLog
{
    /// <summary>Its drive is not connected (nothing answers at its letter, or another drive does).</summary>
    DriveAway,
    /// <summary>Its drive is connected (it answers with the recorded label): the folder with the log was renamed or moved.</summary>
    FolderMoved,
    /// <summary>A drive answers at its letter, but without a label to tell whether it is the job's drive.</summary>
    Either,
}

/// <summary>The sort the user chose to undo, and what they opened to find it (a receipt or a folder; null for a remembered job).</summary>
public sealed record UndoChoice(string Journal, string? StartedFrom = null);

/// <summary>What to do with an undo preview (see <see cref="JobTexts.UndoNext"/>).</summary>
public enum UndoStep
{
    /// <summary>Files can go back: ask before moving them.</summary>
    Ask,
    /// <summary>Nothing can go back (or the undo is blocked): say why.</summary>
    Inform,
    /// <summary>The folder the files came from can't be confirmed: offer to choose it, then preview again.</summary>
    ChooseFolder,
}

/// <summary>Texts about jobs outside the result card: the unfinished-job banner, undo, and the memory-card question.</summary>
public static class JobTexts
{
    /// <summary>
    /// The question asked before sorting on something that looks like a memory card or camera drive. The safe answer
    /// opens the Backup tab with the card selected.
    /// </summary>
    public static string MemoryCardQuestion(string drive) =>
        $"This looks like a memory card or camera drive ({drive}). Sort moves files around on it.\n\n"
        + "Back the card up to another drive first, then sort the backup. "
        + "Sort anyway only if you mean to sort the card itself - a camera that records to an SSD, for example.";

    /// <summary>The safe answer to <see cref="MemoryCardQuestion"/>: moves nothing and opens the Backup tab with the card selected.</summary>
    public const string MemoryCardDontSort = "Back up this card first";

    /// <summary>The status after <see cref="MemoryCardDontSort"/>.</summary>
    public const string MemoryCardNotSorted = "Nothing was moved. The Backup tab is open with the card selected: back it up first, then sort the backup.";

    /// <summary>"26 Sep 15:42", with the year when it is not this year.</summary>
    public static string Date(string? created) => Format.JobDate(created);

    private static string More(int more) => more <= 0 ? "" : $"  (+{RunOutcome.Count(more, "more unfinished job")} - offered when this one is done.)";

    /// <summary>The button that ends an unfinished job and leaves what did not move where it is.</summary>
    public static string EndJobButton(bool undo) => "Stop here...";

    public static string EndJobToolTip(bool undo) => undo
        ? "Ends the undo. The files not moved back yet stay in the sorted folder. Nothing is deleted."
        : "Ends the sort. The files not moved yet stay in the folder to sort. Nothing is deleted.";

    /// <summary>The question before ending a job. What is left can be sorted by a new sort, or put back by another undo.</summary>
    public static (string Title, string Text) EndJobQuestion(bool undo) => undo
        ? ("Stop here", "Stop this undo without moving the rest back?\n\n"
            + "A file that is being moved back is finished or put back, so nothing is left half-done. The files not moved back yet "
            + "stay in the sorted folder, and nothing is deleted. You can undo the rest later with “Undo a sort”.")
        : ("Stop here", "Stop this sort without moving the rest?\n\n"
            + "A file that is being moved is finished or put back, so nothing is left half-done. The files not moved yet "
            + "stay in the folder to sort, and nothing is deleted. You can sort them later.");

    /// <summary>Banner for an unfinished job whose log can be reached: it can be resumed or ended.</summary>
    /// <returns>A title, the job's folders (one line), and what the buttons do.</returns>
    public static (string Title, string Paths, string Text) Banner(JobState job, int more)
    {
        JobHeader h = job.Header;
        string title = $"An unfinished {(job.IsUndo ? "undo" : "sort")} was found: {job.DoneCount:N0} of {job.Items.Count:N0} files "
            + $"{(job.IsUndo ? "moved back" : "moved")} ({Planner.Word(h.Mode)}), started {Date(h.Created)}";
        string text = $"Resume continues exactly where it stopped. “{EndJobButton(job.IsUndo)}” ends it and leaves the rest in "
            + (job.IsUndo ? "the sorted folder" : "the folder to sort") + " - nothing is deleted." + More(more);
        return (title, $"{h.Source}  →  {h.Target}", text);
    }

    /// <summary>
    /// Banner for an unfinished job whose log can't be reached (<paramref name="why"/>, see <see cref="WhyLogMissing"/>):
    /// its drive is not connected, its folder was renamed or moved, or (when that can't be told apart) one of the two.
    /// Either way nothing can be done until the log is back where the job expects it.
    /// </summary>
    public static (string Title, string Paths, string Text) Banner(RecentJob job, int more, MissingLog why = MissingLog.DriveAway)
    {
        string kind = job.Kind == JobKind.Undo ? "undo" : "sort";
        string paths = $"{job.Source}  →  {job.Target}  ·  {Planner.Word(job.Mode)}, started {Date(job.Created)}";
        string folder = LogFolderOf(job.JournalPath, job.Target);
        return why switch
        {
            MissingLog.FolderMoved => ($"Unfinished {kind}: its folder {folder} was renamed or moved ({job.DriveName} is connected)", paths,
                "Nothing was lost. Put the folder back where it was (or give it its old name), then press “Check again” to resume the job. "
                + "If you removed it on purpose, press “Forget this job”." + More(more)),
            MissingLog.Either => ($"Unfinished {kind}: its log is not in {folder} - {job.DriveName} is not connected, or the folder was renamed or moved", paths,
                "Nothing was lost. Connect the drive, or put the folder back where it was (or give it its old name), then press “Check again” "
                + "to resume the job. If you removed it on purpose, press “Forget this job”." + More(more)),
            _ => ($"Unfinished {kind} on {job.DriveName} (not connected) - connect the drive to continue", paths,
                "Nothing was lost: once the drive is back, press “Check again” and resume the job where it stopped." + More(more)),
        };
    }

    /// <summary>
    /// Why a job log can't be found. Its drive letter does not answer, or answers with another label than the job
    /// recorded (another drive got the letter): <see cref="MissingLog.DriveAway"/>. It answers with the recorded label:
    /// the drive is here, so the folder with the log was renamed or moved (<see cref="MissingLog.FolderMoved"/>). No
    /// label was recorded, or it can't be read now: the drive at that letter may be another one (many cards and exFAT
    /// drives have no label), so it can't be told apart (<see cref="MissingLog.Either"/>).
    /// </summary>
    public static MissingLog WhyLogMissing(string journalPath, string label, Func<string, bool> folderExists, Func<string, string?> labelOf)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(journalPath);
        }
        catch (ArgumentException)
        {
            return MissingLog.DriveAway;
        }
        if (string.IsNullOrEmpty(root) || !folderExists(root)) return MissingLog.DriveAway;
        if (label.Length == 0 || labelOf(root) is not { Length: > 0 } now) return MissingLog.Either;
        return string.Equals(now, label, StringComparison.OrdinalIgnoreCase) ? MissingLog.FolderMoved : MissingLog.DriveAway;
    }

    /// <summary>
    /// Why a job log can't be found, deciding first by the serial number of the drive that holds it
    /// (<paramref name="serial"/>, recorded with the job): when it is known, the drive is there for sure when a drive
    /// with that serial number answers at the log's letter (<paramref name="driveWithSerialIsThere"/>, as
    /// RecentJob.LogMissing / JobListing.LogMissing say) - then the folder was renamed or moved - and away otherwise.
    /// Without it (older jobs, receipts without the line), the drive's label decides (<paramref name="byLabel"/>).
    /// </summary>
    public static MissingLog WhyLogMissing(uint serial, bool driveWithSerialIsThere, Func<MissingLog> byLabel) =>
        serial != 0 ? (driveWithSerialIsThere ? MissingLog.FolderMoved : MissingLog.DriveAway) : byLabel();

    /// <summary>The folder a job log should be in (the job's target): where it was when the job was created.</summary>
    private static string LogFolderOf(string journalPath, string target) =>
        journalPath.Length > 0 && JobPaths.RootOfJournal(journalPath) is { } root ? root : target;

    /// <summary>The undo confirmation: a question ("Move 412 videos back to E:\Smith\Card1?") and the details that matter.</summary>
    public static (string Question, string Details) UndoQuestion(UndoPreview p)
    {
        if (p.Blocked is { } blocked)
            return p.NeedsFolder
                ? ("The folder the files came from can't be confirmed.", blocked + "\n\n" + UndoChooseFolderHint)
                : ("This sort can't be undone right now.", blocked);
        MoveMode mode = p.Original.Header.Mode;
        // A file whose original place is taken never moves: an identical file is left as it is, a different one is not overwritten.
        List<JobItem> back = p.GoingBack;
        int primaries = MediaRules.CountPrimaries(back.Select(i => i.Rel), mode);
        string question = back.Count == 0
            ? $"Nothing can go back to {p.To}."
            : $"Move {RunOutcome.Things(back.Count, primaries, mode, back.Sum(i => i.Size))} back to {p.To}?";

        var lines = new List<string>();
        if (p.DriveLetterChanged)
            lines.Add($"The drive the files came from has another letter now: they go back to {p.To} (it was {p.Original.Header.Source}).");
        // The folder the files go back to is not the sort's source folder: it was renamed, or the user chose it.
        if (p.ToNote is { } note) lines.Add(note);
        if (p.CreatesFolder && back.Count > 0) lines.Add($"{p.To} no longer exists; it will be created (on the drive the files came from).");
        foreach (var group in p.CannotGoBack.GroupBy(i => i.Reason).OrderByDescending(g => g.Count()))
        {
            int n = group.Count();
            string files = RunOutcome.Files(n);
            lines.Add(group.Key switch
            {
                SkipReasons.ChangedSinceSorted => $"{files} changed since the sort and will stay where {(n == 1 ? "it is" : "they are")}.",
                SkipReasons.NotInSortedFolder => $"{files} {(n == 1 ? "is" : "are")} no longer in the sorted folder (moved, renamed or deleted since the sort) and can't go back.",
                _ => $"{files} can't go back: {group.Key}.",
            });
        }
        if (p.PlaceTaken.Count > 0)
            lines.Add($"{RunOutcome.Files(p.PlaceTaken.Count)} {(p.PlaceTaken.Count == 1 ? "has" : "have")} a file with the same name in "
                + $"{(p.PlaceTaken.Count == 1 ? "its" : "their")} original place, so {(p.PlaceTaken.Count == 1 ? "it stays" : "they stay")} in the sorted folder: "
                + "an identical file there is left as it is, and a different one is never overwritten.");
        if (p.AlreadyBack.Count > 0)
            lines.Add($"{RunOutcome.Files(p.AlreadyBack.Count)} {(p.AlreadyBack.Count == 1 ? "is" : "are")} already back in the original place.");
        if (back.Count > 0)
            lines.Add(p.Method == TransferMethod.Rename
                ? "Same drive: the files are renamed back; no file data is copied."
                : "Different drives: each file is copied back and checked by SHA-256 before it is removed from the sorted folder.");
        return (question, string.Join("\n", lines));
    }

    /// <summary>Undoing would move something back (the question asks); otherwise it only informs.</summary>
    public static bool UndoCanMove(UndoPreview p) => p.Blocked is null && p.GoingBack.Count > 0;

    /// <summary>
    /// After an undo preview: ask (files can go back), inform (nothing can, or it is blocked), or - when the folder the
    /// files came from can't be confirmed - offer to choose it and preview again with that folder.
    /// </summary>
    public static UndoStep UndoNext(UndoPreview p) => p.NeedsFolder ? UndoStep.ChooseFolder : UndoCanMove(p) ? UndoStep.Ask : UndoStep.Inform;

    /// <summary>The button that picks the folder an undo puts the files back into, when it can't be confirmed.</summary>
    public const string UndoChooseFolderButton = "Choose the folder the files came from...";

    /// <summary>The title of that folder picker.</summary>
    public const string UndoChooseFolderTitle = "Choose the folder the files came from";

    private const string UndoChooseFolderHint =
        "The folder you choose is checked again, and you see what would go back before anything moves.";

    /// <summary>The Status column of the "Undo a previous sort" list. <paramref name="why"/>: see <see cref="WhyLogMissing"/>.</summary>
    public static string ListingStatus(JobListing j, MissingLog why = MissingLog.DriveAway)
    {
        if (!j.IsConnected)
            return why switch
            {
                MissingLog.FolderMoved => "folder renamed or moved",
                MissingLog.Either => "log not found",
                _ => "not connected",
            };
        if (j.Kind == JobKind.Undo) return "undo job";
        // An undo that was ended early, or that left files in the sorted folder: the rest can be undone.
        if (j.PartlyUndone) return "partly undone - can undo the rest";
        if (j.UndoneBy is { Status: "completed" }) return "undone";
        if (j.UndoneBy is { Status: "started" }) return "being undone";
        return j.Status switch
        {
            "unfinished" => "unfinished",
            "never started" => "never started",
            _ when j.Moved == 0 => "nothing moved",
            _ => "can undo",
        };
    }

    /// <summary>A sort worth offering for undo (the undo preview has the final say).</summary>
    public static bool CanTryUndo(JobListing j) => j.IsConnected && j.Kind == JobKind.Sort && j.CanUndo;

    /// <summary>Why a listed job cannot be undone, in plain words; null when it can be tried.</summary>
    public static string? WhyNotUndo(JobListing j, MissingLog why = MissingLog.DriveAway) => CanTryUndo(j) ? null : ListingStatus(j, why) switch
    {
        "not connected" => $"The drive with this job's log ({j.DriveName}) is not connected. Connect it to undo this sort.",
        "folder renamed or moved" => $"This sort's log is no longer in {LogFolderOf(j.JournalPath, j.Target)} (the folder was renamed or moved). "
            + "Choose the sorted folder where it is now (“Choose a folder...”), or open its log (“Open a log file...”).",
        "log not found" => $"This sort's log is not in {LogFolderOf(j.JournalPath, j.Target)}: its drive ({j.DriveName}) is not connected, "
            + "or the folder was renamed or moved. Connect the drive, or choose the sorted folder where it is now (“Choose a folder...”), "
            + "or open its log (“Open a log file...”).",
        "undo job" => "This is an undo job. An undo can't be undone - to separate the files again, run a new sort.",
        "undone" => "This sort was already undone. A sort can be undone once - to separate the files again, run a new sort.",
        "being undone" => "An undo of this sort was started and has not ended. Resume it (or end it) from the banner in the main window first.",
        "unfinished" => "This sort has not finished yet. Resume it (or end it) first, then undo it.",
        "never started" => "This sort never started, so there is nothing to undo.",
        _ => "This sort did not move any files, so there is nothing to undo.",
    };

    /// <summary>After "Open a log file..." found no job log.</summary>
    public const string LogNotFound =
        "This file is not a job log or receipt, or the job log it points to can't be found: its drive is not connected, or the sorted folder "
        + "was renamed or moved. Connect the drive, or choose the sorted folder where it is now (“Choose a folder...”) and open the log in it.";

    /// <summary>
    /// After "Open a log file..." picked a receipt whose job log is not where the receipt says (<paramref name="recordedLog"/>).
    /// <paramref name="why"/>: see <see cref="WhyLogMissing"/>.
    /// </summary>
    public static string ReceiptLogNotFound(string recordedLog, MissingLog why)
    {
        string folder = LogFolderOf(recordedLog, Path.GetDirectoryName(recordedLog) ?? recordedLog);
        return why switch
        {
            MissingLog.FolderMoved => $"The job log this receipt names is no longer in {folder}: its drive is connected, so the sorted folder was renamed or moved. "
                + $"Choose the sorted folder where it is now (“Choose a folder...”), or open the log in its {JobPaths.LogFolderName} folder (“Open a log file...”).",
            MissingLog.Either => $"The job log this receipt names is not in {folder}: its drive is not connected, or the sorted folder was renamed or moved. "
                + "Connect the drive and open the receipt again, or choose the sorted folder where it is now (“Choose a folder...”).",
            _ => $"The job log this receipt names is in {folder}, and that drive is not connected. Connect it and open the receipt again, "
                + "or choose the sorted folder where it is now (“Choose a folder...”).",
        };
    }

    /// <summary>The job id a receipt (the lines of its .moved-out.txt) names, or null.</summary>
    public static string? ReceiptJobId(IEnumerable<string> receiptLines) => receiptLines
        .Where(l => l.StartsWith(ReceiptJobLabel, StringComparison.Ordinal))
        .Select(l => l[ReceiptJobLabel.Length..].Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>
    /// The label of the drive that held a job's log, as the recent-jobs list remembers it (by job id or log path); ""
    /// when the job is not remembered or no label was recorded.
    /// </summary>
    public static string RecordedLabel(IEnumerable<RecentJob> recent, string? jobId, string journalPath) =>
        recent.FirstOrDefault(e => (jobId is { Length: > 0 } && e.JobId == jobId) || JobPaths.SamePath(e.JournalPath, journalPath))?.TargetLabel ?? "";

    /// <summary>The receipt line that names the job id, as JobReports writes it.</summary>
    private const string ReceiptJobLabel = "Job: ";

    /// <summary>The job log a receipt (the lines of its .moved-out.txt) names, or null.</summary>
    public static string? ReceiptLog(IEnumerable<string> receiptLines) => receiptLines
        .Where(l => l.StartsWith(ReceiptJournalLabel, StringComparison.Ordinal))
        .Select(l => l[ReceiptJournalLabel.Length..].Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>The receipt line that names the job log, as JobReports writes it.</summary>
    private const string ReceiptJournalLabel = "Job log: ";
}
