using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using IvarOffload.Core.IO;
using IvarOffload.Core.Sorting;
using Microsoft.Win32.SafeHandles;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.Jobs;

public sealed record RunProgress(
    int ItemsTotal,
    int ItemsFinished,
    int Moved,
    int Skipped,
    int Failed,
    long WorkTotal,
    long WorkDone,
    string? CurrentFile,
    string Phase,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    public double Fraction => WorkTotal > 0 ? (double)WorkDone / WorkTotal : ItemsTotal > 0 ? (double)ItemsFinished / ItemsTotal : 1;
}

public enum RunStatus
{
    /// <summary>
    /// Every file was moved or skipped; the job is ended and its reports are written. Skipped files may still be in
    /// the source: see <see cref="RunResult.StillInSource"/>.
    /// </summary>
    Completed,
    /// <summary>All files were attempted but some failed. The job stays open so the failures can be retried.</summary>
    CompletedWithFailures,
    /// <summary>Stopped by the user. Resume continues where it left off.</summary>
    Stopped,
    /// <summary>Stopped because continuing was not possible (a drive is missing, the target is full, ...). See <see cref="RunResult.Halt"/>.</summary>
    Halted,
    /// <summary>Ended early by Close: files not reached yet stay in the source (<see cref="RunResult.NotStarted"/>). The job is ended.</summary>
    Closed,
}

/// <param name="NotStarted">Files never attempted: for a Close, the files it left in the source; otherwise files still waiting.</param>
public sealed record RunResult(RunStatus Status, int Moved, int Skipped, int Failed, int NotStarted, string? Message, string JournalPath)
{
    /// <summary>
    /// Files of the moving side still in the source: everything planned that did not move, except files skipped
    /// because an identical copy was already there, plus the files the preview held back (<see cref="HeldBack"/>).
    /// Nonzero means something that should have moved is still in the source.
    /// </summary>
    public int StillInSource { get; init; }
    public long StillInSourceBytes { get; init; }
    /// <summary>
    /// Files the preview kept in the source (e.g. a DIFFERENT file with the same name is already in the target). They
    /// were never part of the job, but they are still in the source: included in <see cref="StillInSource"/>.
    /// </summary>
    public int HeldBack { get; init; }
    /// <summary>
    /// Planned files that disappeared from the source (removed by something else) before they were moved: they are in
    /// neither folder (unless a copy was kept under another name), so they are not counted as still in the source.
    /// </summary>
    public int MissingFromSource { get; init; }
    /// <summary>Files skipped because an identical copy was already in the target (nothing is lost by leaving them).</summary>
    public int IdenticalInTarget { get; init; }
    /// <summary>Why the job halted; <see cref="HaltReason.None"/> unless <see cref="Status"/> is Halted.</summary>
    public HaltReason Halt { get; init; }
    public JobKind Kind { get; init; }

    /// <summary>The job ended and every planned file is in the target (or an identical copy was already there).</summary>
    public bool NothingLeftBehind => Status is RunStatus.Completed or RunStatus.Closed && StillInSource == 0 && MissingFromSource == 0 && Failed == 0;
}

public sealed class RunOptions
{
    public IProgress<RunProgress>? Progress { get; init; }
    public PauseGate? Pause { get; init; }
    public IFaultInjector? Faults { get; init; }

    // Test knobs.
    internal int[]? RetryDelaysMs { get; init; }
    internal Func<string, long?>? FreeBytes { get; init; }
    internal bool? TargetStoresStreams { get; init; }
}

/// <summary>Stops the job without blaming the file in progress; the job can be resumed once the cause is fixed.</summary>
internal sealed class JobHaltException(HaltReason reason, string message) : Exception(message)
{
    public HaltReason Reason { get; } = reason;
}

/// <summary>
/// Executes a job from its journal. Every step is recorded before or after it happens, and every interrupted
/// step is reconciled against what is actually on disk before anything else is done. Files are never overwritten,
/// and on the copy path an original is only deleted after its copy has been read back from disk and the original
/// itself has been read a second time, both matching the checksum taken while copying.
/// A missing drive or folder stops the job (Halted); it is never recorded as a skipped file.
/// </summary>
public sealed class JobRunner : IDisposable
{
    private static readonly int[] DefaultRetryDelaysMs = [250, 500, 1000, 2000, 4000];

    /// <summary>Kept free on the target drive on top of the file being copied.</summary>
    internal const long RunSpaceMargin = 32L * 1024 * 1024;

    private readonly JobState _state;
    private readonly JournalWriter _journal;
    private readonly RunOptions _options;
    private readonly JobHeader _job;
    private readonly bool _copy, _undo;
    private readonly string _tempPrefix;
    private readonly int[] _retryDelays;
    private readonly HashSet<string> _existingFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<JobItem>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _groupClear = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the receipt and the reports are rewritten while a job runs (at most; see <see cref="RefreshReports"/>).</summary>
    private const int ReportsIntervalMs = 15_000;
    /// <summary>After this many moved files the reports are rewritten sooner (as long as writing them stays cheap).</summary>
    private const int ReportsEveryFiles = 100;

    private CancellationToken _ct;
    private bool _closing;
    /// <summary>Closing only: the source root folder is there on its drive, so originals can be checked.</summary>
    private bool _sourceReachable = true;
    private bool _reportedAMove;
    private long _reportsDueMs, _reportsCheapMs;
    private int _doneAtReport;
    private bool _targetVolumeRead;
    private VolumeInfo? _targetVolume;
    private int _sourceMismatches, _notRemovableInARow;
    private volatile bool _pausedMidFile;

    private readonly object _reportLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(long Ms, long Work)> _samples = new();
    private long _lastReportMs = -1000;
    private long _workTotal, _workFinished, _itemWork, _itemWorkLimit;
    private int _done, _skipped, _failed;
    private string? _currentFile;
    private string _phase = "";

    private JobRunner(JobState state, JournalWriter journal, RunOptions options)
    {
        _state = state;
        _journal = journal;
        _options = options;
        _job = state.Header;
        _copy = _job.Method == TransferMethod.Copy;
        _undo = _job.Kind == JobKind.Undo;
        _tempPrefix = _job.Id.Length >= 4 ? _job.Id[^4..] : "job";
        _retryDelays = options.RetryDelaysMs ?? DefaultRetryDelaysMs;
        foreach (JobItem item in state.Items.Where(i => i.Group.Length > 0))
        {
            if (!_groups.TryGetValue(item.Group, out List<JobItem>? members)) _groups[item.Group] = members = [];
            members.Add(item);
        }
    }

    public JobState State => _state;

    /// <summary>Creates the journal for a confirmed plan and returns a runner for it.</summary>
    public static JobRunner Start(MovePlan plan, RunOptions? options = null) => Open(JobFactory.CreateJournal(plan), options);

    /// <summary>
    /// Opens an existing job to continue it. Takes the job's lock first: a job can only be open in one place.
    /// </summary>
    /// <exception cref="JournalException">The job cannot be run (never started, already finished, or open elsewhere: InUse).</exception>
    public static JobRunner Open(string journalPath, RunOptions? options = null)
    {
        JournalWriter journal = OpenJournal(journalPath, out JobState state);
        try
        {
            if (!state.PlanComplete)
                throw new JournalException("This job never started: its log does not contain the complete plan. No files moved. Start a new preview instead.");
            if (state.IsEnded) throw new JournalException("This job is already finished.");
            // A backup never deletes anything; the sort engine, which removes originals, must never run one.
            if (state.IsBackup) throw new JournalException("This is the log of a backup, not of a sort. Open it in the Backup tab.");
        }
        catch
        {
            journal.Dispose();
            throw;
        }
        RecentJobs.Add(state); // also updates the remembered location if the log was found somewhere new
        return new JobRunner(state, journal, options ?? new RunOptions());
    }

