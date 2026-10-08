using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using Microsoft.Win32.SafeHandles;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.Backup;

/// <summary>How one destination is doing, for the progress display.</summary>
/// <param name="Offline">Why this destination is not taking part any more (not connected, full, ...); null while it is.</param>
public sealed record BackupDestinationProgress(string Folder, string DriveName, int Verified, int Failed, long BytesVerified, string? Offline);

public sealed record BackupProgress(
    int ItemsTotal,
    int ItemsFinished,
    long WorkTotal,
    long WorkDone,
    string? CurrentFile,
    string Phase,
    double BytesPerSecond,
    TimeSpan? Remaining,
    IReadOnlyList<BackupDestinationProgress> Destinations)
{
    public double Fraction => WorkTotal > 0 ? (double)WorkDone / WorkTotal : ItemsTotal > 0 ? (double)ItemsFinished / ItemsTotal : 1;
}

/// <summary>What happened on one destination.</summary>
/// <param name="Verified">Files whose copy on this destination was read back from disk and matched.</param>
/// <param name="Failed">Files that could not be copied here yet (Resume tries them again).</param>
/// <param name="NotCopied">Files not copied here: not reached yet, skipped (changed on the card, gone from it) or left when the backup was ended.</param>
/// <param name="Problem">Why this destination stopped or could not take part (not connected, full, ...); null when it did not.</param>
/// <param name="MhlSkipped">Why no ASC MHL generation was written although every file is verified (the card's own history can't be read).</param>
public sealed record BackupDestinationResult(string Folder, string DriveName, string JournalPath, int Verified, int Failed, int NotCopied,
    bool Ended, bool MhlWritten, string? Problem, HaltReason Halt, string? MhlSkipped = null)
{
    /// <summary>Volume serial number of the destination's drive (0 when unknown), to tell separate drives apart.</summary>
    public uint Serial { get; init; }
    /// <summary>The card no longer matches the checksums its own ASC MHL history recorded (changed or missing files).</summary>
    public bool MhlMismatch { get; init; }

    // A top-up: what happened to the files the earlier backup held.
    /// <summary>Card files copied here (in a top-up: new on the card, changed since, or no longer in the folder).</summary>
    public int Copied { get; init; }
    /// <summary>Top-ups: card files already in the backup folder that were read again and matched (nothing written).</summary>
    public int Rechecked { get; init; }
    /// <summary>Top-ups: card files changed since the earlier backup; their old versions were moved into _IVAROffload\replaced.</summary>
    public int Replaced { get; init; }
    /// <summary>Top-ups: copies in the backup folder that no longer matched their checksum (damaged since); moved aside and copied again.</summary>
    public int Repaired { get; init; }
    /// <summary>
    /// Top-ups: files in the backup folder changed there since the earlier backup (edited by another program), or not from
    /// it; moved aside, and the card's file copied in their place.
    /// </summary>
    public int Edited { get; init; }
    /// <summary>Top-ups: files of the earlier backup, no longer on the card, changed in the folder since (left as they are, not verified).</summary>
    public int KeptEdited { get; init; }
    /// <summary>
    /// Top-ups: the earlier versions (relative paths, "IMG_0450 (earlier).JPG") of photos and clips changed on the card
    /// since - usually another shot with a reused number - kept next to the new ones.
    /// </summary>
    public IReadOnlyList<string> KeptBeside { get; init; } = [];
    /// <summary>Top-ups: card files that the earlier backup had verified but that were no longer in the folder, copied again.</summary>
    public int Restored { get; init; }
    /// <summary>Top-ups: files of the earlier backup that are no longer on the card, still in the folder and verified there.</summary>
    public int Kept { get; init; }
    /// <summary>Top-ups: files of the earlier backup that are neither on the card nor in the folder any more.</summary>
    public int KeptGone { get; init; }
    /// <summary>Top-ups: files of the earlier backup, no longer on the card, whose copy here was damaged (moved aside; the only copy is gone).</summary>
    public IReadOnlyList<string> KeptDamaged { get; init; } = [];
    /// <summary>Top-ups: where the folder's earlier ASC MHL history was moved before a new one was started; null when it was continued.</summary>
    public string? MhlRestarted { get; init; }

    /// <summary>Every file of the card is verified here, the log is ended, and the ASC MHL generation is written (or could not be, for a stated reason).</summary>
    public bool Complete(int files) => Ended && Verified == files && (MhlWritten || MhlSkipped is not null) && Problem is null;
}

