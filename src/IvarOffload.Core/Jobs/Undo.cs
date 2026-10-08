using System.Globalization;
using IvarOffload.Core.IO;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>A file of a sort that cannot go back, and why (in words shown to the user).</summary>
public sealed record UndoIssue(JobItem Item, string Reason);

/// <summary>What undoing a sort would do. Built from the sort's log and the disk, without changing anything.</summary>
public sealed class UndoPreview
{
    public required JobState Original { get; init; }
    /// <summary>Why this sort cannot be undone, in plain words; null when it can.</summary>
    public string? Blocked { get; internal set; }
    /// <summary>Where the files are now (the sort's target folder, where its log is).</summary>
    public required string From { get; init; }
    /// <summary>
    /// Where they go back to: the sort's source folder, on the drive the sort recorded (by its serial number), even
    /// when that drive has another letter now (see <see cref="DriveLetterChanged"/>); the folder that holds this sort's
    /// receipt when the source folder was renamed (see <see cref="ToNote"/>); or a folder the user chose.
    /// </summary>
    public required string To { get; init; }
    /// <summary>The drive of the original folder has another letter now; <see cref="To"/> is the same folder on that drive.</summary>
    public bool DriveLetterChanged =>
        !JobPaths.SamePath(To, Original.Header.Source) && !string.Equals(Drives.Letter(To), Drives.Letter(Original.Header.Source), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Below(To), Below(Original.Header.Source), StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Why <see cref="To"/> is not the sort's source folder (it was renamed, or the user chose another folder), in
    /// plain words; null when it is (or when only the drive letter changed).
    /// </summary>
    public string? ToNote { get; init; }
    public TransferMethod Method { get; init; }
    /// <summary>
    /// The folder the files go back to no longer exists; the undo creates it. Only ever on the drive the sort recorded,
    /// recognised by its serial number.
    /// </summary>
    public bool CreatesFolder { get; init; }
    /// <summary>
    /// <see cref="Blocked"/> because the folder the files came from cannot be confirmed (a folder with its name holds
    /// no receipt of this sort, or its drive cannot be matched to the sort). Offer "Choose the folder the files came
    /// from..." and preview again with that folder (UndoFactory.Preview with putBackTo).
    /// </summary>
    public bool NeedsFolder { get; init; }
    /// <summary>Files that will go back.</summary>
    public List<JobItem> GoingBack { get; } = [];
    /// <summary>
    /// Files whose original place is taken by a file with the same name. They are compared when the undo runs:
    /// an identical file is left as it is, a different one is never overwritten (both are kept).
    /// </summary>
    public List<JobItem> PlaceTaken { get; } = [];
    /// <summary>
    /// Files that are already back in their original place (an earlier undo that was ended early moved them, or they
    /// are no longer in the sorted folder and a file of the same size and date is in their original place). They are
    /// not part of the undo.
    /// </summary>
    public List<JobItem> AlreadyBack { get; } = [];
    /// <summary>Files that cannot go back (gone from the sorted folder, or changed since the sort). They stay where they are.</summary>
    public List<UndoIssue> CannotGoBack { get; } = [];

    public long GoingBackBytes => GoingBack.Sum(i => i.Size);
    public bool CanRun => Blocked is null && GoingBack.Count + PlaceTaken.Count > 0;

    /// <summary>The preview in a few plain sentences.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>();
        if (Blocked is not null)
        {
            lines.Add(Blocked);
            return lines;
        }
        int count = GoingBack.Count + PlaceTaken.Count;
        lines.Add(count == 0
            ? $"Nothing can return to {To}."
            : $"{JobReports.Files(count)} ({Format.Bytes(GoingBackBytes + PlaceTaken.Sum(i => i.Size))}) will return from {From} to {To}.");
        if (DriveLetterChanged)
            lines.Add($"The drive of {Original.Header.Source} has a different letter now ({Drives.Letter(To)}), so the files return to {To}.");
        if (ToNote is not null) lines.Add(ToNote);
        if (count > 0)
            lines.Add(Method == TransferMethod.Rename
                ? "Same drive: the undo renames each file to its old path. It copies no file data."
                    + (Original.Header.Verify
                        ? " The undo compares the checksum of each file with the checksum that the sort recorded. If a file changed, it stays where it is."
                        : "")
                : "Different drives: the undo copies each file to its old folder and reads the copy again from the disk. "
                  + "It also reads the file in the target folder a second time. "
                  + "The undo removes a file from the target folder only when the copy and the second read both match the checksum that the sort recorded.");
        if (CreatesFolder) lines.Add($"{To} no longer exists. The undo will make this folder again, on the drive that the files came from.");
        if (PlaceTaken.Count > 0)
            lines.Add($"{JobReports.Files(PlaceTaken.Count)} {(PlaceTaken.Count == 1 ? "has" : "have")} a file with the same name in the original location. "
                + "The undo compares the two files. If they are identical, the undo does not change them. "
                + "If they are different, the undo never overwrites a file: it keeps both.");
        if (AlreadyBack.Count > 0)
            lines.Add($"{JobReports.Files(AlreadyBack.Count)} {(AlreadyBack.Count == 1 ? "is" : "are")} already back in the original location. "
                + $"The undo does not include {(AlreadyBack.Count == 1 ? "it" : "them")}.");
        foreach (var group in CannotGoBack.GroupBy(i => i.Reason).OrderByDescending(g => g.Count()))
            lines.Add($"{JobReports.Files(group.Count())} cannot return: {group.Key}.");
        return lines;
    }

    private static string Below(string path) => Path.GetRelativePath(Path.GetPathRoot(path) ?? path, path);
}

