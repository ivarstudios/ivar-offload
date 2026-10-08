using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using IvarOffload.Core;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>
/// Checking = reading the source again after a job, to show honestly what is still there. Removing = removing the
/// empty folders a job left (it holds the job's lock, so nothing else may start meanwhile).
/// </summary>
public enum UiState { Idle, Scanning, Previewed, Running, Checking, Finished, Verifying, Removing }

public sealed record FileRow(string Folder, string Name, string Kind, string Why, long SizeBytes, string Size, string Status, bool Attention)
{
    public string Path => Folder.Length == 0 ? Name : Folder + "\\" + Name;
}

public sealed record FolderRow(string Folder, int Moving, long MovingBytes, string MovingSize, int Staying, string Note);

public sealed record TypeRow(string Extension, string Classification, string Action, int Files, long Bytes, string Size);

public sealed record MessageRow(string Icon, string Text, MessageLevel Level)
{
    /// <summary>The Sort preview's warning about folders that will be split (always shown, not under "Good to know").</summary>
    public bool IsSplit { get; init; }
}

public sealed class MainViewModel : ObservableObject
{
    public const int BackupTab = 0, SortTab = 1;
    public const int MoveListTab = 0, StayListTab = 3;

    private const string IconWarning = "", IconInfo = "", IconError = "";

    private readonly IDialogs _dialogs;
    private string _sourcePath = "";
    private string _targetPath = "";
    private string _lastSuggestedTarget = "";
    private string _browseTargetStart = "";
    private bool _moveVideos = true;
    private bool _verifyChecksums = true;
    private UiState _state = UiState.Idle;
    private MovePlan? _plan;
    private bool _afterRun;
    private SourceCheck? _check;
    /// <summary>Unfinished jobs whose reports were brought up to date from their logs in this window (see RefreshBannerAsync).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _reportsRefreshed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The memory card or camera drive ("F: SONY_A") the running or last sort was confirmed on, or null.</summary>
    private string? _sortCard;
    /// <summary>What the result card said after the last job (kept when "Verify again" adds its own title).</summary>
    private ResultView? _outcome;
    private string _status = "To start, choose the source folder: the folder with your card backup.";
    private string _runVerb = "Move in progress";
    private CancellationTokenSource? _cts;
    private PauseGate? _pause;
    private Task? _activeRun;
    private long _progressBytesTotal;

    public MainViewModel() : this(new WpfDialogs())
    {
    }

    /// <param name="loadSettings">False to start with empty inputs (tests).</param>
    public MainViewModel(IDialogs dialogs, bool loadSettings = true)
    {
        _dialogs = dialogs;
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => CanEditInputs);
        BrowseTargetCommand = new RelayCommand(BrowseTarget, () => CanEditInputs);
        ScanCommand = new AsyncCommand(ScanAsync, () => CanEditInputs && SourcePath.Trim().Length > 0, ShowError);
        ConfirmCommand = new AsyncCommand(ConfirmAsync, () => CanConfirm, ShowError);
        PauseCommand = new RelayCommand(TogglePause, () => State == UiState.Running && _pause is not null);
        StopCommand = new RelayCommand(Stop, () => ShowStop && _cts is { IsCancellationRequested: false });
        ResumeJobCommand = new AsyncCommand(() => RunExistingJobAsync(close: false), () => ResumableJob is not null && CanEditInputs, ShowError);
        CloseJobCommand = new AsyncCommand(CloseJobAsync, () => ResumableJob is not null && CanEditInputs, ShowError);
        RecheckJobsCommand = new AsyncCommand(() => RefreshBannerAsync(), () => CanEditInputs, ShowError);
        ForgetJobCommand = new RelayCommand(ForgetOfflineJob, () => OfflineJob is not null && CanEditInputs);
        OpenTargetCommand = new RelayCommand(() => OpenInExplorer(LastJob?.Header.Target ?? TargetPath), () => LastJob is not null || Directory.Exists(TargetPath));
        OpenLogCommand = new RelayCommand(OpenLog, () => LastJob is not null);
        VerifyCommand = new AsyncCommand(VerifyAsync, () => LastJob is { DoneCount: > 0 } && CanEditInputs, ShowError);
        RemoveEmptyFoldersCommand = new AsyncCommand(RemoveEmptyFoldersAsync, () => EmptyFolderCount > 0 && CanEditInputs, ShowError);
        ChangeSetupCommand = new RelayCommand(ChangeSetup, () => CanEditInputs);
        UseSuggestedSourceCommand = new AsyncCommand(UseSuggestedSourceAsync, () => CanEditInputs && SuggestedSource is not null, ShowError);
        ShowAttentionCommand = new RelayCommand(ShowAttention, () => HasPlan && HasAttention);
        ShowStillInSourceCommand = new RelayCommand(ShowStillInSource, () => ShowLists);
        UndoLastCommand = new AsyncCommand(() => UndoAsync(new UndoChoice(LastJob!.JournalPath)), () => CanUndoLastJob && CanEditInputs, ShowError);
        UndoPreviousCommand = new AsyncCommand(UndoPreviousAsync, () => CanEditInputs, ShowError);
        Backup = new BackupViewModel(dialogs, SortFolder, () => IsBusy, loadSettings);

