using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using IvarOffload.Core;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>One sort in the "Undo a previous sort" list.</summary>
/// <param name="why">Why the job's log can't be read (when it can't): its drive is away, or its folder was renamed or moved.</param>
/// <param name="startedFrom">What the user opened to find it: a receipt or a folder (null for a remembered job).</param>
public sealed class UndoRow(JobListing job, MissingLog why = MissingLog.DriveAway, string? startedFrom = null)
{
    public JobListing Job { get; } = job;
    public MissingLog Why { get; } = why;
    /// <summary>What the user opened to find this sort (a receipt or a folder): the undo checks whether the files go back there.</summary>
    public string? StartedFrom { get; } = startedFrom;
    public string Date => JobTexts.Date(Job.Created);
    public string What => Planner.Word(Job.Mode);
    public string From => Job.Source;
    public string To => Job.Target;
    public string Files => Job.IsConnected ? $"{Job.Moved:N0} ({Format.Bytes(Job.MovedBytes)})" : "?";
    public string Status => JobTexts.ListingStatus(Job, Why);
}

/// <summary>
/// Lists the sorts that can be undone: this PC's recent jobs, the jobs of a folder the user picks (its log folder and
/// receipts), or the job of a log or receipt file the user opens.
/// </summary>
public sealed class UndoListViewModel : ObservableObject
{
    private const string LogFilter = "Job logs and receipts|*.journal.jsonl;*.moved-out.csv;*.moved-out.txt;*.manifest.csv;*.summary.txt|All files|*.*";

    private readonly IDialogs _dialogs;
    private UndoRow? _selected;
    private string _status = "Search for earlier sorts in progress...";
    private bool _busy;
    private string? _lastFolder;

    public UndoListViewModel(IDialogs dialogs, bool load = true)
    {
        _dialogs = dialogs;
        ChooseFolderCommand = new AsyncCommand(ChooseFolderAsync, () => !IsBusy, ShowError);
        OpenLogCommand = new AsyncCommand(OpenLogAsync, () => !IsBusy, ShowError);
        UndoCommand = new RelayCommand(Choose, () => Selected is { } row && JobTexts.CanTryUndo(row.Job));
        if (load) _ = AddAsync(() => JobCatalog.Recent(), select: false, "This PC does not remember any earlier sorts. Choose a folder, or open a log file.", startedFrom: null);
    }

    public ObservableCollection<UndoRow> Rows { get; } = [];