/// <summary>How an undo was asked for: where the user found the sort, and where the files should go back to.</summary>
/// <param name="StartedFrom">
/// What the user opened to find the sort: a receipt (".moved-out.txt" / ".moved-out.csv") or a folder. When that folder
/// holds this sort's receipt, it is where the files go back (the original folder may have been renamed since).
/// </param>
/// <param name="PutBackTo">A folder the user chose to put the files back into, when the original folder cannot be confirmed.</param>
internal sealed record UndoRequest(string? StartedFrom = null, string? PutBackTo = null, TransferMethod? Method = null, long? FreeBytes = null);

/// <summary>
/// Undo of a finished sort: an ordinary job (same journal, same crash safety) that moves the sort's files from its
/// target back to its source. Its log is in the source folder's _IVAROffload folder, and the sort's own log records
/// that it was undone, so it cannot be undone twice.
/// </summary>
public static class UndoFactory
{
    private const string UndoneStarted = "started";

    /// <summary>What undoing the sort in this log would do. Reads only; nothing is written.</summary>
    public static UndoPreview Preview(string originalJournalPath) => Preview(originalJournalPath, new UndoRequest());

    /// <summary>What undoing the sort in this log would do. Reads only; nothing is written.</summary>
    /// <param name="startedFrom">
    /// What the user opened to find the sort: its receipt or a folder. When that folder holds this sort's receipt, the
    /// files go back there (the original folder may have been renamed since).
    /// </param>
    /// <param name="putBackTo">A folder the user chose for the files (when <see cref="UndoPreview.NeedsFolder"/>).</param>
    public static UndoPreview Preview(string originalJournalPath, string? startedFrom, string? putBackTo = null) =>
        Preview(originalJournalPath, new UndoRequest(startedFrom, putBackTo));

    internal static UndoPreview Preview(string originalJournalPath, TransferMethod? method, long? freeBytes) =>
        Preview(originalJournalPath, new UndoRequest(Method: method, FreeBytes: freeBytes));

    internal static UndoPreview Preview(string originalJournalPath, UndoRequest request)
    {
        string journalPath = Path.GetFullPath(originalJournalPath);
        return Build(JournalReader.Read(journalPath), journalPath, request);
    }