    /// <summary>
    /// Takes the job's lock. Another window (or <c>status</c>) may hold it for a moment while it brings the job's reports
    /// up to date (<see cref="JobReports.TryRefresh"/>): waited for, up to about two seconds, before the job counts as
    /// open elsewhere.
    /// </summary>
    private static JournalWriter OpenJournal(string journalPath, out JobState state)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return JournalWriter.OpenForAppend(journalPath, out state);
            }
            catch (JournalException e) when (e.InUse && attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Moves every file that is not finished yet. Safe to call again after a stop, crash, halt or failure.</summary>
    public RunResult Run(CancellationToken ct = default) => Execute(closing: false, ct);

    /// <summary>
    /// Closes an unfinished job without starting any new file: files that were in the middle of moving are completed
    /// or rolled back, everything else is recorded as not moved (it stays in the source), and the job is ended.
    /// </summary>
    public RunResult Close(CancellationToken ct = default) => Execute(closing: true, ct);

    private RunResult Execute(bool closing, CancellationToken ct)
    {
        _ct = ct;
        _closing = closing;
        using var awake = new KeepAwake();
        bool resuming = _state.Items.Any(i => i.Stage != ItemStage.Pending || i.Failed);
        Record(new JournalRecord { Type = "session", What = closing ? "close" : resuming ? "resume" : "start", At = JournalRecord.Now(), Machine = Environment.MachineName, User = Environment.UserName });

        _workTotal = _state.Items.Sum(WorkFor);
        _workFinished = _state.Items.Where(i => i.IsFinished).Sum(WorkFor);
        _done = _state.DoneCount;
        _skipped = _state.SkippedCount;
        _failed = _state.FailedCount;
        string? haltMessage = null;
        HaltReason halt = HaltReason.None;

        try
        {
            // Both drives must be there, and the log where the job was started, before any file is touched.
            // Closing a job with nothing in flight touches no file, so it works even when a drive is gone for good;
            // closing one with files in flight needs only the target, where the log and the copies are (see CheckRoots).
            if (!closing || _state.Items.Any(i => !i.IsFinished && i.Stage != ItemStage.Pending)) CheckRoots();
            RefreshReports(force: true);

            foreach (JobItem item in _state.Items)
            {
                if (item.IsFinished) continue;
                if (closing && item.Stage == ItemStage.Pending) continue;
                if (ct.IsCancellationRequested) break;
                try
                {
                    // A drive can be unplugged while the job is paused: check again before going on.
                    if (_options.Pause?.Wait(ct) == true || _pausedMidFile)
                    {
                        _pausedMidFile = false;
                        CheckRoots();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _currentFile = item.Rel;
                _itemWork = 0;
                _itemWorkLimit = WorkFor(item);
                try
                {
                    if (!closing && HeldWithGroup(item)) continue;
                    Process(item);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (IOErrors.IsDeviceGone(e))
                {
                    DriveStoppedResponding(item, e);
                }
                catch (Exception e) when (IOErrors.IsDiskFull(e))
                {
                    TargetFullOrTooLarge(item, e);
                }
                catch (Exception e) when (IOErrors.IsFileTooLarge(e))
                {
                    Fail(item, FailReasons.TooLargeForTarget);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    Fail(item, e.Message);
                }
                finally
                {
                    if (item.IsFinished || item.Failed) _workFinished += WorkFor(item);
                    _itemWork = 0;
                    Report();
                    RefreshReports();
                }
            }
        }
        catch (JobHaltException h)
        {
            haltMessage = h.Message;
            halt = h.Reason;
        }

        _currentFile = null;
        RunStatus status;
        int closedNow = 0;
        if (haltMessage is not null) status = RunStatus.Halted;
        else if (ct.IsCancellationRequested) status = RunStatus.Stopped;
        else if (closing)
        {
            foreach (JobItem item in _state.Items.Where(i => i.Stage == ItemStage.Pending && !i.Failed))
            {
                Skip(item, SkipReasons.Closed);
                closedNow++;
            }
            Finish("closed");
            status = RunStatus.Closed;
        }
        else if (_state.Items.All(i => i.IsFinished))
        {
            Finish("completed");
            status = RunStatus.Completed;
        }
        else status = RunStatus.CompletedWithFailures;

        if (status is not (RunStatus.Completed or RunStatus.Closed)) JobReports.TryWrite(_state, createReceipt: true, interrupted: true); // not running any more
        Report(force: true);
        return new RunResult(status, _state.DoneCount, _state.SkippedCount, _state.FailedCount,
            closing && status == RunStatus.Closed ? closedNow : _state.Items.Count(i => i.Stage == ItemStage.Pending && !i.Failed),
            haltMessage, _state.JournalPath)
        {
            StillInSource = _state.StillInSourceCount,
            StillInSourceBytes = _state.StillInSourceBytes,
            HeldBack = _state.HeldBack.Count,
            MissingFromSource = _state.MissingCount,
            IdenticalInTarget = _state.IdenticalInTargetCount,
            Halt = halt,
            Kind = _job.Kind,
        };
    }

    private void Process(JobItem item)
    {
        string src = Path.Join(_job.Source, item.Rel);
        string dst = Path.Join(_job.Target, item.Rel);
        if (_closing && item.Stage != ItemStage.Pending && !(_sourceReachable && SafeFile.DirectoryExists(Path.GetDirectoryName(src)!)))
        {
            CloseWithoutSource(item, dst);
            return;
        }
        switch (item.Stage)
        {
            case ItemStage.Pending: Begin(item, src, dst); break;
            case ItemStage.Prepared: ResumePrepared(item, src, dst); break;
            case ItemStage.Copying: ResumeCopying(item, src, dst); break;
            case ItemStage.Copied: ResumeCopied(item, src, dst); break;
            case ItemStage.Placed: FinishPlaced(item, src, dst); break;
        }
    }

    // ---- Drives, folders and groups -------------------------------------------------------------------------

    /// <summary>
    /// Halts unless the job's log is still in the target it was started in, and both root folders are there on the
    /// drives the job was started with. Nothing is skipped or failed because of a missing drive.
    /// Closing needs only the target (the log and every copy are there): a source that cannot be reached is noted
    /// instead, and the files in flight are ended without touching or judging their originals (<see cref="CloseWithoutSource"/>).
    /// </summary>
    private void CheckRoots()
    {
        string? here = JobPaths.RootOfJournal(_state.JournalPath);
        if (here is null || !JobPaths.SamePath(here, _job.Target))
        {
            if (here is not null && Drives.Elsewhere(Drives.OnDrive(_job.Target, _job.TargetReal), _job.TargetSerial, here).Any(p => JobPaths.SamePath(p, here)))
                throw new JobHaltException(HaltReason.LogMoved, LetterChanged("target", _job.TargetLabel, _job.Target, here));
            throw new JobHaltException(HaltReason.LogMoved,
                $"This job started in {_job.Target}, but its log is now in {here ?? Path.GetDirectoryName(Path.GetFullPath(_state.JournalPath))}. "
                + "Move the folder to where it was, or give it its old name again. Then resume the job.");
        }
        if (_closing)
        {
            try
            {
                CheckRoot(_job.Source, _job.SourceSerial, _job.SourceLabel, "source", HaltReason.SourceNotConnected, _job.SourceReal);
                _sourceReachable = true;
            }
            catch (JobHaltException)
            {
                _sourceReachable = false;
            }
        }
        else CheckRoot(_job.Source, _job.SourceSerial, _job.SourceLabel, "source", HaltReason.SourceNotConnected, _job.SourceReal);
        CheckRoot(_job.Target, _job.TargetSerial, _job.TargetLabel, "target", HaltReason.TargetNotConnected, _job.TargetReal);
    }

    /// <summary>Halts (with <paramref name="reason"/>) unless the folder is there on the drive with this serial. Also used by backups.</summary>
    /// <param name="real">Where the folder really was, when it was named through a subst drive letter (<see cref="JobHeader.SourceReal"/>).</param>
    internal static void CheckRoot(string root, uint serial, string label, string side, HaltReason reason, string? real = null)
    {
        bool present;
        try
        {
            present = SafeFile.DirectoryExists(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new JobHaltException(reason, $"The {side} folder is not readable ({e.Message}). Fix the problem. Then click Resume. Nothing was lost.");
        }
        // A folder named through a subst drive letter that now stands for another folder is another folder.
        if (present && !Drives.LeadsTo(root, real))
            throw new JobHaltException(reason,
                $"The {side} folder {root} is on the subst drive letter {Drives.Letter(root)}, which now shows a different folder. "
                + $"When the job started, {Drives.Letter(root)} showed {real}. Use subst to give {Drives.Letter(root)} its old folder again. Then click Resume. Nothing was lost.");
        uint now = serial == 0 || !present ? 0 : Drives.SerialOf(root);
        if (present && (now == 0 || now == serial)) return;
        // The same drive under another letter (another PC, or another plug order): say so, the job cannot follow it.
        if (Drives.Elsewhere(Drives.OnDrive(root, real), serial).FirstOrDefault(SafeFile.DirectoryExists) is { } moved)
            throw new JobHaltException(reason, LetterChanged(side, label, root, moved));
        // The drive is there (the same one, as far as it can tell): only the folder is gone. Never say "not connected" then.
        if (!present && Drives.IsOn(root, serial)) throw new JobHaltException(reason, FolderGone(side, root, label));
        throw new JobHaltException(reason, NotConnected(side, root, label));
    }

    private static string NotConnected(string side, string root, string label) =>
        $"The {DriveOf(side)} ({DriveWas(root, label)}) is not connected, or the {side} folder {root} has a new name. "
        + "Connect the drive again. Then click Resume. Nothing was lost.";

    private static string FolderGone(string side, string root, string label) =>
        $"The {side} folder is missing: {root}. Its drive ({(label.Length > 0 ? $"{label}, {Drives.Letter(root)}" : Drives.Letter(root))}) is connected, "
        + "so someone probably renamed, moved or deleted the folder. Move the folder to where it was, or give it its old name again. Then click Resume. Nothing was lost.";

    /// <summary>"the backup drive", or "the card" (a card is a drive of its own).</summary>
    private static string DriveOf(string side) => side == "card" ? "card" : $"{side} drive";

    /// <summary>"SONY_A was F:", or "F:" when the drive has no label.</summary>
    private static string DriveWas(string root, string label) => label.Length > 0 ? $"{label} was {Drives.Letter(root)}" : Drives.Letter(root);

    private static string LetterChanged(string side, string label, string was, string now) =>
        $"The {DriveOf(side)}{(label.Length > 0 ? $" ({label})" : "")} has a different letter now: {was} is now {now}. "
        + $"In Windows Disk Management, use Change Drive Letter to give the drive its old letter ({Drives.Letter(was)}) again. Then click Resume. Nothing was lost.";

    /// <summary>A pause in the middle of a file: check both drives again before the next step that changes anything.</summary>
    private void CheckRootsIfPaused()
    {
        if (!_pausedMidFile) return;
        _pausedMidFile = false;
        CheckRoots();
    }

    /// <summary>
    /// A source file is missing. It is only really gone when its folder is still there: a missing folder means a
    /// renamed folder or a disconnected drive, which must never become a permanent skip. Halts when a drive or a root
    /// folder is gone; otherwise records a failure (Resume retries the file) and returns true. While closing, the file
    /// is ended without its original instead (<see cref="CloseWithoutSource"/>).
    /// </summary>
    private bool SourceFolderMissing(JobItem item, string src)
    {
        string folder = Path.GetDirectoryName(src)!;
        if (SafeFile.DirectoryExists(folder)) return false;
        CheckRoots();
        if (_closing) CloseWithoutSource(item, Path.Join(_job.Target, item.Rel));
        else Fail(item, FailReasons.FolderMissing(folder));
        return true;
    }

    /// <summary>
    /// Keeps a file with its group (a clip with its proxies, telemetry and sidecars). When an earlier file of the group
    /// was skipped, this one is skipped too; when it failed, this one waits for it (recorded as failed without being
    /// tried), so Resume retries that file first. Files already moved stay moved.
    /// An undo holds nothing back for a skipped file: each file is checked against its own recorded checksum, and a
    /// sibling that is no longer in the sorted folder (deleted proxies) or already back cannot be kept together with
    /// anything, so every file that can go back goes back (as the undo preview says).
    /// </summary>
    private bool HeldWithGroup(JobItem item)
    {
        if (item.Stage != ItemStage.Pending || !_groups.TryGetValue(item.Group, out List<JobItem>? members)) return false;
        // Earlier members do not change any more in this run, so the ones already found harmless are not checked again
        // (a CinemaDNG clip or a card structure can hold thousands of files in one group).
        for (int k = _groupClear.GetValueOrDefault(item.Group); k < members.Count; k++)
        {
            JobItem other = members[k];
            if (other.Index >= item.Index)
            {
                _groupClear[item.Group] = k;
                break;
            }
            if (other.Stage == ItemStage.Skipped && !SkipReasons.IsIdenticalInTarget(other.Note) && !_undo)
            {
                Skip(item, SkipReasons.IsKeptWithGroup(other.Note) ? other.Note! : SkipReasons.KeptWith(Path.GetFileName(other.Rel)));
                return true;
            }
            if (other.Failed && !other.IsFinished)
            {
                Fail(item, FailReasons.IsWaiting(other.Note) ? other.Note! : FailReasons.WaitingFor(Path.GetFileName(other.Rel)));
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A "device not ready / removed / I/O error" while working on a file. A drive or root folder that is gone halts the
    /// job. When both still answer, the first time it halts too (a drive that dropped out for a moment), but a file
    /// that hits it again after a resume (a bad spot on the drive), or any file while closing, just fails, so one bad
    /// file cannot keep every other file from moving.
    /// </summary>
    private void DriveStoppedResponding(JobItem item, Exception e)
    {
        CheckRoots(); // a whole drive or root folder is gone: say that (the file is retried on resume)
        string detail = IOErrors.NativeError(e) is int code ? new System.ComponentModel.Win32Exception(code).Message.TrimEnd('.') : e.Message;
        bool again = FailReasons.IsDeviceError(item.Note);
        Fail(item, FailReasons.DeviceError(detail));
        if (again || _closing) return;
        string which = e is Win32IOException { Path: var path } ? SideOf(path) : "";
        throw new JobHaltException(HaltReason.DriveStoppedResponding,
            $"The {(which.Length > 0 ? which : "card or drive")} did not respond ({detail}). "
            + "Make sure that it is connected (cable, port or card reader). Then click Resume. Nothing was lost.");
    }

    private string SideOf(string path) =>
        Planner.IsInside(path, _job.Source) ? $"source drive{(_job.SourceLabel.Length > 0 ? $" ({_job.SourceLabel})" : "")}"
        : Planner.IsInside(path, _job.Target) ? $"target drive{(_job.TargetLabel.Length > 0 ? $" ({_job.TargetLabel})" : "")}"
        : "";

    private void TargetFullOrTooLarge(JobItem item, Exception e)
    {
        // FAT32 reports a file of 4 GB or more as "disk full"; only a drive that is really full stops the job.
        if (item.Size > uint.MaxValue && FreeBytes(_job.Target) is long free && free >= item.Size + RunSpaceMargin)
        {
            Fail(item, FailReasons.TooLargeForTarget);
            return;
        }
        Fail(item, e.Message);
        throw new JobHaltException(HaltReason.TargetFull, "The target drive is full. Make more space available on it. Then resume the job. Nothing was lost.");
    }

    // ---- Starting a file ------------------------------------------------------------------------------------

    private void Begin(JobItem item, string src, string dst, bool afterSetAside = false)
    {
        item.Stage = ItemStage.Pending;
        if (_closing)
        {
            // Ending the job: a file that was in flight is not started again. It stays in the source - unless it is no
            // longer there (something else removed it), which is recorded as such, never as "still in the source".
            if (!SafeFile.Exists(src) && SafeFile.DirectoryExists(Path.GetDirectoryName(src)!)) SourceVanished(item, src, dst);
            return;
        }
        SetPhase("File check in progress");
        FileSnapshot? source = SafeFile.TrySnapshot(src);
        if (source is null && _undo)
        {
            NotInSortedFolder(item, src, dst);
            return;
        }
        if (source is null || source.IsDirectory)
        {
            if (source is null && SourceFolderMissing(item, src)) return;
            Skip(item, SkipReasons.SourceGone);
            return;
        }
        if (!MatchesPlan(item, source))
        {
            Skip(item, ChangedReason);
            return;
        }
        FileSnapshot? existing = SafeFile.TrySnapshot(dst);
        if (existing is not null)
        {
            Skip(item, DescribeConflict(item, src, existing));
            return;
        }
        if (_copy) CopyVerifyAndDelete(item, src, dst, afterSetAside);
        else MoveByRename(item, src, dst);
    }

    /// <summary>
    /// Undo: the file is no longer in the sorted folder. It may already be back in its original place (an earlier undo
    /// that was ended early moved it); otherwise the user moved, renamed or deleted it after the sort and it cannot go
    /// back. Either way nothing is touched. A missing drive or sorted folder still halts.
    /// </summary>
    private void NotInSortedFolder(JobItem item, string src, string dst)
    {
        if (!SafeFile.DirectoryExists(Path.GetDirectoryName(src)!)) CheckRoots();
        FileSnapshot? back = SafeFile.TrySnapshot(dst);
        bool isBack = back is { IsDirectory: false } && back.Size == item.Size
                      && (item.ExpectedSha256 is { } sha ? Hash(dst, back.Size, countWork: false) == sha : back.LastWriteTime == item.LastWriteTime);
        Skip(item, isBack ? SkipReasons.AlreadyBack : SkipReasons.NotInSortedFolder);
    }

    /// <summary>The file went away just as it was about to move (its folder is still there).</summary>
    private void SourceVanished(JobItem item, string src, string dst)
    {
        if (_undo) NotInSortedFolder(item, src, dst);
        else Skip(item, SkipReasons.SourceGone);
    }

    private string DescribeConflict(JobItem item, string src, FileSnapshot existing)
    {
        if (existing.IsDirectory) return SkipReasons.FolderInTarget;
        if (existing.Size != item.Size) return _undo ? SkipReasons.AlreadyBackDifferent : SkipReasons.DifferentInTarget;
        if (!_job.Verify) return _undo ? SkipReasons.AlreadyBackSameSize : SkipReasons.SameNameAndSizeInTarget;
        SetPhase("Comparison with the file in the target folder");
        string dst = Path.Join(_job.Target, item.Rel);
        bool identical = Hash(src, item.Size, countWork: false) == Hash(dst, existing.Size, countWork: false);
        return identical
            ? _undo ? SkipReasons.AlreadyBackIdentical : SkipReasons.IdenticalInTarget
            : _undo ? SkipReasons.AlreadyBackDifferent : SkipReasons.DifferentInTarget;
    }

    // ---- Same drive: rename ---------------------------------------------------------------------------------

    private void MoveByRename(JobItem item, string src, string dst)
    {
        SetPhase(_job.Verify ? "Checksum in progress" : "Move in progress");
        FileSnapshot before;
        string? sha;
        try
        {
            (before, sha) = SnapshotAndHash(item, src, _job.Verify);
        }
        catch (Win32IOException e) when (e.IsNotFound)
        {
            if (!SourceFolderMissing(item, src)) SourceVanished(item, src, dst);
            return;
        }
        if (!MatchesPlan(item, before))
        {
            Skip(item, ChangedReason);
            return;
        }
        if (sha is not null && item.ExpectedSha256 is not null && sha != item.ExpectedSha256)
        {
            Skip(item, SkipReasons.ChangedSinceSorted);
            return;
        }
        CheckRootsIfPaused();

        string? fileId = _job.CompareIds ? before.FileId : null;
        FileAttributes attributes = before.Attributes & SafeFile.CopyableAttributes;
        Record(new JournalRecord
        {
            Type = "pre", Index = item.Index, FileId = fileId, Sha256 = sha, CreationTime = before.CreationTime, LastWriteTime = before.LastWriteTime,
            Attributes = (int)attributes,
        });
        item.Stage = ItemStage.Prepared;
        item.FileId = fileId;
        item.Sha256 = sha;
        item.SeenCreationTime = before.CreationTime;
        item.SeenLastWriteTime = before.LastWriteTime;
        item.SeenAttributes = attributes;
        Fault("after-pre", item);

        EnsureFolder(Path.GetDirectoryName(dst)!);
        SetPhase("Move in progress");
        int error;
        try
        {
            error = RetryRename(src, dst);
        }
        catch (Win32IOException e) when (e.IsAccessDenied)
        {
            CannotRemoveOriginal(item, FailReasons.NotMovable);
            return;
        }
        if (error is ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS)
        {
            Skip(item, SkipReasons.AppearedDuringMove);
            return;
        }
        if (error == ERROR_NOT_SAME_DEVICE)
        {
            // The target path crosses into another drive (e.g. a mounted folder): copy this file instead.
            CopyVerifyAndDelete(item, src, dst);
            return;
        }
        if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
        {
            // The file or its folder went away just now: never a skip. The item stays prepared and is reconciled on resume.
            if (SourceFolderMissing(item, src)) return;
            CheckRoots();
        }
        if (error != 0) throw new Win32IOException(error, "The move", src);
        Fault("after-rename", item);

        FileSnapshot after = SafeFile.TrySnapshot(dst) ?? throw new IOException("The file is not in the target folder right after the move.");
        if (fileId is not null && after.FileId != fileId)
            throw new IOException("Check failed: the file in the target folder is not the file that the job moved.");
        if (after.Size != before.Size || after.LastWriteTime != before.LastWriteTime)
            throw new IOException("Check failed: the size or the modification time is different after the move.");
        if (after.CreationTime != before.CreationTime)
            SafeFile.ApplyTimesAndAttributes(dst, before); // NTFS name tunnelling can substitute an old creation time
        else if (!SameAttributes(after, attributes))
            TryRestoreAttributes(dst, before);
        if (SafeFile.Exists(src)) throw new IOException("The source file is still there after the move.");
        Done(item, "rename");
    }

    private static bool SameAttributes(FileSnapshot s, FileAttributes attributes) => (s.Attributes & SafeFile.CopyableAttributes) == attributes;

    /// <summary>
    /// A rename sets the Archive attribute: the file gets back the attributes (and times) it had. Best effort - the file
    /// has moved either way, and its content and dates are unchanged.
    /// </summary>
    private static void TryRestoreAttributes(string path, FileSnapshot from)
    {
        try
        {
            SafeFile.ApplyTimesAndAttributes(path, from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ResumePrepared(JobItem item, string src, string dst)
    {
        FileSnapshot? s = SafeFile.TrySnapshot(src), d = SafeFile.TrySnapshot(dst);
        if (s is not null && d is null)
        {
            Begin(item, src, dst); // the rename never happened
            return;
        }
        if (s is null && d is not null)
        {
            RenameHappened(item, dst, d);
            return;
        }
        if (s is null)
        {
            if (SourceFolderMissing(item, src)) return;
            CheckRoots();
            Fail(item, FailReasons.InNeitherPlace);
            return;
        }
        Fail(item, "Interrupted move: the file is in the source folder and in the target folder. The job did not change either file. Check the two files.");
    }

    /// <summary>An interrupted rename: the file is in the target now. Recorded as moved if it is the file that was being moved.</summary>
    private void RenameHappened(JobItem item, string dst, FileSnapshot d)
    {
        long creation = item.SeenCreationTime ?? item.CreationTime, lastWrite = item.SeenLastWriteTime ?? item.LastWriteTime;
        bool same = d.Size == item.Size && d.LastWriteTime == lastWrite && (item.FileId is null || d.FileId == item.FileId);
        if (!same)
        {
            Fail(item, "Interrupted move: the file in the target folder is not the file that the job started to move. The job changed nothing. Check the file.");
            return;
        }
        // The attributes the file had before the rename, when the log recorded them (a rename sets Archive).
        FileAttributes attributes = item.SeenAttributes ?? d.Attributes & SafeFile.CopyableAttributes;
        if (d.CreationTime != creation) SafeFile.ApplyTimesAndAttributes(dst, d with { CreationTime = creation, Attributes = attributes });
        else if (!SameAttributes(d, attributes)) TryRestoreAttributes(dst, d with { Attributes = attributes });
        Done(item, "rename");
    }

    // ---- Different drives: copy, verify, delete -------------------------------------------------------------

    private void CopyVerifyAndDelete(JobItem item, string src, string dst, bool afterSetAside = false)
    {
        string folder = Path.GetDirectoryName(dst)!;
        if (TargetVolume is { } volume && item.Size > volume.MaxFileSize)
        {
            Fail(item, FailReasons.TooLargeForTarget);
            return;
        }
        bool unbuffered = true;
        int mismatches = afterSetAside ? 1 : 0; // a copy made after setting a damaged one aside must match the first time
        while (true)
        {
            string tempName = $"~{_tempPrefix}-{item.Index:D6}{JobPaths.TempExtension}";
            string temp = Path.Join(folder, tempName);
            FileSnapshot source;
            string sha;
            var streams = new List<(NamedStream Stream, string Sha)>();
            SetPhase("Copy in progress");
            SafeFileHandle handle;
            try
            {
                handle = Retry(() => SafeFile.OpenRead(src, unbuffered));
            }
            catch (Win32IOException e) when (e.IsNotFound)
            {
                if (!SourceFolderMissing(item, src)) SourceVanished(item, src, dst);
                return;
            }
            using (handle)
            {
                source = SafeFile.Snapshot(handle, src);
                if (!MatchesPlan(item, source))
                {
                    Skip(item, ChangedReason);
                    return;
                }
                List<NamedStream> named = SafeFile.NamedStreams(src);
                if (named.Count > 0 && !TargetStoresStreams)
                {
                    if (named.Any(s => !s.IsZoneIdentifier))
                    {
                        Fail(item, FailReasons.StreamsNotSupported);
                        return;
                    }
                    named.Clear(); // only the "downloaded from the internet" mark, which is harmless to lose
                }
                EnsureSpaceFor(item.Size + named.Sum(s => s.Size));

                string? fileId = _job.CompareIds ? source.FileId : null;
                Record(new JournalRecord { Type = "copy", Index = item.Index, Temp = tempName, FileId = fileId, CreationTime = source.CreationTime, LastWriteTime = source.LastWriteTime });
                item.Stage = ItemStage.Copying;
                item.Temp = tempName;
                item.FileId = fileId;
                item.SeenCreationTime = source.CreationTime;
                item.SeenLastWriteTime = source.LastWriteTime;
                Fault("after-copy-journal", item);

                EnsureFolder(folder);
                _itemWork = 0;
                try
                {
                    sha = SafeFile.ToHex(SafeFile.CopyToNewFile(handle, temp, source.Size, n =>
                    {
                        AddWork(n);
                        Fault("mid-copy", item);
                    }, _ct));
                    foreach (NamedStream stream in named)
                        streams.Add((stream, SafeFile.ToHex(SafeFile.CopyStream(src, temp, stream, _ct))));
                }
                catch (Win32IOException e) when (unbuffered && e.NativeError == ERROR_INVALID_PARAMETER)
                {
                    TryDeleteTemp(temp);
                    unbuffered = false; // this volume does not support unbuffered reads
                    continue;
                }
                catch
                {
                    DiscardCopy(temp, src);
                    throw;
                }
            }

            SafeFile.ApplyTimesAndAttributes(temp, Stamp(item, source)); // after the streams: writing them updates the modification time
            Fault("after-copy", item);

            // Read the copy back from the target and the original a second time from the source, at the same time.
            SetPhase("Copy check in progress");
            ReadBack check;
            bool streamsMatch;
            try
            {
                check = ReadBoth(temp, source.Size, src);
                streamsMatch = streams.All(s => SafeFile.ToHex(SafeFile.HashStream(temp, s.Stream, _ct)) == s.Sha);
            }
            catch
            {
                DiscardCopy(temp, src);
                throw;
            }

            switch (CheckSecondRead(item, src, check, source, sha, placed: false))
            {
                case SecondRead.Handled:
                    DiscardCopy(temp, src);
                    return;
                case SecondRead.Gone when check.Copy != sha || !streamsMatch:
                    KeepDamagedCopy(item, temp, dst);
                    return;
            }
            if (check.Copy != sha || !streamsMatch)
            {
                TryDeleteTemp(temp);
                if (++mismatches < 2) continue;
                TargetDamagingFiles(item);
            }
            if (item.ExpectedSha256 is not null && sha != item.ExpectedSha256)
            {
                // Changed since it was sorted: it stays where it is - unless it has just disappeared from there too.
                if (check.Original is null) KeepCopy(item, temp, dst, JobPaths.UnverifiedCopySuffix, FailReasons.ChangedCopyKept);
                else
                {
                    TryDeleteTemp(temp);
                    Skip(item, SkipReasons.ChangedSinceSorted);
                }
                return;
            }

            Record(new JournalRecord { Type = "copied", Index = item.Index, Sha256 = sha, Streams = streams.Count > 0 ? streams.Count : null });
            item.Stage = ItemStage.Copied;
            item.Sha256 = sha;
            Fault("after-copied", item);
            PlaceAndDeleteSource(item, src, dst, temp, check.Original is null ? null : source);
            return;
        }
    }

    private readonly record struct ReadBack(string Copy, FileSnapshot? Original, string? OriginalSha);

    private enum SecondRead { Matches, Gone, Handled }

    /// <summary>
    /// Hashes the copy and, at the same time, reads the original a second time (they are on different drives).
    /// The original may only be deleted when both match the checksum taken while copying.
    /// </summary>
    private ReadBack ReadBoth(string copyPath, long copySize, string src)
    {
        Task<(FileSnapshot?, string?)> original = Task.Run(() => ReadOriginalAgain(src));
        string copy;
        try
        {
            copy = Hash(copyPath, copySize);
        }
        finally
        {
            // Never leave a read running while the caller may delete or rename a file.
            try { original.Wait(); } catch { /* reported below, unless the copy's own error is already on its way */ }
        }
        (FileSnapshot? snapshot, string? sha) = original.GetAwaiter().GetResult();
        return new ReadBack(copy, snapshot, sha);
    }

    private (FileSnapshot? Snapshot, string? Sha) ReadOriginalAgain(string src)
    {
        bool unbuffered = true;
        while (true)
        {
            try
            {
                using SafeFileHandle h = Retry(() => SafeFile.OpenRead(src, unbuffered));
                FileSnapshot snapshot = SafeFile.Snapshot(h, src);
                return (snapshot, SafeFile.ToHex(SafeFile.HashHandle(h, snapshot.Size, AddWork, _ct)));
            }
            catch (Win32IOException e) when (unbuffered && e.NativeError == ERROR_INVALID_PARAMETER)
            {
                unbuffered = false;
            }
            catch (Win32IOException e) when (e.IsNotFound)
            {
                return (null, null);
            }
        }
    }

    /// <summary>
    /// Judges the second read of the original. Gone: the original disappeared (its folder is still there), so the copy
    /// is all that is left. Handled: something was recorded (or the job halts), stop here.
    /// </summary>
    private SecondRead CheckSecondRead(JobItem item, string src, ReadBack check, FileSnapshot expected, string expectedSha, bool placed)
    {
        if (check.Original is null) return SourceFolderMissing(item, src) ? SecondRead.Handled : SecondRead.Gone;
        if (!check.Original.SameFileAs(expected, _job.CompareIds))
        {
            if (placed) Fail(item, FailReasons.OriginalChangedAfterCopy);
            else Skip(item, SkipReasons.ChangedDuringMove);
            return SecondRead.Handled;
        }
        if (check.OriginalSha == expectedSha) return SecondRead.Matches;
        Fail(item, FailReasons.SourceReadTwiceDiffers);
        // Closing only finishes what was in flight: it records the file and goes on, so the job can always be ended.
        if (!_closing && ++_sourceMismatches >= 2)
            throw new JobHaltException(HaltReason.SourceInconsistent,
                $"The source drive gives inconsistent data: {_sourceMismatches} files were different on the second read. The job kept their originals. "
                + "Before you continue, check the drive, cable, port or card reader.");
        return SecondRead.Handled;
    }

    private void TargetDamagingFiles(JobItem item)
    {
        Fail(item, "The copy did not match the original two times (checksum mismatch). The job kept the original. Check the target drive.");
        throw new JobHaltException(HaltReason.TargetDamagingFiles,
            $"The target drive damages files: a new copy of {Path.GetFileName(item.Rel)} did not match the original. "
            + "The job kept the original. Before you continue, check the target drive, cable or port.");
    }

    private void PlaceAndDeleteSource(JobItem item, string src, string dst, string temp, FileSnapshot? source)
    {
        CheckRootsIfPaused();
        SetPhase("Final rename in progress");
        int error = RetryRename(temp, dst);
        if (error is ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS)
        {
            if (source is null)
            {
                // The original is gone: this verified copy is all that is left, so it is kept under another name.
                KeepCopy(item, temp, dst, JobPaths.VerifiedCopySuffix, FailReasons.VerifiedCopyKept);
                return;
            }
            TryDeleteTemp(temp);
            Skip(item, SkipReasons.AppearedDuringCopy);
            return;
        }
        if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) CheckRoots();
        if (error != 0) throw new Win32IOException(error, "The final rename of the checked copy", dst);
        Fault("after-place-rename", item);
        if (source is not null) SafeFile.ApplyTimesAndAttributes(dst, Stamp(item, source)); // guard against NTFS name tunnelling

        Record(new JournalRecord { Type = "placed", Index = item.Index });
        item.Stage = ItemStage.Placed;
        Fault("after-placed", item);

        if (source is null) Done(item, "copy"); // the original had already disappeared (its folder is there); the verified copy is kept
        else DeleteSource(item, src, source);
    }

    private void DeleteSource(JobItem item, string src, FileSnapshot expected)
    {
        CheckRootsIfPaused();
        SetPhase("Removal of the original");
        DeleteOutcome outcome;
        try
        {
            outcome = Retry(() => SafeFile.DeleteIfUnchanged(src, expected, _job.CompareIds), retryAccessDenied: true);
        }
        catch (Win32IOException e) when (e.IsNotFound)
        {
            // The folder is missing (renamed or disconnected): the original may still be there, so this is not "moved".
            if (!SourceFolderMissing(item, src)) throw;
            return;
        }
        catch (Win32IOException e) when (e.IsAccessDenied)
        {
            CannotRemoveOriginal(item, FailReasons.OriginalNotRemovable);
            return;
        }
        if (outcome == DeleteOutcome.ChangedNotDeleted)
        {
            Fail(item, FailReasons.OriginalChangedAfterCopy);
            return;
        }
        Fault("after-delete", item);
        Done(item, "copy");
    }

    /// <summary>
    /// An original that cannot be removed (write-protected drive or no permission). After two in a row the job halts,
    /// because every further file would fail the same way after its retries. Closing never halts here: the file is
    /// recorded (a verified copy in the target, the original kept) and the job can be ended.
    /// </summary>
    private void CannotRemoveOriginal(JobItem item, string reason)
    {
        Fail(item, reason);
        if (_closing || ++_notRemovableInARow < 2) return;
        int copies = _state.Items.Count(i => i.Failed && i.Stage == ItemStage.Placed);
        throw new JobHaltException(HaltReason.OriginalsNotRemovable, _copy
            ? $"The job cannot remove the originals from the source folder (read-only or no permission). {copies:N0} checked copies are in the target folder. "
              + "Nothing was lost. Fix the permission or the write protection. Then click Resume."
            : "The job cannot move files out of the source folder (read-only or no permission). Nothing was lost. "
              + "Fix the permission or the write protection. Then click Resume.");
    }

    /// <summary>
    /// The copy never finished (or was never read back). While the original is there the unfinished copy is not
    /// needed: it is removed and the file starts over. When the original has disappeared, the copy may be all that is
    /// left, so it is kept - also when the job is being ended.
    /// </summary>
    private void ResumeCopying(JobItem item, string src, string dst)
    {
        string? temp = item.Temp is null ? null : Path.Join(Path.GetDirectoryName(dst)!, item.Temp);
        if (temp is not null && SafeFile.TrySnapshot(temp) is { IsDirectory: false } t)
        {
            FileSnapshot? s = SafeFile.TrySnapshot(src);
            if (s is null)
            {
                if (SourceFolderMissing(item, src)) return; // the copy stays until the original can be checked
                OriginalGoneDuringCopy(item, src, dst, temp, t);
                return;
            }
            TryDeleteTemp(temp);
        }
        Begin(item, src, dst);
    }

    /// <summary>
    /// The original disappeared (its folder is still there) while its copy was being made, before the copy was read
    /// back. The copy is never deleted: an undo, which knows the checksum the file must have, puts back a complete copy
    /// that has it; otherwise the copy is kept under a name that says it was not checked, and the file is failed.
    /// </summary>
    private void OriginalGoneDuringCopy(JobItem item, string src, string dst, string temp, FileSnapshot t)
    {
        if (item.ExpectedSha256 is { } expected && t.Size == item.Size && SafeFile.TrySnapshot(dst) is null)
        {
            SetPhase("Copy check in progress");
            if (Hash(temp, t.Size, countWork: false) == expected)
            {
                Record(new JournalRecord { Type = "copied", Index = item.Index, Sha256 = expected });
                item.Stage = ItemStage.Copied;
                item.Sha256 = expected;
                PlaceAndDeleteSource(item, src, dst, temp, null);
                return;
            }
        }
        KeepCopy(item, temp, dst, JobPaths.UnverifiedCopySuffix, FailReasons.OriginalGoneDuringCopy);
    }

    private void ResumeCopied(JobItem item, string src, string dst)
    {
        string temp = Path.Join(Path.GetDirectoryName(dst)!, item.Temp ?? "");
        FileSnapshot? t = item.Temp is null ? null : SafeFile.TrySnapshot(temp);
        FileSnapshot? d = SafeFile.TrySnapshot(dst);
        FileSnapshot recorded = Recorded(item);

        if (t is not null)
        {
            // The original is looked at first: the verified copy may only be removed while the original is there.
            FileSnapshot? s = SafeFile.TrySnapshot(src);
            if (s is null && SourceFolderMissing(item, src)) return;
            if (s is null)
            {
                // The original is gone (its folder is still there): the verified copy is all that is left, so it is
                // kept - also when the job is being ended.
                SetPhase("Copy check in progress");
                if (item.Sha256 is null) KeepCopy(item, temp, dst, JobPaths.UnverifiedCopySuffix, FailReasons.OriginalGoneDuringCopy);
                else if (Hash(temp, item.Size) != item.Sha256) KeepDamagedCopy(item, temp, dst);
                else PlaceAndDeleteSource(item, src, dst, temp, null); // under another name if its place is taken
                return;
            }
            if (_closing || d is not null)
            {
                TryDeleteTemp(temp); // the original is still in the source
                if (d is not null && !_closing) Skip(item, SkipReasons.AppearedDuringCopy);
                else item.Stage = ItemStage.Pending;
                return;
            }
            if (!s.SameFileAs(recorded, _job.CompareIds))
            {
                TryDeleteTemp(temp);
                Skip(item, SkipReasons.ChangedDuringMove);
                return;
            }
            SetPhase("Copy check in progress");
            if (item.Sha256 is null)
            {
                TryDeleteTemp(temp);
                Begin(item, src, dst);
                return;
            }
            ReadBack check = ReadBoth(temp, item.Size, src);
            switch (CheckSecondRead(item, src, check, recorded, item.Sha256, placed: false))
            {
                case SecondRead.Handled:
                    DiscardCopy(temp, src);
                    return;
                case SecondRead.Gone:
                    if (check.Copy == item.Sha256) PlaceAndDeleteSource(item, src, dst, temp, null);
                    else KeepDamagedCopy(item, temp, dst);
                    return;
            }
            if (check.Copy != item.Sha256)
            {
                TryDeleteTemp(temp);
                Begin(item, src, dst); // the unfinished copy was damaged: copy again
                return;
            }
            PlaceAndDeleteSource(item, src, dst, temp, s);
            return;
        }

        if (d is not null)
        {
            // The verified copy had already been renamed into place when the job stopped.
            FinishCopyInPlace(item, src, dst, d, recordPlaced: true);
            return;
        }

        Begin(item, src, dst);
    }

    private void FinishPlaced(JobItem item, string src, string dst) => FinishCopyInPlace(item, src, dst, SafeFile.TrySnapshot(dst), recordPlaced: false);

    /// <summary>
    /// The verified copy is in its final place. The original is removed once the copy and a second read of the
    /// original both match the recorded checksum. A copy that decayed on the target drive is set aside and made again.
    /// </summary>
    private void FinishCopyInPlace(JobItem item, string src, string dst, FileSnapshot? d, bool recordPlaced)
    {
        if (d is null) CheckRoots(); // never mistake a disconnected target for a missing copy
        FileSnapshot? s = SafeFile.TrySnapshot(src);
        if (d is null || d.IsDirectory)
        {
            if (s is not null)
            {
                Begin(item, src, dst); // the copy is gone but the original is there: copy it again
                return;
            }
            if (SourceFolderMissing(item, src)) return;
            Fail(item, FailReasons.CopyAndOriginalGone);
            return;
        }
        if (item.Sha256 is null)
        {
            Fail(item, "The job log has no checksum for this copy, so the job kept the original. Check the copy.");
            return;
        }
        SetPhase("Copy check in progress");
        if (s is null)
        {
            if (SourceFolderMissing(item, src)) return;
            // The original is already gone: the copy is all there is, so it is kept (after checking it).
            if (Hash(dst, d.Size) != item.Sha256)
            {
                Fail(item, FailReasons.PlacedCopyDamagedOriginalGone);
                return;
            }
            if (recordPlaced) RecordPlaced(item);
            Done(item, "copy");
            return;
        }
        ReadBack check = ReadBoth(dst, d.Size, src);
        if (check.Copy != item.Sha256)
        {
            if (IsUntouchedCopy(d, item)) SetAsideAndCopyAgain(item, src, dst);
            else Fail(item, FailReasons.CopyChangedByOtherProgram);
            return;
        }
        FileSnapshot recorded = Recorded(item);
        switch (CheckSecondRead(item, src, check, recorded, item.Sha256, placed: true))
        {
            case SecondRead.Handled:
                return;
            case SecondRead.Gone:
                if (recordPlaced) RecordPlaced(item);
                Done(item, "copy");
                return;
        }
        if (recordPlaced) RecordPlaced(item);
        DeleteSource(item, src, recorded);
    }

    private void RecordPlaced(JobItem item)
    {
        Record(new JournalRecord { Type = "placed", Index = item.Index });
        item.Stage = ItemStage.Placed;
    }

    /// <summary>
    /// The copy in the target still has exactly the size and times the job gave it, so a checksum mismatch means it
    /// decayed on the drive, not that a program edited it (which would have changed its modification time).
    /// </summary>
    private bool IsUntouchedCopy(FileSnapshot d, JobItem item)
    {
        FileSnapshot stamped = Stamp(item, Recorded(item)); // the times this job gave the copy
        long creation = stamped.CreationTime, lastWrite = stamped.LastWriteTime;
        long tolerance = TargetVolume?.IsFatFamily == true ? 2 * TimeSpan.TicksPerSecond : 0; // FAT rounds times
        return d.Size == item.Size && Math.Abs(d.LastWriteTime - lastWrite) <= tolerance && Math.Abs(d.CreationTime - creation) <= tolerance;
    }

    /// <summary>Renames a damaged copy out of the way ("name.damaged-copy") and copies the original again.</summary>
    private void SetAsideAndCopyAgain(JobItem item, string src, string dst)
    {
        SetPhase("Rename of a damaged copy");
        string aside = RenameAside(dst, dst, JobPaths.DamagedCopySuffix);
        // Recorded after the rename: if the job stops in between, the copy is simply missing and is made again.
        string rel = Path.GetRelativePath(_job.Target, aside);
        Record(new JournalRecord { Type = "setaside", Index = item.Index, Rel = rel, At = JournalRecord.Now() });
        item.SetAside = rel;
        item.Temp = null;
        Begin(item, src, dst, afterSetAside: true);
    }

    /// <summary>
    /// The original disappeared (someone else removed it) and the copy does not match its checksum. Nothing better
    /// exists any more, so the copy is kept under a name that says it is damaged, never deleted.
    /// </summary>
    private void KeepDamagedCopy(JobItem item, string copy, string dst) =>
        KeepCopy(item, copy, dst, JobPaths.DamagedCopySuffix, FailReasons.DamagedCopyKept);

    /// <summary>
    /// Keeps a copy that may be all that is left of a file under a name that says what it is ("name.unverified-copy",
    /// ...), records where it is, and fails the file with <paramref name="why"/> (given the kept file's name).
    /// </summary>
    private void KeepCopy(JobItem item, string copy, string dst, string suffix, Func<string, string> why)
    {
        string kept = RenameAside(copy, dst, suffix);
        string rel = Path.GetRelativePath(_job.Target, kept);
        Record(new JournalRecord { Type = "setaside", Index = item.Index, Rel = rel, At = JournalRecord.Now() });
        item.SetAside = rel;
        item.Temp = null;
        item.Stage = ItemStage.Pending;
        Fail(item, why(Path.GetFileName(kept)));
    }

    /// <summary>Renames a file to "&lt;dst&gt;&lt;suffix&gt;" (or "-2", "-3", ... when taken). Never overwrites.</summary>
    private static string RenameAside(string file, string dst, string suffix)
    {
        string aside = dst + suffix;
        for (int n = 2; ; n++)
        {
            int error = SafeFile.TryRename(file, aside);
            if (error == 0) return aside;
            if (error is not (ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS) || n > 99) throw new Win32IOException(error, "The rename of the copy (to keep it)", file);
            aside = $"{dst}{suffix}-{n}";
        }
    }

    /// <summary>
    /// Ends a file that was in flight while its original cannot be checked (closing a job whose source drive, or the
    /// file's folder in it, is not there). Nothing that may be the only copy is deleted and no original is judged: an
    /// unfinished copy is kept under a name that says so, a verified copy is put in its place, and the file is recorded
    /// as not moved ("copied, original kept" when its verified copy is in place). The job can then be ended, and the
    /// files that did move can be undone.
    /// </summary>
    private void CloseWithoutSource(JobItem item, string dst)
    {
        string? temp = item.Temp is null ? null : Path.Join(Path.GetDirectoryName(dst)!, item.Temp);
        FileSnapshot? t = temp is null ? null : SafeFile.TrySnapshot(temp);
        switch (item.Stage)
        {
            case ItemStage.Prepared:
                // A rename is all or nothing: the file is either in the target now, or still in the source.
                if (SafeFile.TrySnapshot(dst) is { IsDirectory: false } moved) RenameHappened(item, dst, moved);
                else item.Stage = ItemStage.Pending;
                return;
            case ItemStage.Copying when t is not null:
                KeepCopy(item, temp!, dst, JobPaths.UnverifiedCopySuffix, FailReasons.UnfinishedCopyKept);
                return;
            case ItemStage.Copied when t is not null:
                SetPhase("Copy check in progress");
                // The original could not be looked at, so it is never reported as gone: it is probably still in the source.
                if (item.Sha256 is null) KeepCopy(item, temp!, dst, JobPaths.UnverifiedCopySuffix, FailReasons.UnfinishedCopyKept);
                else if (Hash(temp!, t.Size) != item.Sha256) KeepCopy(item, temp!, dst, JobPaths.DamagedCopySuffix, FailReasons.DamagedCopyKeptNotChecked);
                else if (SafeFile.TryRename(temp!, dst) != 0) KeepCopy(item, temp!, dst, JobPaths.VerifiedCopySuffix, FailReasons.VerifiedCopyKeptNotChecked);
                else
                {
                    RecordPlaced(item);
                    Fail(item, FailReasons.OriginalNotChecked);
                }
                return;
            case ItemStage.Copied or ItemStage.Placed:
                if (SafeFile.TrySnapshot(dst) is not { IsDirectory: false })
                {
                    item.Stage = ItemStage.Pending; // no copy in the target: the original was never touched
                    return;
                }
                if (item.Stage == ItemStage.Copied) RecordPlaced(item); // renamed into place just before the job stopped
                Fail(item, FailReasons.OriginalNotChecked);
                return;
            default:
                item.Stage = ItemStage.Pending; // nothing of it is in the target
                return;
        }
    }

    // ---- Finishing ------------------------------------------------------------------------------------------

    private void Finish(string how)
    {
        SetPhase("Report update in progress");
        // A close with nothing in flight works without checking the drives; never touch a folder that is not this job's.
        bool ownTarget = JobPaths.RootOfJournal(_state.JournalPath) is { } here && JobPaths.SamePath(here, _job.Target)
                         && Drives.IsOn(_job.Target, _job.TargetSerial) && Drives.LeadsTo(_job.Target, _job.TargetReal);
        foreach (string rel in ownTarget ? FoldersToDate() : [])
        {
            if (!_state.SourceFolders.TryGetValue(rel, out FolderTimes times)) continue;
            string path = Path.Join(_job.Target, rel);
            try
            {
                if (SafeFile.TrySnapshot(path) is { IsDirectory: true }) // never a file that happens to have the folder's name
                    SafeFile.SetDirectoryTimes(path, times.CreationTime, times.LastWriteTime);
            }
            catch (IOException)
            {
                // Folder dates are cosmetic.
            }
        }
        var end = new JournalRecord
        {
            Type = "end",
            What = how,
            At = JournalRecord.Now(),
            Done = _state.DoneCount,
            Skipped = _state.SkippedCount,
            Failed = _state.FailedCount,
            Bytes = _state.BytesDone,
        };
        Record(end);
        _state.End = end;
        RecentJobs.Add(_state);
        if (_undo) UndoFactory.RecordUndoEnded(_state, how);
        JobReports.TryWrite(_state, createReceipt: true);
    }

    /// <summary>
    /// Keeps the receipt in the source, and the manifest and summary next to the log, up to date while the job runs,
    /// so after a crash or a power cut every file that left can be traced without the app: written when the job starts,
    /// as soon as the first file has left, and then every quarter of a minute or every hundred files or so - never so
    /// often that rewriting the lists of a very big job slows it down (at most 1/30 of the time goes into them).
    /// </summary>
    private void RefreshReports(bool force = false)
    {
        long now = _clock.ElapsedMilliseconds;
        bool firstMove = _done > 0 && !_reportedAMove;
        bool manyMoved = _done - _doneAtReport >= ReportsEveryFiles && now >= _reportsCheapMs;
        if (!force && !firstMove && !manyMoved && now < _reportsDueMs) return;
        JobReports.TryWrite(_state, createReceipt: true);
        _reportedAMove |= _done > 0;
        _doneAtReport = _done;
        long end = _clock.ElapsedMilliseconds, took = end - now;
        _reportsDueMs = end + Math.Max(ReportsIntervalMs, 30 * took);
        _reportsCheapMs = end + 30 * took;
    }

    /// <summary>Folders the job created get their source folder's dates; an undo also restores the folders it put files back into.</summary>
    private HashSet<string> FoldersToDate()
    {
        var folders = new HashSet<string>(_state.CreatedFolders, StringComparer.OrdinalIgnoreCase);
        if (_undo)
            foreach (JobItem item in _state.Items.Where(i => i.Stage == ItemStage.Done))
                for (string? d = Path.GetDirectoryName(item.Rel); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                    folders.Add(d);
        return folders;
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    /// <summary>
    /// The file is still the one that was planned. For an undo with a known checksum only the size must match here:
    /// the content decides (a drive with coarse timestamps may have rounded the times when the file was sorted).
    /// </summary>
    private bool MatchesPlan(JobItem item, FileSnapshot s) =>
        s.Size == item.Size && (s.LastWriteTime == item.LastWriteTime || (_undo && item.ExpectedSha256 is not null));

    private string ChangedReason => _undo ? SkipReasons.ChangedSinceSorted : SkipReasons.ChangedAfterPreview;

    /// <summary>
    /// The times a copy gets: those of the file it was copied from, except that an undo restores the exact times the
    /// file had before it was sorted (a FAT or exFAT drive in between may have rounded them).
    /// </summary>
    private FileSnapshot Stamp(JobItem item, FileSnapshot source) =>
        _undo ? source with { CreationTime = item.CreationTime, LastWriteTime = item.LastWriteTime } : source;

    /// <summary>The file as it was when its move started (falls back to the plan for older journals).</summary>
    private static FileSnapshot Recorded(JobItem item) =>
        new(item.Size, item.SeenCreationTime ?? item.CreationTime, item.SeenLastWriteTime ?? item.LastWriteTime, 0, 0, item.FileId, 0);

    private VolumeInfo? TargetVolume
    {
        get
        {
            if (_targetVolumeRead) return _targetVolume;
            _targetVolumeRead = true;
            try
            {
                _targetVolume = VolumeInfo.Of(_job.Target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _targetVolume = null;
            }
            return _targetVolume;
        }
    }

    /// <summary>The target file system can hold alternate data streams (NTFS, ReFS; not FAT or exFAT). Unknown counts as yes.</summary>
    private bool TargetStoresStreams => _options.TargetStoresStreams ?? TargetVolume is not { } v || (v.FileSystemFlags & FILE_NAMED_STREAMS) != 0;

    private long? FreeBytes(string folder)
    {
        if (_options.FreeBytes is { } probe) return probe(folder);
        string root = Path.TrimEndingDirectorySeparator(_job.Target) + "\\";
        return GetDiskFreeSpaceEx(root, out ulong free, out _, out _) ? (long)free : null;
    }

    /// <summary>Halts before copying when the next file does not fit, rather than finding out from a write error.</summary>
    private void EnsureSpaceFor(long bytes)
    {
        if (FreeBytes(_job.Target) is not long free || free >= bytes + RunSpaceMargin) return;
        string drive = TargetVolume?.DisplayNameWithLabel ?? (Path.GetPathRoot(_job.Target) ?? _job.Target).TrimEnd('\\');
        throw new JobHaltException(HaltReason.TargetFull,
            $"The target drive {drive} is full: the next file needs {Format.Bytes(bytes)}, and {Format.Bytes(Math.Max(0, free - RunSpaceMargin))} is free. "
            + "Make more space available on it. Then resume the job. Nothing was lost.");
    }

    private (FileSnapshot Snapshot, string? Sha) SnapshotAndHash(JobItem item, string path, bool hash)
    {
        bool unbuffered = true;
        while (true)
        {
            try
            {
                using SafeFileHandle h = Retry(() => SafeFile.OpenRead(path, unbuffered));
                FileSnapshot snapshot = SafeFile.Snapshot(h, path);
                if (!hash || !MatchesPlan(item, snapshot)) return (snapshot, null);
                _itemWork = 0;
                return (snapshot, SafeFile.ToHex(SafeFile.HashHandle(h, snapshot.Size, AddWork, _ct)));
            }
            catch (Win32IOException e) when (unbuffered && e.NativeError == ERROR_INVALID_PARAMETER)
            {
                unbuffered = false;
            }
        }
    }

    private string Hash(string path, long size, bool countWork = true) =>
        SafeFile.ToHex(Retry(() => SafeFile.HashFile(path, size, countWork ? AddWork : null, _ct)));

    /// <summary>Creates missing folders below the target folder. Never the target folder itself or anything above it.</summary>
    private void EnsureFolder(string folder)
    {
        if (_existingFolders.Contains(folder)) return;
        var missing = new Stack<string>();
        for (string f = folder; !JobPaths.SamePath(f, _job.Target) && !Directory.Exists(f);
             f = Path.GetDirectoryName(f) ?? throw new IOException($"The folder {folder} is not inside the target folder."))
            missing.Push(f);
        if (missing.Count > 0 && !SafeFile.DirectoryExists(_job.Target))
        {
            CheckRoots();
            throw new JobHaltException(HaltReason.TargetNotConnected, Drives.IsOn(_job.Target, _job.TargetSerial)
                ? FolderGone("target", _job.Target, _job.TargetLabel)
                : NotConnected("target", _job.Target, _job.TargetLabel));
        }
        while (missing.TryPop(out string? f))
        {
            Directory.CreateDirectory(f);
            string rel = Path.GetRelativePath(_job.Target, f);
            Record(new JournalRecord { Type = "mkdir", Rel = rel });
            _state.CreatedFolders.Add(rel);
        }
        _existingFolders.Add(folder);
    }

    /// <summary>
    /// Removes a copy that is not needed, but only while its original is there: otherwise the copy may be all that is
    /// left, so it stays (its stage in the log is unchanged, and Resume or Close sorts it out).
    /// </summary>
    private void DiscardCopy(string temp, string src)
    {
        try
        {
            if (SafeFile.TrySnapshot(src) is not { IsDirectory: false }) return;
        }
        catch (IOException)
        {
            return; // the original cannot even be looked at (its drive stopped responding)
        }
        TryDeleteTemp(temp);
    }

    private void TryDeleteTemp(string temp)
    {
        try
        {
            SafeFile.DeleteOwnTempFile(temp);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless and is cleaned up when the job resumes.
        }
    }

    private int RetryRename(string from, string to) => Retry(() =>
    {
        int error = SafeFile.TryRename(from, to);
        if (error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT)
            throw new Win32IOException(error, "The move", from);
        return error;
    }, retryAccessDenied: true);

    /// <summary>Retries while another program (antivirus, sync tool, thumbnailer) briefly holds the file.</summary>
    private T Retry<T>(Func<T> action, bool retryAccessDenied = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Win32IOException e) when ((e.IsSharingViolation || (retryAccessDenied && e.NativeError == ERROR_ACCESS_DENIED))
                                             && attempt < _retryDelays.Length)
            {
                string phase = _phase;
                SetPhase(e.IsSharingViolation ? "Short pause (another program uses the file)" : "Access denied (another attempt follows)");
                if (_ct.WaitHandle.WaitOne(_retryDelays[attempt])) _ct.ThrowIfCancellationRequested();
                SetPhase(phase);
            }
        }
    }

    private void Record(JournalRecord record) => _journal.Write(record);

    private void Done(JobItem item, string how)
    {
        Record(new JournalRecord { Type = "done", Index = item.Index, How = how, Sha256 = item.Sha256, At = JournalRecord.Now() });
        if (item.Failed) _failed--;
        _done++;
        _notRemovableInARow = 0;
        item.Stage = ItemStage.Done;
        item.How = how;
        item.Failed = false;
        item.Note = null;
    }

    private void Skip(JobItem item, string why)
    {
        Record(new JournalRecord { Type = "skip", Index = item.Index, Why = why, At = JournalRecord.Now() });
        if (item.Failed) _failed--;
        _skipped++;
        item.Stage = ItemStage.Skipped;
        item.Note = why;
        item.Failed = false;
    }

    private void Fail(JobItem item, string error)
    {
        Record(new JournalRecord { Type = "fail", Index = item.Index, Error = error, At = JournalRecord.Now() });
        if (!item.Failed) _failed++;
        item.Failed = true;
        item.Note = error;
    }

    private void Fault(string point, JobItem item) => _options.Faults?.Hit(point, item.Index);

    /// <summary>Progress units: the copy path reads the original twice and the copy once; a checksummed rename reads once.</summary>
    private long WorkFor(JobItem item) => _copy ? item.Size * 3 : _job.Verify ? item.Size : 0;

    private void AddWork(long bytes)
    {
        Interlocked.Add(ref _itemWork, bytes);
        if (_options.Pause?.Wait(_ct) == true) _pausedMidFile = true;
        Report();
    }

    private void SetPhase(string phase)
    {
        lock (_reportLock) _phase = phase;
        Report();
    }

    private void Report(bool force = false)
    {
        if (_options.Progress is null) return;
        lock (_reportLock)
        {
            long ms = _clock.ElapsedMilliseconds;
            if (!force && ms - _lastReportMs < 100) return;
            _lastReportMs = ms;

            long done = _workFinished + Math.Min(Interlocked.Read(ref _itemWork), _itemWorkLimit);
            _samples.Enqueue((ms, done));
            while (_samples.Count > 2 && ms - _samples.Peek().Ms > 5000) _samples.Dequeue();
            (long firstMs, long firstWork) = _samples.Peek();
            double rate = ms - firstMs > 250 ? (done - firstWork) * 1000.0 / (ms - firstMs) : 0;
            TimeSpan? remaining = rate > 0 && _workTotal > done ? TimeSpan.FromSeconds((_workTotal - done) / rate) : null;
            _options.Progress.Report(new RunProgress(_state.Items.Count, _done + _skipped + _failed, _done, _skipped, _failed,
                _workTotal, done, _currentFile, _phase, rate, remaining));
        }
    }

    public void Dispose() => _journal.Dispose();
}

/// <summary>Writes the journal for a confirmed plan. The journal, not the in-memory plan, is what gets executed.</summary>
public static class JobFactory
{
    public static string ToolName { get; } = "IVAR Offload " + (typeof(JobFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");

    internal const int JournalVersion = 2;

    public static string CreateJournal(MovePlan plan)
    {
        if (!plan.CanRun) throw new InvalidOperationException("This plan cannot start: " + string.Join(" ", plan.Messages.Where(m => m.Level == MessageLevel.Error).Select(m => m.Text)));

        Directory.CreateDirectory(plan.TargetRoot);
        string logFolder = JobPaths.LogFolder(plan.TargetRoot);
        Directory.CreateDirectory(logFolder);
        string id = NewJobId(Planner.Word(plan.Mode));
        string path = Path.Join(logFolder, id + JobPaths.JournalSuffix);

        using (JournalWriter writer = JournalWriter.CreateNew(path))
        {
            writer.Write(new JournalRecord
            {
                Type = "job",
                Version = JournalVersion,
                JobId = id,
                Kind = "sort",
                At = JournalRecord.Now(),
                Tool = ToolName,
                Machine = Environment.MachineName,
                User = Environment.UserName,
                Source = plan.SourceRoot,
                Target = plan.TargetRoot,
                SourceSerial = plan.SourceVolume?.SerialNumber,
                SourceLabel = plan.SourceVolume?.Label,
                TargetSerial = plan.TargetVolume?.SerialNumber,
                TargetLabel = plan.TargetVolume?.Label,
                SourceReal = Drives.RealIfSubst(plan.SourceRoot),
                TargetReal = Drives.RealIfSubst(plan.TargetRoot),
                Mode = plan.Mode.ToString(),
                Method = plan.Method.ToString(),
                Verify = plan.VerifyChecksums,
                CompareIds = plan.SourceVolume?.HasStableFileIds ?? false,
                Count = plan.ToMove.Count,
                Bytes = plan.BytesToMove,
            }, flush: false);

            // Original dates of every folder that will be recreated in the target.
            var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SourceFile f in plan.ToMove)
                for (string? d = f.Directory; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                    folders.Add(d);
            foreach (string folder in folders)
                if (plan.Scan.Folders.TryGetValue(folder, out FolderTimes t))
                    writer.Write(new JournalRecord { Type = "dir", Rel = folder, CreationTime = t.CreationTime, LastWriteTime = t.LastWriteTime }, flush: false);

            int index = 0;
            foreach (SourceFile f in GroupedOrder(plan.ToMove))
            {
                writer.Write(new JournalRecord
                {
                    Type = "item",
                    Index = index++,
                    Rel = f.RelativePath,
                    Group = f.GroupKey.Length > 0 ? f.GroupKey : null,
                    Size = f.Size,
                    CreationTime = f.CreationTime,
                    LastWriteTime = f.LastWriteTime,
                    Side = f.Side.ToString(),
                    Why = f.Reason,
                }, flush: false);
            }
            // Files the preview kept in the source: never run, but the job's reports say they are still there.
            foreach (SourceFile f in plan.HeldBack)
                writer.Write(new JournalRecord
                {
                    Type = "held",
                    Rel = f.RelativePath,
                    Group = f.GroupKey.Length > 0 ? f.GroupKey : null,
                    Size = f.Size,
                    CreationTime = f.CreationTime,
                    LastWriteTime = f.LastWriteTime,
                    Side = f.Side.ToString(),
                    Why = f.Reason,
                }, flush: false);
            writer.Write(new JournalRecord { Type = "ready", Count = plan.ToMove.Count, Bytes = plan.BytesToMove });
        }
        RecentJobs.Add(path);
        return path;
    }

    /// <summary>"20260926-140211-videos-a1b2": sorts by time, and says what kind of job it is.</summary>
    internal static string NewJobId(string word) => $"{DateTime.Now:yyyyMMdd-HHmmss}-{word}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}";

    /// <summary>
    /// The order files are moved in: groups in the order their first file appears in the plan, and inside a group the
    /// main files (clips, photos) first, so a companion never moves before the file it belongs to.
    /// </summary>
    internal static List<SourceFile> GroupedOrder(IReadOnlyList<SourceFile> files)
    {
        var groups = new Dictionary<string, List<SourceFile>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<List<SourceFile>>();
        foreach (SourceFile f in files)
        {
            if (f.GroupKey.Length == 0)
            {
                order.Add([f]);
                continue;
            }
            if (!groups.TryGetValue(f.GroupKey, out List<SourceFile>? members))
            {
                groups[f.GroupKey] = members = [];
                order.Add(members);
            }
            members.Add(f);
        }
        return order.SelectMany(g => g.OrderBy(f => f.Role == FileRole.Primary ? 0 : 1)).ToList();
    }
}