/// <summary>The outcome of running (or ending) a backup.</summary>
public sealed record BackupResult(RunStatus Status, int Files, long Bytes, IReadOnlyList<BackupDestinationResult> Destinations,
    string? Message, HaltReason Halt, string JournalPath)
{
    /// <summary>Files, linked folders and unreadable folders of the card that the backup left out (see the plan's warnings).</summary>
    public int LeftOut { get; init; }
    /// <summary>
    /// Files found on the card after the backup that are not in it as they are now: added, or changed, after the preview.
    /// Only known when the card could be scanned again after a backup that finished.
    /// </summary>
    public IReadOnlyList<string> NotInBackup { get; init; } = [];
    /// <summary>The card could not be scanned again when the backup finished (so <see cref="NotInBackup"/> is not known).</summary>
    public bool CardNotRescanned { get; init; }
    /// <summary>Volume serial number of the card's drive (0 when unknown).</summary>
    public uint SourceSerial { get; init; }
    /// <summary>A top-up: the job id of the earlier backup the card's new files were added to; null for a full backup.</summary>
    public string? AddsTo { get; init; }

    /// <summary>
    /// How many of the copies are on a drive of their own, apart from the other copies and from the drive being backed
    /// up (destinations whose drive has no known serial number count as separate).
    /// </summary>
    public int SeparateDrives => Destinations.Where(d => d.Serial == 0 || d.Serial != SourceSerial)
        .Select(d => d.Serial == 0 ? "?" + d.Folder : d.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct().Count();

    /// <summary>
    /// Every file of the card has a verified copy on every destination, each destination has its ASC MHL generation (or
    /// says why the card's own history prevented it), and nothing on the card was left out. Whether the copies are on
    /// separate drives is <see cref="SeparateDrives"/>.
    /// </summary>
    public bool AllVerified => Status == RunStatus.Completed && LeftOut == 0 && NotInBackup.Count == 0 && !CardNotRescanned
                               && Destinations.Count > 0 && Destinations.All(d => d.Complete(Files) && !d.MhlMismatch && d.KeptDamaged.Count == 0);
}

public sealed class BackupOptions
{
    public IProgress<BackupProgress>? Progress { get; init; }
    public PauseGate? Pause { get; init; }
    public IFaultInjector? Faults { get; init; }

    // Test knobs.
    internal int[]? RetryDelaysMs { get; init; }
    internal Func<string, long?>? FreeBytes { get; init; }
    internal bool? DestinationsStoreStreams { get; init; }
}

/// <summary>Stops one destination (full, gone, damaging files) while the others go on. Resume retries it.</summary>
internal sealed class DestinationHaltException(HaltReason reason, string message) : Exception(message)
{
    public HaltReason Reason { get; } = reason;
}

/// <summary>
/// Runs a backup from the logs of its destinations. Every card file is read once and written to every destination at
/// the same time; each copy is flushed, read back from its own disk (bypassing the cache) and compared by SHA-256, and
/// the card file is read a second time (unless that is turned off) - only then is the copy renamed into place. A
/// destination that fails (full, unplugged, damaging files) drops out and the others go on; Resume retries it. Nothing
/// on the card is ever written, renamed or deleted: it is opened for reading only. Every step is recorded in each
/// destination's own log before or after it happens, so a crash, a pulled card or a power cut is reconciled on resume.
/// </summary>
public sealed class BackupRunner : IDisposable
{
    private static readonly int[] DefaultRetryDelaysMs = [250, 500, 1000, 2000, 4000];
    private const int ReportsIntervalMs = 15_000;

    private sealed class Dest
    {
        public required int Index { get; init; }
        public required string Folder { get; init; }
        public required string JournalPath { get; init; }
        public uint Serial { get; init; }
        public string Label { get; init; } = "";
        public JobState? State { get; set; }
        public JournalWriter? Writer { get; set; }
        public string? Offline { get; set; }
        public HaltReason Halt { get; set; }
        public VolumeInfo? Volume { get; set; }
        public bool VolumeRead { get; set; }
        public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Online => Writer is not null && Offline is null;
        /// <summary>This destination's log had already ended when the backup was opened (it finished in an earlier run).</summary>
        public bool EndedBefore { get; set; }
        public string DriveName => Label.Length > 0 ? $"{Drives.Letter(Folder)} ({Label})" : Drives.Letter(Folder);
        /// <summary>The log folder this destination's log is in (IVAR Ingest's _IVARIngest for a backup it started).</summary>
        public string LogFolderName => JobPaths.LogFolderNameOf(JournalPath);
    }

    private readonly List<Dest> _dests;
    private readonly JobHeader _job;
    /// <summary>
    /// Where the card is now: the recorded folder, or the same folder on the drive with the card's serial number when
    /// the card came back under another letter (card readers often change letters). Only ever read from.
    /// </summary>
    private string _source;
    private readonly List<JobItem> _plan;
    private readonly BackupOptions _options;
    private readonly string _tempPrefix;
    private readonly int[] _retryDelays;
    private CancellationToken _ct;
    private bool _closing;
    private int _sourceMismatches;
    private volatile bool _pausedMidFile;

    private readonly object _reportLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(long Ms, long Work)> _samples = new();
    private long _lastReportMs = -1000, _reportsDueMs;
    private long _workTotal, _workFinished, _itemWork, _itemWorkLimit;
    private int _itemsFinished;
    private string? _currentFile;
    private string _phase = "";

    private BackupRunner(List<Dest> dests, JobHeader job, BackupOptions options)
    {
        _dests = dests;
        _job = job;
        _plan = dests.First(d => d.State is not null).State!.Items;
        _cardFiles = _plan.Count(i => !IsKept(i));
        _cardNames = _plan.Select(i => i.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _source = job.Source;
        _options = options;
        _tempPrefix = job.Id.Length >= 4 ? job.Id[^4..] : "bak";
        _retryDelays = options.RetryDelaysMs ?? DefaultRetryDelaysMs;
    }

    public JobHeader Header => _job;
    /// <summary>The card's files (a top-up's logs also list the earlier backup's files that are no longer on the card).</summary>
    public int FileCount => _cardFiles;
    public long Bytes => _plan.Where(i => !IsKept(i)).Sum(i => i.Size);

    private readonly int _cardFiles;
    /// <summary>Every file the logs list: an earlier version kept next to a new one never takes one of their names.</summary>
    private readonly HashSet<string> _cardNames;

    /// <summary>This backup adds the card's new files to an earlier backup, in its folder.</summary>
    private bool TopUp => _job.AddsTo is not null;

    private static bool IsKept(JobItem item) => item.Why == BackupWhy.Kept;

    /// <summary>Every card file is verified here (and every file of the earlier backup no longer on the card was looked at).</summary>
    private static bool AllDone(JobState state) => state.Items.All(i => IsKept(i) ? i.IsFinished : i.Stage == ItemStage.Done);

    /// <summary>The log of each destination (in the order they were chosen).</summary>
    public IReadOnlyList<string> Journals => _dests.Select(d => d.JournalPath).ToList();

    /// <summary>Creates the logs for a confirmed plan and returns a runner for them.</summary>
    public static BackupRunner Start(BackupPlan plan, BackupOptions? options = null) => Open(BackupFactory.Create(plan), options);

    /// <summary>
    /// Opens a backup from the log of any of its destinations, to continue it. The logs of the other destinations are
    /// found where the backup put them; a destination whose drive is not connected sits this run out (Resume later).
    /// </summary>
    /// <exception cref="JournalException">The backup cannot be run: not a backup, already finished, open elsewhere (InUse), or no unfinished destination is connected.</exception>
    public static BackupRunner Open(string journalPath, BackupOptions? options = null)
    {
        JobState first = JournalReader.Read(journalPath);
        if (!first.IsBackup) throw new JournalException("This is not the log of a backup.");
        JobHeader h = first.Header;
        IReadOnlyList<string> targets = h.Targets.Count > 0 ? h.Targets : [h.Target];
        var dests = new List<Dest>();
        try
        {
            for (int k = 0; k < targets.Count; k++)
            {
                uint serial = h.TargetSerials.ElementAtOrDefault(k);
                string folder = BackupPaths.FindDestination(targets[k], serial, h.Id);
                var d = new Dest
                {
                    Index = k,
                    Folder = folder,
                    JournalPath = BackupPaths.JournalPath(folder, h.Id),
                    Serial = serial,
                    Label = h.TargetLabels.ElementAtOrDefault(k) ?? "",
                };
                dests.Add(d);
                if (!File.Exists(d.JournalPath))
                {
                    (d.Halt, d.Offline) = WhyUnreachable(d);
                    continue;
                }
                JournalWriter writer;
                JobState state;
                try
                {
                    writer = JournalWriter.OpenForAppend(d.JournalPath, out state);
                }
                catch (Exception e) when (e is JournalException { InUse: false } or IOException or UnauthorizedAccessException)
                {
                    // One damaged log never keeps the other destinations from going on: this one sits out, saying why.
                    (d.Halt, d.Offline) = (HaltReason.LogMoved, $"The log of this backup on {d.DriveName} can't be read ({e.Message.TrimEnd('.')}). The other destinations go on.");
                    continue;
                }
                if (!state.IsBackup || state.Header.Id != h.Id || !state.PlanComplete)
                {
                    writer.Dispose();
                    (d.Halt, d.Offline) = (HaltReason.LogMoved, $"The log in {d.Folder} does not belong to this backup (or was never completed). The other destinations go on.");
                    continue;
                }
                d.State = state;
                d.EndedBefore = state.IsEnded;
                if (state.IsEnded) writer.Dispose();
                else d.Writer = writer;
            }
            List<JobItem>? reference = null;
            foreach (Dest d in dests.Where(d => d.State is not null))
            {
                reference ??= d.State!.Items;
                if (d.State!.Items.Count != reference.Count || d.State.Items.Where((item, i) => item.Rel != reference[i].Rel).Any())
                    throw new JournalException($"The log in {d.Folder} lists other files than the rest of this backup; it was changed. Check the destinations.");
            }
            if (dests.All(d => d.State?.IsEnded == true)) throw new JournalException("This backup has already finished.");
            if (!dests.Any(d => d.State is not null))
                throw new JournalException("None of this backup's logs can be used: " + string.Join(" ", dests.Select(d => d.Offline)));
            if (!dests.Any(d => d.Writer is not null))
                throw new JournalException("None of the destinations that still need copies is connected: "
                    + string.Join(" ", dests.Where(d => d.Offline is not null).Select(d => d.Offline)));
        }
        catch
        {
            foreach (Dest d in dests) d.Writer?.Dispose();
            throw;
        }
        var runner = new BackupRunner(dests, h, options ?? new BackupOptions());
        runner.Remember();
        return runner;
    }

    /// <summary>Copies every file not yet verified on every connected destination. Safe to call again after a stop, crash, halt or failure.</summary>
    public BackupResult Run(CancellationToken ct = default) => Execute(closing: false, ct);

    /// <summary>
    /// Ends the backup without copying anything new: copies that were in the middle of being made are finished (when
    /// they were already verified) or removed, and everything else is recorded as not copied. Needs no card.
    /// </summary>
    public BackupResult Close(CancellationToken ct = default) => Execute(closing: true, ct);

    private BackupResult Execute(bool closing, CancellationToken ct)
    {
        _ct = ct;
        _closing = closing;
        using var awake = new KeepAwake();
        string? haltMessage = null;
        HaltReason halt = HaltReason.None;
        foreach (Dest d in _dests.Where(d => d.Online))
            OnDest(d, null, () => Record(d, new JournalRecord { Type = "session", What = closing ? "close" : "run", At = JournalRecord.Now(), Machine = Environment.MachineName, User = Environment.UserName }));

        try
        {
            if (!closing)
            {
                FollowCard();
                CheckSource();
            }
            foreach (Dest d in _dests.Where(d => d.Online)) CheckDestination(d);
            if (!_dests.Any(d => d.Online)) throw NoDestinationLeft();
            ComputeWork();
            RefreshReports(force: true);

            for (int i = 0; i < _plan.Count; i++)
            {
                List<Dest> active = _dests.Where(d => d.Online && !d.State!.Items[i].IsFinished).ToList();
                if (active.Count == 0) continue;
                if (ct.IsCancellationRequested) break;
                try
                {
                    // A drive can be unplugged while the job is paused: check again before going on.
                    if (_options.Pause?.Wait(ct) == true || _pausedMidFile)
                    {
                        _pausedMidFile = false;
                        if (!closing) CheckSource();
                        foreach (Dest d in active) CheckDestination(d);
                        active.RemoveAll(d => !d.Online);
                        if (active.Count == 0)
                        {
                            if (!_dests.Any(d => d.Online)) throw NoDestinationLeft();
                            continue;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _currentFile = _plan[i].Rel;
                _itemWork = 0;
                _itemWorkLimit = WorkFor(i, active.Count);
                try
                {
                    ProcessItem(i, active);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    _workFinished += _itemWorkLimit;
                    _itemWork = 0;
                    _itemsFinished++;
                    Report();
                    RefreshReports();
                }
                if (!_dests.Any(d => d.Online)) throw NoDestinationLeft();
            }
        }
        catch (JobHaltException h)
        {
            haltMessage = h.Message;
            halt = h.Reason;
        }
        _currentFile = null;

        bool stopped = ct.IsCancellationRequested && haltMessage is null;
        if (haltMessage is null && !stopped)
            foreach (Dest d in _dests.Where(d => d.Online))
            {
                if (closing && AllDone(d.State!)) FinishDest(d, "completed"); // nothing was left to copy
                else if (closing)
                {
                    // A file that failed keeps its reason (e.g. a foreign file with its name in the destination).
                    foreach (JobItem item in d.State!.Items.Where(i => !i.IsFinished))
                    {
                        string ended = EndedReason(d, item);
                        OnDest(d, item, () => Skip(d, item, item.Failed && item.Note is { Length: > 0 } why ? $"{ended} ({why})" : ended));
                    }
                    FinishDest(d, "closed");
                }
                else if (d.State!.Items.All(i => i.IsFinished)) FinishDest(d, "completed");
            }
        foreach (Dest d in _dests.Where(d => d.Writer is not null && d.State is { IsEnded: false }))
            BackupReports.TryWrite(d.State!, DestinationNames());

        RunStatus status = haltMessage is not null ? RunStatus.Halted
            : stopped ? RunStatus.Stopped
            : closing && !_dests.All(d => d.State?.End?.What == "completed") ? RunStatus.Closed
            : _dests.All(d => d.State?.IsEnded == true) ? RunStatus.Completed
            : RunStatus.CompletedWithFailures;
        if (status == RunStatus.Completed)
            foreach (Dest d in _dests.Where(d => d.EndedBefore)) RecheckEarlier(d);
        IReadOnlyList<string> notInBackup = [];
        bool notRescanned = false;
        if (status == RunStatus.Completed)
        {
            if (closing) FollowCard();
            SetPhase("Checking the card again");
            (notInBackup, notRescanned) = RescanCard();
        }
        Remember();
        Report(force: true);
        string? message = haltMessage ?? (status == RunStatus.CompletedWithFailures || status == RunStatus.Closed
            ? _dests.FirstOrDefault(d => d.Offline is not null)?.Offline : null);
        JobState any = _dests.First(d => d.State is not null).State!;
        return new BackupResult(status, _cardFiles, Bytes, _dests.Select(ResultOf).ToList(), message, halt, _dests[0].JournalPath)
        {
            SourceSerial = _job.SourceSerial,
            AddsTo = _job.AddsTo,
            LeftOut = any.HeldBack.Count,
            NotInBackup = notInBackup,
            CardNotRescanned = notRescanned,
        };
    }

    private BackupDestinationResult ResultOf(Dest d)
    {
        List<JobItem> card = d.State?.Items.Where(i => !IsKept(i)).ToList() ?? [];
        List<JobItem> kept = d.State?.Items.Where(IsKept).ToList() ?? [];
        return new BackupDestinationResult(d.Folder, d.DriveName, d.JournalPath,
            card.Count(i => i.Stage == ItemStage.Done), card.Count(i => i.Failed && !i.IsFinished),
            d.State is null ? _cardFiles : card.Count(i => i.Stage != ItemStage.Done && !i.Failed),
            d.State?.IsEnded == true, d.State?.Mhl is not null, d.Offline, d.Halt, d.State?.MhlSkipped)
        {
            Serial = d.Serial,
            MhlMismatch = d.State?.MhlMismatch == true,
            Copied = card.Count(i => i.Stage == ItemStage.Done && i.How == "copy"),
            Rechecked = card.Count(i => i.Stage == ItemStage.Done && i.How == "verify"),
            Replaced = card.Count(i => i.Stage == ItemStage.Done && i.SetAside is not null && i.SetAsideWhy == BackupReasons.AsideChanged && !IsBeside(i)),
            KeptBeside = card.Where(i => i.Stage == ItemStage.Done && IsBeside(i)).Select(i => i.SetAside!).ToList(),
            Repaired = card.Count(i => i.Stage == ItemStage.Done && i.SetAside is not null && i.SetAsideWhy == BackupReasons.AsideDamaged),
            Edited = card.Count(i => i.Stage == ItemStage.Done && i.SetAside is not null && i.SetAsideWhy is BackupReasons.AsideEdited or BackupReasons.AsideNotFromBackup),
            KeptEdited = kept.Count(i => i.Stage == ItemStage.Skipped && i.Note == BackupReasons.KeptEdited),
            Restored = card.Count(i => i.Stage == ItemStage.Done && i.How == "copy" && i.SetAside is null && i.Why is BackupWhy.BackedUp or BackupWhy.Chain or BackupWhy.Changed),
            Kept = kept.Count(i => i.Stage == ItemStage.Done),
            KeptGone = kept.Count(i => i.Stage == ItemStage.Skipped && i.SetAside is null && i.Note == BackupReasons.KeptGone),
            KeptDamaged = kept.Where(i => i.Stage == ItemStage.Skipped && i.Note == BackupReasons.KeptDiffers).Select(i => i.Rel).ToList(),
            MhlRestarted = d.State?.Mhl is not null ? d.State.MhlRestarted : null,
        };
    }

    /// <summary>Why a file is recorded as not done when the backup is ended before it got to it.</summary>
    private static string EndedReason(Dest d, JobItem item) =>
        item.ReplaceWhy is null && item.SetAside is null && item.Why is BackupWhy.BackedUp or BackupWhy.Chain or BackupWhy.Kept
        && SafeFile.TrySnapshot(Path.Join(d.Folder, item.Rel)) is { IsDirectory: false }
            ? BackupReasons.NotCheckedEnded : BackupReasons.NotCopiedEnded;

    private static JobHaltException NoDestinationLeft() =>
        new(HaltReason.TargetNotConnected, "No destination can be written to any more (see each destination). Fix that, then press Resume. Nothing was lost.");

    // ---- One file -------------------------------------------------------------------------------------------

    private void ProcessItem(int i, List<Dest> active)
    {
        if (IsKept(_plan[i]))
        {
            foreach (Dest d in active)
            {
                JobItem kept = d.State!.Items[i];
                OnDest(d, kept, () =>
                {
                    if (_closing) Skip(d, kept, EndedReason(d, kept));
                    else CheckKept(d, kept);
                });
            }
            return;
        }
        var copyTo = new List<Dest>();
        var check = new List<Dest>();
        foreach (Dest d in active)
        {
            JobItem item = d.State!.Items[i];
            OnDest(d, item, () =>
            {
                switch (Reconcile(d, item))
                {
                    case Next.Copy:
                        copyTo.Add(d);
                        break;
                    case Next.Check:
                        check.Add(d);
                        break;
                }
            });
        }
        if (copyTo.Count + check.Count == 0) return;
        if (_closing)
        {
            foreach (Dest d in copyTo.Concat(check)) OnDest(d, d.State!.Items[i], () => Skip(d, d.State!.Items[i], EndedReason(d, d.State!.Items[i])));
            return;
        }
        if (check.Count > 0) CheckExisting(i, check, copyTo);
        if (copyTo.Count > 0) CopyTo(i, copyTo, attempt: 1);
    }

    private enum Next { None, Copy, Check }

    /// <summary>
    /// Brings an interrupted file on one destination back to a known state. A copy that was already verified is put
    /// into place; anything unfinished is removed. Returns what the file needs on this destination: to be copied (again),
    /// or, in a top-up, the file in the backup folder to be checked (nothing when it is done or failed here).
    /// </summary>
    private Next Reconcile(Dest d, JobItem item)
    {
        string dst = Path.Join(d.Folder, item.Rel);
        string? temp = item.Temp is null ? null : Path.Join(Path.GetDirectoryName(dst)!, item.Temp);
        switch (item.Stage)
        {
            case ItemStage.Copying:
                if (temp is not null && SafeFile.TrySnapshot(temp) is { IsDirectory: false }) TryDeleteTemp(temp);
                item.Stage = ItemStage.Pending;
                break;
            case ItemStage.Copied:
                FileSnapshot? t = temp is null ? null : SafeFile.TrySnapshot(temp);
                FileSnapshot? f = SafeFile.TrySnapshot(dst);
                if (f is null && t is { IsDirectory: false })
                {
                    SetPhase("Verifying");
                    if (Hash(temp!, t.Size) == item.Sha256)
                    {
                        Place(d, item, temp!, dst, null);
                        return Next.None;
                    }
                    TryDeleteTemp(temp!);
                }
                else if (f is { IsDirectory: false } && t is { IsDirectory: false } && item.ReplaceWhy is not null)
                {
                    // A top-up stopped before the old file was moved aside: the verified copy is put in its place now.
                    SetPhase("Verifying");
                    if (Hash(temp!, t.Size) == item.Sha256)
                    {
                        Place(d, item, temp!, dst, null);
                        return Next.None;
                    }
                    TryDeleteTemp(temp!);
                }
                else if (f is { IsDirectory: false } && TopUp && Hash(dst, f.Size) != item.Sha256)
                {
                    // A top-up whose copy did not get into place: the file there is looked at again.
                    if (t is { IsDirectory: false }) TryDeleteTemp(temp!);
                }
                else if (f is { IsDirectory: false })
                {
                    // The verified copy had been renamed into place when the backup stopped: check it again.
                    if (t is { IsDirectory: false }) TryDeleteTemp(temp!);
                    SetPhase("Verifying");
                    if (Hash(dst, f.Size) == item.Sha256) Done(d, item);
                    else Fail(d, item, BackupReasons.DifferentInDestination);
                    return Next.None;
                }
                item.Stage = ItemStage.Pending;
                break;
        }
        if (SafeFile.TrySnapshot(dst) is { } there)
        {
            // A top-up finds the earlier backup's file there: it is checked (and moved aside when it is not the card's).
            if (TopUp && !there.IsDirectory) return Next.Check;
            Fail(d, item, BackupReasons.NameTakenInDestination);
            return Next.None;
        }
        return Next.Copy;
    }

    // ---- Top-ups: the files already in the backup folder ----------------------------------------------------

    /// <summary>
    /// A top-up, for card files that are already in the backup folder: nothing is written. A file the earlier backup
    /// verified is read again from the card and from each copy, and counts as verified here when both match the checksum
    /// the earlier backup recorded. Anything else in the folder under the file's name (an older version of a file changed
    /// on the card since, a copy damaged since, a file that is not from the earlier backup) is moved into
    /// _IVAROffload\replaced - never overwritten, never deleted - and the card file is copied as usual.
    /// </summary>
    private void CheckExisting(int i, List<Dest> dests, List<Dest> copyTo)
    {
        JobItem planned = _plan[i];
        string src = Path.Join(_source, planned.Rel);
        var compare = new List<Dest>();
        foreach (Dest d in dests)
        {
            JobItem item = d.State!.Items[i];
            if (item.Why is BackupWhy.BackedUp or BackupWhy.Chain && item.ExpectedSha256 is not null) compare.Add(d);
            else OnDest(d, item, () => ReplaceWithCopy(d, item, item.Why == BackupWhy.Changed ? BackupReasons.AsideChanged : BackupReasons.AsideNotFromBackup, copyTo));
        }
        if (compare.Count == 0) return;

        SetPhase("Verifying");
        Task<(FileSnapshot?, string?)> card = Task.Run(() => ReadCardAgain(src));
        // A copy of another size is not the card's: it is not read (a chain file carries this folder's own history and is never compared).
        var copies = compare.Select(d => Task.Run(() =>
        {
            JobItem item = d.State!.Items[i];
            if (item.Why == BackupWhy.Chain) return ((FileSnapshot?)null, (string?)null);
            string path = Path.Join(d.Folder, item.Rel);
            FileSnapshot? s = SafeFile.TrySnapshot(path);
            return (s, s is { IsDirectory: false } && s.Size == item.Size ? SafeFile.ToHex(Retry(() => SafeFile.HashFile(path, s.Size, AddWork, _ct))) : null);
        })).ToList();
        try
        {
            Task.WaitAll([card, .. copies]);
        }
        catch (AggregateException)
        {
            // Looked at one by one below.
        }
        _ct.ThrowIfCancellationRequested();

        if (card.IsFaulted)
        {
            Exception e = card.Exception!.InnerException!;
            if (e is OperationCanceledException) throw e;
            CardFileProblem(i, compare, src, e);
            return;
        }
        (FileSnapshot? snapshot, string? cardSha) = card.Result;
        if (snapshot is null)
        {
            CardFileProblem(i, compare, src, new Win32IOException(ERROR_FILE_NOT_FOUND, "Reading again", src));
            return;
        }
        if (snapshot.Size != planned.Size || snapshot.LastWriteTime != planned.LastWriteTime)
        {
            foreach (Dest d in compare) OnDest(d, d.State!.Items[i], () => Skip(d, d.State!.Items[i], BackupReasons.ChangedOnCard));
            return;
        }
        string expected = compare[0].State!.Items[i].ExpectedSha256!;
        bool cardChanged = false;
        if (cardSha != expected)
        {
            // The card returns other data than the earlier backup verified, for a file with the same size and date. Read
            // twice alike, it was changed in place (a camera that rewrites a file without changing its date); read
            // differently, the card or reader is flaky, and nothing is touched.
            Fault("card-differs-from-backup", i);
            (FileSnapshot? again, string? secondSha) = ReadCardAgain(src);
            _ct.ThrowIfCancellationRequested();
            if (again is null || secondSha != cardSha)
            {
                foreach (Dest d in compare) OnDest(d, d.State!.Items[i], () => Fail(d, d.State!.Items[i], BackupReasons.CardDiffersFromBackup));
                if (++_sourceMismatches >= 2)
                    throw new JobHaltException(HaltReason.SourceInconsistent,
                        $"The card is returning inconsistent data: {_sourceMismatches} files read differently each time. "
                        + "Check the card, the card reader, the cable or the port, then press Resume. Nothing on the card was changed.");
                return;
            }
            cardChanged = true;
        }
        for (int k = 0; k < compare.Count; k++)
        {
            Dest d = compare[k];
            JobItem item = d.State!.Items[i];
            Task<(FileSnapshot?, string?)> read = copies[k];
            if (read.IsFaulted)
            {
                Exception e = read.Exception!.InnerException!;
                OnDest(d, item, () => ExceptionDispatchInfo.Throw(e));
                continue;
            }
            (FileSnapshot? copy, string? copySha) = read.Result;
            OnDest(d, item, () =>
            {
                if (cardChanged) ReplaceWithCopy(d, item, BackupReasons.AsideChanged, copyTo);
                else if (item.Why == BackupWhy.Chain || copySha == expected)
                {
                    Fault("before-recheck-done", i);
                    Done(d, item, "verify", expected, item.ExpectedXxh64);
                }
                // Another size or date than the verified copy had: changed by a program since, not damaged by the drive.
                else ReplaceWithCopy(d, item, EditedSince(copy, item.Size, planned.LastWriteTime) ? BackupReasons.AsideEdited : BackupReasons.AsideDamaged, copyTo);
            });
        }
    }

    /// <summary>
    /// A file in the backup folder that is not what the earlier backup verified was changed by a program since (another
    /// size, or a date other than the card's - allowing the whole quarter hours a FAT card's dates can be read off by),
    /// rather than damaged by the drive (same size and date, other content).
    /// </summary>
    private static bool EditedSince(FileSnapshot? copy, long size, long cardDate)
    {
        if (copy is null) return false;
        if (copy.Size != size) return true;
        long d = copy.LastWriteTime - cardDate;
        return d % QuarterHour != 0 || Math.Abs(d) > 14 * 4 * QuarterHour;
    }

    private const long QuarterHour = 15L * 60 * 10_000_000;

    /// <summary>
    /// Records that the file in the backup folder under the item's name is to be replaced by the card's, and why; the
    /// card file is then copied as usual. Only once that copy is verified is the old file moved aside and the copy put in
    /// its place (see <see cref="Place"/>): a copy that can't be made (the file left the card, the card was pulled, the
    /// backup was ended) leaves the old file where it is.
    /// </summary>
    private void ReplaceWithCopy(Dest d, JobItem item, string why, List<Dest> copyTo)
    {
        Record(d, new JournalRecord { Type = "replace", Index = item.Index, Why = why, At = JournalRecord.Now() });
        item.ReplaceWhy = why;
        copyTo.Add(d);
    }

    /// <summary>The folder (in the backup folder's log folder) that a top-up moves the files it does not keep in place into.</summary>
    public const string ReplacedFolderName = "replaced";

    /// <summary>
    /// Moves the file in the backup folder under the item's name into _IVAROffload\replaced\&lt;job&gt;\, keeping its
    /// sub-folders: never overwritten, never deleted. Only called with the card's verified copy waiting under its
    /// temporary name. The move is recorded before it happens; after a crash the old file is either still in its place
    /// (and moved then) or already moved, and the verified copy is put in its place either way.
    /// </summary>
    private void MoveAside(Dest d, JobItem item, string why)
    {
        string dst = Path.Join(d.Folder, item.Rel);
        // The earlier version of a photo or clip changed on the card (usually another shot whose number the camera used
        // again) stays in view, next to the new one; camera index files and anything else go into the log folder.
        bool beside = why == BackupReasons.AsideChanged && !EarlierBackups.IsCameraIndex(item.Rel);
        SetPhase("Moving the old version aside");
        KeptBeside? kept = null;
        for (int n = 1; n < 1000; n++)
        {
            string rel = beside
                ? Path.Join(Path.GetDirectoryName(item.Rel), $"{Path.GetFileNameWithoutExtension(item.Rel)} (earlier{(n == 1 ? "" : $" {n}")}){Path.GetExtension(item.Rel)}")
                : Path.Join(d.LogFolderName, ReplacedFolderName, _job.Id,
                    n == 1 ? item.Rel : Path.Join(Path.GetDirectoryName(item.Rel), $"{Path.GetFileNameWithoutExtension(item.Rel)} ({n}){Path.GetExtension(item.Rel)}"));
            string aside = Path.Join(d.Folder, rel);
            if (SafeFile.TrySnapshot(aside) is not null || beside && _cardNames.Contains(rel)) continue;
            EnsureFolder(d, Path.GetDirectoryName(aside)!);
            if (beside && kept is null && SafeFile.TrySnapshot(dst) is { IsDirectory: false } old)
            {
                // Read now, so the backup folder's checksum list (and later top-ups) can name it with what it holds.
                SetPhase("Verifying");
                kept = new KeptBeside(old.Size, old.CreationTime, old.LastWriteTime,
                    SafeFile.ToHex(Retry(() => SafeFile.HashFile(dst, old.Size, AddWork, _ct))), AscMhl.Xxh64OfFile(dst));
            }
            Record(d, new JournalRecord
            {
                Type = "aside", Index = item.Index, Rel = rel, Why = why, At = JournalRecord.Now(),
                Size = kept?.Size, CreationTime = kept?.CreationTime, LastWriteTime = kept?.LastWriteTime, Sha256 = kept?.Sha256, Xxh64 = kept?.Xxh64,
            });
            item.SetAside = rel;
            item.SetAsideWhy = why;
            item.Beside = kept;
            Fault("after-aside-journal", item.Index);
            int error = Retry(() =>
            {
                int e = SafeFile.TryRename(dst, aside);
                if (e is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or ERROR_ACCESS_DENIED)
                    throw new Win32IOException(e, "Moving the old version aside", dst);
                return e;
            }, retryAccessDenied: true);
            if (error is ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS) continue;
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND && (!CheckDestination(d) || SafeFile.TrySnapshot(dst) is null)) return;
            if (error != 0) throw new Win32IOException(error, "Moving the old version aside", dst);
            Fault("after-aside", item.Index);
            return;
        }
        throw new IOException($"No free name to move {item.Rel} aside.");
    }

    /// <summary>The earlier version of a file kept next to the new one, in the backup folder (not in its log folder).</summary>
    internal static bool IsBeside(JobItem item) => item.Beside is not null && item.SetAside is { } rel
        && !BackupPaths.LogFolderNames.Any(name => rel.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A top-up, for a file of the earlier backup that is no longer on the card: it stays in the backup, as it is, and
    /// its copy is read again. There is no other copy to put in its place, so it is never moved: one that no longer
    /// matches the earlier checksum is reported (edited by a program since, or - same size and date, read alike twice -
    /// damaged on the drive).
    /// </summary>
    private void CheckKept(Dest d, JobItem item)
    {
        string dst = Path.Join(d.Folder, item.Rel);
        FileSnapshot? s = SafeFile.TrySnapshot(dst);
        if (s is not { IsDirectory: false } || item.ExpectedSha256 is null) // not in this folder, or never in its backup
        {
            if (!CheckDestination(d)) return;
            Skip(d, item, BackupReasons.KeptGone);
            return;
        }
        SetPhase("Verifying");
        string? sha = s.Size == item.Size ? SafeFile.ToHex(Retry(() => SafeFile.HashFile(dst, s.Size, AddWork, _ct))) : null;
        if (sha is not null && sha == item.ExpectedSha256)
        {
            Done(d, item, "kept", sha, item.ExpectedXxh64);
            return;
        }
        if (EditedSince(s, item.Size, item.LastWriteTime))
        {
            // Edited by a program since (e.g. a photo culled or tagged in place): the card can't replace it, so it stays as it is.
            Skip(d, item, BackupReasons.KeptEdited);
            return;
        }
        // Read once more: a flaky drive or cable is far more likely than damage, and then Resume reads it again.
        string again = SafeFile.ToHex(Retry(() => SafeFile.HashFile(dst, s.Size, null, _ct)));
        if (again != sha) Fail(d, item, BackupReasons.KeptReadDiffers);
        else Skip(d, item, BackupReasons.KeptDiffers);
    }

    /// <summary>Reads the card file once into a new copy on each destination, checks every copy, and puts the good ones in place.</summary>
    private void CopyTo(int i, List<Dest> dests, int attempt)
    {
        JobItem planned = _plan[i];
        string src = Path.Join(_source, planned.Rel);
        var ready = new List<Dest>();
        foreach (Dest d in dests)
        {
            JobItem item = d.State!.Items[i];
            OnDest(d, item, () =>
            {
                EnsureFolder(d, Path.GetDirectoryName(Path.Join(d.Folder, item.Rel))!);
                if (VolumeOf(d) is { } v && item.Size > v.MaxFileSize)
                {
                    Fail(d, item, BackupReasons.TooLargeForDestination);
                    return;
                }
                EnsureSpaceFor(d, item.Size);
                ready.Add(d);
            });
        }
        if (ready.Count == 0) return;

        bool unbuffered = true;
        while (true)
        {
            SetPhase("Copying");
            SafeFileHandle handle;
            try
            {
                handle = Retry(() => SafeFile.OpenRead(src, unbuffered));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CardFileProblem(i, ready, src, e);
                return;
            }
            var copies = new List<(Dest Dest, string Temp, CopyDestination Target)>();
            FileSnapshot source;
            List<NamedStream> streams;
            MultiCopyHashes? hashes;
            using (handle)
            {
                try
                {
                    source = SafeFile.Snapshot(handle, src);
                    streams = SafeFile.NamedStreams(src).Where(s => !s.IsZoneIdentifier).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    CardFileProblem(i, ready, src, e);
                    return;
                }
                if (source.Size != planned.Size || source.LastWriteTime != planned.LastWriteTime)
                {
                    foreach (Dest d in ready) OnDest(d, d.State!.Items[i], () => Skip(d, d.State!.Items[i], BackupReasons.ChangedOnCard));
                    return;
                }
                foreach (Dest d in ready)
                {
                    JobItem item = d.State!.Items[i];
                    OnDest(d, item, () =>
                    {
                        if (streams.Count > 0 && !StoresStreams(d))
                        {
                            Fail(d, item, BackupReasons.StreamsNotSupported);
                            return;
                        }
                        string tempName = $"~{_tempPrefix}-{i:D6}{JobPaths.TempExtension}";
                        string temp = Path.Join(Path.GetDirectoryName(Path.Join(d.Folder, item.Rel))!, tempName);
                        if (SafeFile.TrySnapshot(temp) is { IsDirectory: false }) TryDeleteTemp(temp); // left by an earlier attempt
                        Record(d, new JournalRecord { Type = "copy", Index = i, Temp = tempName, CreationTime = source.CreationTime, LastWriteTime = source.LastWriteTime });
                        item.Stage = ItemStage.Copying;
                        item.Temp = tempName;
                        item.Failed = false;
                        copies.Add((d, temp, new CopyDestination(temp)));
                    });
                }
                if (copies.Count == 0) return;
                Fault("after-copy-journal", i);

                _itemWork = 0;
                try
                {
                    hashes = SafeFile.CopyToNewFiles(handle, copies.Select(c => c.Target).ToList(), source.Size, n =>
                    {
                        AddWork(n);
                        Fault("mid-copy", i);
                    }, _ct);
                }
                catch (Win32IOException e) when (unbuffered && e.NativeError == ERROR_INVALID_PARAMETER)
                {
                    foreach (var c in copies) TryDeleteTemp(c.Temp);
                    unbuffered = false; // the card's file system does not support unbuffered reads
                    continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    foreach (var c in copies) TryDeleteTemp(c.Temp);
                    CardFileProblem(i, copies.Select(c => c.Dest).ToList(), src, e);
                    return;
                }
                catch (OperationCanceledException)
                {
                    foreach (var c in copies) TryDeleteTemp(c.Temp);
                    throw;
                }
            }

            // Destinations whose copy could not be written drop out of this file; the others go on.
            foreach (var c in copies.Where(c => c.Target.Error is not null))
            {
                Exception error = c.Target.Error!;
                OnDest(c.Dest, c.Dest.State!.Items[i], () =>
                {
                    TryDeleteTemp(c.Temp);
                    ExceptionDispatchInfo.Throw(error);
                });
            }
            var written = copies.Where(c => c.Target.Error is null && c.Dest.Online).ToList();
            if (hashes is null || written.Count == 0) return;
            foreach (var c in written.ToList())
            {
                OnDest(c.Dest, c.Dest.State!.Items[i], () =>
                {
                    foreach (NamedStream s in streams) SafeFile.CopyStream(src, c.Temp, s, _ct);
                    SafeFile.ApplyTimesAndAttributes(c.Temp, source); // after the streams: writing them updates the modification time
                });
                if (!c.Dest.Online || c.Dest.State!.Items[i].Failed)
                {
                    TryDeleteTemp(c.Temp);
                    written.Remove(c);
                }
            }
            Fault("after-copy", i);
            Verify(i, src, source, hashes, streams, written, attempt);
            return;
        }
    }

    /// <summary>
    /// Reads every new copy back from its own disk and, at the same time, the card file a second time. A copy only
    /// counts when it matches the checksum taken while copying, and so does the second read of the card.
    /// </summary>
    private void Verify(int i, string src, FileSnapshot source, MultiCopyHashes hashes, List<NamedStream> streams,
        List<(Dest Dest, string Temp, CopyDestination Target)> written, int attempt)
    {
        if (written.Count == 0) return;
        SetPhase("Verifying");
        Task<(FileSnapshot?, string?)>? again = _job.Reread ? Task.Run(() => ReadCardAgain(src)) : null;
        var readBacks = written.Select(c => Task.Run(() =>
        {
            string sha = SafeFile.ToHex(Retry(() => SafeFile.HashFile(c.Temp, source.Size, AddWork, _ct)));
            bool streamsMatch = streams.All(s => SafeFile.ToHex(SafeFile.HashStream(src, s, _ct)) == SafeFile.ToHex(SafeFile.HashStream(c.Temp, s, _ct)));
            return streamsMatch ? sha : "streams differ";
        })).ToList();
        try
        {
            Task.WaitAll([.. readBacks, .. again is null ? [] : new Task[] { again }]);
        }
        catch (AggregateException)
        {
            // Looked at one by one below.
        }
        _ct.ThrowIfCancellationRequested();

        if (again is not null)
        {
            string? problem = null;
            if (again.IsFaulted)
            {
                foreach (var c in written) TryDeleteTemp(c.Temp);
                Exception e = again.Exception!.InnerException!;
                if (e is OperationCanceledException) throw e;
                CardFileProblem(i, written.Select(c => c.Dest).ToList(), src, e);
                return;
            }
            (FileSnapshot? snapshot, string? sha) = again.Result;
            if (snapshot is null)
            {
                foreach (var c in written) TryDeleteTemp(c.Temp);
                CardFileProblem(i, written.Select(c => c.Dest).ToList(), src, new Win32IOException(ERROR_FILE_NOT_FOUND, "Reading again", src));
                return;
            }
            if (!snapshot.SameFileAs(source, compareFileId: false)) problem = BackupReasons.ChangedWhileCopied;
            else if (sha != hashes.Sha256) problem = BackupReasons.CardReadTwiceDiffers;
            if (problem is not null)
            {
                foreach (var c in written)
                {
                    TryDeleteTemp(c.Temp);
                    OnDest(c.Dest, c.Dest.State!.Items[i], () => Fail(c.Dest, c.Dest.State!.Items[i], problem));
                }
                if (problem == BackupReasons.CardReadTwiceDiffers && !_closing && ++_sourceMismatches >= 2)
                    throw new JobHaltException(HaltReason.SourceInconsistent,
                        $"The card is returning inconsistent data: {_sourceMismatches} files read differently the second time. "
                        + "Check the card, the card reader, the cable or the port, then press Resume. Nothing on the card was changed.");
                return;
            }
        }

        // Every copy of a file must hold the same content: a copy made later (on resume, or on a second attempt) must match
        // the one already verified on another destination, also when the card now returns the same new data twice.
        string? earlier = _dests.Where(o => o.State is not null).Select(o => o.State!.Items[i])
            .FirstOrDefault(it => it.Stage is ItemStage.Copied or ItemStage.Done && it.Sha256 is not null)?.Sha256;
        if (earlier is not null && earlier != hashes.Sha256)
        {
            foreach (var c in written)
            {
                TryDeleteTemp(c.Temp);
                OnDest(c.Dest, c.Dest.State!.Items[i], () => Fail(c.Dest, c.Dest.State!.Items[i], BackupReasons.DiffersFromOtherCopies));
            }
            return;
        }

        var again2 = new List<Dest>();
        for (int k = 0; k < written.Count; k++)
        {
            (Dest d, string temp, _) = written[k];
            JobItem item = d.State!.Items[i];
            Task<string> read = readBacks[k];
            if (read.IsFaulted)
            {
                Exception e = read.Exception!.InnerException!;
                OnDest(d, item, () =>
                {
                    TryDeleteTemp(temp);
                    ExceptionDispatchInfo.Throw(e);
                });
                continue;
            }
            if (read.Result != hashes.Sha256)
            {
                TryDeleteTemp(temp);
                again2.Add(d);
                continue;
            }
            OnDest(d, item, () =>
            {
                Record(d, new JournalRecord { Type = "copied", Index = i, Sha256 = hashes.Sha256, Xxh64 = hashes.Xxh64, Streams = streams.Count > 0 ? streams.Count : null });
                item.Stage = ItemStage.Copied;
                item.Sha256 = hashes.Sha256;
                item.Xxh64 = hashes.Xxh64;
                Fault("after-copied", i);
                Place(d, item, temp, Path.Join(d.Folder, item.Rel), source);
            });
        }
        if (again2.Count == 0) return;
        if (attempt == 1)
        {
            CopyTo(i, again2, attempt: 2); // a copy that did not match is made once more before the destination is blamed
            return;
        }
        foreach (Dest d in again2)
            OnDest(d, d.State!.Items[i], () =>
            {
                Fail(d, d.State!.Items[i], BackupReasons.CopyMismatchTwice);
                throw new DestinationHaltException(HaltReason.TargetDamagingFiles,
                    $"The destination {d.DriveName} is damaging files: a fresh copy of {Path.GetFileName(_plan[i].Rel)} did not match the card twice. "
                    + "Check that drive, its cable or port, then press Resume. The other destinations went on.");
            });
    }

    /// <summary>Gives a verified copy its name. Never replaces a file that is already there.</summary>
    private void Place(Dest d, JobItem item, string temp, string dst, FileSnapshot? source)
    {
        if (_pausedMidFile && !CheckDestination(d)) return;
        // A top-up replacing a file in the backup folder: the verified copy is ready, so the old file is moved aside now.
        if (item.ReplaceWhy is { } why && SafeFile.TrySnapshot(dst) is { IsDirectory: false })
        {
            MoveAside(d, item, why);
            if (!d.Online) return;
        }
        SetPhase("Moving into place");
        int error = Retry(() =>
        {
            int e = SafeFile.TryRename(temp, dst);
            if (e is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or ERROR_ACCESS_DENIED)
                throw new Win32IOException(e, "Moving into place", temp);
            return e;
        }, retryAccessDenied: true);
        if (error is ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS)
        {
            TryDeleteTemp(temp);
            Fail(d, item, BackupReasons.NameTakenInDestination);
            return;
        }
        if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND && !CheckDestination(d)) return;
        if (error != 0) throw new Win32IOException(error, "Moving the verified copy into place", dst);
        Fault("after-place-rename", item.Index);
        if (source is not null) SafeFile.ApplyTimesAndAttributes(dst, source); // guard against NTFS name tunnelling
        Done(d, item);
        Fault("after-done", item.Index);
    }

    /// <summary>
    /// Reading the card file failed (or it is gone). A card that is gone or stopped answering stops the whole backup;
    /// a file that disappeared is recorded as not copied; any other error fails the file (Resume retries it).
    /// </summary>
    private void CardFileProblem(int i, List<Dest> dests, string src, Exception e)
    {
        if (e is Win32IOException { IsNotFound: true } || IOErrors.NativeError(e) is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
        {
            string folder = Path.GetDirectoryName(src)!;
            if (!SafeFile.DirectoryExists(folder))
            {
                CheckSource(); // the card or its top folder is gone: halt
                foreach (Dest d in dests) OnDest(d, d.State!.Items[i], () => Fail(d, d.State!.Items[i], FailReasons.FolderMissing(folder)));
                return;
            }
            foreach (Dest d in dests) OnDest(d, d.State!.Items[i], () => Skip(d, d.State!.Items[i], BackupReasons.GoneFromCard));
            return;
        }
        if (IOErrors.IsDeviceGone(e))
        {
            CheckSource();
            string detail = IOErrors.NativeError(e) is int code ? new System.ComponentModel.Win32Exception(code).Message.TrimEnd('.') : e.Message;
            bool again = dests.Any(d => BackupReasons.IsCardError(d.State!.Items[i].Note));
            foreach (Dest d in dests) OnDest(d, d.State!.Items[i], () => Fail(d, d.State!.Items[i], BackupReasons.CardError(detail)));
            if (again || _closing) return; // the same file again after a resume: a bad spot on the card, not a dropped reader
            throw new JobHaltException(HaltReason.DriveStoppedResponding,
                $"The card stopped responding ({detail}). Check that it is connected (card reader, cable or port), then press Resume. Nothing on the card was changed.");
        }
        foreach (Dest d in dests) OnDest(d, d.State!.Items[i], () => Fail(d, d.State!.Items[i], $"The file could not be read from the card: {e.Message}"));
    }

    private (FileSnapshot?, string?) ReadCardAgain(string src)
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

    // ---- Destinations ---------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one step for one destination. A problem that concerns only that destination is recorded there (or, when
    /// its drive is gone, it just drops out); the other destinations are never affected. A problem with the card, a
    /// stop or a crash goes up.
    /// </summary>
    private void OnDest(Dest d, JobItem? item, Action action)
    {
        if (!d.Online) return;
        try
        {
            action();
        }
        catch (DestinationHaltException h)
        {
            GoOffline(d, h.Reason, h.Message);
        }
        catch (JournalException e)
        {
            // A drive that is gone drops out with that reason; one that is there but refuses its log says so.
            if (CheckDestination(d))
                GoOffline(d, HaltReason.TargetNotConnected, $"The log on {d.DriveName} can't be written ({e.Message}). Check that drive, then press Resume.");
        }
        catch (Exception e) when (IOErrors.IsDeviceGone(e))
        {
            if (!CheckDestination(d)) return;
            string detail = IOErrors.NativeError(e) is int code ? new System.ComponentModel.Win32Exception(code).Message.TrimEnd('.') : e.Message;
            if (item is not null) TryFail(d, item, BackupReasons.DestinationError(detail));
            GoOffline(d, HaltReason.DriveStoppedResponding,
                $"The destination {d.DriveName} stopped responding ({detail}). Check that it is connected, then press Resume. The other destinations went on.");
        }
        catch (Exception e) when (IOErrors.IsDiskFull(e))
        {
            // FAT32 reports a file of 4 GB or more as "disk full"; only a drive that is really full stops this destination.
            if (item is not null && item.Size > uint.MaxValue && FreeBytes(d) is long free && free >= item.Size + JobRunner.RunSpaceMargin)
            {
                TryFail(d, item, BackupReasons.TooLargeForDestination);
                return;
            }
            if (item is not null) TryFail(d, item, e.Message);
            GoOffline(d, HaltReason.TargetFull, $"The destination {d.DriveName} is full. Free up space there, then press Resume. The other destinations went on.");
        }
        catch (Exception e) when (IOErrors.IsFileTooLarge(e))
        {
            if (item is not null) TryFail(d, item, BackupReasons.TooLargeForDestination);
        }
        catch (Exception e) when (IOErrors.NativeError(e) is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
        {
            // A destination folder or drive that went away (renamed, unplugged, drive letter removed) drops out.
            if (CheckDestination(d) && item is not null) TryFail(d, item, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (item is not null) TryFail(d, item, e.Message);
            // A step for the whole destination (finishing it) that fails is never silent: it drops out, saying why.
            else GoOffline(d, HaltReason.TargetNotConnected, $"Finishing the backup on {d.DriveName} failed: {e.Message.TrimEnd('.')}. Fix that, then press Resume.");
        }
    }

    private void TryFail(Dest d, JobItem item, string error)
    {
        try
        {
            Fail(d, item, error);
        }
        catch (Exception e) when (e is JournalException or IOException)
        {
            GoOffline(d, HaltReason.TargetNotConnected, $"The log on {d.DriveName} can't be written ({e.Message}). Check that drive, then press Resume.");
        }
    }

    private static void GoOffline(Dest d, HaltReason reason, string message)
    {
        if (d.Offline is not null) return;
        d.Offline = message;
        d.Halt = reason;
    }

    /// <summary>The destination folder and its log are there, on the drive the backup was started with. Otherwise it drops out.</summary>
    private bool CheckDestination(Dest d)
    {
        if (!d.Online) return false;
        try
        {
            JobRunner.CheckRoot(d.Folder, d.Serial, d.Label, "destination", HaltReason.TargetNotConnected);
            if (!File.Exists(d.JournalPath))
                throw new JobHaltException(HaltReason.LogMoved, $"The log of this backup is no longer in {d.Folder}. Put it back, then press Resume.");
            return true;
        }
        catch (JobHaltException h)
        {
            GoOffline(d, h.Reason, h.Message.Replace("Nothing was lost.", "The other destinations went on.", StringComparison.Ordinal));
            return false;
        }
    }

    /// <summary>
    /// A card that is not at its recorded letter but at another one (same drive serial number, same folder below the
    /// root) is read from there: reading never changes anything, and the serial number proves it is the same card.
    /// </summary>
    private void FollowCard()
    {
        if (_job.SourceSerial == 0 || Drives.IsOn(_source, _job.SourceSerial) && SafeFile.DirectoryExists(_source)) return;
        if (Drives.Elsewhere(Drives.OnDrive(_job.Source, _job.SourceReal), _job.SourceSerial).FirstOrDefault(SafeFile.DirectoryExists) is { } moved) _source = moved;
    }

    /// <summary>The card (or folder) is there on the drive the backup was started with; otherwise the whole backup halts.</summary>
    private void CheckSource()
    {
        try
        {
            JobRunner.CheckRoot(_source, _job.SourceSerial, _job.SourceLabel, "card", HaltReason.SourceNotConnected,
                JobPaths.SamePath(_source, _job.Source) ? _job.SourceReal : null);
        }
        catch (JobHaltException h)
        {
            throw new JobHaltException(h.Reason, h.Message.Replace("Nothing was lost.", "Nothing on the card was changed.", StringComparison.Ordinal));
        }
    }

    private static (HaltReason, string) WhyUnreachable(Dest d)
    {
        try
        {
            JobRunner.CheckRoot(d.Folder, d.Serial, d.Label, "destination", HaltReason.TargetNotConnected);
            return (HaltReason.LogMoved, $"The log of this backup is missing from {d.Folder}.");
        }
        catch (JobHaltException h)
        {
            return (h.Reason, h.Message.Replace(" Nothing was lost.", "", StringComparison.Ordinal));
        }
    }

    /// <summary>Creates missing folders below the destination folder (never the destination folder itself or anything above).</summary>
    private void EnsureFolder(Dest d, string folder)
    {
        if (d.Folders.Contains(folder)) return;
        var missing = new Stack<string>();
        for (string f = folder; !JobPaths.SamePath(f, d.Folder) && !Directory.Exists(f);
             f = Path.GetDirectoryName(f) ?? throw new IOException($"The folder {folder} is not inside the destination {d.Folder}."))
            missing.Push(f);
        if (missing.Count > 0 && !SafeFile.DirectoryExists(d.Folder))
        {
            CheckDestination(d);
            throw new DestinationHaltException(d.Halt, d.Offline ?? $"The destination folder {d.Folder} is missing.");
        }
        while (missing.TryPop(out string? f)) Directory.CreateDirectory(f);
        d.Folders.Add(folder);
    }

    private VolumeInfo? VolumeOf(Dest d)
    {
        if (d.VolumeRead) return d.Volume;
        d.VolumeRead = true;
        try
        {
            d.Volume = VolumeInfo.Of(d.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return d.Volume;
    }

    private bool StoresStreams(Dest d) => _options.DestinationsStoreStreams ?? VolumeOf(d) is not { } v || (v.FileSystemFlags & FILE_NAMED_STREAMS) != 0;

    private long? FreeBytes(Dest d)
    {
        if (_options.FreeBytes is { } probe) return probe(d.Folder);
        string root = Path.GetPathRoot(Path.GetFullPath(d.Folder)) ?? d.Folder;
        return GetDiskFreeSpaceEx(root, out ulong free, out _, out _) ? (long)free : null;
    }

    private void EnsureSpaceFor(Dest d, long bytes)
    {
        if (FreeBytes(d) is not long free || free >= bytes + JobRunner.RunSpaceMargin) return;
        throw new DestinationHaltException(HaltReason.TargetFull,
            $"The destination {d.DriveName} is full: {Format.Bytes(bytes)} needed for the next file, {Format.Bytes(Math.Max(0, free - JobRunner.RunSpaceMargin))} free. "
            + "Free up space there, then press Resume. The other destinations went on.");
    }

    // ---- Finishing ------------------------------------------------------------------------------------------

    /// <summary>
    /// Ends one destination: every folder of the card is created (empty ones too), the ASC MHL generation is written
    /// when every file has a verified copy there, folders get the card's dates, and the log is ended.
    /// </summary>
    private void FinishDest(Dest d, string how)
    {
        JobState state = d.State!;
        OnDest(d, null, () =>
        {
            SetPhase("Writing the checksum files");
            if (how == "completed")
            {
                foreach (string rel in state.SourceFolders.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    EnsureFolder(d, Path.Join(d.Folder, rel));
                if (state.Mhl is null && state.MhlSkipped is null && AllDone(state))
                {
                    Fault("before-mhl", -1);
                    // Every file in the backup folder: the card's, and in a top-up the earlier backup's files no longer on the card.
                    var files = state.Items.Where(i => i.Stage == ItemStage.Done)
                        .Select(i => new MhlFile(i.Rel, i.Size, i.SeenLastWriteTime ?? i.LastWriteTime, i.Xxh64 ?? ""))
                        // and the earlier versions kept next to new ones (read when they were renamed)
                        .Concat(state.Items.Where(i => i.Stage == ItemStage.Done && IsBeside(i))
                            .Select(i => new MhlFile(i.SetAside!, i.Beside!.Size, i.Beside.LastWriteTime, i.Beside.Xxh64))).ToList();
                    if (files.Any(f => f.Xxh64.Length == 0))
                        throw new IOException("Some copies have no xxHash64 checksum recorded, so no ASC MHL manifest was written.");
                    try
                    {
                        List<string> written;
                        try
                        {
                            written = AscMhl.WriteGenerations(d.Folder, files, state.SourceFolders, JobFactory.ToolName, DateTimeOffset.Now, OrphansFolder(d));
                        }
                        catch (MhlHistoryException e) when (state.IsTopUp && e.CardChanged && e.History.Length == 0 && !CardHasTopHistory(state))
                        {
                            // The folder's own history (this app wrote it; the card has none) lists files that changed or are
                            // gone since, which an ASC MHL history can't record: it is moved aside, and a new one is started.
                            RestartMhl(d, state);
                            written = AscMhl.WriteGenerations(d.Folder, files, state.SourceFolders, JobFactory.ToolName, DateTimeOffset.Now, OrphansFolder(d));
                        }
                        Fault("mhl-written", -1);
                        Record(d, new JournalRecord { Type = "mhl", Rel = written[^1], Count = written.Count, At = JournalRecord.Now() });
                        state.Mhl = written[^1];
                    }
                    catch (MhlHistoryException e)
                    {
                        Record(d, new JournalRecord { Type = "mhl", What = "skipped", How = e.CardChanged ? "mismatch" : null, Error = e.Message, At = JournalRecord.Now() });
                        state.MhlSkipped = e.Message;
                        state.MhlMismatch = e.CardChanged;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                              && !IOErrors.IsDeviceGone(e) && !IOErrors.IsDiskFull(e))
                    {
                        // The copies are verified; only the checksum files could not be written. Said once, not retried forever.
                        string why = $"the ASC MHL files could not be written ({e.Message.TrimEnd('.')})";
                        Record(d, new JournalRecord { Type = "mhl", What = "skipped", Error = why, At = JournalRecord.Now() });
                        state.MhlSkipped = why;
                    }
                    Fault("after-mhl", -1);
                }
            }
            // Deepest first, so dating a folder never changes a parent that was already dated.
            foreach ((string rel, FolderTimes times) in state.SourceFolders.OrderByDescending(f => f.Key.Length))
            {
                string path = Path.Join(d.Folder, rel);
                try
                {
                    if (SafeFile.TrySnapshot(path) is { IsDirectory: true }) SafeFile.SetDirectoryTimes(path, times.CreationTime, times.LastWriteTime);
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
                Done = state.DoneCount,
                Skipped = state.SkippedCount,
                Failed = state.FailedCount,
                Bytes = state.BytesDone,
            };
            Record(d, end);
            state.End = end;
            BackupReports.TryWrite(state, DestinationNames());
        });
    }

    /// <summary>The card has an ASC MHL history in its top folder (its files include ascmhl\ascmhl_chain.xml).</summary>
    private static bool CardHasTopHistory(JobState state) =>
        state.Items.Any(i => !IsKept(i) && string.Equals(i.Rel, Path.Join(AscMhl.FolderName, AscMhl.ChainFileName), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moves the backup folder's own ASC MHL history (its top-level ascmhl folder) into _IVAROffload\replaced\&lt;job&gt;,
    /// recorded first, so a new history can be started that describes the folder as it is now.
    /// </summary>
    private void RestartMhl(Dest d, JobState state)
    {
        string from = Path.Join(d.Folder, AscMhl.FolderName);
        if (!Directory.Exists(from)) return; // moved already (the backup stopped right after)
        string rel = Path.Join(d.LogFolderName, ReplacedFolderName, _job.Id, AscMhl.FolderName);
        for (int n = 2; Directory.Exists(Path.Join(d.Folder, rel)) || File.Exists(Path.Join(d.Folder, rel)); n++)
            rel = Path.Join(d.LogFolderName, ReplacedFolderName, _job.Id, $"{AscMhl.FolderName} ({n})");
        EnsureFolder(d, Path.GetDirectoryName(Path.Join(d.Folder, rel))!);
        Record(d, new JournalRecord { Type = "mhl", What = "restarted", Rel = rel, At = JournalRecord.Now() });
        state.MhlRestarted = rel;
        Fault("before-mhl-restart", -1);
        try
        {
            Directory.Move(from, Path.Join(d.Folder, rel));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && !IOErrors.IsDeviceGone(e))
        {
            // Never a green result over a history that no longer describes the folder: this destination waits for Resume.
            throw new DestinationHaltException(HaltReason.None,
                $"The earlier ASC MHL history in {d.Folder} could not be moved aside to start a new one ({e.Message.TrimEnd('.')}). "
                + "Close any program that has a file in its ascmhl folder open, then press Resume. The copies are verified.");
        }
        Fault("after-mhl-restart", -1);
    }

    /// <summary>
    /// A top-up: where a generation manifest that no chain lists any more is moved (the earlier backup's, when the card's
    /// chain was copied back); null for a new backup, whose only such manifests are its own, from a crash.
    /// </summary>
    private string? OrphansFolder(Dest d) => TopUp ? Path.Join(d.Folder, d.LogFolderName, ReplacedFolderName, _job.Id, "ascmhl-unlisted") : null;

    /// <summary>
    /// A destination that finished in an earlier run is not trusted blindly: every file verified there must still be
    /// there with its size (it may have been sorted, edited or lost since). Otherwise it does not count as complete.
    /// </summary>
    private void RecheckEarlier(Dest d)
    {
        var gone = new List<string>();
        foreach (JobItem item in d.State!.Items.Where(i => i.Stage == ItemStage.Done))
        {
            FileSnapshot? s;
            try
            {
                s = SafeFile.TrySnapshot(Path.Join(d.Folder, item.Rel));
            }
            catch (IOException)
            {
                s = null;
            }
            bool updatedChain = (d.State.Mhl is not null || item.Why == BackupWhy.Chain) && Path.GetFileName(item.Rel) == AscMhl.ChainFileName;
            if (s is not { IsDirectory: false } || s.Size != item.Size && !updatedChain) gone.Add(item.Rel);
        }
        if (gone.Count > 0)
            (d.Halt, d.Offline) = (HaltReason.None, $"{gone.Count:N0} file{(gone.Count == 1 ? " that was" : "s that were")} verified here in an earlier run "
                + $"{(gone.Count == 1 ? "is" : "are")} missing or changed now (e.g. {gone[0]}) - moved by a sort, edited or lost. This copy is not complete any more.");
    }

    /// <summary>
    /// Scans the card again after a finished backup: files that are on it now but not in the backup as they are
    /// (added or changed after the preview) keep the result from being green.
    /// </summary>
    private (IReadOnlyList<string> NotInBackup, bool NotRescanned) RescanCard()
    {
        try
        {
            if (!Drives.IsOn(_source, _job.SourceSerial) || !SafeFile.DirectoryExists(_source)) return ([], true);
            BackupScan now = BackupScanner.Scan(_source, ct: _ct);
            var planned = _plan.Where(i => !IsKept(i)).ToDictionary(i => i.Rel, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (SourceFile f in now.Files)
            {
                if (!planned.TryGetValue(f.RelativePath, out JobItem? item)) result.Add($"{f.RelativePath} (added after the preview)");
                else if (item.Size != f.Size || item.LastWriteTime != f.LastWriteTime) result.Add($"{f.RelativePath} (changed after the preview)");
            }
            return (result, now.Problems.Count > 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return ([], true);
        }
    }

    private IReadOnlyList<string> DestinationNames() => _dests.Select(d => d.Folder).ToList();

    private void Remember()
    {
        RecentBackups.Remember(_job.Id, _dests.Select(d => d.JournalPath), _job.Source, _job.SourceLabel, _job.Created,
            ended: _dests.All(d => d.State?.IsEnded == true));
    }

    private void RefreshReports(bool force = false)
    {
        long now = _clock.ElapsedMilliseconds;
        if (!force && now < _reportsDueMs) return;
        foreach (Dest d in _dests.Where(d => d.Online)) BackupReports.TryWrite(d.State!, DestinationNames());
        long took = _clock.ElapsedMilliseconds - now;
        _reportsDueMs = _clock.ElapsedMilliseconds + Math.Max(ReportsIntervalMs, 30 * took);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private string Hash(string path, long size) => SafeFile.ToHex(Retry(() => SafeFile.HashFile(path, size, null, _ct)));

    private static void TryDeleteTemp(string temp)
    {
        try
        {
            SafeFile.DeleteOwnTempFile(temp);
        }
        catch (IOException)
        {
            // A leftover temporary file is removed when the backup resumes.
        }
    }

    /// <summary>Retries while another program (antivirus, indexer, sync tool) briefly holds the file.</summary>
    private T Retry<T>(Func<T> action, bool retryAccessDenied = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Win32IOException e) when ((e.IsSharingViolation || (retryAccessDenied && e.NativeError == ERROR_ACCESS_DENIED)) && attempt < _retryDelays.Length)
            {
                if (_ct.WaitHandle.WaitOne(_retryDelays[attempt])) _ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static void Record(Dest d, JournalRecord record) => d.Writer!.Write(record);

    private void Done(Dest d, JobItem item) => Done(d, item, "copy", item.Sha256, item.Xxh64);

    /// <param name="how">"copy" (copied and verified), "verify" (a top-up found it in the backup folder and read it again) or "kept".</param>
    private static void Done(Dest d, JobItem item, string how, string? sha256, string? xxh64)
    {
        Record(d, new JournalRecord { Type = "done", Index = item.Index, How = how, Sha256 = sha256, Xxh64 = xxh64, At = JournalRecord.Now() });
        item.Stage = ItemStage.Done;
        item.How = how;
        item.Sha256 = sha256;
        item.Xxh64 = xxh64;
        item.Failed = false;
        item.Note = null;
    }

    private static void Skip(Dest d, JobItem item, string why)
    {
        Record(d, new JournalRecord { Type = "skip", Index = item.Index, Why = why, At = JournalRecord.Now() });
        item.Stage = ItemStage.Skipped;
        item.Note = why;
        item.Failed = false;
    }

    private static void Fail(Dest d, JobItem item, string error)
    {
        Record(d, new JournalRecord { Type = "fail", Index = item.Index, Error = error, At = JournalRecord.Now() });
        item.Failed = true;
        item.Note = error;
    }

    private void Fault(string point, int index) => _options.Faults?.Hit(point, index);

    /// <summary>
    /// Progress units for one file: one read of the card, one read-back per destination, and the second read of the card
    /// (a file of a top-up that is no longer on the card: one read per destination).
    /// </summary>
    private long WorkFor(int i, int destinations) => IsKept(_plan[i]) ? _plan[i].Size * destinations : _plan[i].Size * (1 + destinations + (_job.Reread ? 1 : 0));

    private void ComputeWork()
    {
        _workTotal = 0;
        _itemsFinished = 0;
        for (int i = 0; i < _plan.Count; i++)
        {
            int n = _dests.Count(d => d.Online && !d.State!.Items[i].IsFinished);
            if (n > 0) _workTotal += WorkFor(i, n);
            else _itemsFinished++;
        }
    }

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
            var destinations = _dests.Select(d => new BackupDestinationProgress(d.Folder, d.DriveName,
                d.State?.DoneCount ?? 0, d.State?.FailedCount ?? 0, d.State?.BytesDone ?? 0, d.Offline)).ToList();
            _options.Progress.Report(new BackupProgress(_plan.Count, _itemsFinished, _workTotal, done, _currentFile, _phase, rate, remaining, destinations));
        }
    }

    public void Dispose()
    {
        foreach (Dest d in _dests)
        {
            try
            {
                d.Writer?.Dispose();
            }
            catch (IOException)
            {
                // The drive is gone; everything was flushed when it was written.
            }
            d.Writer = null;
        }
    }
}