    /// <summary>The preview of undoing this sort (also used, under the sort's lock, to create the undo job).</summary>
    private static UndoPreview Build(JobState original, string journalPath, UndoRequest request)
    {
        JobHeader h = original.Header;
        string from = JobPaths.RootOfJournal(journalPath) ?? h.Target;
        Destination where = WhereBack(original, from, request);
        string to = where.To;
        List<JobState> undoJobs = UndoJobsFor(to, h.Id);
        string? blocked = WhyBlocked(original, journalPath, undoJobs, where.Blocked);
        TransferMethod how = TransferMethod.Rename;
        VolumeInfo? toVolume = null;
        if (blocked is null)
        {
            try
            {
                toVolume = VolumeInfo.Of(to);
                how = request.Method ?? (VolumeInfo.Of(from).IsSameVolume(toVolume) ? TransferMethod.Rename : TransferMethod.Copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                blocked = $"The drives are not readable: {e.Message}";
            }
        }
        var preview = new UndoPreview
        {
            Original = original,
            Blocked = blocked,
            From = from,
            To = to,
            ToNote = where.Note,
            Method = how,
            CreatesFolder = blocked is null && where.CreatesFolder,
            NeedsFolder = blocked is not null && blocked == where.Blocked && where.NeedsFolder,
        };
        if (blocked is not null) return preview;

        // Files an earlier undo of this sort (one that was ended early) already put back are not part of this one.
        var back = undoJobs.SelectMany(j => j.Items.Where(i => i.Stage == ItemStage.Done || (i.Stage == ItemStage.Skipped && SkipReasons.IsIdenticalInTarget(i.Note))))
            .Select(i => i.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (JobItem item in original.Items.Where(i => i.Stage == ItemStage.Done))
        {
            if (back.Contains(item.Rel))
            {
                preview.AlreadyBack.Add(item);
                continue;
            }
            long lastWrite = item.SeenLastWriteTime ?? item.LastWriteTime;
            try
            {
                FileSnapshot? now = SafeFile.TrySnapshot(Path.Join(from, item.Rel));
                FileSnapshot? there = SafeFile.TrySnapshot(Path.Join(to, item.Rel));
                if (now is null || now.IsDirectory)
                {
                    if (there is { IsDirectory: false } && there.Size == item.Size && there.LastWriteTime == lastWrite) preview.AlreadyBack.Add(item);
                    else preview.CannotGoBack.Add(new UndoIssue(item, SkipReasons.NotInSortedFolder));
                }
                else if (now.Size != item.Size || (item.Sha256 is null && now.LastWriteTime != lastWrite))
                    preview.CannotGoBack.Add(new UndoIssue(item, SkipReasons.ChangedSinceSorted));
                else if (there is not null)
                    preview.PlaceTaken.Add(item);
                else
                    preview.GoingBack.Add(item);
            }
            catch (IOException e)
            {
                preview.CannotGoBack.Add(new UndoIssue(item, e.Message));
            }
        }

        // Copying back needs room for the files that go back (a file whose place is taken is never copied).
        if (how == TransferMethod.Copy && toVolume is not null && (request.FreeBytes ?? toVolume.FreeBytes) is var free
            && preview.GoingBackBytes + Planner.FreeSpaceMargin > free)
            preview.Blocked = $"Not enough free space on {toVolume.DisplayNameWithLabel} to return the files: the files need {Format.Bytes(preview.GoingBackBytes)}, "
                + $"and {Format.Bytes(Math.Max(0, free - Planner.FreeSpaceMargin))} is available. Make more space available on the drive. Then try the undo again.";
        return preview;
    }

    /// <summary>Creates the undo job for a finished sort and returns a runner for it.</summary>
    /// <exception cref="JournalException">The sort cannot be undone (the message says why in plain words).</exception>
    public static JobRunner Start(string originalJournalPath, RunOptions? options = null) =>
        JobRunner.Open(CreateUndoJournal(originalJournalPath), options);

    /// <summary>Creates the undo job for a finished sort (see <see cref="Preview(string, string?, string?)"/>) and returns a runner for it.</summary>
    /// <exception cref="JournalException">The sort cannot be undone (the message says why in plain words).</exception>
    public static JobRunner Start(string originalJournalPath, string? startedFrom, string? putBackTo, RunOptions? options = null) =>
        JobRunner.Open(CreateUndoJournal(originalJournalPath, startedFrom, putBackTo), options);

    /// <summary>
    /// Writes the undo job's log (in the sort's source folder) and marks the sort as being undone. Returns the path of
    /// the undo job's log; run it with <see cref="JobRunner.Open"/>.
    /// </summary>
    /// <exception cref="JournalException">The sort cannot be undone (the message says why in plain words).</exception>
    public static string CreateUndoJournal(string originalJournalPath) => CreateUndoJournal(originalJournalPath, new UndoRequest());

    /// <inheritdoc cref="CreateUndoJournal(string)"/>
    /// <param name="startedFrom">What the user opened to find the sort (see <see cref="Preview(string, string?, string?)"/>).</param>
    /// <param name="putBackTo">A folder the user chose for the files.</param>
    public static string CreateUndoJournal(string originalJournalPath, string? startedFrom, string? putBackTo = null) =>
        CreateUndoJournal(originalJournalPath, new UndoRequest(startedFrom, putBackTo));

    internal static string CreateUndoJournal(string originalJournalPath, TransferMethod? method, long? freeBytes = null) =>
        CreateUndoJournal(originalJournalPath, new UndoRequest(Method: method, FreeBytes: freeBytes));

    internal static string CreateUndoJournal(string originalJournalPath, UndoRequest request)
    {
        try
        {
            return CreateUndoJournalLocked(Path.GetFullPath(originalJournalPath), request);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new JournalException($"The undo could not start: {e.Message}", e);
        }
    }

    private static string CreateUndoJournalLocked(string journalPath, UndoRequest request)
    {
        // Holding the sort's lock: nobody can run it, verify it or start another undo of it meanwhile.
        using JournalWriter original = JournalWriter.OpenForAppend(journalPath, out JobState state);
        JobHeader h = state.Header;
        UndoPreview preview = Build(state, journalPath, request);
        if (preview.Blocked is { } blocked) throw new JournalException(blocked);
        if (!preview.CanRun) throw new JournalException(NothingCanGoBack(preview));
        string from = preview.From, to = preview.To;
        TransferMethod how = preview.Method;
        // A folder is only ever recreated on the drive the files came from (matched by its serial number).
        if (!preview.CreatesFolder && !SafeFile.DirectoryExists(to)) throw new JournalException($"The folder {to} no longer exists. Start the undo again to see where the files can go.");

        VolumeInfo fromVolume = VolumeInfo.Of(from), toVolume = VolumeInfo.Of(to);
        // Every moved file except those already back: the ones that cannot go back are reported by the undo, with why.
        HashSet<JobItem> alreadyBack = [.. preview.AlreadyBack];
        var items = state.Items.Where(i => i.Stage == ItemStage.Done && !alreadyBack.Contains(i)).ToList();
        long bytes = items.Sum(i => i.Size);

        string logFolder = JobPaths.LogFolder(to);
        Directory.CreateDirectory(logFolder); // also recreates the source folder when the preview said so (see CreatesFolder)
        string id = JobFactory.NewJobId("undo");
        string path = Path.Join(logFolder, id + JobPaths.JournalSuffix);
        try
        {
            using (JournalWriter writer = JournalWriter.CreateNew(path))
            {
                writer.Write(new JournalRecord
                {
                    Type = "job",
                    Version = JobFactory.JournalVersion,
                    JobId = id,
                    Kind = "undo",
                    Undoes = journalPath,
                    UndoesId = h.Id,
                    At = JournalRecord.Now(),
                    Tool = JobFactory.ToolName,
                    Machine = Environment.MachineName,
                    User = Environment.UserName,
                    Source = from,
                    Target = to,
                    SourceSerial = fromVolume.SerialNumber,
                    SourceLabel = fromVolume.Label,
                    TargetSerial = toVolume.SerialNumber,
                    TargetLabel = toVolume.Label,
                    SourceReal = Drives.RealIfSubst(from),
                    TargetReal = Drives.RealIfSubst(to),
                    Mode = h.Mode.ToString(),
                    Method = how.ToString(),
                    Verify = h.Verify || how == TransferMethod.Copy,
                    CompareIds = fromVolume.HasStableFileIds,
                    Count = items.Count,
                    Bytes = bytes,
                }, flush: false);
                // The folders get back the dates they had before the sort.
                foreach ((string rel, FolderTimes times) in state.SourceFolders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    writer.Write(new JournalRecord { Type = "dir", Rel = rel, CreationTime = times.CreationTime, LastWriteTime = times.LastWriteTime }, flush: false);
                int index = 0;
                foreach (JobItem item in items)
                {
                    writer.Write(new JournalRecord
                    {
                        Type = "item",
                        Index = index++,
                        Rel = item.Rel,
                        Group = item.Group.Length > 0 ? item.Group : null,
                        Size = item.Size,
                        CreationTime = item.SeenCreationTime ?? item.CreationTime,
                        LastWriteTime = item.SeenLastWriteTime ?? item.LastWriteTime,
                        Why = $"moved back by undoing sort {h.Id}",
                        Sha256 = item.Sha256, // the file only goes back if it still has this checksum
                    }, flush: false);
                }
                writer.Write(new JournalRecord { Type = "ready", Count = items.Count, Bytes = bytes });
            }
            original.Write(new JournalRecord { Type = "undo", What = UndoneStarted, JobId = id, JournalFile = path, At = JournalRecord.Now() });
        }
        catch
        {
            TryDelete(path); // nothing has run yet
            throw;
        }
        RecentJobs.Add(path);
        return path;
    }

    /// <summary>
    /// Called when an undo job ends: records in the sort's log that it was undone (and how many of its files were not
    /// moved back), and refreshes its reports. The sort's receipt is rewritten only in the folder the files went back
    /// to - the folder the undo proved to be the sort's source - never in whatever folder now has its old name.
    /// </summary>
    internal static void RecordUndoEnded(JobState undo, string how)
    {
        JobHeader h = undo.Header;
        string? path = FindOriginal(h);
        if (path is null) return;
        try
        {
            using JournalWriter writer = JournalWriter.OpenForAppend(path, out JobState original);
            var record = new JournalRecord
            {
                Type = "undo", What = how, JobId = h.Id, JournalFile = Path.GetFullPath(undo.JournalPath), At = JournalRecord.Now(),
                Skipped = undo.StillInSourceCount + undo.MissingCount, // files not moved back
            };
            writer.Write(record);
            original.UndoneBy = new UndoMark(h.Id, record.JournalFile, record.At, how, record.Skipped);
            JobReports.TryWrite(original, receiptFolder: h.Target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
            // The undo job's own log is the record; this mark is a convenience for the sort's reports.
        }
    }

    /// <summary>
    /// Why the sort in this log cannot be undone now, in plain words; null when it can. An undo that was ended early,
    /// or that completed but left files in the sorted folder, can be followed by a new undo of what is still there.
    /// </summary>
    private static string? WhyBlocked(JobState original, string journalPath, List<JobState> undoJobs, string? notConnected)
    {
        if (original.IsBackup) return "This is the log of a backup. A backup only copies, so there is nothing to undo.";
        if (original.IsUndo) return "This is an undo job. You cannot undo an undo. To separate the files again, start a new sort.";
        if (!original.PlanComplete) return "This sort never started, so there is nothing to undo.";
        if (!original.IsEnded) return "This sort is not finished yet. Resume it or end it first. Then undo it.";
        if (original.DoneCount == 0) return "There is nothing to undo: this sort did not move any files.";
        if (JobPaths.RootOfJournal(journalPath) is null)
            return $"Open the job log in the {JobPaths.LogFolderName} folder (or {JobPaths.IvarIngestLogFolderName} or {JobPaths.IngestSorterLogFolderName} for older sorts) in the target folder, "
                + "not a copy of the log. The undo can find the files only from that log.";

        // The undo jobs' own logs are the truth (an undo's end may not have reached the sort's log).
        foreach (JobState undo in undoJobs)
        {
            if (!undo.IsEnded) return $"An undo of this sort is already in progress (undo job {undo.Header.Id}). Resume that job instead.";
            if (undo.End!.What == "completed" && undo.StillInSourceCount + undo.MissingCount == 0)
                return $"This sort is already undone (undo job {undo.Header.Id}, {Date(undo.End.At)}).";
        }
        if (original.UndoneBy is { } mark && !undoJobs.Any(j => j.Header.Id == mark.JobId))
        {
            // That undo's log is not in the original folder (its drive is not connected, or the log was removed): trust the mark.
            if (mark.Status == "completed") return $"This sort is already undone (undo job {mark.JobId}, {Date(mark.At)}).";
            if (mark.Status == UndoneStarted && File.Exists(mark.JournalPath))
                return $"An undo of this sort is already in progress (undo job {mark.JobId}). Resume that job instead.";
            // "closed", or an undo whose log was deleted before it ran: a new undo takes what is left.
        }
        return notConnected;
    }

    /// <summary>Where the files go back to; <see cref="Blocked"/> says why they can't when it is not null.</summary>
    private sealed record Destination(string To, string? Blocked = null, bool CreatesFolder = false, string? Note = null, bool NeedsFolder = false);

    /// <summary>
    /// Where the files go back to: the folder the files came from, which must be proven to be it - it holds this sort's
    /// receipt, or (older sorts left none) a folder or a file the sort recorded, with the same dates. That is the sort's
    /// source folder on the drive the sort recorded (by its volume serial number, also under another letter), or the
    /// folder with the receipt when the source folder was renamed. A folder that merely has the old name (another card,
    /// dumped into a new folder with the same name) is never taken, and a missing folder is only recreated on a drive
    /// matched by its serial number. A folder the user chose is taken as it is.
    /// </summary>
    private static Destination WhereBack(JobState sort, string from, UndoRequest request)
    {
        JobHeader h = sort.Header;
        string drive = h.SourceLabel.Length > 0 ? $" ({h.SourceLabel})" : "";

        if (request.PutBackTo is { } chosen) return Chosen(chosen, from);
        // The folder the user found the sort in, when it holds the sort's receipt. It is where the files go back only
        // when the folder they came from is not still in its place: it may as well be a copy of that folder (a backup
        // or an archive copies the receipt too).
        string? started = FolderOf(request.StartedFrom) is { } opened && !JobPaths.SamePath(opened, from) && HasReceipt(opened, h.Id) ? opened : null;

        var candidates = new List<string>();
        if (Drives.IsOn(h.Source, h.SourceSerial) && Drives.LeadsTo(h.Source, h.SourceReal)) candidates.Add(h.Source); // not a subst letter given another folder
        // A source named through a subst drive letter: the folder that letter led to, also once the letter is gone.
        if (h.SourceReal is { } real && !JobPaths.SamePath(real, h.Source) && Drives.IsOn(real, h.SourceSerial)) candidates.Add(real);
        candidates.AddRange(Drives.Elsewhere(Drives.OnDrive(h.Source, h.SourceReal), h.SourceSerial, preferred: from)
            .Where(c => !candidates.Any(known => JobPaths.SamePath(c, known))).ToList());

        // First the folder the files came from, still in its place on the drive the sort recorded, and proven to be it.
        foreach (string candidate in candidates.Where(SafeFile.DirectoryExists))
        {
            bool elsewhere = started is not null && !JobPaths.SamePath(started, candidate);
            if (HasReceipt(candidate, h.Id))
                return new Destination(candidate, Note: elsewhere ? CopyOfReceipt(started!, candidate) : null);
            if (Trusted(candidate, h, from) && HasRecordedContent(candidate, sort))
            {
                // Two folders could be it: the one where the sort left its receipt, and one that only matches what the
                // sort recorded (a copy keeps the files' dates). The user decides.
                if (elsewhere)
                    return new Destination(candidate, NeedsFolder: true, Blocked:
                        $"It is not clear which folder the files came from. {started} has the receipt of this sort. {candidate} matches the data that the sort recorded, "
                        + "but it has no receipt. Choose the folder that the files return to.");
                return new Destination(candidate);
            }
        }
        // The folder the files came from is not in its place any more (or can't be proven to be it): the folder the user
        // found the sort in holds its receipt, so it is that folder, renamed or moved.
        if (started is not null) return new Destination(started, Note: SameFolder(started, h.Source) ? null : Renamed(h.Source, started));

        string? unconfirmed = null, creatable = null, otherLetter = null;
        foreach (string candidate in candidates)
        {
            bool sameLetter = IsRecordedSource(candidate, h);
            if (SafeFile.DirectoryExists(candidate))
            {
                if (sameLetter) unconfirmed ??= candidate;
            }
            else if (Trusted(candidate, h, from) && h.SourceSerial != 0 && Drives.SerialOf(candidate) == h.SourceSerial) creatable ??= candidate;
            if (RenamedFolder(candidate, sort, from) is { } renamed) return new Destination(renamed, Note: Renamed(h.Source, renamed));
            if (!sameLetter) otherLetter ??= candidate;
        }

        if (unconfirmed is not null)
            return new Destination(unconfirmed, NeedsFolder: true, Blocked:
                $"The folder {unconfirmed} is not the folder that the files came from. It has no receipt of this sort, and nothing in it matches the data that the sort recorded. "
                + "It is possible that someone renamed the source folder and gave its name to a different folder. Choose the folder that the files came from.");
        if (creatable is not null) return new Destination(creatable, CreatesFolder: true);
        if (otherLetter is not null)
            return new Destination(h.Source, Blocked:
                $"It is possible that the drive that held {h.Source}{drive} has a different letter now ({Drives.Letter(otherLetter)}). "
                + $"But the undo cannot confirm that {otherLetter} is the folder that the files came from. "
                + $"In Windows Disk Management, use Change Drive Letter to give the drive its old letter ({Drives.Letter(h.Source)}) again. Then try the undo again.");
        if (candidates.Count > 0)
            return new Destination(h.Source, NeedsFolder: true, Blocked:
                $"{h.Source} no longer exists. The undo cannot confirm that the drive at {Drives.Letter(h.Source)} is the drive that the files came from "
                + "(the sort recorded no serial number for it, or the drive is not readable). For this reason, the undo does not make the folder on that drive. "
                + "Choose the folder that the files return to.");
        return new Destination(h.Source, Blocked: $"The drive that held {h.Source}{drive} is not connected. Connect it. Then try the undo again.");
    }

    /// <summary>A folder the user chose: taken as it is, as long as it exists and is not the sorted folder.</summary>
    private static Destination Chosen(string chosen, string from)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen.Trim().Trim('"')));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Destination(chosen, NeedsFolder: true, Blocked: $"{chosen} is not a folder path.");
        }
        if (!SafeFile.DirectoryExists(full)) return new Destination(full, NeedsFolder: true, Blocked: $"The folder {full} does not exist. Choose the folder that the files came from.");
        // (The sorted folder may be inside the folder the files came from: a target inside the source is allowed.)
        if (JobPaths.SamePath(full, from) || Planner.IsInside(full, from))
            return new Destination(full, NeedsFolder: true,
                Blocked: $"The files cannot return to {full}: it is the target folder of the sort, or a folder in it. Choose the folder that the files came from.");
        return new Destination(full, Note: $"The files return to {full}, the folder that you chose.");
    }