    public UndoRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectionNote));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Why the selected sort cannot be undone, or where its files would go.</summary>
    public string SelectionNote => Selected is not { } row ? ""
        : JobTexts.WhyNotUndo(row.Job, row.Why) ?? $"Its files return to their source folder ({row.From}, or where that folder is now). "
            + "Before anything moves, the app shows you exactly where the files go and what will happen.";

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The sort the user chose to undo, and what they opened to find it.</summary>
    public UndoChoice? Choice { get; private set; }

    /// <summary>Raised when the user has chosen a sort; the window closes.</summary>
    public event EventHandler? Chosen;

    public ICommand ChooseFolderCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand UndoCommand { get; }

    /// <summary>
    /// Adds jobs to the list (sorts only, newest first); jobs already listed are refreshed. <paramref name="missing"/>:
    /// why the log of a listed job (by id) can't be read, when its drive may be connected. <paramref name="startedFrom"/>:
    /// the folder or receipt the user opened to find them.
    /// </summary>
    public void Add(IEnumerable<JobListing> jobs, bool select, IReadOnlyDictionary<string, MissingLog>? missing = null, string? startedFrom = null)
    {
        UndoRow? first = null;
        foreach (JobListing job in jobs.Where(j => j.Kind == JobKind.Sort))
        {
            var row = new UndoRow(job, missing is not null && missing.TryGetValue(job.Id, out MissingLog why) ? why : MissingLog.DriveAway, startedFrom);
            int known = Rows.ToList().FindIndex(r => r.Job.Id == job.Id);
            if (known >= 0)
            {
                if (!job.IsConnected && Rows[known].Job.IsConnected) continue;
                Rows[known] = row;
            }
            else Rows.Add(row);
            first ??= row;
        }
        List<UndoRow> sorted = Rows.OrderByDescending(r => r.Job.Created ?? "", StringComparer.Ordinal).ToList();
        for (int i = 0; i < sorted.Count; i++)
            if (!ReferenceEquals(Rows[i], sorted[i])) Rows[i] = sorted[i];
        if (select && first is not null) Selected = Rows.FirstOrDefault(r => r.Job.Id == first.Job.Id);
        else Selected ??= Rows.FirstOrDefault(r => JobTexts.CanTryUndo(r.Job));
    }

    private async Task AddAsync(Func<List<JobListing>> find, bool select, string emptyText, string? startedFrom)
    {
        IsBusy = true;
        try
        {
            (List<JobListing> jobs, Dictionary<string, MissingLog> missing) = await Task.Run(() =>
            {
                List<JobListing> found = find();
                // "Not connected" only when the drive really is not: a renamed or moved folder is said as such. The
                // drive's serial number decides when the job recorded it, else its label.
                return (found, found.Where(j => !j.IsConnected).DistinctBy(j => j.Id)
                    .ToDictionary(j => j.Id, j => JobTexts.WhyLogMissing(j.TargetSerial, j.LogMissing, () => LogDrive.Why(j.JournalPath, j.TargetLabel))));
            });
            int before = Rows.Count;
            Add(jobs, select, missing, startedFrom);
            Status = jobs.Count == 0 ? emptyText
                : Rows.Count == before && select ? "Already in the list (selected)."
                : $"The list shows {RunOutcome.Count(Rows.Count, "sort")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChooseFolderAsync()
    {
        string? folder = _dialogs.PickFolder("Choose the source folder or the target folder of a sort", _lastFolder);
        if (folder is null) return;
        _lastFolder = folder;
        Status = "Search in progress: sorts of " + folder + "...";
        await AddAsync(() => JobCatalog.ForFolder(folder), select: true, $"The app found no sorts for {folder}.", startedFrom: folder);
    }

    private async Task OpenLogAsync()
    {
        string? file = _dialogs.PickFile("Open a job log or a receipt", LogFilter, _lastFolder);
        if (file is null) return;
        _lastFolder = Path.GetDirectoryName(file);
        IsBusy = true;
        JobListing? job;
        string notFound = JobTexts.LogNotFound;
        try
        {
            string? whyNot = null;
            job = await Task.Run(() => UndoFactory.ResolveJournal(file, out whyNot) is { } journal ? JobCatalog.Load(journal) : null);
            // A receipt names its job log: say where that was, and whether its drive is here (then the folder was renamed or moved).
            // The drive's serial number (in the receipt, or remembered with the job) decides; without it, the label the
            // recent-jobs list remembers - so another drive that got the letter is not taken for it.
            if (job is null && await Task.Run(() => ReceiptLog(file)) is { } receipt)
                notFound = JobTexts.ReceiptLogNotFound(receipt.Log, await Task.Run(() =>
                {
                    uint serial = UndoFactory.LogDriveSerial(receipt.Text, receipt.JobId);
                    return JobTexts.WhyLogMissing(serial, JobPaths.DriveIsThere(receipt.Log, serial),
                        () => LogDrive.Why(receipt.Log, JobTexts.RecordedLabel(RecentJobs.Entries(), receipt.JobId, receipt.Log)));
                }));
            // Any other file: the engine says what it looked for.
            else if (job is null && whyNot is not null) notFound = whyNot;
        }
        finally
        {
            IsBusy = false;
        }
        if (job is null)
        {
            _dialogs.Inform("Undo a previous sort", notFound, isError: true);
            return;
        }
        Add([job], select: true, startedFrom: file);
        Status = job.Kind == JobKind.Undo ? "That is the log of an undo job. You cannot undo an undo job." : "The app opened " + Path.GetFileName(file) + ".";
    }

    private void Choose()
    {
        if (Selected is not { } row || !JobTexts.CanTryUndo(row.Job)) return;
        Choice = new UndoChoice(row.Job.JournalPath, row.StartedFrom);
        Chosen?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The job log a receipt (its .moved-out.txt, also when the .csv was picked) names, with the job's id (from the
    /// receipt, else from its file name) and the path of its .moved-out.txt, or null. Never throws.
    /// </summary>
    private static (string Log, string JobId, string Text)? ReceiptLog(string file)
    {
        foreach (string suffix in new[] { JobPaths.ReceiptCsvSuffix, JobPaths.ReceiptTextSuffix })
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                try
                {
                    string text = file[..^suffix.Length] + JobPaths.ReceiptTextSuffix;
                    if (!File.Exists(text)) return null;
                    List<string> lines = File.ReadLines(text).ToList();
                    return JobTexts.ReceiptLog(lines) is { } log
                        ? (log, JobTexts.ReceiptJobId(lines) ?? Path.GetFileName(file)[..^suffix.Length], text)
                        : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return null;
                }
        return null;
    }

    private void ShowError(Exception e) => _dialogs.Inform("Undo a previous sort", e.Message, isError: true);
}