        if (loadSettings) LoadSettings();
        _ = RefreshBannerAsync(startup: true);
    }

    /// <summary>The Backup tab.</summary>
    public BackupViewModel Backup { get; }

    /// <summary>A sort, undo or backup is running (the window asks before closing).</summary>
    public bool IsAnyJobRunning => IsRunning || Backup.IsRunning;

    public static string Version { get; } =
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";

    public string VersionText => "version " + Version;

    // ---- Modes ------------------------------------------------------------------------------------------------------

    private int _selectedModeTab;
    /// <summary>0 = Backup, 1 = Sort.</summary>
    public int SelectedModeTab
    {
        get => _selectedModeTab;
        set
        {
            if (!Set(ref _selectedModeTab, value)) return;
            Raise(nameof(IsSortTab));
            Raise(nameof(IsBackupTab));
        }
    }

    public bool IsSortTab => SelectedModeTab == SortTab;
    public bool IsBackupTab => SelectedModeTab == BackupTab;

    /// <summary>"Sort this backup": the Sort tab, with the backup folder as its source.</summary>
    private void SortFolder(string folder)
    {
        SelectedModeTab = SortTab;
        if (!CanEditInputs) return;
        SetSource(folder, keepTarget: false);
        Status = "The backup is now the source folder. Choose what to move. Then click “Check folder”.";
    }

    // ---- Inputs ---------------------------------------------------------------------------------------------------

    public string SourcePath
    {
        get => _sourcePath;
        set => SetSource(value, keepTarget: false);
    }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (!Set(ref _targetPath, value)) return;
            Raise(nameof(ShowBannerPaths));
            InvalidatePlan();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool MoveVideos
    {
        get => _moveVideos;
        set
        {
            if (!Set(ref _moveVideos, value)) return;
            Raise(nameof(MovePhotos));
            Raise(nameof(ModeHint));
            Raise(nameof(ModeWord));
            if (TargetWasSuggested) SuggestTarget();
            InvalidatePlan();
        }
    }

    public bool MovePhotos
    {
        get => !_moveVideos;
        set => MoveVideos = !value;
    }

    public bool VerifyChecksums
    {
        get => _verifyChecksums;
        set
        {
            if (Set(ref _verifyChecksums, value)) InvalidatePlan();
        }
    }

    public MoveMode Mode => MoveVideos ? MoveMode.Videos : MoveMode.Photos;

    /// <summary>"Videos" / "Photos", for the one-line setup summary.</summary>
    public string ModeWord => char.ToUpperInvariant(Planner.Word(Mode)[0]) + Planner.Word(Mode)[1..];

    public string ModeHint => MoveVideos
        ? "Videos move, together with the small files that cameras and drones make next to them (previews, subtitles, info files). Sound recordings that do not belong to a photo also move. Photos and everything else stay."
        : "Photos and raw files move, with their small companion files (edits, voice memos, Live Photo clips). Videos, sound recordings and everything else stay.";

    private bool TargetWasSuggested =>
        TargetPath.Trim().Length == 0 || string.Equals(TargetPath.Trim(), _lastSuggestedTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A new source gets its own suggested target: a target chosen for another source is never kept (it only becomes
    /// the start folder of Browse). A target typed before any source was chosen is kept.
    /// </summary>
    private void SetSource(string value, bool keepTarget)
    {
        string old = _sourcePath;
        if (!Set(ref _sourcePath, value, nameof(SourcePath))) return;
        Raise(nameof(ShowBannerPaths));
        if (TargetWasSuggested) SuggestTarget();
        else if (!keepTarget && old.Trim().Length > 0)
        {
            _browseTargetStart = TargetPath.Trim();
            SuggestTarget();
        }
        InvalidatePlan();
        CommandManager.InvalidateRequerySuggested(); // the text box commits after a delay, with no input event to trigger this
    }

    private void SuggestTarget()
    {
        _lastSuggestedTarget = PlanFacts.SuggestTarget(SourcePath, Mode) ?? "";
        _targetPath = _lastSuggestedTarget;
        Raise(nameof(TargetPath));
        Raise(nameof(ShowBannerPaths));
    }

    // ---- State ----------------------------------------------------------------------------------------------------

    public UiState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            RaiseAll(); // nearly every visible part depends on it
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditInputs => State is UiState.Idle or UiState.Previewed or UiState.Finished;
    public bool IsBusy => State is UiState.Scanning or UiState.Running or UiState.Verifying or UiState.Checking or UiState.Removing;
    /// <summary>A job or a verification is running: the progress bar is shown.</summary>
    public bool IsRunning => State is UiState.Running or UiState.Verifying;
    public bool ShowPause => State == UiState.Running;
    public bool ShowStop => State is UiState.Running or UiState.Verifying or UiState.Checking;

    /// <summary>The Confirm button: in the preview, and after a job when the fresh scan found files that can move.</summary>
    public bool ShowConfirm => State == UiState.Previewed || State == UiState.Finished && _afterRun && _plan is { CanRun: true } && ResumableJob is null;
    /// <summary>
    /// Not while the setup is open for changes: the preview below is for the settings it was made with, and a path
    /// being typed reaches the view model only after a short delay.
    /// </summary>
    private bool CanConfirm => (State == UiState.Previewed || State == UiState.Finished && _afterRun) && _plan is { CanRun: true } && ResumableJob is null
        && !SetupExpanded;

    public System.Windows.Shell.TaskbarItemProgressState TaskbarState =>
        !IsRunning ? System.Windows.Shell.TaskbarItemProgressState.None
        : IsPaused ? System.Windows.Shell.TaskbarItemProgressState.Paused
        : System.Windows.Shell.TaskbarItemProgressState.Normal;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private bool _setupExpanded;
    /// <summary>The user pressed "Change" on the one-line setup summary.</summary>
    public bool SetupExpanded
    {
        get => _setupExpanded;
        set
        {
            if (!Set(ref _setupExpanded, value)) return;
            Raise(nameof(IsSetupCompact));
            Raise(nameof(IsSetupFull));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>After a scan the setup shrinks to one line, so the preview gets the room.</summary>
    public bool IsSetupCompact => !SetupExpanded && (HasPlan || HasResult || IsBusy);
    public bool IsSetupFull => !IsSetupCompact;

    // ---- Preview --------------------------------------------------------------------------------------------------

    public bool HasPlan => _plan is not null;
    /// <summary>The preview cards (tiles): only before a job; afterwards the result card says what happened.</summary>
    public bool ShowPlanSummary => HasPlan && !_afterRun;
    /// <summary>Warnings and errors come first, above the tiles, so they are seen even on a short screen.</summary>
    public bool ShowAlerts => Alerts.Count > 0 && (!_afterRun || ShowConfirm);
    public bool ShowNotes => Notes.Count > 0 && !_afterRun;
    /// <summary>The file lists: the preview, or after a job what is still in the source.</summary>
    public bool ShowLists => HasPlan || _afterRun && _check is { Left.Count: > 0 };
    public bool IsAfterRun => _afterRun;
    public string ListsHeading { get; private set; } = "";
    /// <summary>
    /// Height the lists keep when the window is short. The preview's lists are the point of the preview; after a job
    /// the result card comes first and a short list needs little room.
    /// </summary>
    public double ListsMinHeight => !_afterRun ? UpperAreaHeight.ListsMinHeight : Math.Clamp(110 + 32 * (_check?.Left.Count ?? 0), 150, UpperAreaHeight.ListsMinHeight);
    public bool ListsGrow => !_afterRun;
    /// <summary>Shown instead of an empty "Still in the source" list.</summary>
    public string MoveListEmptyText => _afterRun && _check is { Left.Count: 0 }
        ? (_check.NotChecked is null ? "Nothing to move remains in the source folder. The “Files that stay” tab shows these files and why they stay." : "")
        : "";
    /// <summary>The filter box and "only files that need a look" apply to the file lists.</summary>
    public bool ShowListFilter => SelectedListTab is MoveListTab or StayListTab;

    public ICollectionView? MoveView { get; private set; }
    public ICollectionView? StayView { get; private set; }
    public IReadOnlyList<FolderRow> FolderRows { get; private set; } = [];
    public IReadOnlyList<TypeRow> TypeRows { get; private set; } = [];
    public IReadOnlyList<MessageRow> Messages { get; private set; } = [];
    /// <summary>
    /// What must be read before moving: the errors, and folders that will be split (the memory-card warning has a line
    /// of its own next to the button). Everything else is under "Good to know".
    /// </summary>
    public IReadOnlyList<MessageRow> Alerts => Messages.Where(IsAlert).ToList();
    public IReadOnlyList<MessageRow> Notes => Messages.Where(m => !IsAlert(m)).ToList();
    public string NotesLabel => $"Good to know ({Notes.Count})";
    /// <summary>The button on the one-line setup: "Change" before a sort; afterwards it starts the next one.</summary>
    public string ChangeSetupText => _afterRun ? "Sort another folder" : "Change";

    private static bool IsAlert(MessageRow m) => m.Level == MessageLevel.Error || m.IsSplit;
    public string MoveCount { get; private set; } = "";
    public string MoveSize { get; private set; } = "";
    public string StayCount { get; private set; } = "";
    public string StaySize { get; private set; } = "";
    public string MethodTitle { get; private set; } = "";
    public string MethodDetail { get; private set; } = "";
    public string AttentionCount { get; private set; } = "";
    public string AttentionDetail { get; private set; } = "";
    public bool HasAttention { get; private set; }
    public string ConfirmText { get; private set; } = "Move";
    public string DestinationLine { get; private set; } = "";
    /// <summary>
    /// The line is amber: the target is on the memory card being sorted, or it already holds files sorted from another
    /// folder or in the other mode. <see cref="DestinationWarnText"/> says which.
    /// </summary>
    public bool DestinationWarn => DestinationWarnText.Length > 0;
    public string DestinationWarnText { get; private set; } = "";
    /// <summary>The memory-card warning next to Confirm, where no scrolling can hide it (empty when the source is not flagged).</summary>
    public string CardLine { get; private set; } = "";
    public bool HasCardLine => CardLine.Length > 0;
    public string MoveTabHeader { get; private set; } = "Files to move";
    public string StayTabHeader { get; private set; } = "Files that stay";
    public string? SuggestedSource { get; private set; }
    public bool HasSuggestedSource => SuggestedSource is not null;

    private int _selectedListTab;
    public int SelectedListTab
    {
        get => _selectedListTab;
        set
        {
            if (Set(ref _selectedListTab, value)) Raise(nameof(ShowListFilter));
        }
    }

    private string _listFilter = "";
    /// <summary>Shows only rows whose path, type, reason or note contains this text (both file lists).</summary>
    public string ListFilter
    {
        get => _listFilter;
        set
        {
            if (!Set(ref _listFilter, value)) return;
            MoveView?.Refresh();
            StayView?.Refresh();
        }
    }

    private bool _attentionOnly;
    /// <summary>Show only the files that need a look (both lists).</summary>
    public bool AttentionOnly
    {
        get => _attentionOnly;
        set
        {
            if (!Set(ref _attentionOnly, value)) return;
            MoveView?.Refresh();
            StayView?.Refresh();
        }
    }

    // ---- Progress & results -----------------------------------------------------------------------------------------

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private string _progressLeft = "";
    public string ProgressLeft { get => _progressLeft; private set => Set(ref _progressLeft, value); }
    private string _progressRight = "";
    public string ProgressRight { get => _progressRight; private set => Set(ref _progressRight, value); }
    private string _progressFile = "";
    public string ProgressFile { get => _progressFile; private set => Set(ref _progressFile, value); }
    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!Set(ref _isPaused, value)) return;
            Raise(nameof(PauseText));
            Raise(nameof(TaskbarState));
        }
    }
    public string PauseText => IsPaused ? "Continue" : "Pause";

    public ResultTone ResultTone { get; private set; }
    public bool ResultIsGood => ResultTone == ResultTone.Good;
    public bool ResultIsChecking => ResultTone == ResultTone.Checking;
    public bool ResultIsAttention => ResultTone == ResultTone.Attention;
    public string ResultTitle { get; private set; } = "";
    public string ResultLeftLine { get; private set; } = "";
    public IReadOnlyList<string> ResultReasons { get; private set; } = [];
    public bool HasResultReasons => ResultReasons.Count > 0;
    public string ResultDetail { get; private set; } = "";
    public string ResultAlarm { get; private set; } = "";
    public bool HasResult => ResultTitle.Length > 0;
    /// <summary>"Show files still in the source": something that should have moved is still there, or files there need a look.</summary>
    public bool HasStillInSource => _afterRun && _check is { } c && (c.Left.Count > 0 || HasPlan && c.Unrecognized + c.OtherLooks > 0);
    public string ShowStillInSourceText => RunOutcome.ShowLeftText(LastJob?.IsUndo == true);

    private JobState? _lastJob;
    public JobState? LastJob
    {
        get => _lastJob;
        private set
        {
            if (!Set(ref _lastJob, value)) return;
            Raise(nameof(HasLastJob));
            Raise(nameof(CanUndoLastJob));
            Raise(nameof(OpenTargetText));
            Raise(nameof(RemoveEmptyText));
            Raise(nameof(ShowStillInSourceText));
        }
    }
    public bool HasLastJob => LastJob is not null;

    /// <summary>
    /// "Undo this sort": an ended sort that moved files and was not undone - or whose undo was ended early or left files
    /// in the sorted folder (<see cref="UndoMark.IsPartial"/>): another undo can try the rest.
    /// </summary>
    public bool CanUndoLastJob => LastJob is { IsEnded: true, IsUndo: false, DoneCount: > 0 } j && (j.UndoneBy is null || j.UndoneBy.IsPartial);

    public string OpenTargetText => LastJob?.IsUndo == true ? "Open the source folder" : "Open the target folder";

    private int _emptyFolderCount;
    public int EmptyFolderCount
    {
        get => _emptyFolderCount;
        private set { if (Set(ref _emptyFolderCount, value)) Raise(nameof(RemoveEmptyText)); }
    }
    public string RemoveEmptyText => $"Remove {RunOutcome.Count(EmptyFolderCount, "empty folder")}"
        + (LastJob?.IsUndo == true ? " in the target folder" : " in the source folder");
    private List<string> _emptyFolders = [];

    // ---- Unfinished job banner --------------------------------------------------------------------------------------

    private List<JobState> _unfinished = [];
    private List<RecentJob> _offline = [];
    /// <summary>Why the log of an unfinished job (by id) can't be reached: its drive is away, or its folder was renamed or moved.</summary>
    private Dictionary<string, MissingLog> _offlineWhy = [];

    private JobState? _resumableJob;
    /// <summary>An unfinished job whose log can be reached: Resume / End job.</summary>
    public JobState? ResumableJob
    {
        get => _resumableJob;
        private set
        {
            if (!Set(ref _resumableJob, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>An unfinished job whose drive is not connected (shown when no reachable job is waiting).</summary>
    public RecentJob? OfflineJob { get; private set; }
    public bool HasBanner => ResumableJob is not null || OfflineJob is not null;
    public bool BannerOffline => ResumableJob is null && OfflineJob is not null;
    public string BannerTitle { get; private set; } = "";
    /// <summary>The job's folders, on one line.</summary>
    public string BannerPaths { get; private set; } = "";
    /// <summary>The folders are left out when they are the ones already shown in the setup.</summary>
    public bool ShowBannerPaths => BannerPaths.Length > 0
        && !(ResumableJob is { } job && JobPaths.SamePath(job.Header.Source, SourcePath.Trim()) && JobPaths.SamePath(job.Header.Target, TargetPath.Trim()));
    public string BannerText { get; private set; } = "";
    /// <summary>Why the unfinished job's log can't be reached: its drive is away, or its folder was renamed or moved.</summary>
    public MissingLog OfflineWhy => OfflineJob is { } away && _offlineWhy.TryGetValue(away.JobId, out MissingLog why) ? why : MissingLog.DriveAway;
    public string CloseJobText => JobTexts.EndJobButton(ResumableJob?.IsUndo == true);
    public string CloseJobToolTip => JobTexts.EndJobToolTip(ResumableJob?.IsUndo == true);

    // ---- Commands ---------------------------------------------------------------------------------------------------

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseTargetCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResumeJobCommand { get; }
    public ICommand CloseJobCommand { get; }
    public ICommand RecheckJobsCommand { get; }
    public ICommand ForgetJobCommand { get; }
    public ICommand OpenTargetCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RemoveEmptyFoldersCommand { get; }
    public ICommand ChangeSetupCommand { get; }
    public ICommand UseSuggestedSourceCommand { get; }
    public ICommand ShowAttentionCommand { get; }
    public ICommand ShowStillInSourceCommand { get; }
    public ICommand UndoLastCommand { get; }
    public ICommand UndoPreviousCommand { get; }

    private void BrowseSource()
    {
        if (_dialogs.PickFolder("Choose the folder with the card backup", SourcePath.Trim()) is { } folder) SourcePath = folder;
    }

    private void BrowseTarget()
    {
        string? start = Directory.Exists(TargetPath) ? TargetPath
            : Directory.Exists(_browseTargetStart) ? _browseTargetStart
            : Directory.Exists(SourcePath) ? Path.GetDirectoryName(SourcePath.TrimEnd('\\')) ?? SourcePath
            : null;
        if (_dialogs.PickFolder($"Choose the target folder for the {Planner.Word(Mode)}", start) is { } folder) TargetPath = folder;
    }

    private async Task ScanAsync()
    {
        string source = SourcePath.Trim().Trim('"');
        string target = TargetPath.Trim().Trim('"');
        MoveMode mode = Mode;
        bool verify = VerifyChecksums;
        if (!Directory.Exists(source))
        {
            Status = "The source folder does not exist.";
            return;
        }

        ClearPreview();
        ClearResult();
        SetupExpanded = false;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        State = UiState.Scanning;
        Status = "Folder check in progress...";
        var progress = new Progress<ScanProgress>(p => Status = $"Folder check in progress: {p.Files:N0} files in {p.Folders:N0} folders");
        try
        {
            MovePlan plan = await Task.Run(() => Leftovers.WithEarlierSorts(Planner.Build(Scanner.Scan(source, progress, ct, target), target, mode, verify)));
            if (!InputsAre(source, target, mode))
            {
                // A path typed just before pressing Scan reached the view model during the scan.
                State = UiState.Idle;
                SetupExpanded = true;
                Status = "Something changed during the check. Check the folder again.";
                return;
            }
            ShowPlan(plan);
            SaveSettings();
            if (plan.TargetRoot.Length > 0)
            {
                List<JobState> unfinished = await Task.Run(() => JobPaths.FindUnfinished(plan.TargetRoot));
                if (unfinished.FirstOrDefault(j => JobPaths.SamePath(j.Header.Source, plan.SourceRoot)) is { } same) ShowUnfinishedFirst(same);
            }
            State = UiState.Previewed;
            Status = ResumableJob is not null
                ? "There is an unfinished job (above). Resume it, or end it, before you start a new job."
                : plan.CanRun ? $"Review the preview{WarningsToRead()}. Then click “{ConfirmText}”. The app did not change anything yet."
                : plan.HasErrors ? "The move cannot start. See the messages above." : $"Nothing to move: the app found no {Planner.Word(mode)} that can move.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            State = UiState.Idle;
            SetupExpanded = true;
            Status = e is OperationCanceledException ? "The check stopped." : "The app could not check the folder: " + e.Message;
        }
    }

    private bool InputsAre(string source, string target, MoveMode mode) =>
        SourcePath.Trim().Trim('"') == source && TargetPath.Trim().Trim('"') == target && Mode == mode;

    /// <summary>" and its 3 warnings": on a short screen some of them may be below the visible part.</summary>
    private string WarningsToRead()
    {
        int warnings = Alerts.Count(m => m.Level == MessageLevel.Warning);
        return warnings == 0 ? "" : $" and its {RunOutcome.Count(warnings, "warning")}";
    }

    private void ChangeSetup()
    {
        SetupExpanded = true;
        if (ShowConfirm) Status = "Change what you need. Then click “Check folder” to see the new preview.";
    }

    private async Task UseSuggestedSourceAsync()
    {
        if (SuggestedSource is not { } folder) return;
        SetSource(folder, keepTarget: true);
        await ScanAsync();
    }

    private async Task ConfirmAsync()
    {
        if (_plan is not { CanRun: true } plan) return;
        if (plan.MemoryCard is { } card)
        {
            switch (_dialogs.AskMemoryCard(card))
            {
                case CardAnswer.GoToBackup:
                    SelectedModeTab = BackupTab;
                    Status = Backup.UseCard(plan.SourceRoot)
                        ? JobTexts.MemoryCardNotSorted
                        : "Nothing moved. A backup is in progress on the Backup tab. When it is finished, back up this card. Then sort the backup.";
                    return;
                case CardAnswer.Cancel:
                    Status = "Nothing moved.";
                    return;
            }
        }
        if (Backup.IsBusy)
        {
            _dialogs.Inform(WpfDialogs.Caption, "A backup is in progress in this window. Before you start a sort, wait until the backup is finished, or stop it.");
            return;
        }
        ClearResult();
        _sortCard = plan.MemoryCard; // the result reminds that the card still holds the rest (and maybe everything)
        await RunJobAsync(options => JobRunner.Start(plan, options), (runner, ct) => runner.Run(ct), "Move in progress");
    }

    private async Task RunExistingJobAsync(bool close)
    {
        if (ResumableJob is not { } job) return;
        if (Backup.IsBusy)
        {
            _dialogs.Inform(WpfDialogs.Caption, "A backup is in progress in this window. First, wait until it is finished, or stop it.");
            return;
        }
        if (!job.IsUndo)
        {
            // Show the job's own folders and mode (an undo's folders are the other way round and are not sort inputs).
            _sourcePath = job.Header.Source;
            _targetPath = job.Header.Target;
            _moveVideos = job.Header.Mode == MoveMode.Videos;
            _lastSuggestedTarget = PlanFacts.SuggestTarget(_sourcePath, job.Header.Mode) ?? "";
        }
        ClearPreview();
        ClearResult();
        string verb = close ? "The app ends the job" : job.IsUndo ? "Undo in progress" : "The sort continues";
        await RunJobAsync(options => JobRunner.Open(job.JournalPath, options), (runner, ct) => close ? runner.Close(ct) : runner.Run(ct), verb);
    }

    private async Task CloseJobAsync()
    {
        (string title, string question) = JobTexts.EndJobQuestion(ResumableJob?.IsUndo == true);
        if (!_dialogs.Confirm(title, question)) return;
        await RunExistingJobAsync(close: true);
    }

    private async Task RunJobAsync(Func<RunOptions, JobRunner> open, Func<JobRunner, CancellationToken, RunResult> run, string verb)
    {
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _pause = new PauseGate();
        RunOptions options = Options(); // created here so progress is reported on the UI thread
        IsPaused = false;
        Progress = 0;
        ProgressLeft = ProgressRight = ProgressFile = "";
        Interlocked.Exchange(ref _progressBytesTotal, 0);
        _runVerb = verb;
        State = UiState.Running;
        Status = verb + "...";
        string? journal = null;
        RunResult? result = null;
        try
        {
            Task<RunResult> task = Task.Run(() =>
            {
                using JobRunner runner = open(options);
                journal = runner.State.JournalPath;
                Interlocked.Exchange(ref _progressBytesTotal, runner.State.BytesTotal);
                return run(runner, ct);
            });
            _activeRun = task;
            result = await task;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or InvalidOperationException)
        {
            SetResult(ResultTone.Attention, "The job could not continue", "", [],
                e.Message + (journal is null ? "" : "\n\nNothing is lost: the job log records every completed step, and you can resume the job."), null);
            Status = "The job could not continue. See the message above.";
        }
        finally
        {
            _activeRun = null;
            _pause = null;
            IsPaused = false;
        }
        if (journal is not null) LoadLastJob(journal);
        await RefreshBannerAsync(preferred: LastJob);
        if (result is not null) await ShowOutcomeAsync(result, LastJob);
        State = UiState.Finished;
    }

    /// <summary>
    /// The honest result: the job's own counts first; then, for a sort that ended, the source is scanned again (in
    /// the background) and anything that should still move keeps the result amber and is listed.
    /// </summary>
    private async Task ShowOutcomeAsync(RunResult r, JobState? job)
    {
        bool ended = r.Status is RunStatus.Completed or RunStatus.Closed or RunStatus.CompletedWithFailures;
        // A resumed or ended job had no preview in this window: ask its source drive (a fresh scan below asks again).
        _sortCard ??= job is { IsUndo: false } ? await Task.Run(() => CardDrive.Of(job.Header.Source)) : null;
        string? card = _sortCard;
        if (job is null || job.IsUndo || !ended)
        {
            // Stopped or halted: the job's log lists exactly what is left, and the drives may be unplugged now.
            SourceCheck? own = job is null ? null : SourceCheck.ForJob(job, memoryCard: card);
            ApplyOutcome(r, job, own, checking: false);
            if (job is not null && own is { Left.Count: > 0 }) ShowAfterRun(job, own, null);
            return;
        }

        ApplyOutcome(r, job, SourceCheck.ForJob(job, memoryCard: card), checking: true);
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        State = UiState.Checking;
        var progress = new Progress<ScanProgress>(p => Status = $"The app checks what remains in the source folder: {p.Files:N0} files");
        // The source again, with what this job and earlier sorts of the folder left behind (the plan "Move the remaining"
        // runs). What it finds also goes into the job's log, so its summary says the same as this result.
        (SourceCheck check, MovePlan? plan) = await Task.Run(() =>
        {
            (SourceCheck Check, MovePlan? Plan) again = SourceCheck.Rescan(job, progress, ct, card);
            SourceCheck.Record(job, again.Check);
            return again;
        });
        ApplyOutcome(r, job, check, checking: false);
        ShowAfterRun(job, check, plan);
    }

    private void ApplyOutcome(RunResult r, JobState? job, SourceCheck? check, bool checking)
    {
        ResultView view = RunOutcome.Describe(r, job, check, checking);
        _check = check;
        if (check?.MemoryCard is not null) KeepCardFolders();
        ShowResult(view);
        _outcome = view;
    }

    private void ShowResult(ResultView view)
    {
        SetResult(view.Tone, view.Title, view.LeftLine, view.Reasons, view.Detail, view.Alarm, view.Technical);
        Status = view.Status;
    }

    private RunOptions Options() => new()
    {
        Progress = new Progress<RunProgress>(UpdateProgress),
        Pause = _pause,
    };

    private void UpdateProgress(RunProgress p)
    {
        Progress = p.Fraction;
        long dataTotal = Interlocked.Read(ref _progressBytesTotal) is > 0 and var total ? total : p.WorkTotal;
        string data = p.WorkTotal > 0 ? $"  ·  {Format.Bytes((long)(p.Fraction * dataTotal))} of {Format.Bytes(dataTotal)}" : "";
        ProgressLeft = $"{p.ItemsFinished:N0} of {p.ItemsTotal:N0} files{data}"
            + (p.Skipped > 0 ? $"  ·  {p.Skipped:N0} skipped" : "") + (p.Failed > 0 ? $"  ·  {p.Failed:N0} failed" : "");
        ProgressRight = IsPaused ? "Paused"
            : p.BytesPerSecond > 0 ? $"{Format.Rate(p.BytesPerSecond)}" + (p.Remaining is { } r ? $"  ·  approximately {Format.Duration(r)} left" : "")
            : "";
        ProgressFile = p.CurrentFile is null ? p.Phase : $"{p.Phase}:  {p.CurrentFile}";
    }

    private void TogglePause()
    {
        if (_pause is null) return;
        if (_pause.IsPaused) _pause.Resume();
        else _pause.Pause();
        IsPaused = _pause.IsPaused;
        ProgressRight = IsPaused ? "Paused" : "";
        Status = IsPaused ? "Paused. Do not disconnect the drives while the job is paused. To disconnect them, click Stop first." : _runVerb + "...";
    }

    private void Stop()
    {
        _cts?.Cancel();
        _pause?.Resume();
        IsPaused = false;
        Status = State == UiState.Checking ? "The check stops..." : "The job stops safely: the app finishes the file in progress or rolls it back...";
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task VerifyAsync()
    {
        if (LastJob is not { } job) return;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _runVerb = "The app checks the moved files again";
        State = UiState.Verifying;
        Status = "The app checks the moved files again...";
        Progress = 0;
        ProgressLeft = ProgressRight = ProgressFile = "";
        try
        {
            var progress = new Progress<RunProgress>(UpdateProgress);
            VerifyResult result = await Task.Run(() => JobVerifier.Verify(job.JournalPath, progress, ct));
            ShowResult(VerifyOutcome.Show(result, _outcome, job.IsUndo)); // keeps what the job's result said about the source
        }
        catch (OperationCanceledException)
        {
            Status = "The check stopped.";
        }
        catch (JournalException e)
        {
            SetResult(ResultTone.Attention, "The app could not check again", _outcome?.LeftLine ?? "", _outcome?.Reasons ?? [],
                e.InUse ? "This job is in use in another window or in a command-line run. Close the job there. Then check again." : e.Message, _outcome?.Alarm);
            Status = "The app did not check again.";
        }
        finally
        {
            State = UiState.Finished;
        }
    }

    private async Task RemoveEmptyFoldersAsync()
    {
        if (LastJob is not { } job || _emptyFolders.Count == 0) return;
        string sample = string.Join("\n", _emptyFolders.Take(10).Select(f => "  " + f)) + (_emptyFolders.Count > 10 ? "\n  ..." : "");
        string where = job.IsUndo ? "in the target folder held only files that returned to the source folder" : "in the source folder held only moved files";
        if (!_dialogs.Confirm("Remove empty folders",
                $"These folders {where} and are now completely empty:\n\n{sample}\n\nRemove them? The app never removes or changes a folder that is not empty.")) return;
        List<string> folders = _emptyFolders;
        State = UiState.Removing;
        Status = "The app removes the empty folders...";
        try
        {
            int removed = await Task.Run(() => EmptyFolders.Remove(job.JournalPath, folders));
            Status = $"Removed: {RunOutcome.Count(removed, "empty folder")}. The job log records each removal.";
        }
        catch (JournalException e)
        {
            Status = e.InUse ? "This job is in use in another window. The app did not remove any folders." : e.Message;
        }
        finally
        {
            State = UiState.Finished;
        }
        LoadLastJob(job.JournalPath);
    }

    // ---- Undo -------------------------------------------------------------------------------------------------------

    private async Task UndoPreviousAsync()
    {
        if (_dialogs.PickSortToUndo() is { } choice) await UndoAsync(choice);
    }

    /// <summary>
    /// Shows what undoing the sort would do, in plain words, and runs it as a normal job when confirmed. What the user
    /// opened to find the sort (a receipt or a folder) is passed on: the files go back there when it holds the sort's
    /// receipt. When the folder the files came from can't be confirmed, the user can choose it, and the undo is
    /// previewed again for that folder.
    /// </summary>
    private async Task UndoAsync(UndoChoice choice)
    {
        string journal = choice.Journal;
        string? putBackTo = null;
        UndoPreview? preview;
        while (true)
        {
            UiState before = State;
            string statusBefore = Status;
            State = UiState.Scanning;
            Status = "The app checks which files can return...";
            preview = null;
            string? error = null;
            try
            {
                string? to = putBackTo;
                preview = await Task.Run(() => UndoFactory.Preview(journal, choice.StartedFrom, to));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
            {
                error = e.Message;
            }
            finally
            {
                State = before;
                Status = statusBefore;
            }
            if (preview is null)
            {
                _dialogs.Inform("Undo a sort", "The app could not read the job log of the sort: " + error, isError: true);
                return;
            }
            if (JobTexts.UndoNext(preview) != UndoStep.ChooseFolder) break;
            (string blocked, string why) = JobTexts.UndoQuestion(preview);
            if (!_dialogs.AskChoice(blocked, why, JobTexts.UndoChooseFolderButton, "Cancel")) return;
            string? parent = Path.GetDirectoryName(preview.Original.Header.Source.TrimEnd('\\'));
            string? folder = _dialogs.PickFolder(JobTexts.UndoChooseFolderTitle, putBackTo ?? parent);
            if (folder is null) return;
            putBackTo = folder;
        }
        (string question, string details) = JobTexts.UndoQuestion(preview);
        string text = details.Length > 0 ? question + "\n\n" + details : question;
        if (JobTexts.UndoNext(preview) != UndoStep.Ask) // nothing would move back: say so instead of asking
        {
            _dialogs.Inform("Undo a sort", text);
            return;
        }
        if (!_dialogs.Confirm("Undo a sort", text)) return;
        ClearPreview();
        ClearResult();
        string? chosen = putBackTo;
        await RunJobAsync(options => UndoFactory.Start(journal, choice.StartedFrom, chosen, options), (runner, ct) => runner.Run(ct), "Undo in progress");
    }

    // ---- Banner -----------------------------------------------------------------------------------------------------

    /// <summary>Unfinished jobs: reachable ones first (they can be resumed), then those whose drive is not connected.</summary>
    private async Task RefreshBannerAsync(JobState? preferred = null, bool startup = false)
    {
        List<JobState> unfinished;
        List<RecentJob> offline;
        Dictionary<string, MissingLog> why;
        try
        {
            (unfinished, offline, why) = await Task.Run(() =>
            {
                // Unfinished() and NotConnected() first follow a sorted folder that was renamed or moved.
                List<JobState> unfinished = RecentJobs.Unfinished();
                List<RecentJob> away = RecentJobs.NotConnected();
                // After a crash the receipt and reports written while the job ran can be a little behind its log: bring
                // them up to date (once per job in this window; a job open elsewhere keeps its own).
                foreach (JobState job in unfinished)
                    if (_reportsRefreshed.TryAdd(job.JournalPath, true)) JobReports.TryRefresh(job.JournalPath);
                // The drive's serial number decides whether it is here (then the folder was renamed or moved); without
                // one, its label.
                return (unfinished, away, away.DistinctBy(j => j.JobId).ToDictionary(j => j.JobId,
                    j => JobTexts.WhyLogMissing(j.TargetSerial, j.LogMissing, () => LogDrive.Why(j.JournalPath, j.TargetLabel))));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        _offlineWhy = why;
        if (preferred is not null)
        {
            unfinished.RemoveAll(j => j.Header.Id == preferred.Header.Id || JobPaths.SamePath(j.JournalPath, preferred.JournalPath));
            if (preferred.IsResumable) unfinished.Insert(0, preferred);
        }
        SetBanner(unfinished, offline);
        // An unfinished sort is on the Sort tab: show it (the Backup tab, shown first, has a banner of its own - looked
        // for at the same time, so wait for it).
        if (startup && HasBanner)
        {
            try
            {
                await Backup.BannerReady;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            if (!Backup.HasBanner) SelectedModeTab = SortTab;
        }
        if (startup && HasBanner && State == UiState.Idle)
            Status = ResumableJob is not null ? "The app found an unfinished job (above). Resume it, or end it. If you end it, the remaining files stay where they are."
                : OfflineWhy switch
                {
                    MissingLog.FolderMoved => "The folder of an unfinished job has a new name or location (above).",
                    MissingLog.Either => "The app cannot find the log of an unfinished job (above).",
                    _ => "An unfinished job (above) needs its drive.",
                };
    }

    private void ShowUnfinishedFirst(JobState job)
    {
        var unfinished = _unfinished.Where(j => j.Header.Id != job.Header.Id).ToList();
        unfinished.Insert(0, job);
        SetBanner(unfinished, _offline);
    }

    private void SetBanner(List<JobState> unfinished, List<RecentJob> offline)
    {
        offline.RemoveAll(o => unfinished.Any(u => u.Header.Id == o.JobId));
        _unfinished = unfinished;
        _offline = offline;
        ResumableJob = unfinished.FirstOrDefault();
        OfflineJob = ResumableJob is null ? offline.FirstOrDefault() : null;
        int more = unfinished.Count + offline.Count - 1;
        (BannerTitle, BannerPaths, BannerText) = ResumableJob is { } job ? JobTexts.Banner(job, more)
            : OfflineJob is { } away ? JobTexts.Banner(away, more, OfflineWhy)
            : ("", "", "");
        Raise(nameof(OfflineJob));
        Raise(nameof(OfflineWhy));
        Raise(nameof(HasBanner));
        Raise(nameof(BannerOffline));
        Raise(nameof(BannerTitle));
        Raise(nameof(BannerPaths));
        Raise(nameof(ShowBannerPaths));
        Raise(nameof(BannerText));
        Raise(nameof(CloseJobText));
        Raise(nameof(CloseJobToolTip));
        Raise(nameof(ShowConfirm));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ForgetOfflineJob()
    {
        if (OfflineJob is not { } job) return;
        string kind = job.Kind == JobKind.Undo ? "undo" : "sort";
        if (!_dialogs.Confirm("Forget this job",
                $"Forget the unfinished {kind} on {job.DriveName}? The app will not remind you about it again.\n\n"
                + "The app deletes nothing: the job log stays on the drive. When you check that folder again (or open its log), the app finds the job, and you can resume it.")) return;
        RecentJobs.Forget(job.JournalPath);
        if (job.JobId.Length > 0) RecentJobs.Forget(job.JobId);
        _ = RefreshBannerAsync();
    }

    // ---- Lists ------------------------------------------------------------------------------------------------------

    private void ShowAttention()
    {
        ListFilter = "";
        AttentionOnly = true;
        SelectedListTab = _stayAttention > 0 ? StayListTab : MoveListTab;
    }

    private void ShowStillInSource()
    {
        ListFilter = "";
        if (_check is { Left.Count: > 0 })
        {
            AttentionOnly = false;
            SelectedListTab = MoveListTab;
        }
        else
        {
            AttentionOnly = true;
            SelectedListTab = StayListTab;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private int _stayAttention;

    private void ShowPlan(MovePlan plan)
    {
        _plan = plan;
        _afterRun = false;
        _check = null;
        var identical = new HashSet<SourceFile>(plan.IdenticalConflicts);
        var moveRows = plan.ToMove.Select(f => Row(f, identical.Contains(f) ? "Already in the target folder: the app skips it" : FileNotes.Describe(f.Note),
            PlanFacts.NeedsLook(f, identical))).ToList();
        FillLists(plan, moveRows, afterRun: false);

        (int files, _, long bytes) = PlanFacts.Transfer(plan);
        MoveCount = RunOutcome.Files(files);
        MoveSize = Format.Bytes(bytes) + $"  ·  {Planner.Word(plan.Mode)} and their companion files"
            + (identical.Count > 0 ? $" (+{identical.Count:N0} already in the target folder)" : "");
        StayCount = RunOutcome.Files(plan.Staying.Count);
        StaySize = Format.Bytes(plan.BytesStaying) + "  ·  they stay exactly where they are";
        (MethodTitle, MethodDetail) = plan.HasErrors ? ("Cannot start", "See the messages below.")
            : plan.Method == TransferMethod.Rename
                ? ("Instant move", $"Same drive ({plan.SourceVolume?.DisplayName}): the files move in place and keep their names")
                : ("Copy, check, then move", $"{plan.SourceVolume?.DisplayName} → {plan.TargetVolume?.DisplayName}: the app copies and checks each file before it removes the original");
        Attention attention = PlanFacts.Attention(plan);
        AttentionCount = attention.CountText;
        AttentionDetail = attention.Detail;
        HasAttention = attention.Any;
        ConfirmText = PlanFacts.ConfirmText(plan);
        ShowDestination(plan);
        MoveTabHeader = $"Files to move ({plan.ToMove.Count:N0})";
        SuggestedSource = plan.SuggestedSource;
        ListsHeading = "";
        SelectedListTab = MoveListTab;
        RaiseAll();
    }

    /// <summary>After a job: the lists show what is still in the source (and, from the fresh scan, what stays and why).</summary>
    private void ShowAfterRun(JobState job, SourceCheck check, MovePlan? plan)
    {
        bool undo = job.IsUndo;
        MoveMode mode = job.Header.Mode;
        _plan = plan;
        _afterRun = true;
        _check = check;
        var left = check.Left.Select(f => new FileRow(Path.GetDirectoryName(f.Rel) ?? "", Path.GetFileName(f.Rel), PlanFacts.Kind(f.IsPrimary, mode),
            f.Detail, f.Size, Format.Bytes(f.Size), f.Status, true)).ToList();
        FillLists(plan, left, afterRun: true);
        MoveTabHeader = $"Still in the {(undo ? "target" : "source")} folder ({left.Count:N0})";
        ListsHeading = undo ? "After the undo: still in the target folder" : "After the sort: still in the source folder";
        if (plan is not null)
        {
            ConfirmText = PlanFacts.ConfirmText(plan, remaining: true);
            ShowDestination(plan);
        }
        SuggestedSource = null;
        SelectedListTab = MoveListTab;
        RaiseAll();
    }

    /// <summary>The lines above Confirm: where the files go (amber when that needs a second look) and the memory-card warning.</summary>
    private void ShowDestination(MovePlan plan)
    {
        DestinationLine = PlanFacts.Destination(plan);
        DestinationWarnText = PlanFacts.DestinationWarning(plan) ?? "";
        CardLine = PlanFacts.CardLine(plan) ?? "";
    }

    private void FillLists(MovePlan? plan, List<FileRow> firstRows, bool afterRun)
    {
        _listFilter = "";
        _attentionOnly = false;
        MoveView = CollectionViewSource.GetDefaultView(firstRows);
        MoveView.Filter = o => Matches((FileRow)o, ListFilter);
        if (plan is null)
        {
            StayView = null;
            FolderRows = [];
            TypeRows = [];
            Messages = [];
            _stayAttention = 0;
            return;
        }
        var identical = new HashSet<SourceFile>(plan.IdenticalConflicts);
        var stayRows = plan.Staying.Select(f => Row(f, FileNotes.Describe(f.Note), PlanFacts.NeedsLook(f, identical)))
            .OrderByDescending(r => r.Attention).ThenByDescending(r => r.SizeBytes).ToList();
        _stayAttention = stayRows.Count(r => r.Attention);
        StayView = CollectionViewSource.GetDefaultView(stayRows);
        StayView.Filter = o => Matches((FileRow)o, ListFilter);
        FolderRows = plan.ByFolder.Select(s => new FolderRow(s.Folder, s.MovingFiles, s.MovingBytes, Format.Bytes(s.MovingBytes), s.StayingFiles,
            s.Split ? "Split: the files next to them stay" : "")).ToList();
        TypeRows = plan.ByType.Select(t => new TypeRow(t.Extension, t.Classification, t.Moves ? "Moves" : "Stays", t.Files, t.Bytes, Format.Bytes(t.Bytes))).ToList();
        // How files move is shown in its own tile; the list keeps what needs reading. After a job only warnings matter.
        string method = plan.Method == TransferMethod.Rename ? "Same drive (" : "Different drives (";
        // The memory-card warning is next to Confirm instead (PlanFacts.CardLine).
        Messages = PlanFacts.ShownMessages(plan)
            .Where(m => !(m.Level == MessageLevel.Info && (afterRun || m.Text.StartsWith(method, StringComparison.Ordinal))))
            .Select(m => new MessageRow(m.Level switch
            {
                MessageLevel.Error => IconError,
                MessageLevel.Warning => IconWarning,
                _ => IconInfo,
            }, m.Text, m.Level) { IsSplit = Leftovers.IsSplitMessage(m) }).ToList();
        StayTabHeader = $"Files that stay ({plan.Staying.Count:N0})";
    }

    private static FileRow Row(SourceFile f, string note, bool attention) =>
        new(f.Directory, f.Name, PlanFacts.Kind(f), f.Reason, f.Size, Format.Bytes(f.Size), note, attention);

    private bool Matches(FileRow row, string filter) =>
        (!AttentionOnly || row.Attention)
        && (filter.Length == 0 || row.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.Why.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.Status.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void ClearPreview()
    {
        _plan = null;
        _afterRun = false;
        _check = null;
        MoveView = StayView = null;
        FolderRows = [];
        TypeRows = [];
        Messages = [];
        _listFilter = "";
        _attentionOnly = false;
        _stayAttention = 0;
        SuggestedSource = null;
        ListsHeading = "";
        DestinationLine = "";
        DestinationWarnText = "";
        CardLine = "";
        RaiseAll();
    }

    private void InvalidatePlan(bool keepStatus = false)
    {
        // A path box commits after a short delay, so a change can arrive while a job runs: that job and its result stay.
        if (_plan is null && !_afterRun || State is UiState.Running or UiState.Checking or UiState.Verifying or UiState.Removing) return;
        ClearPreview();
        if (State == UiState.Previewed) State = UiState.Idle;
        if (!keepStatus) Status = "Something changed. Check the folder again.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearResult()
    {
        _outcome = null;
        _sortCard = null;
        SetResult(ResultTone.Attention, "", "", [], "", null);
        LastJob = null;
        EmptyFolderCount = 0;
        _emptyFolders = [];
    }

    private void SetResult(ResultTone tone, string title, string leftLine, IReadOnlyList<string> reasons, string detail, string? alarm,
        IReadOnlyList<string>? technical = null)
    {
        ResultTone = tone;
        ResultTitle = title;
        ResultLeftLine = leftLine;
        ResultReasons = reasons;
        ResultDetail = detail;
        ResultAlarm = alarm ?? "";
        ResultTechnical = technical ?? [];
        if (title.Length == 0) ShowMore = false; // closed with the result, kept open while it updates (the check after a job)
        RaiseAll();
    }

    /// <summary>How the files were moved and checked, and where the log and the receipt are: behind "Details".</summary>
    public IReadOnlyList<string> ResultTechnical { get; private set; } = [];
    public bool HasResultTechnical => ResultTechnical.Count > 0;

    private bool _showMore;
    /// <summary>The result's less common actions (check again, the report, remove empty folders), behind "More".</summary>
    public bool ShowMore
    {
        get => _showMore;
        set => Set(ref _showMore, value);
    }

    private void LoadLastJob(string journal)
    {
        LastJob = TryRead(journal);
        _emptyFolders = LastJob is { } job ? EmptyFolders.Find(job) : [];
        EmptyFolderCount = _emptyFolders.Count;
        if ((_check?.MemoryCard ?? _sortCard) is not null) KeepCardFolders();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>After sorting a memory card: its camera folders (DCIM, PRIVATE\M4ROOT, ...) are not offered for removal, even when empty.</summary>
    private void KeepCardFolders()
    {
        if (LastJob is not { IsUndo: false } job) return;
        _emptyFolders = PlanFacts.WithoutCardFolders(_emptyFolders, job.Header.Source);
        EmptyFolderCount = _emptyFolders.Count;
    }

    private static JobState? TryRead(string journal)
    {
        try
        {
            return JournalReader.Read(journal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
            return null;
        }
    }

    private void OpenLog()
    {
        if (LastJob is not { } job) return;
        string summary = JobPaths.SummaryPath(job.JournalPath);
        Shell(File.Exists(summary) ? summary : job.JournalPath);
    }

    private static void OpenInExplorer(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void ShowError(Exception e) => _dialogs.Inform(WpfDialogs.Caption, e.Message, isError: true);

    /// <summary>Called by the window when the user tries to close it during a job.</summary>
    public async Task<bool> TryStopForCloseAsync()
    {
        if (!IsAnyJobRunning) return true;
        if (Backup.IsRunning)
        {
            if (!_dialogs.Confirm(WpfDialogs.Caption,
                    "A backup is in progress. Stop it safely and close the app?\n\nThe app finishes or removes the copy in progress. You can resume the backup later.")) return false;
            await Backup.StopForCloseAsync();
            return true;
        }
        if (!_dialogs.Confirm(WpfDialogs.Caption,
                "A job is in progress. Stop it safely and close the app?\n\nThe app finishes the file in progress or rolls it back. You can resume the job later.")) return false;
        Stop();
        Task? run = _activeRun;
        if (run is not null) await run.ContinueWith(_ => { });
        return true;
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------

    private sealed record Settings(string Source, string Target, bool Videos, bool Verify);

    private const string SettingsFile = "settings.json";

    private static string SettingsPath => Path.Join(RecentJobs.AppDataFolder, SettingsFile);

    private void LoadSettings()
    {
        try
        {
            string path = RecentJobs.AppDataFileToRead(SettingsFile); // an earlier version's settings until the first save
            if (!File.Exists(path)) return;
            Settings? s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            if (s is null) return;
            _sourcePath = s.Source;
            _targetPath = s.Target;
            _browseTargetStart = s.Target;
            _moveVideos = s.Videos;
            _verifyChecksums = s.Verify;
            _lastSuggestedTarget = PlanFacts.SuggestTarget(s.Source, Mode) ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(RecentJobs.AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(SourcePath, TargetPath, MoveVideos, VerifyChecksums)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