    /// <summary>
    /// On the sort's own drive letter, and on the drive that holds its log (a sort within one drive), what the sort
    /// recorded proves the folder. On any other drive only the receipt does, so a cloned drive (same serial number, same
    /// folder dates) is never taken for the original.
    /// </summary>
    private static bool Trusted(string candidate, JobHeader h, string from) =>
        IsRecordedSource(candidate, h)
        || (JobPaths.SamePath(Path.GetPathRoot(candidate)!, Path.GetPathRoot(from)!) && h.SourceSerial == h.TargetSerial);

    /// <summary>The sort's source folder as it recorded it: by its path, or where its subst drive letter led (<see cref="JobHeader.SourceReal"/>).</summary>
    private static bool IsRecordedSource(string candidate, JobHeader h) =>
        JobPaths.SamePath(candidate, h.Source) || h.SourceReal is { } real && JobPaths.SamePath(candidate, real);

    private static string Renamed(string source, string now) => SafeFile.DirectoryExists(source)
        ? $"The undo can no longer confirm that {source} is the folder that the files came from. "
          + $"Probably, someone renamed or moved that folder and gave its name to a different folder. The files return to {now}, which has the receipt of this sort."
        : $"{source} is no longer there (someone renamed or moved it). The files return to {now}, which has the receipt of this sort.";

