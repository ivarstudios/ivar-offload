using System.Globalization;
using System.Text;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>
/// Human-readable outputs next to the journal: a manifest of every file (opens in Excel) and a short summary.
/// A receipt (the same list, plus a few lines in plain words) is also left in the source folder, so every file that
/// left it can be traced from there without the app.
/// </summary>
public static class JobReports
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Writes (best effort) the manifest and summary next to the job log, and the receipt in the source.</summary>
    /// <param name="createReceipt">
    /// The job itself is writing (it has just checked that its source is there): the receipt may be created. Otherwise
    /// an existing receipt is only brought up to date where it already is - never created in whatever folder now has
    /// the source's name (it may be another folder that was given that name).
    /// </param>
    /// <param name="receiptFolder">Where the receipt goes (and may be created) instead of the job's recorded source folder.</param>
    /// <param name="interrupted">
    /// The job is not running (it was stopped, halted or interrupted): what its log says is not moved has not moved.
    /// Otherwise an unfinished job's reports say that a file not moved yet may have moved since they were written.
    /// </param>
    public static void TryWrite(JobState state, bool createReceipt = false, string? receiptFolder = null, bool interrupted = false)
    {
        try
        {
            WriteManifest(state, JobPaths.ManifestPath(state.JournalPath), interrupted);
            WriteText(JobPaths.SummaryPath(state.JournalPath), Summary(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reports can always be regenerated from the journal.
        }
        TryWriteReceipt(state, createReceipt || receiptFolder is not null, receiptFolder, interrupted);
    }

    /// <summary>
    /// Brings the reports and the receipt of a job that is not running up to date from its log - after a crash or a
    /// power cut, those written while it ran can be up to a quarter of a minute behind (see JobRunner's reports). Only
    /// while nobody runs the job: its lock is held meanwhile (a job open elsewhere keeps its reports as they are). An
    /// existing receipt is updated where it is; none is created. Best effort, like every report.
    /// </summary>
    public static void TryRefresh(string journalPath)
    {
        try
        {
            using JournalWriter writer = JournalWriter.OpenForAppend(journalPath, out JobState state);
            if (!state.PlanComplete || state.IsBackup) return;
            TryWrite(state, interrupted: !state.IsEnded);
        }
        catch (Exception e) when (e is JournalException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Replaces a report as a whole: written next to it first, then swapped in, so a crash or power cut while it is
    /// written never leaves a cut-off list behind.
    /// </summary>
    private static void WriteText(string path, string text)
    {
        string temp = path + ".writing";
        try
        {
            File.WriteAllText(temp, text, Utf8WithBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    public static string Status(JobItem item) => item.Stage switch
    {
        ItemStage.Done => "moved",
        _ when item.IsMissing => "missing",
        ItemStage.Skipped => "skipped",
        ItemStage.Placed when item.Failed => "copied, original kept",
        _ when item.Failed => "failed",
        ItemStage.Pending => "not moved",
        _ => "interrupted",
    };

    public static void WriteManifest(JobState state) => WriteManifest(state, JobPaths.ManifestPath(state.JournalPath), interrupted: false);

    private static void WriteManifest(JobState state, string path, bool interrupted)
    {
        // Excel splits columns on the regional list separator (";" in many locales), so use it.
        string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } s ? s : ",";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep, "status", "source", "destination", "size_bytes", "sha256", "method", "modified_utc", "detail", "time"));
        // While the job runs, a file not moved at the time of writing may have moved since; not while it is interrupted.
        string? asOf = state.IsEnded ? null
            : interrupted ? $"not moved yet: the job was interrupted (as of {Now()}); Resume continues it"
            : $"as of {Now()}, while the job was running - it may be in the destination by now";
        foreach (JobItem item in state.Items)
        {
            string detail = (item.Note ?? "") + (item.SetAside is { } kept ? $" (a copy was kept as {Path.Join(state.Header.Target, kept)})" : "");
            if (asOf is not null && item.Stage is not (ItemStage.Done or ItemStage.Skipped) && !item.Failed)
            {
                // A file that was being moved when the job was interrupted is in the source or in the target: Resume decides.
                string note = interrupted && item.Stage != ItemStage.Pending
                    ? $"it was being moved when the job was interrupted (as of {Now()}) - it is in the source or already in the destination; Resume finishes it"
                    : asOf;
                detail = detail.Length > 0 ? $"{detail} ({note})" : note;
            }
            sb.AppendLine(string.Join(sep,
                Status(item),
                Csv(Path.Join(state.Header.Source, item.Rel), sep),
                Csv(Path.Join(state.Header.Target, item.Rel), sep),
                item.Size.ToString(CultureInfo.InvariantCulture),
                item.Sha256 ?? item.ExpectedSha256 ?? "",
                item.How ?? "",
                DateTime.FromFileTimeUtc(item.LastWriteTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(detail, sep),
                item.At ?? ""));
        }
        foreach (HeldFile held in state.HeldBack)
            sb.AppendLine(string.Join(sep, "held back", Csv(Path.Join(state.Header.Source, held.Rel), sep), "", held.Size.ToString(CultureInfo.InvariantCulture),
                "", "", "", Csv(held.Why, sep), ""));
        WriteText(path, sb.ToString());
    }

    public static string Summary(JobState state)
    {
        JobHeader h = state.Header;
        var moved = state.Items.Where(i => i.Stage == ItemStage.Done).ToList();
        var skipped = state.Items.Where(i => i.Stage == ItemStage.Skipped).ToList();
        var failed = state.Items.Where(i => i.Failed && !i.IsFinished).ToList();
        var left = state.StillInSource.ToList();
        var missing = state.Missing.ToList();
        List<HeldFile> held = state.HeldBack;
        int notMoved = state.Items.Count(i => i.Stage == ItemStage.Pending && !i.Failed);
        int identical = state.IdenticalInTargetCount;

        var sb = new StringBuilder();
        sb.AppendLine($"IVAR Offload {(state.IsUndo ? "undo job" : "job")} {h.Id}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Source:    {h.Source}");
        sb.AppendLine($"Target:    {h.Target}");
        if (state.IsUndo)
        {
            sb.AppendLine($"Undoes:    sort job {h.UndoesId} ({h.UndoesJournal})");
            sb.AppendLine($"Moving:    the {Planner.Word(h.Mode)} of that sort, and the files that belong to them, back to where they were");
        }
        else
            sb.AppendLine($"Moving:    {Planner.Word(h.Mode)} and the files that belong to them");
        sb.AppendLine(h.Method == TransferMethod.Rename
            ? "Method:    same drive - files were renamed into place (file data never copied)" + (h.CompareIds ? "; each move confirmed by NTFS file id" : "")
            : "Method:    different drives - each file was copied and read back from disk, and the original was read a second time; "
              + "both had to match the SHA-256 before the original was deleted");
        sb.AppendLine($"Checksums: {(h.Verify ? "SHA-256 recorded for every file" : "not recorded")}");
        sb.AppendLine($"Started:   {h.Created}  on {h.Machine} by {h.User}");
        sb.AppendLine($"Status:    {(state.End is { } end ? $"{end.What} {end.At}" : "not finished (can be resumed)")}");
        if (state.UndoneBy is { } undo)
            sb.AppendLine($"Undone:    {(undo.IsFinished ? $"by undo job {undo.JobId} ({undo.Status} {UndoFactory.Date(undo.At)})" : $"an undo job was started: {undo.JobId}")}");
        sb.AppendLine();
        sb.AppendLine($"Moved:     {moved.Count:N0} files, {Format.Bytes(moved.Sum(i => i.Size))}");
        sb.AppendLine($"Skipped:   {skipped.Count:N0}" + (identical > 0
            ? state.IsUndo ? $" ({identical:N0} because they were already back)" : $" ({identical:N0} because an identical copy was already in the target)"
            : ""));
        sb.AppendLine($"Failed:    {failed.Count:N0}");
        if (notMoved > 0) sb.AppendLine($"Not moved: {notMoved:N0}");
        // An undo's "source" is the sorted folder; its files that did not go back are not necessarily still there.
        string leftTitle = state.IsUndo ? "Not moved back" : "Left in the source";
        int withCopy = left.Count(i => i.Failed && i.Stage == ItemStage.Placed);
        RescanRecord? rescan = state.IsUndo ? null : state.Rescan;
        if (left.Count == 0 && held.Count == 0)
            sb.AppendLine(state.IsUndo ? "Not moved back: nothing."
                : rescan is { Left.Count: > 0 } ? "Still in source: none of the files this sort planned to move."
                : "Still in source: nothing that should have moved.");
        else if (state.IsUndo)
            sb.AppendLine($"Not moved back: {Files(left.Count)} ({Format.Bytes(left.Sum(i => i.Size))}) - see '{leftTitle}' below.");
        else
        {
            if (left.Count > 0)
                sb.AppendLine($"Still in source: {Files(left.Count)} ({Format.Bytes(left.Sum(i => i.Size))}) that should have moved {(left.Count == 1 ? "is" : "are")} still in the source (not moved)"
                    + (withCopy > 0 ? $"; {withCopy:N0} of them also {(withCopy == 1 ? "has" : "have")} a verified copy in the target" : "")
                    + $" - see '{leftTitle}' below.");
            if (held.Count > 0)
                sb.AppendLine($"{(left.Count > 0 ? "Also still" : "Still")} in source: {Files(held.Count)} ({Format.Bytes(held.Sum(h => h.Size))}) of the {Planner.Word(h.Mode)} side "
                    + $"{(held.Count == 1 ? "was" : "were")} kept there by the preview and not moved - see '{HeldTitle}' below.");
        }
        if (rescan is not null)
            sb.AppendLine(RescanLine(rescan));
        if (missing.Count > 0)
            sb.AppendLine($"Missing:   {Files(missing.Count)} ({Format.Bytes(missing.Sum(i => i.Size))}) disappeared from {(state.IsUndo ? "the sorted folder" : "the source")} "
                + $"before {(missing.Count == 1 ? "it" : "they")} could be moved (removed by something else) - {(missing.Count == 1 ? "it is" : "they are")} in neither folder; "
                + $"see '{MissingTitle(state)}' below.");
        foreach (var group in skipped.Where(i => !i.IsMissing).GroupBy(i => i.Note ?? "").OrderByDescending(g => g.Count()))
            sb.AppendLine($"  skipped x{group.Count()}: {group.Key}");
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failed files:");
            foreach (JobItem item in failed) sb.AppendLine($"  {item.Rel}: {item.Note}");
        }
        if (left.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{leftTitle} ({Files(left.Count)}, {Format.Bytes(left.Sum(i => i.Size))}):");
            foreach (var group in left.GroupBy(LeftReason).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  {group.Key} - {Files(group.Count())}, {Format.Bytes(group.Sum(i => i.Size))}:");
                foreach (JobItem item in group)
                    sb.AppendLine($"    {item.Rel}  ({Format.Bytes(item.Size)})"
                        + (item.Failed && item.Stage == ItemStage.Placed ? "  - a verified copy is also in the target" : ""));
            }
        }
        if (held.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{HeldTitle} ({Files(held.Count)}, {Format.Bytes(held.Sum(f => f.Size))}):");
            foreach (var group in held.GroupBy(f => f.Why).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  {(group.Key.Length > 0 ? group.Key : "kept in the source by the preview")} - {Files(group.Count())}, {Format.Bytes(group.Sum(f => f.Size))}:");
                foreach (HeldFile f in group) sb.AppendLine($"    {f.Rel}  ({Format.Bytes(f.Size)})");
            }
        }
        if (rescan is { Left.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"{RescanTitle} ({Files(rescan.Left.Count)}, {Format.Bytes(rescan.Left.Sum(f => f.Size))}; checked {Format.JobDate(rescan.At)}):");
            foreach (var group in rescan.Left.GroupBy(f => f.Why).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  {group.Key} - {Files(group.Count())}, {Format.Bytes(group.Sum(f => f.Size))}:");
                foreach (RescanFile f in group) sb.AppendLine($"    {f.Rel}  ({Format.Bytes(f.Size)})");
            }
        }
        if (rescan is { Unreadable.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Folders of the source that could not be read when it was checked again (what is in them was not checked):");
            foreach (string folder in rescan.Unreadable) sb.AppendLine($"  {folder}");
        }
        if (missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{MissingTitle(state)} ({Files(missing.Count)}, {Format.Bytes(missing.Sum(i => i.Size))}):");
            foreach (JobItem item in missing)
                sb.AppendLine($"    {item.Rel}  ({Format.Bytes(item.Size)})" + (item.SetAside is { } kept ? $"  - a copy was kept in the target as {kept}" : ""));
        }
        var setAside = state.Items.Where(i => i.SetAside is not null).ToList();
        foreach (var kind in setAside.GroupBy(SetAsideKind))
        {
            sb.AppendLine();
            sb.AppendLine(kind.Key);
            foreach (JobItem item in kind) sb.AppendLine($"  {item.SetAside}");
        }
        sb.AppendLine();
        sb.AppendLine($"Every file is listed with its checksum in {Path.GetFileName(JobPaths.ManifestPath(state.JournalPath))}.");
        sb.AppendLine($"The complete step-by-step log is {Path.GetFileName(state.JournalPath)}.");
        sb.AppendLine("A checksum can be checked in PowerShell with:  Get-FileHash -Algorithm SHA256 <file>");
        return sb.ToString();
    }

    /// <summary>"1 file", "12 files".</summary>
    internal static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    private const string HeldTitle = "Left in the source (not part of this sort)";
    private const string RescanTitle = "Found in the source when it was checked again after the job";

    /// <summary>The summary's line about the check of the source after the job (<see cref="SourceCheck.Record"/>).</summary>
    private static string RescanLine(RescanRecord rescan)
    {
        string when = Format.JobDate(rescan.At);
        if (rescan.NotChecked is { } why)
            return $"Checked:   the source could not be checked again after the job ({why}), so files added to it after the preview, or online-only ones, "
                + "were not looked for.";
        string unreadable = rescan.Unreadable.Count == 0 ? ""
            : $" {Format.Count(rescan.Unreadable.Count, "folder")} of the source could not be read, so what is in {(rescan.Unreadable.Count == 1 ? "it" : "them")} "
              + "was not checked - see below.";
        if (rescan.Left.Count == 0)
            return $"Checked:   the source was scanned again after the job ({when}): nothing else that should move was found.{unreadable}";
        int n = rescan.Left.Count;
        return $"Also still in source: {Files(n)} ({Format.Bytes(rescan.Left.Sum(f => f.Size))}) that should move {(n == 1 ? "was" : "were")} found when the source "
            + $"was scanned again after the job ({when}), not part of this sort - see '{RescanTitle}' below."
            + (rescan.MovableNow > 0 ? $" A new sort of the folder can move {Format.Count(rescan.MovableNow, "file")} now." : "")
            + unreadable;
    }
    private static string MissingTitle(JobState state) => state.IsUndo
        ? "Missing from the sorted folder (removed by something else; not back in the original place either)"
        : "Missing from the source (removed by something else; not in the target either)";

    /// <summary>The heading for copies kept in the target under another name, by the suffix they were given and why.</summary>
    private static string SetAsideKind(JobItem item)
    {
        string rel = item.SetAside!;
        if (FailReasons.IsMadeBy(item.Note, FailReasons.VerifiedCopyKeptNotChecked))
            return "Verified copies kept under another name in the target (the job was ended while their originals could not be checked, and another file "
                + "has their name; the originals were not touched):";
        if (FailReasons.IsMadeBy(item.Note, FailReasons.DamagedCopyKeptNotChecked))
            return "Copies kept in the target that do not match their checksum (the job was ended while their originals could not be checked; the originals "
                + "were not touched):";
        return rel.Contains(JobPaths.UnverifiedCopySuffix, StringComparison.OrdinalIgnoreCase)
            ? "Unverified copies kept in the target (never checked - their originals disappeared while they were copied, or the job was ended while the source could not be reached):"
            : rel.Contains(JobPaths.VerifiedCopySuffix, StringComparison.OrdinalIgnoreCase)
                ? "Verified copies kept under another name in the target (their originals are gone, and another file has their name):"
                : "Damaged copies set aside in the target (the target drive changed them after they were verified):";
    }

    private static string LeftReason(JobItem item) =>
        item.Note is { Length: > 0 } note ? note : item.Stage == ItemStage.Pending ? "not moved yet (the job is not finished)" : "interrupted (the job is not finished)";

    // ---- Receipt in the source ------------------------------------------------------------------------------

    /// <summary>
    /// Writes (best effort) the receipt into the source folder's log folder, under the same name as the job's own log
    /// folder (<see cref="JobPaths.LogFolderNameOf"/>): the full file list with destinations and checksums, and a short
    /// text that says what happened and how to undo it.
    /// </summary>
    /// <param name="create">
    /// The receipt may be created (the job is writing it, or <paramref name="folder"/> is where an undo put the files
    /// back). Otherwise only a receipt that already exists is brought up to date.
    /// </param>
    /// <param name="folder">The folder to write into instead of the job's recorded source folder.</param>
    /// <param name="interrupted">The job is not running (see <see cref="TryWrite"/>).</param>
    public static void TryWriteReceipt(JobState state, bool create = false, string? folder = null, bool interrupted = false)
    {
        JobHeader h = state.Header;
        try
        {
            string source = folder ?? h.Source;
            string logFolderName = JobPaths.LogFolderNameOf(state.JournalPath);
            string csv = JobPaths.ReceiptCsvPath(source, h.Id, logFolderName);
            if (!Directory.Exists(source)) return; // never create the source folder
            // Never onto another drive that now has the source's letter (e.g. a second backup copy of the same card).
            if (folder is null && (!Drives.IsOn(h.Source, h.SourceSerial) || !Drives.LeadsTo(h.Source, h.SourceReal))) return;
            bool exists = File.Exists(csv);
            if (!exists && (!create || state.DoneCount == 0)) return; // nothing has left the source (yet), or this may not be its folder
            Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
            WriteManifest(state, csv, interrupted);
            WriteText(JobPaths.ReceiptTextPath(source, h.Id, logFolderName), ReceiptText(state, source, interrupted));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The receipt is a convenience; the job log in the target is the record.
        }
    }

    /// <param name="folder">The folder the receipt is in (the job's source folder, unless it was renamed since).</param>
    /// <param name="interrupted">The job is not running (see <see cref="TryWrite"/>).</param>
    public static string ReceiptText(JobState state, string? folder = null, bool interrupted = false)
    {
        JobHeader h = state.Header;
        var moved = state.Items.Where(i => i.Stage == ItemStage.Done).ToList();
        var left = state.StillInSource.ToList();
        var missing = state.Missing.ToList();
        string when = DateTimeOffset.TryParse(h.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t)
            ? t.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : h.Created ?? "";
        string logFolder = Path.GetDirectoryName(Path.GetFullPath(state.JournalPath)) ?? JobPaths.LogFolder(h.Target);

        var sb = new StringBuilder();
        sb.AppendLine("IVAR Offload - files moved out of this folder");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine(state.IsUndo
            ? $"On {when}, IVAR Offload moved {Files(moved.Count)} ({Format.Bytes(moved.Sum(i => i.Size))}) out of this folder, back to where they were before sort job {h.UndoesId}:"
            : $"On {when}, IVAR Offload moved {Files(moved.Count)} ({Format.Bytes(moved.Sum(i => i.Size))}) out of this folder ({Planner.Word(h.Mode)} and the files that belong to them):");
        sb.AppendLine($"  From: {h.Source}");
        sb.AppendLine($"  To:   {h.Target}");
        sb.AppendLine("Each file kept its folder path, so a file that was in <From>\\a\\b is now in <To>\\a\\b.");
        string asOf = Now();
        if (!state.IsEnded && interrupted)
            sb.AppendLine($"The job is not finished: it was interrupted, and can be resumed or ended in IVAR Offload. This receipt was brought up to date from the job's log "
                + $"at {asOf}, while the job was not running. It is brought up to date again when the job continues or ends.");
        else if (!state.IsEnded)
            sb.AppendLine($"The job is not finished yet. This receipt was written at {asOf}, while the job was running: files it lists as not moved had not moved "
                + $"at that time, and may have been moved to {h.Target} since - if one is not in this folder, look for it there. "
                + "The receipt is brought up to date when the job continues or ends.");
        // Interrupted mid-file: that file is in this folder or already in the target (the job's log can't tell until Resume looks).
        List<JobItem> inFlight = state.IsEnded || !interrupted ? [] : left.Where(i => i.Stage != ItemStage.Pending && !i.Failed).ToList();
        var here = left.Except(inFlight).ToList();
        if (here.Count > 0)
            sb.AppendLine(state.IsEnded || interrupted
                ? $"{Files(here.Count)} ({Format.Bytes(here.Sum(i => i.Size))}) that were planned to move {(here.Count == 1 ? "is" : "are")} still in this folder - the status column of the list says why."
                : $"{Files(here.Count)} ({Format.Bytes(here.Sum(i => i.Size))}) that were planned to move had not left this folder at {asOf} - the status column of the list says why.");
        if (inFlight.Count > 0)
            sb.AppendLine($"{Files(inFlight.Count)} {(inFlight.Count == 1 ? "was" : "were")} being moved when the job was interrupted (\"interrupted\" in the list): "
                + $"{(inFlight.Count == 1 ? "it is" : "each is")} in this folder or already in {h.Target} - Resume (or ending the job) finishes {(inFlight.Count == 1 ? "it" : "them")}.");
        if (state.HeldBack.Count > 0)
            sb.AppendLine($"{Files(state.HeldBack.Count)} ({Format.Bytes(state.HeldBack.Sum(f => f.Size))}) of the {Planner.Word(h.Mode)} side {(state.HeldBack.Count == 1 ? "was" : "were")} not moved and {(state.HeldBack.Count == 1 ? "is" : "are")} still in this folder "
                + "(\"held back\" in the list, with the reason - usually a DIFFERENT file with the same name was already in the target).");
        if (missing.Count > 0)
            sb.AppendLine($"{Files(missing.Count)} ({Format.Bytes(missing.Sum(i => i.Size))}) disappeared from this folder before {(missing.Count == 1 ? "it" : "they")} could be moved (\"missing\" in the list): "
                + $"{(missing.Count == 1 ? "it is" : "they are")} not in {h.Target} either.");
        bool undone = state.UndoneBy is { Status: "completed" };
        if (state.UndoneBy is { IsFinished: true } undo)
        {
            // An undo logs in _IVAROffload; the receipt of a sort logged by an earlier version stays in _IVARIngest or _IngestSorter.
            string? undoLog = undo.JournalPath.Length > 0 ? Path.GetDirectoryName(undo.JournalPath) : null;
            string where = undoLog is null || JobPaths.SamePath(undoLog, JobPaths.ReceiptFolder(folder ?? h.Source, state.JournalPath))
                ? "its log is next to this file"
                : $"its log is in {undoLog}";
            sb.AppendLine(undone
                ? $"This sort was undone on {UndoFactory.Date(undo.At)} (undo job {undo.JobId}, {where}): the files that could go back were moved back here."
                : $"An undo of this sort was ended early on {UndoFactory.Date(undo.At)} (undo job {undo.JobId}, {where}): some of its files were moved back here.");
        }
        sb.AppendLine();
        sb.AppendLine($"Every file is listed with where it went and its SHA-256 checksum in {h.Id}{JobPaths.ReceiptCsvSuffix} (opens in Excel).");
        sb.AppendLine("A checksum can be checked in PowerShell with:  Get-FileHash -Algorithm SHA256 <file>");
        sb.AppendLine();
        if (state.IsUndo) sb.AppendLine("This was an undo. It cannot be undone itself; to separate the files again, run a new sort.");
        else if (!undone) sb.AppendLine($"To undo: open IVAR Offload, click \"Undo a previous sort...\", then \"Open a log file...\" and pick this file (or the job log in {logFolder}).");
        sb.AppendLine();
        sb.AppendLine($"Job: {h.Id}");
        sb.AppendLine($"{ReceiptJournalLabel}{Path.GetFullPath(state.JournalPath)}");
        // Tells "the log's drive is not connected" from "the sorted folder was renamed" when the log can't be found.
        if (h.TargetSerial != 0) sb.AppendLine($"{ReceiptLogDriveLabel}{h.TargetSerial.ToString("X8", CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }

    /// <summary>The receipt line that names the job log (read back by <see cref="UndoFactory.ResolveJournal"/>).</summary>
    internal const string ReceiptJournalLabel = "Job log: ";

    /// <summary>The receipt line with the serial number of the drive that holds the job log (see <see cref="UndoFactory.LogDriveSerial"/>).</summary>
    internal const string ReceiptLogDriveLabel = "Job log drive serial number: ";

    /// <summary>"28 Sep 2026 14:05:09": when a report of a job that is still running was written.</summary>
    private static string Now() => DateTime.Now.ToString("d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Csv(string value, string sep) =>
        value.Contains('"') || value.Contains(sep) || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

public sealed record VerifyResult(int Checked, int Matched, List<string> Problems)
{
    /// <summary>Planned files that did not move (still in the source). Each one is also listed in <see cref="Problems"/>.</summary>
    public int NotMoved { get; init; }
    /// <summary>Files the preview kept in the source (never part of the job, still there). Each one is also listed in <see cref="Problems"/>.</summary>
    public int HeldBack { get; init; }
    /// <summary>Planned files that disappeared from the source before they were moved. Each one is also listed in <see cref="Problems"/>.</summary>
    public int Missing { get; init; }
    public bool AllGood => Problems.Count == 0;
}

/// <summary>
/// Re-reads every moved file from disk and compares it with the checksum recorded when it was moved. Files that were
/// planned but did not move are reported too, so the result is never all-good while something is still in the source.
/// </summary>
public static class JobVerifier
{
    /// <exception cref="JournalException">The job is open in another window (InUse), or its log is damaged.</exception>
    public static VerifyResult Verify(string journalPath, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
    {
        // Hold the job's lock for the whole check: nobody can resume it meanwhile, and the result is recorded
        // without cutting off anything another process wrote.
        JournalWriter? writer = null;
        JobState state;
        try
        {
            writer = JournalWriter.OpenForAppend(journalPath, out state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state = JournalReader.Read(journalPath); // e.g. a write-protected drive: verify anyway, without recording it
        }
        using (writer)
        {
            string target = JobPaths.RootOfJournal(journalPath) ?? state.Header.Target; // where the files are now
            var items = state.Items.Where(i => i.Stage == ItemStage.Done).ToList();
            long total = items.Sum(i => i.Size), done = 0;
            int matched = 0;
            var problems = new List<string>();
            if (state.UndoneBy is { } undo)
                problems.Add($"This sort was undone (undo job {undo.JobId}, {undo.Status}), so its files are no longer expected in the target.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = 0;
            foreach (JobItem item in items)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Join(target, item.Rel);
                IO.FileSnapshot? now = IO.SafeFile.TrySnapshot(path);
                if (now is null || now.IsDirectory) problems.Add($"missing: {path}");
                else if (now.Size != item.Size) problems.Add($"size changed: {path}");
                else if (item.Sha256 is null)
                {
                    if (now.LastWriteTime == (item.SeenLastWriteTime ?? item.LastWriteTime)) matched++;
                    else problems.Add($"modified since the move: {path}");
                }
                else
                {
                    long before = done;
                    string sha = IO.SafeFile.ToHex(IO.SafeFile.HashFile(path, now.Size, n =>
                    {
                        done += n;
                        if (progress is not null && clock.ElapsedMilliseconds - lastReport > 100)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            double rate = clock.Elapsed.TotalSeconds > 0.25 ? done / clock.Elapsed.TotalSeconds : 0;
                            progress.Report(new RunProgress(items.Count, matched + problems.Count, matched, 0, problems.Count, total, done,
                                item.Rel, "Verifying", rate, rate > 0 ? TimeSpan.FromSeconds((total - done) / rate) : null));
                        }
                    }, ct));
                    done = before + item.Size;
                    if (sha == item.Sha256) matched++;
                    else problems.Add($"CHECKSUM MISMATCH: {path}");
                }
            }
            var notMoved = state.StillInSource.ToList();
            foreach (JobItem item in notMoved)
                problems.Add($"not moved (still in the source): {Path.Join(state.Header.Source, item.Rel)} - {item.Note ?? "not moved yet"}");
            foreach (HeldFile held in state.HeldBack)
                problems.Add($"held back by the preview (still in the source): {Path.Join(state.Header.Source, held.Rel)} - {held.Why}");
            foreach (JobItem item in state.Missing)
                problems.Add($"missing (removed from the source before it was moved; not in the target either): {Path.Join(state.Header.Source, item.Rel)}"
                    + (item.SetAside is { } kept ? $" - a copy was kept as {Path.Join(target, kept)}" : ""));
            progress?.Report(new RunProgress(items.Count, items.Count, matched, 0, problems.Count, total, total, null, "Verified", 0, null));

            try
            {
                writer?.Write(new JournalRecord
                {
                    Type = "verify",
                    At = JournalRecord.Now(),
                    Count = items.Count,
                    Done = matched,
                    Failed = problems.Count,
                    Error = problems.Count == 0 ? null : string.Join(" | ", problems.Take(20)),
                });
            }
            catch (JournalException)
            {
                // The result is still returned; recording it is a bonus.
            }
            return new VerifyResult(items.Count, matched, problems) { NotMoved = notMoved.Count, HeldBack = state.HeldBack.Count, Missing = state.MissingCount };
        }
    }
}

/// <summary>Optional clean-up: source folders that only held moved files and are now completely empty.</summary>
public static class EmptyFolders
{
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>Relative paths of removable folders, deepest first. Never the source folder itself.</summary>
    public static List<string> Find(JobState state)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JobItem item in state.Items.Where(i => i.Stage == ItemStage.Done))
            for (string? d = Path.GetDirectoryName(item.Rel); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                candidates.Add(d);

        var removable = new List<string>();
        var removableFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in candidates.OrderByDescending(d => d.Count(c => c == '\\')).ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.Join(state.Header.Source, rel);
            try
            {
                if (!Directory.Exists(full)) continue;
                if (Directory.EnumerateFileSystemEntries(full, "*", AllEntries).All(removableFull.Contains))
                {
                    removable.Add(rel);
                    removableFull.Add(full);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removable;
    }

    /// <summary>Removes the given folders (deepest first) if they are still empty, recording each in the journal.</summary>
    /// <exception cref="JournalException">The job is open in another window (InUse).</exception>
    public static int Remove(string journalPath, IReadOnlyList<string> folders)
    {
        using JournalWriter writer = JournalWriter.OpenForAppend(journalPath, out JobState state);
        int removed = 0;
        foreach (string rel in folders)
        {
            try
            {
                Directory.Delete(Path.Join(state.Header.Source, rel), recursive: false); // fails if anything is inside
                writer.Write(new JournalRecord { Type = "rmdir", Rel = rel, At = JournalRecord.Now() });
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }
}
