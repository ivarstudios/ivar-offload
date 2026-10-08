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
        $"This looks like a memory card or camera drive ({drive}). A sort moves files to different folders on it.\n\n"
        + "Back up the card to a different drive first. Then sort the backup. "
        + "Click “Sort anyway” only if you want to sort the card itself (for example, if your camera records to an SSD).";

    /// <summary>The safe answer to <see cref="MemoryCardQuestion"/>: moves nothing and opens the Backup tab with the card selected.</summary>
    public const string MemoryCardDontSort = "Back up this card first";

    /// <summary>The status after <see cref="MemoryCardDontSort"/>.</summary>
    public const string MemoryCardNotSorted = "The app did not move any files. The Backup tab is open, and the card is selected. Back up the card first. Then sort the backup.";

    /// <summary>"26 Sep 15:42", with the year when it is not this year.</summary>
    public static string Date(string? created) => Format.JobDate(created);

    private static string More(int more) => more <= 0 ? ""
        : $"  (+{RunOutcome.Count(more, "more unfinished job")}: the app shows {(more == 1 ? "it" : "them")} when this job is done.)";

    /// <summary>The button that ends an unfinished job and leaves what did not move where it is.</summary>
    public static string EndJobButton(bool undo) => "Stop here...";

    public static string EndJobToolTip(bool undo) => undo
        ? "Ends the undo. The files that did not return yet stay in the target folder. The app does not delete any files."
        : "Ends the sort. The files that did not move yet stay in the source folder. The app does not delete any files.";

    /// <summary>The question before ending a job. What is left can be sorted by a new sort, or put back by another undo.</summary>
    public static (string Title, string Text) EndJobQuestion(bool undo) => undo
        ? ("Stop here", "Stop this undo here? The remaining files will not return.\n\n"
            + "The app completes or rolls back the file that is in progress, so no move stays half-done. "
            + "The remaining files stay in the target folder. The app does not delete any files. "
            + "Later, you can undo the remaining files with “Undo a sort”.")
        : ("Stop here", "Stop this sort here? The remaining files will not move.\n\n"
            + "The app completes or rolls back the file that is in progress, so no move stays half-done. "
            + "The remaining files stay in the source folder. The app does not delete any files. You can sort them later.");

    /// <summary>Banner for an unfinished job whose log can be reached: it can be resumed or ended.</summary>
    /// <returns>A title, the job's folders (one line), and what the buttons do.</returns>
    public static (string Title, string Paths, string Text) Banner(JobState job, int more)
    {
        JobHeader h = job.Header;
        string title = $"Unfinished {(job.IsUndo ? "undo" : "sort")}: {job.DoneCount:N0} of {job.Items.Count:N0} files "
            + $"{(job.IsUndo ? "returned" : "moved")} ({Planner.Word(h.Mode)}), started {Date(h.Created)}";
        string text = $"Resume continues the job from the point where it stopped. “{EndJobButton(job.IsUndo)}” ends the job and keeps the remaining files in "
            + (job.IsUndo ? "the target folder" : "the source folder") + ". The app does not delete any files." + More(more);
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
            MissingLog.FolderMoved => ($"Unfinished {kind}: the folder {folder} has a new name or location ({job.DriveName} is connected)", paths,
                "Nothing is lost. Move the folder to its old location, or give it its old name again. Then click “Check again” to resume the job. "
                + "If you removed the folder on purpose, click “Forget this job”." + More(more)),
            MissingLog.Either => ($"The log of this unfinished {kind} is not in {folder}. {job.DriveName} is not connected, or the folder has a new name or location", paths,
                "Nothing is lost. Connect the drive, or move the folder to its old location (or give it its old name again). Then click “Check again” "
                + "to resume the job. If you removed the folder on purpose, click “Forget this job”." + More(more)),
            _ => ($"Unfinished {kind} on {job.DriveName} (not connected). Connect the drive to continue the job.", paths,
                "Nothing is lost. After you connect the drive, click “Check again”. Then resume the job from the point where it stopped." + More(more)),
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
                ? ("The app cannot identify the folder that the files came from.", blocked + "\n\n" + UndoChooseFolderHint)
                : ("You cannot undo this sort now.", blocked);
        MoveMode mode = p.Original.Header.Mode;
        // A file whose original place is taken never moves: an identical file is left as it is, a different one is not overwritten.
        List<JobItem> back = p.GoingBack;
        int primaries = MediaRules.CountPrimaries(back.Select(i => i.Rel), mode);
        string question = back.Count == 0
            ? $"No files can return to {p.To}."
            : $"Return {RunOutcome.Things(back.Count, primaries, mode, back.Sum(i => i.Size))} to {p.To}?";

        var lines = new List<string>();
        if (p.DriveLetterChanged)
            lines.Add($"The drive that the files came from has a different letter now. The files return to {p.To}, not to {p.Original.Header.Source}.");
        // The folder the files go back to is not the sort's source folder: it was renamed, or the user chose it.
        if (p.ToNote is { } note) lines.Add(note);
        if (p.CreatesFolder && back.Count > 0) lines.Add($"{p.To} does not exist now. The app will make this folder again on the drive that the files came from.");
        foreach (var group in p.CannotGoBack.GroupBy(i => i.Reason).OrderByDescending(g => g.Count()))
        {
            int n = group.Count();
            string files = RunOutcome.Files(n);
            lines.Add(group.Key switch
            {
                SkipReasons.ChangedSinceSorted => $"{files} changed after the sort and will stay where {(n == 1 ? "it is" : "they are")}.",
                SkipReasons.NotInSortedFolder => $"{files} {(n == 1 ? "is" : "are")} no longer in the target folder (someone moved, renamed or deleted {(n == 1 ? "it" : "them")} after the sort), "
                    + $"so {(n == 1 ? "it" : "they")} cannot return.",
                _ => $"{files} cannot return: {group.Key}.",
            });
        }
        if (p.PlaceTaken.Count > 0)
            lines.Add($"{RunOutcome.Files(p.PlaceTaken.Count)} {(p.PlaceTaken.Count == 1 ? "has" : "have")} a file with the same name in "
                + $"{(p.PlaceTaken.Count == 1 ? "its" : "their")} original location, so {(p.PlaceTaken.Count == 1 ? "it stays" : "they stay")} in the target folder. "
                + "An identical file there stays as it is. The app never overwrites a different file.");
        if (p.AlreadyBack.Count > 0)
            lines.Add($"{RunOutcome.Files(p.AlreadyBack.Count)} {(p.AlreadyBack.Count == 1 ? "is" : "are")} already in the original location.");
        if (back.Count > 0)
            lines.Add(p.Method == TransferMethod.Rename
                ? "Same drive: the app renames the files to their old paths. It does not copy file data."
                : "Different drives: the app copies each file and checks the copy with SHA-256 before it removes the file from the target folder.");
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
    public const string UndoChooseFolderButton = "Choose the folder that the files came from...";

    /// <summary>The title of that folder picker.</summary>
    public const string UndoChooseFolderTitle = "Choose the folder that the files came from";

    private const string UndoChooseFolderHint =
        "The app then checks the folder that you choose. Before any file moves, the app shows you which files can return.";

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
        if (j.PartlyUndone) return "partly undone (you can undo the rest)";
        if (j.UndoneBy is { Status: "completed" }) return "undone";
        if (j.UndoneBy is { Status: "started" }) return "undo in progress";
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
        "not connected" => $"The drive with the log of this job ({j.DriveName}) is not connected. To undo this sort, connect the drive.",
        "folder renamed or moved" => $"The log of this sort is no longer in {LogFolderOf(j.JournalPath, j.Target)}, because the folder has a new name or location. "
            + "Choose the target folder where it is now (“Choose a folder...”), or open its log (“Open a log file...”).",
        "log not found" => $"The log of this sort is not in {LogFolderOf(j.JournalPath, j.Target)}. Its drive ({j.DriveName}) is not connected, "
            + "or the folder has a new name or location. Connect the drive, or choose the target folder where it is now (“Choose a folder...”), "
            + "or open its log (“Open a log file...”).",
        "undo job" => "This is an undo job. You cannot undo an undo. To separate the files again, start a new sort.",
        "undone" => "This sort is already undone. You can undo a sort only one time. To separate the files again, start a new sort.",
        "undo in progress" => "An undo of this sort started and is not complete. First, resume the undo or end it, with the banner in the main window.",
        "unfinished" => "This sort is not finished. First, resume the sort or end it. Then undo it.",
        "never started" => "This sort never started, so there is nothing to undo.",
        _ => "This sort did not move any files, so there is nothing to undo.",
    };

    /// <summary>After "Open a log file..." found no job log.</summary>
    public const string LogNotFound =
        "This file is not a job log or a receipt, or the app cannot find the job log that it refers to. The drive of that job log is not connected, "
        + "or the target folder has a new name or location. Connect the drive, or choose the target folder where it is now (“Choose a folder...”). "
        + "Then open the log in that folder.";

    /// <summary>
    /// After "Open a log file..." picked a receipt whose job log is not where the receipt says (<paramref name="recordedLog"/>).
    /// <paramref name="why"/>: see <see cref="WhyLogMissing"/>.
    /// </summary>
    public static string ReceiptLogNotFound(string recordedLog, MissingLog why)
    {
        string folder = LogFolderOf(recordedLog, Path.GetDirectoryName(recordedLog) ?? recordedLog);
        return why switch
        {
            MissingLog.FolderMoved => $"The job log that this receipt names is no longer in {folder}. Its drive is connected, so the target folder has a new name or location. "
                + $"Choose the target folder where it is now (“Choose a folder...”), or open the log in its {JobPaths.LogFolderName} folder (“Open a log file...”).",
            MissingLog.Either => $"The job log that this receipt names is not in {folder}. Its drive is not connected, or the target folder has a new name or location. "
                + "You can connect the drive and open the receipt again. Or you can choose the target folder where it is now (“Choose a folder...”).",
            _ => $"The job log that this receipt names is in {folder}, and that drive is not connected. You can connect the drive and open the receipt again. "
                + "Or you can choose the target folder where it is now (“Choose a folder...”).",
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