    private static string CopyOfReceipt(string started, string original) =>
        $"The files return to {original}, the folder that they came from (it is still there, with the receipt of this sort). "
        + $"{started} has a copy of the receipt (it is probably a copy of that folder). The undo does not change {started}. "
        + "To return the files to a different folder, choose that folder.";

    private static bool SameFolder(string a, string b) => JobPaths.SamePath(a, b);

    /// <summary>The folder a receipt, a log folder or a folder the user opened stands for; null when there is none.</summary>
    private static string? FolderOf(string? startedFrom)
    {
        if (string.IsNullOrWhiteSpace(startedFrom)) return null;
        try
        {
            string full = Path.GetFullPath(startedFrom.Trim().Trim('"'));
            if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
            if (JobPaths.IsLogFolderName(Path.GetFileName(full))) full = Path.GetDirectoryName(full)!;
            return SafeFile.DirectoryExists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder the files came from was renamed (or moved) next to where it was: the one folder beside it that holds
    /// this sort's receipt. Null when there is none, or more than one (e.g. copies of it).
    /// </summary>
    private static string? RenamedFolder(string recorded, JobState sort, string from)
    {
        try
        {
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(recorded));
            if (parent is null || !SafeFile.DirectoryExists(parent)) return null;
            var found = Directory.EnumerateDirectories(parent)
                .Where(d => !JobPaths.SamePath(d, recorded) && !JobPaths.SamePath(d, from) && HasReceipt(d, sort.Header.Id))
                .Take(2).ToList();
            return found.Count == 1 ? Path.GetFullPath(found[0]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Proof for sorts without a receipt in the folder (older sorts, or a receipt someone deleted): a folder the sort
    /// recorded is there with the same creation time, or a file it did not move is there with the same size and date.
    /// </summary>
    private static bool HasRecordedContent(string folder, JobState sort)
    {
        const int Limit = 500; // enough to find a match in the right folder, without scanning a huge one for nothing
        try
        {
            var removed = new HashSet<string>(sort.RemovedFolders, StringComparer.OrdinalIgnoreCase);
            foreach ((string rel, FolderTimes times) in sort.SourceFolders.Where(f => f.Value.CreationTime != 0 && !removed.Contains(f.Key)).Take(Limit))
                if (SafeFile.TrySnapshot(Path.Join(folder, rel)) is { IsDirectory: true } d && d.CreationTime == times.CreationTime) return true;
            var stayed = sort.Items.Where(i => i.Stage != ItemStage.Done).Select(i => (i.Rel, i.Size, i.LastWriteTime))
                .Concat(sort.HeldBack.Select(f => (f.Rel, f.Size, f.LastWriteTime)));
            foreach ((string rel, long size, long lastWrite) in stayed.Where(f => f.Size > 0 && f.LastWriteTime != 0).Take(Limit))
                if (SafeFile.TrySnapshot(Path.Join(folder, rel)) is { IsDirectory: false } f && f.Size == size && f.LastWriteTime == lastWrite)
                    return true;
        }
        catch (IOException)
        {
        }
        return false;
    }

    /// <summary>The folder holds this job's receipt (in either log folder).</summary>
    private static bool HasReceipt(string folder, string jobId) =>
        JobPaths.LogFolderNames.Any(name =>
            File.Exists(JobPaths.ReceiptTextPath(folder, jobId, name)) || File.Exists(JobPaths.ReceiptCsvPath(folder, jobId, name)));

    private static string NothingCanGoBack(UndoPreview preview) => preview.CannotGoBack.Count == 0
        ? $"There is nothing to undo: every file of this sort is already back in {preview.To}."
        : "No file can return. " + string.Join(". ", preview.CannotGoBack.GroupBy(i => i.Reason).Select(g => $"{JobReports.Files(g.Count())}: {g.Key}")) + ".";

    /// <summary>
    /// Undo jobs of the given sort whose logs are in the folder's log folders. An undo log whose plan was never
    /// completely written (the app was stopped while creating it) never ran and is ignored.
    /// </summary>
    private static List<JobState> UndoJobsFor(string to, string sortId)
    {
        var result = new List<JobState>();
        List<string> journals;
        try
        {
            journals = JobPaths.FindJournals(to).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (string journal in journals)
        {
            JobHeader? header = JournalReader.TryReadHeader(journal);
            if (header is not { Kind: JobKind.Undo } || header.UndoesId != sortId) continue;
            try
            {
                JobState state = JournalReader.Read(journal);
                if (state.PlanComplete) result.Add(state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
            {
            }
        }
        return result;
    }

    /// <summary>The sort job an undo job belongs to: its recorded log, or the log with its id in the undo's source folder.</summary>
    private static string? FindOriginal(JobHeader undo)
    {
        if (undo.UndoesJournal is { } recorded && File.Exists(recorded)) return recorded;
        if (undo.UndoesId is null) return null;
        return JobPaths.LogFolderNames.Select(name => Path.Join(undo.Source, name, undo.UndoesId + JobPaths.JournalSuffix)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Finds the job log for any file a job wrote: the log itself, its manifest or summary, or a receipt
    /// (".moved-out.csv" / ".moved-out.txt") left in the source folder. Null when it cannot be found.
    /// </summary>
    public static string? ResolveJournal(string anyPath) => ResolveJournal(anyPath, out _);

    /// <inheritdoc cref="ResolveJournal(string)"/>
    /// <param name="whyNot">
    /// When the log cannot be found, why, in plain words: its drive is not connected, or (the drive is there) the
    /// sorted folder was renamed or moved somewhere it could not be found.
    /// </param>
    public static string? ResolveJournal(string anyPath, out string? whyNot)
    {
        whyNot = null;
        string full;
        try
        {
            full = Path.GetFullPath(anyPath.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            whyNot = $"{anyPath} is not a file path.";
            return null;
        }
        string name = Path.GetFileName(full), folder = Path.GetDirectoryName(full) ?? "";
        if (name.EndsWith(JobPaths.JournalSuffix, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(full)) return full;
            whyNot = $"The job log {full} does not exist.";
            return null;
        }
        foreach (string suffix in new[] { ".manifest.csv", ".summary.txt" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string journal = full[..^suffix.Length] + JobPaths.JournalSuffix;
                if (File.Exists(journal)) return journal;
                whyNot = $"There is no job log next to {full} (the expected name is {Path.GetFileName(journal)}).";
                return null;
            }
        foreach (string suffix in new[] { JobPaths.ReceiptCsvSuffix, JobPaths.ReceiptTextSuffix })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string id = name[..^suffix.Length];
                string? recorded = ReceiptJournal(Path.Join(folder, id + JobPaths.ReceiptTextSuffix));
                if (recorded is not null)
                {
                    try
                    {
                        if (File.Exists(recorded)) return recorded;
                        // On another PC (or after re-plugging) the drive may have another letter: the same log on
                        // another drive, the receipt's own drive first. The job id in the log must match.
                        foreach (string elsewhere in Drives.OnOtherLetters(recorded, preferred: full))
                            if (File.Exists(elsewhere) && JournalReader.TryReadHeader(elsewhere)?.Id == id) return elsewhere;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                    }
                }
                // Where this PC last saw the log (it follows moves it found before), then a search for the sorted
                // folder, renamed or moved (see FindMovedJournal).
                List<RecentJob> remembered = RecentJobs.Entries().Where(e => e.JobId == id).ToList();
                if (remembered.FirstOrDefault(e => e.IsConnected) is { } known) return known.JournalPath;
                if (recorded is not null && JobPaths.FindMovedJournal(recorded, id) is { } moved)
                {
                    RecentJobs.Add(moved);
                    return moved;
                }
                foreach (RecentJob entry in remembered.Where(e => recorded is null || !JobPaths.SamePath(e.JournalPath, recorded)))
                    if (RecentJobs.FindMoved(entry) is { } found) return found;
                whyNot = recorded is null
                    ? $"The receipt {id}{JobPaths.ReceiptTextSuffix} (which names the job log) is not next to {full}."
                    : JobPaths.WhyNotReachable(recorded, LogDriveSerial(Path.Join(folder, id + JobPaths.ReceiptTextSuffix), id), Drives.Letter(recorded));
                return null;
            }
        whyNot = $"{full} is not a job log, the manifest or summary of a job, or a receipt.";
        return null;
    }

    /// <summary>The job log a receipt names ("Job log: ..."); null when the receipt is not there or names none.</summary>
    internal static string? ReceiptJournal(string receiptText) => ReceiptLine(receiptText, JobReports.ReceiptJournalLabel);

    /// <summary>
    /// Serial number of the drive that holds the job log: from the receipt (receipts written since this version name
    /// it), else from this PC's recent-jobs list; 0 when unknown.
    /// </summary>
    /// <param name="receiptText">The receipt's .moved-out.txt.</param>
    public static uint LogDriveSerial(string receiptText, string jobId)
    {
        if (ReceiptLine(receiptText, JobReports.ReceiptLogDriveLabel) is { } text
            && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint serial) && serial != 0)
            return serial;
        return RecentJobs.Entries().FirstOrDefault(e => e.JobId == jobId && e.TargetSerial != 0)?.TargetSerial ?? 0;
    }

    private static string? ReceiptLine(string receiptText, string label)
    {
        try
        {
            if (File.Exists(receiptText))
                foreach (string line in File.ReadLines(receiptText))
                    if (line.StartsWith(label, StringComparison.Ordinal) && line[label.Length..].Trim() is { Length: > 0 } value)
                        return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    internal static string Date(string? at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t)
            ? t.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : at ?? "";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A job found in a folder or in the recent-jobs list (for "Undo a previous sort" and the jobs command).</summary>
public sealed class JobListing
{
    public required string JournalPath { get; init; }
    public required string Id { get; init; }
    public JobKind Kind { get; init; }
    public MoveMode Mode { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public string? Created { get; init; }
    /// <summary>Drive label of the target when the job was created (for "not connected" messages).</summary>
    public string TargetLabel { get; init; } = "";
    /// <summary>
    /// Volume serial number of the drive with the job log (its target) when the job was created; 0 when unknown. With
    /// it, a log that can't be found is told apart for sure: its drive is not connected, or the drive is there and the
    /// sorted folder was renamed or moved (<see cref="LogMissing"/>).
    /// </summary>
    public uint TargetSerial { get; init; }
    /// <summary>The job's full state; null when its log cannot be read right now (drive not connected, folder renamed).</summary>
    public JobState? State { get; init; }
    /// <summary>
    /// The log cannot be read, but its drive is connected: the sorted folder was renamed, moved or deleted (and could
    /// not be found next to where it was). Such a job is never called "not connected".
    /// </summary>
    public bool LogMissing { get; init; }
    /// <summary>Why the log cannot be read, in plain words (null while it can).</summary>
    public string? WhyNotReachable { get; init; }

    public bool IsConnected => State is not null;
    /// <summary>"completed", "closed", "unfinished", "never started", "log not found" (drive connected) or "not connected".</summary>
    public string Status => State switch
    {
        null => LogMissing ? "log not found" : "not connected",
        { End.What: { } how } => how,
        { PlanComplete: false } => "never started",
        _ => "unfinished",
    };
    public UndoMark? UndoneBy => State?.UndoneBy;
    public int Moved => State?.DoneCount ?? 0;
    public long MovedBytes => State?.BytesDone ?? 0;
    public int StillInSource => State?.StillInSourceCount ?? 0;
    /// <summary>
    /// A finished sort that moved files and was not undone, or whose undo was ended early or left files in the sorted
    /// folder: offer "Undo" (of the rest). <see cref="UndoFactory.Preview(string)"/> has the final say.
    /// </summary>
    public bool CanUndo => State is { IsEnded: true, IsUndo: false } s && s.DoneCount > 0 && (s.UndoneBy is null || s.UndoneBy.IsPartial);
    /// <summary>An undo of this sort ended, but not every file went back.</summary>
    public bool PartlyUndone => UndoneBy is { IsFinished: true, IsPartial: true };
    /// <summary>"SONY_SSD" or "F:": the drive with the job log, for "not connected" messages.</summary>
    public string DriveName => TargetLabel.Length > 0 ? TargetLabel : (Path.GetPathRoot(Target) ?? Target).TrimEnd('\\');
}

public static class JobCatalog
{
    /// <summary>
    /// Jobs that involve a folder, newest first: jobs whose log is in its log folder (_IVAROffload, or _IVARIngest or
    /// _IngestSorter for older jobs; it was their target), jobs whose receipt is there (files were moved out of it), and recent jobs from
    /// or to it.
    /// </summary>
    public static List<JobListing> ForFolder(string folder)
    {
        var found = new Dictionary<string, JobListing>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string journal in JobPaths.FindJournals(folder)) Add(found, journal, null);
            foreach (string logFolder in JobPaths.LogFolderNames.Select(name => Path.Join(folder, name)).Where(Directory.Exists))
                foreach (string receipt in Directory.EnumerateFiles(logFolder, "*" + JobPaths.ReceiptTextSuffix))
                {
                    if (UndoFactory.ResolveJournal(receipt) is { } journal) Add(found, journal, null);
                    else if (FromReceipt(receipt, folder) is { } unreachable) found.TryAdd(unreachable.Id, unreachable);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        foreach (RecentJob entry in RecentJobs.Entries())
            if (JobPaths.SamePath(entry.Source, folder) || JobPaths.SamePath(entry.Target, folder))
                Add(found, entry.JournalPath, entry);
        return Newest(found.Values);
    }

    /// <summary>Every job in this PC's recent-jobs list (also those whose drive is not connected), newest first.</summary>
    public static List<JobListing> Recent()
    {
        var found = new Dictionary<string, JobListing>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentJob entry in RecentJobs.Entries()) Add(found, entry.JournalPath, entry);
        return Newest(found.Values);
    }

    /// <summary>Reads one job log for a listing; null when it cannot be read.</summary>
    public static JobListing? Load(string journalPath)
    {
        try
        {
            JobState state = JournalReader.Read(journalPath);
            JobHeader h = state.Header;
            return new JobListing
            {
                JournalPath = Path.GetFullPath(journalPath),
                Id = h.Id,
                Kind = h.Kind,
                Mode = h.Mode,
                Source = h.Source,
                Target = h.Target,
                Created = h.Created,
                TargetLabel = h.TargetLabel,
                TargetSerial = h.TargetSerial,
                State = state,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A sort whose receipt is in the folder but whose log cannot be found: listed with why.</summary>
    private static JobListing? FromReceipt(string receiptText, string folder)
    {
        string id = Path.GetFileName(receiptText)[..^JobPaths.ReceiptTextSuffix.Length];
        if (UndoFactory.ReceiptJournal(receiptText) is not { } journal || id.Length == 0) return null;
        string text;
        try
        {
            text = File.ReadAllText(receiptText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        uint serial = UndoFactory.LogDriveSerial(receiptText, id);
        return new JobListing
        {
            JournalPath = journal,
            Id = id,
            Kind = text.Contains("back to where they were before sort job", StringComparison.Ordinal) ? JobKind.Undo : JobKind.Sort,
            Mode = text.Contains($"({Planner.Word(MoveMode.Photos)} and the files", StringComparison.Ordinal) ? MoveMode.Photos : MoveMode.Videos,
            Source = folder,
            Target = JobPaths.RootOfJournal(journal) ?? Path.GetDirectoryName(journal) ?? "",
            Created = DateTime.TryParseExact(id.Length >= 15 ? id[..15] : id, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime t)
                ? new DateTimeOffset(t).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture) : null,
            TargetSerial = serial,
            LogMissing = JobPaths.DriveIsThere(journal, serial),
            WhyNotReachable = JobPaths.WhyNotReachable(journal, serial, Drives.Letter(journal)),
        };
    }

    private static void Add(Dictionary<string, JobListing> found, string journalPath, RecentJob? remembered)
    {
        JobListing? listing = Load(journalPath);
        // The sorted folder was renamed next to where it was: follow it (and remember it there).
        if (listing is null && remembered is not null && RecentJobs.FindMoved(remembered) is { } moved) listing = Load(moved);
        if (listing is null && remembered is { JobId.Length: > 0 })
            listing = new JobListing
            {
                JournalPath = remembered.JournalPath,
                Id = remembered.JobId,
                Kind = remembered.Kind,
                Mode = remembered.Mode,
                Source = remembered.Source,
                Target = remembered.Target,
                Created = remembered.Created,
                TargetLabel = remembered.TargetLabel,
                TargetSerial = remembered.TargetSerial,
                LogMissing = remembered.LogMissing,
                WhyNotReachable = remembered.WhyNotReachable,
            };
        if (listing is null || listing.Id.Length == 0) return;
        if (found.TryGetValue(listing.Id, out JobListing? known) && known.IsConnected) return;
        found[listing.Id] = listing;
    }

    private static List<JobListing> Newest(IEnumerable<JobListing> jobs) =>
        jobs.OrderByDescending(j => DateTimeOffset.TryParse(j.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t) ? t : DateTimeOffset.MinValue)
            .ThenByDescending(j => j.Id, StringComparer.Ordinal).ToList();
}
