using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using IvarOffload.Core;
using IvarOffload.Core.Backup;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

public enum BackupState { Idle, Scanning, Previewed, Running, Finished, Verifying }

/// <summary>One destination row of the setup: the folder the user picked for it.</summary>
public sealed class DestinationSlot : ObservableObject
{
    private string _path = "";

    public DestinationSlot(BackupViewModel owner, int index)
    {
        Index = index;
        BrowseCommand = new RelayCommand(() => owner.BrowseDestination(this), () => owner.CanEditInputs);
        RemoveCommand = new RelayCommand(() => owner.RemoveDestination(this), () => owner.CanEditInputs && owner.Destinations.Count > 1);
        _owner = owner;
    }

    private readonly BackupViewModel _owner;

    public int Index { get; set; }
    public string Label => $"Backup drive {Index + 1}";

    /// <summary>
    /// What is wrong with this folder, said next to it while the setup is filled in (the preview checks it all again):
    /// the same folder as another drive's, or a drive that is not a separate copy. Empty when nothing is.
    /// </summary>
    public string Problem { get; private set; } = "";
    /// <summary>The problem stops the backup (red); otherwise it is a caution (amber).</summary>
    public bool ProblemIsError { get; private set; }
    public bool HasProblem => Problem.Length > 0;

    internal void SetProblem(string problem, bool isError)
    {
        if (problem == Problem && isError == ProblemIsError) return;
        Problem = problem;
        ProblemIsError = isError;
        Raise(nameof(Problem));
        Raise(nameof(ProblemIsError));
        Raise(nameof(HasProblem));
    }
    public string BoxId => $"BackupDest{Index + 1}Box";
    public string BrowseId => $"BackupBrowseDest{Index + 1}";
    public string RemoveId => $"BackupRemoveDest{Index + 1}";

    public string Path
    {
        get => _path;
        set
        {
            if (Set(ref _path, value)) _owner.DestinationChanged();
        }
    }

    public ICommand BrowseCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>A destination can be removed while there is another one.</summary>
    public bool CanRemove => _owner.Destinations.Count > 1;

    internal void RefreshCanRemove() => Raise(nameof(CanRemove));

    public void Renumber(int index)
    {
        Index = index;
        Raise(nameof(Label));
        Raise(nameof(BoxId));
        Raise(nameof(BrowseId));
        Raise(nameof(RemoveId));
    }
}

/// <summary>A destination in the preview: where the copy goes and how much room there is.</summary>
public sealed record DestinationRow(string Folder, string Drive, string Space);

/// <summary>How a destination is doing while the backup runs.</summary>
public sealed record DestinationProgressRow(string Drive, string Folder, double Fraction, string Text, bool Offline);

/// <summary>A way to make the backup folder's name, in the list above the name: choosing one fills in the name.</summary>
public sealed class NamePreset(string label, string template) : ObservableObject
{
    private string _example = "";

    public string Label => label;
    public string Template => template;

    /// <summary>"260930_SONY_A": what the folder would be called for the card chosen now.</summary>
    public string Example
    {
        get => _example;
        set => Set(ref _example, value);
    }
}

/// <summary>A connected card the user can pick with one click.</summary>
/// <param name="Camera">The camera make the card's folders show ("Sony"); empty when they don't.</param>
public sealed record CardChoice(string Root, string Name, string Camera, string Detail)
{
    public string Id => "BackupCard_" + Root.TrimEnd('\\').TrimEnd(':');
    public bool HasCamera => Camera.Length > 0;
}

/// <summary>
/// The Backup tab: choose a card (or folder) and one to three destinations, preview, then copy and verify. The work is
/// done by <see cref="BackupRunner"/>; this only asks, shows and remembers.
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly Action<string> _sortFolder;
    private readonly Func<bool> _otherJobRunning;
    private string _sourcePath = "";
    private string _name = "";
    private string _template = BackupPlanner.DefaultTemplate;
    private string _templateText = BackupPlanner.DefaultTemplate;
    private bool _showTemplate;
    private Settings _saved = new([], true);
    private bool _reread = true;
    private BackupState _state = BackupState.Idle;
    private BackupPlan? _plan;
    /// <summary>The previewed plans: a new full backup, and (when the card has an earlier backup to add to) the top-up.</summary>
    private BackupPlan? _fullPlan, _topUpPlan;
    private BackupResult? _result;
    private string? _lastJournal;
    private string _status = "Choose what to back up, and where to save the copies.";
    private CancellationTokenSource? _cts;
    private PauseGate? _pause;
    private Task? _activeRun;

    /// <param name="sortFolder">Opens the Sort tab with a folder as its source ("Sort this backup").</param>
    /// <param name="otherJobRunning">A sort (or undo) is running in this window.</param>
    public BackupViewModel(IDialogs dialogs, Action<string> sortFolder, Func<bool> otherJobRunning, bool loadSettings = true)
    {
        _dialogs = dialogs;
        _sortFolder = sortFolder;
        _otherJobRunning = otherJobRunning;
        Destinations.Add(new DestinationSlot(this, 0));
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => CanEditInputs);
        PickCardCommand = new RelayCommand<CardChoice>(c => SourcePath = c.Root, _ => CanEditInputs);
        RefreshCardsCommand = new AsyncCommand(async () => { await RefreshCardsAsync(); await RefreshBannerAsync(); }, () => CanEditInputs, ShowError);
        ChangeSetupCommand = new RelayCommand(ChangeSetup, () => CanEditInputs);
        AddDestinationCommand = new RelayCommand(AddDestination, () => CanEditInputs && Destinations.Count < BackupPlanner.MaxTargets);
        ScanCommand = new AsyncCommand(ScanAsync, () => CanEditInputs && SourcePath.Trim().Length > 0, ShowError);
        StartCommand = new AsyncCommand(StartAsync, () => State == BackupState.Previewed && _plan is { CanRun: true }, ShowError);
        PauseCommand = new RelayCommand(TogglePause, () => State == BackupState.Running && _pause is not null);
        StopCommand = new RelayCommand(Stop, () => State is BackupState.Running or BackupState.Verifying && _cts is { IsCancellationRequested: false });
        ResumeCommand = new AsyncCommand(() => RunExistingAsync(close: false), () => ResumableJob is not null && CanEditInputs, ShowError);
        CloseJobCommand = new AsyncCommand(CloseJobAsync, () => ResumableJob is not null && CanEditInputs, ShowError);
        RecheckCommand = new AsyncCommand(() => RefreshBannerAsync(), () => CanEditInputs, ShowError);
        ForgetCommand = new RelayCommand(Forget, () => OfflineJob is not null && CanEditInputs);
        OpenDestinationCommand = new RelayCommand(() => OpenInExplorer(FirstDestination), () => FirstDestination is not null);
        OpenLogCommand = new RelayCommand(OpenLog, () => _lastJournal is not null);
        VerifyCommand = new AsyncCommand(VerifyAsync, () => _lastJournal is not null && CanEditInputs, ShowError);
        SortThisCommand = new RelayCommand(() => _sortFolder(FirstDestination!), () => FirstDestination is not null && CanEditInputs);
        NewBackupCommand = new RelayCommand(NewBackup, () => CanEditInputs);
        ToggleTemplateCommand = new RelayCommand(() => ShowTemplate = !ShowTemplate, () => CanEditInputs);
        ResetTemplateCommand = new RelayCommand(() => NameTemplate = BackupPlanner.DefaultTemplate, () => CanEditInputs && NameTemplate != BackupPlanner.DefaultTemplate);
        if (loadSettings) LoadSettings();
        RefreshPresets();
        _ = RefreshCardsAsync();
        BannerReady = RefreshBannerAsync();
    }

    /// <summary>The first look for unfinished backups (the window waits for it before choosing the tab to show).</summary>
    public Task BannerReady { get; }

    public ICommand BrowseSourceCommand { get; }
    public ICommand PickCardCommand { get; }
    public ICommand RefreshCardsCommand { get; }
    public ICommand AddDestinationCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CloseJobCommand { get; }
    public ICommand RecheckCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand OpenDestinationCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand SortThisCommand { get; }
    public ICommand NewBackupCommand { get; }
    public ICommand ChangeSetupCommand { get; }
    public ICommand ToggleTemplateCommand { get; }
    public ICommand ResetTemplateCommand { get; }

    // ---- Inputs -----------------------------------------------------------------------------------------------------

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (!Set(ref _sourcePath, value)) return;
            SuggestName();
            SourceInfo = DescribeSource(value);
            Raise(nameof(SourceInfo));
            Raise(nameof(TemplateExample));
            Raise(nameof(ShowNoCard));
            RefreshPresets();
            InputsChanged();
        }
    }

    /// <summary>"F: SONY_A · exFAT · memory card or camera drive" for the chosen source; empty when it is not there.</summary>
    public string SourceInfo { get; private set; } = "";

    /// <summary>
    /// The backup folder's name: always made from the choice in "Made from" (for the card chosen), with the description
    /// added ("260930_MAVIC_KebnekaiseFlight"), and "_2", "_3", ... when a backup drive already has that folder.
    /// </summary>
    public string BackupName => _name;

    private string _description = "";
    /// <summary>What the backup is ("KebnekaiseFlight"), added to the end of the folder name. Optional.</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (Set(ref _description, value)) SuggestName();
        }
    }

    private void SetName(string value)
    {
        if (!Set(ref _name, value, nameof(BackupName))) return;
        InputsChanged();
    }

    public bool Reread
    {
        get => _reread;
        set
        {
            if (Set(ref _reread, value)) InputsChanged();
        }
    }

    public ObservableCollection<DestinationSlot> Destinations { get; } = [];

    public ObservableCollection<CardChoice> Cards { get; } = [];
    public bool HasCards => Cards.Count > 0;
    /// <summary>"Insert a card, or choose a folder": only while nothing is chosen (never under a chosen folder).</summary>
    public bool ShowNoCard => !HasCards && SourcePath.Trim().Length == 0;

    /// <summary>
    /// Right under the folder name, as it will be: the full folder the copy goes into on every backup drive chosen so
    /// far, one per line, with "(backup drive)" and "(card name)" standing in for what is not chosen yet.
    /// </summary>
    public string WhereLine
    {
        get
        {
            string name = BackupName.Length > 0 ? BackupName
                : BackupPlanner.WithDescription(BackupPlanner.ExpandTemplate(_template, DateTime.Now, "(card name)", null), Description);
            var folders = Destinations.Where(d => d.Path.Trim().Length > 0).Select(d => Path.Join(d.Path.Trim().Trim('"'), name)).ToList();
            return folders.Count == 0 ? $@"(backup drive)\{name}" : string.Join("\n", folders);
        }
    }
    public bool HasWhereLine => WhereLine.Length > 0;

    /// <summary>
    /// Flags, next to each backup drive, the same folder chosen twice and a drive that is not a separate copy (the same
    /// drive as the card, or as another backup drive). By the drive letter only: the preview checks it all properly.
    /// </summary>
    private void CheckDestinations()
    {
        string sourceRoot = Root(SourcePath);
        bool sourceIsCard = sourceRoot.Length > 0 && Cards.Any(c => string.Equals(Root(c.Root), sourceRoot, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < Destinations.Count; i++)
        {
            string path = Full(Destinations[i].Path), root = Root(Destinations[i].Path);
            int twice = path.Length == 0 ? -1 : Destinations.Take(i).ToList().FindIndex(d => string.Equals(Full(d.Path), path, StringComparison.OrdinalIgnoreCase));
            int sameDrive = root.Length == 0 ? -1 : Destinations.Take(i).ToList().FindIndex(d => string.Equals(Root(d.Path), root, StringComparison.OrdinalIgnoreCase));
            if (twice >= 0) Destinations[i].SetProblem($"This is the same folder as backup drive {twice + 1}. Choose another drive.", isError: true);
            else if (root.Length > 0 && string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase))
                Destinations[i].SetProblem(sourceIsCard ? "This is on the card itself. Choose a folder on another drive." : "Not a separate copy: this drive also holds what you back up.", isError: sourceIsCard);
            else if (sameDrive >= 0) Destinations[i].SetProblem($"Not a separate copy: this is the same drive as backup drive {sameDrive + 1}.", isError: false);
            else Destinations[i].SetProblem("", isError: false);
        }

        static string Full(string p)
        {
            try
            {
                return p.Trim().Trim('"').Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Trim().Trim('"')));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "";
            }
        }

        static string Root(string p) => Full(p) is { Length: > 0 } f ? Path.GetPathRoot(f)?.TrimEnd('\\') ?? "" : "";
    }

    // ---- Folder name presets ------------------------------------------------------------------------------------------

    private static readonly (string Label, string Template)[] Presets =
    [
        ("Date and card name", BackupPlanner.DefaultTemplate),
        ("Date, camera and card name", "{YYMMDD}_{camera}_{card}"),
        ("Card name and date", "{card}_{YYMMDD}"),
        ("Date, time and card name", "{YYMMDD_HHMM}_{card}"),
    ];
    private const int BuiltInPresets = 4;
    /// <summary>One list for the window's life (a new list would lose the dropdown's selection); only examples change.</summary>
    private readonly ObservableCollection<NamePreset> _presets = new(Presets.Select(p => new NamePreset(p.Label, p.Template)));

    /// <summary>
    /// The ways to make the folder name, in the list above it, each with an example for the card chosen; and the
    /// pattern of your own when one is in use.
    /// </summary>
    public IReadOnlyList<NamePreset> NamePresets => _presets;

    /// <summary>The way the name is made now; choosing another fills in the name box with it (and is remembered).</summary>
    public NamePreset? SelectedPreset
    {
        get => _presets.FirstOrDefault(p => p.Template == _template);
        set
        {
            if (value is not null) UsePreset(value.Template);
        }
    }

    private void RefreshPresets()
    {
        // The pattern of your own is the last entry while it is in use.
        bool custom = Presets.All(p => p.Template != _template);
        if (_presets.Count > BuiltInPresets && (!custom || _presets[BuiltInPresets].Template != _template)) _presets.RemoveAt(BuiltInPresets);
        if (custom && _presets.Count == BuiltInPresets) _presets.Add(new NamePreset("Your own pattern", _template));
        foreach (NamePreset p in _presets) p.Example = ExampleOf(p.Template);
        Raise(nameof(SelectedPreset));
    }

    /// <summary>An example of a pattern's name for the card chosen now (or a sample card).</summary>
    private string ExampleOf(string template) => SourcePath.Trim().Length > 0
        ? BackupPlanner.DefaultName(SourcePath, DateTime.Now, template: template)
        : BackupPlanner.ExpandTemplate(template, DateTime.Now, "SONY_A", "Sony");

    /// <summary>Makes the name from <paramref name="template"/> from now on (and remembers it).</summary>
    private void UsePreset(string template)
    {
        if (CanEditInputs && template != _template) NameTemplate = template;
    }

    /// <summary>
    /// The folder name: made from the pattern for the card chosen, with the description added, and "_2", "_3", ... when
    /// that folder already holds something on a destination (the second card of a camera that labels every card alike).
    /// </summary>
    private void SuggestName()
    {
        SetName(SourcePath.Trim().Length > 0 ? BackupPlanner.DefaultName(SourcePath, DateTime.Now, Destinations.Select(d => d.Path), _template, Description) : "");
        Raise(nameof(WhereLine));
        Raise(nameof(HasWhereLine));
    }

    // ---- The folder name pattern ---------------------------------------------------------------------------------------

    /// <summary>
    /// The pattern the folder name is made from ({YYMMDD}_{card} unless the user sets another), as typed. A valid pattern
    /// is used and remembered at once, and the name follows it again (also when the user had typed a name of their own).
    /// </summary>
    public string NameTemplate
    {
        get => _templateText;
        set
        {
            if (!Set(ref _templateText, value)) return;
            TemplateError = BackupPlanner.ValidateTemplate(value.Trim()) ?? "";
            if (TemplateError.Length == 0 && value.Trim() != _template)
            {
                _template = value.Trim();
                SaveTemplate();
                SuggestName();
            }
            foreach (string p in new[] { nameof(TemplateError), nameof(HasTemplateError), nameof(TemplateExample) }) Raise(p);
            RefreshPresets();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string TemplateError { get; private set; } = "";
    public bool HasTemplateError => TemplateError.Length > 0;

    /// <summary>
    /// "For example: 260928_Sony_SONY_A" (for the card chosen, or a sample card) with the pattern in use. Only while
    /// the pattern is shown: it may look at the card.
    /// </summary>
    public string TemplateExample => !ShowTemplate ? "" : "For example: " + (SourcePath.Trim().Length > 0
        ? BackupPlanner.DefaultName(SourcePath, DateTime.Now, template: _template)
        : BackupPlanner.ExpandTemplate(_template, DateTime.Now, "SONY_A", "Sony"));

    /// <summary>What each part of a pattern becomes, one per line.</summary>
    public string TemplateHelp { get; } = string.Join("\n", BackupPlanner.TemplateTokens.Select(t => $"{t.Token}   {t.Meaning}"));


    public bool ShowTemplate
    {
        get => _showTemplate;
        set
        {
            if (Set(ref _showTemplate, value)) Raise(nameof(TemplateExample));
        }
    }

    internal void DestinationChanged()
    {
        SuggestName();
        InputsChanged();
    }

    internal void InputsChanged()
    {
        Raise(nameof(WhereLine));
        Raise(nameof(HasWhereLine));
        CheckDestinations();
        if (State is BackupState.Previewed or BackupState.Finished && _plan is not null) ClearPlan("Something changed. Check the card again.");
        else if (State == BackupState.Idle && !HasResult) Status = SetupStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The line at the bottom while the setup is filled in: what is still missing, then what the button does.</summary>
    private string SetupStatus() =>
        SourcePath.Trim().Length == 0 ? "Choose what to back up, and where to save the copies."
        : !Destinations.Any(d => d.Path.Trim().Length > 0) ? "Now choose where to save the copies."
        : "Click “Check card” to see what the backup will copy. The app copies nothing until you start the backup.";

    private void BrowseSource()
    {
        string? picked = _dialogs.PickFolder("Choose the card or folder to back up", SourcePath.Length > 0 ? SourcePath : null);
        if (picked is not null) SourcePath = picked;
    }

    internal void BrowseDestination(DestinationSlot slot)
    {
        string? picked = _dialogs.PickFolder($"Choose the folder for {slot.Label.ToLowerInvariant()} (the app makes the backup folder in it)",
            slot.Path.Length > 0 ? slot.Path : null);
        if (picked is not null) slot.Path = picked;
    }

    private void AddDestination()
    {
        if (Destinations.Count >= BackupPlanner.MaxTargets) return;
        Destinations.Add(new DestinationSlot(this, Destinations.Count));
        RaiseDestinations();
    }

    internal void RemoveDestination(DestinationSlot slot)
    {
        if (Destinations.Count <= 1) return;
        Destinations.Remove(slot);
        for (int i = 0; i < Destinations.Count; i++) Destinations[i].Renumber(i);
        RaiseDestinations();
        DestinationChanged();
    }

    private void RaiseDestinations()
    {
        foreach (DestinationSlot d in Destinations) d.RefreshCanRemove();
        Raise(nameof(CanAddDestination));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool CanAddDestination => Destinations.Count < BackupPlanner.MaxTargets;

    private string DescribeSource(string source)
    {
        try
        {
            string full = Path.GetFullPath(source.Trim().Trim('"'));
            if (!Directory.Exists(full)) return "";
            VolumeInfo v = VolumeInfo.Of(full);
            bool card = SourceGuards.MemoryCardNote(full) is not null;
            // A card from the list: its make was read off the window's thread when the list was made.
            CardChoice? listed = Cards.FirstOrDefault(c => string.Equals(c.Root, v.Root, StringComparison.OrdinalIgnoreCase));
            string? camera = !card ? null : listed is not null ? listed.Camera is { Length: > 0 } make ? make : null : CardCamera.OfDrive(full);
            return $"{SourceGuards.DriveName(v)} · {v.FileSystem}{(card ? camera is null ? " · memory card or camera drive" : $" · {camera} card" : "")}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    private async Task RefreshCardsAsync()
    {
        List<ConnectedCard> found = await Task.Run(BackupPlanner.ConnectedCards);
        Cards.Clear();
        foreach (ConnectedCard c in found)
            Cards.Add(new CardChoice(c.Root, c.Name, c.Camera ?? "",
                $"{(c.Camera is null ? "" : c.Camera + " card · ")}{c.FileSystem} · {Format.Bytes(c.UsedBytes)} used of {Format.Bytes(c.TotalBytes)}"));
        Raise(nameof(HasCards));
        Raise(nameof(ShowNoCard));
        CheckDestinations();
        // With exactly one card connected and nothing chosen yet, it is the obvious choice.
        if (Cards.Count == 1 && SourcePath.Length == 0 && State == BackupState.Idle) SourcePath = Cards[0].Root;
    }

    /// <summary>
    /// Called from the Sort tab's memory-card question: this card, ready to back up, in a fresh setup (never under the
    /// result of an earlier backup). False when a backup is running, so nothing could be changed.
    /// </summary>
    public bool UseCard(string folder)
    {
        if (!CanEditInputs) return false;
        if (State == BackupState.Finished || HasResult) NewBackup();
        string root = Path.GetPathRoot(folder) is { Length: > 0 } r && SourceGuards.MemoryCardNote(r) is not null ? r : folder;
        SourcePath = root;
        Status = JobTexts.MemoryCardNotSorted;
        return true;
    }

    // ---- State --------------------------------------------------------------------------------------------------------

    public BackupState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            foreach (string p in new[] { nameof(CanEditInputs), nameof(IsBusy), nameof(IsRunning), nameof(ShowPause), nameof(ShowStop), nameof(ShowStart),
                         nameof(ShowPreview), nameof(ShowProgress), nameof(IsSetupFull), nameof(IsSetupCompact), nameof(SummaryVerb),
                         nameof(ShowChangeSetup), nameof(HasTopUpOffer) })
                Raise(p);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditInputs => State is BackupState.Idle or BackupState.Previewed or BackupState.Finished;
    public bool IsBusy => State is BackupState.Scanning or BackupState.Running or BackupState.Verifying;
    public bool IsRunning => State is BackupState.Running or BackupState.Verifying;
    public bool ShowPause => State == BackupState.Running;
    public bool ShowStop => State is BackupState.Running or BackupState.Verifying;
    public bool ShowStart => State == BackupState.Previewed && _plan is not null;
    public bool ShowPreview => _plan is not null && State is BackupState.Previewed;
    public bool ShowProgress => State == BackupState.Running;
    /// <summary>After a scan, and while and after a backup runs, the setup is one line (so the warnings and the result have room).</summary>
    public bool IsSetupCompact => !_setupExpanded && (State is BackupState.Previewed or BackupState.Running or BackupState.Verifying || (State == BackupState.Finished && HasResult));
    public bool IsSetupFull => !IsSetupCompact;
    public bool ShowChangeSetup => State == BackupState.Previewed;
    /// <summary>The start of the one-line setup summary.</summary>
    public string SummaryVerb => State == BackupState.Previewed ? _plan?.IsTopUp == true ? "Add the new files of" : "Back up"
        : State == BackupState.Finished && _result?.Status is RunStatus.Completed ? "Backed up"
        : "Backup of";
    /// <summary>The one-line summary: what the preview or the backup was made for (not what is typed in the boxes now).</summary>
    public string SummarySource { get; private set; } = "";
    public string SummaryWhere { get; private set; } = "";
    private bool _setupExpanded;

    private void ChangeSetup()
    {
        _setupExpanded = true;
        Raise(nameof(IsSetupCompact));
        Raise(nameof(IsSetupFull));
    }

    private void SetSummary(string source, IEnumerable<string> folders)
    {
        SummarySource = source;
        SummaryWhere = string.Join("   ·   ", folders);
        Raise(nameof(SummarySource));
        Raise(nameof(SummaryWhere));
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    // ---- Preview -------------------------------------------------------------------------------------------------------

    public string FilesCount { get; private set; } = "";
    public string FilesSize { get; private set; } = "";
    public string DestinationsCount { get; private set; } = "";
    public string DestinationsDetail { get; private set; } = "";
    public string EstimateText { get; private set; } = "";
    public string StartText { get; private set; } = "Back up";
    public IReadOnlyList<DestinationRow> DestinationRows { get; private set; } = [];
    public IReadOnlyList<MessageRow> Alerts { get; private set; } = [];
    public IReadOnlyList<MessageRow> Notes { get; private set; } = [];
    public bool HasAlerts => Alerts.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    /// <summary>The line above Start: where the copies go, never scrolled away.</summary>
    public string StartLine { get; private set; } = "";
    /// <summary>Next to Start, in the caution colour: the copies will not all be on drives of their own.</summary>
    public string StartWarn { get; private set; } = "";
    public bool HasStartWarn => StartWarn.Length > 0;

    private async Task ScanAsync()
    {
        string source = SourcePath.Trim().Trim('"');
        var parents = Destinations.Select(d => d.Path.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        string name = BackupName.Trim();
        bool reread = Reread;
        ClearResult();
        ClearPlan(null);
        _setupExpanded = false;
        State = BackupState.Scanning;
        Status = "Card check in progress...";
        var progress = new Progress<ScanProgress>(p => Status = $"Card check in progress: {p.Files:N0} files in {p.Folders:N0} folders");
        try
        {
            (BackupPlan plan, BackupPlan? topUp) = await Task.Run(() =>
            {
                BackupScan scan = BackupScanner.Scan(source, progress);
                BackupPlan full = BackupPlanner.Build(scan, parents, name, reread);
                // An earlier backup of this card on every destination: adding to it is offered, and chosen unless the user says otherwise.
                return (full, full.TopUp is null ? null : BackupPlanner.Build(scan, parents, name, reread, topUp: true));
            });
            // A box that was still committing its text when Scan was pressed: this preview is not for what is shown now.
            if (source != SourcePath.Trim().Trim('"') || name != BackupName.Trim() || reread != Reread
                || !parents.SequenceEqual(Destinations.Select(d => d.Path.Trim().Trim('"')).Where(p => p.Length > 0)))
            {
                State = BackupState.Idle;
                Status = "Something changed during the check. Check the card again.";
                return;
            }
            _fullPlan = plan;
            _topUpPlan = topUp;
            // Adding to the earlier backup is chosen unless it can't run (then the full backup is shown, and the choice stays).
            _useTopUp = topUp is { CanRun: true } || topUp is not null && !plan.CanRun;
            plan = _useTopUp ? topUp! : plan;
            ShowPlan(plan);
            State = BackupState.Previewed;
            if (plan.UnfinishedJournals.Count > 0)
            {
                // An unfinished backup in a chosen destination (started on another PC, or forgotten): offer to resume it.
                await Task.Run(() => RememberFound(plan.UnfinishedJournals));
                await RefreshBannerAsync();
            }
            Status = PreviewStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            State = BackupState.Idle;
            Status = "The app could not check the card: " + e.Message;
        }
    }

    private void ShowPlan(BackupPlan plan)
    {
        _plan = plan;
        if (plan.IsTopUp)
        {
            // What is written: the files each folder does not have as the card has them (the most of any destination).
            var toCopy = plan.Targets.Select(t => t.Earlier!.ToCopy).MaxBy(l => l.Sum(f => f.Size)) ?? [];
            CopyTileTitle = "WILL ADD";
            FilesCount = RunOutcome.Count(toCopy.Count, "file");
            FilesSize = $"{Format.Bytes(toCopy.Sum(f => f.Size))}, then a check of all {RunOutcome.Count(plan.Files.Count, "file")} on the card";
        }
        else
        {
            CopyTileTitle = "WILL COPY";
            FilesCount = RunOutcome.Count(plan.Files.Count, "file");
            FilesSize = $"{Format.Bytes(plan.Bytes)} in {RunOutcome.Count(plan.Scan.Folders.Count, "folder")}" + (plan.IsCard ? " · memory card" : "");
        }
        DestinationsCount = RunOutcome.Count(plan.Targets.Count, "drive");
        DestinationsDetail = plan.Targets.Count == 0 ? "none chosen" : string.Join(", ", plan.Targets.Select(DriveOf));
        EstimateText = BackupTexts.Estimate(plan.Estimate());
        StartText = plan.IsTopUp
            ? BackupTexts.TopUpStartButton(plan.Targets.Max(t => t.Earlier!.ToCopy.Count), plan.Files.Count)
            : BackupTexts.StartButton(plan.Files.Count, plan.Targets.Count);
        DestinationRows = plan.Targets.Select(t => new DestinationRow(t.Folder, DriveOf(t), t.Volume is { } v ? $"{Format.Bytes(v.FreeBytes)} free" : "")).ToList();
        // Copies that are not on drives of their own: one line of their own instead of a warning per destination.
        var alerts = plan.Messages.Where(m => m.Level != MessageLevel.Info && !BackupTexts.IsSameDriveWarning(m)).Select(ToRow).ToList();
        if (BackupTexts.SameDriveAlert(plan) is { } sameDrive) alerts.Insert(alerts.Count(a => a.Level == MessageLevel.Error), ToRow(new PlanMessage(MessageLevel.Warning, sameDrive)));
        Alerts = alerts;
        // How the copies are checked, then the planner's notes: behind "Details".
        Notes = plan.Messages.Where(m => m.Level == MessageLevel.Info).Select(ToRow)
            .Prepend(ToRow(new PlanMessage(MessageLevel.Info, (plan.Reread ? "The app reads the card twice. " : "")
                + "After the app writes each copy, it reads the copy from its own drive and compares it with the card by SHA-256. "
                + "It also writes ASC MHL checksum files next to the copies, for other tools.")))
            .ToList();
        StartLine = plan.Targets.Count == 0 ? ""
            : (plan.IsTopUp ? "Will add to: " : "Saved as: ") + string.Join("  ·  ", plan.Targets.Select(t => t.Folder));
        StartWarn = plan.SameDriveNote ?? "";
        TopUpText = _fullPlan?.TopUp is { } offer ? offer.Describe(adding: plan.IsTopUp, plan.Files.Count) : "";
        SetSummary(plan.SourceName, plan.Targets.Select(t => t.Folder));
        RaisePreview();
    }

    /// <summary>The title of the first preview tile: what is written.</summary>
    public string CopyTileTitle { get; private set; } = "WILL COPY";

    /// <summary>The card has an earlier backup on every destination that its new files can be added to: the preview offers the choice.</summary>
    public bool HasTopUpOffer => _topUpPlan is not null && ShowPreview;

    /// <summary>What the earlier backup holds and what adding to it does (or what a new full backup would not use).</summary>
    public string TopUpText { get; private set; } = "";

    private bool _useTopUp;

    /// <summary>Add the card's new files to its earlier backup and verify the whole card (the default when it is offered).</summary>
    public bool UseTopUp
    {
        get => _useTopUp;
        set => ChooseTopUp(value);
    }

    /// <summary>Make a new full backup instead of adding to the earlier one.</summary>
    public bool UseFullBackup
    {
        get => !_useTopUp;
        set => ChooseTopUp(!value);
    }

    private void ChooseTopUp(bool topUp)
    {
        if (_topUpPlan is null || _fullPlan is null || State != BackupState.Previewed || topUp == _useTopUp) return;
        _useTopUp = topUp;
        ShowPlan(topUp ? _topUpPlan : _fullPlan);
        Raise(nameof(UseTopUp));
        Raise(nameof(UseFullBackup));
        Status = PreviewStatus();
    }

    /// <summary>The line at the bottom after a scan.</summary>
    private string PreviewStatus() =>
        _plan is not { CanRun: true } ? "The backup cannot start. For the reason, see the messages above."
        : Alerts.Count == 0 ? "Ready. The app did not copy any files yet."
        : $"Read the {(Alerts.Count == 1 ? "warning" : $"{Alerts.Count} warnings")} above before you start. The app did not copy any files yet.";

    private static string DriveOf(BackupTarget t) => t.Volume is { } v ? BackupTexts.DriveText(v.Root, v.Label) : t.DriveName;

    private static MessageRow ToRow(PlanMessage m) => new(m.Level switch
    {
        MessageLevel.Error => "\uEA39",
        MessageLevel.Warning => "\uE7BA",
        _ => "\uE946",
    }, m.Text, m.Level);

    private void ClearPlan(string? status)
    {
        if (_plan is null) return;
        _plan = _fullPlan = _topUpPlan = null;
        TopUpText = "";
        Alerts = Notes = [];
        DestinationRows = [];
        StartLine = StartWarn = "";
        if (State == BackupState.Previewed) State = BackupState.Idle;
        if (status is not null) Status = status;
        RaisePreview();
    }

    private void RaisePreview()
    {
        foreach (string p in new[] { nameof(FilesCount), nameof(FilesSize), nameof(DestinationsCount), nameof(DestinationsDetail), nameof(EstimateText),
                     nameof(StartText), nameof(DestinationRows), nameof(Alerts), nameof(Notes), nameof(HasAlerts), nameof(HasNotes),
                     nameof(StartLine), nameof(StartWarn), nameof(HasStartWarn), nameof(ShowPreview), nameof(ShowStart),
                     nameof(IsSetupCompact), nameof(IsSetupFull), nameof(CopyTileTitle), nameof(HasTopUpOffer), nameof(TopUpText),
                     nameof(UseTopUp), nameof(UseFullBackup), nameof(SummaryVerb) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Running ----------------------------------------------------------------------------------------------------

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private string _progressLeft = "", _progressRight = "", _progressFile = "";
    public string ProgressLeft { get => _progressLeft; private set => Set(ref _progressLeft, value); }
    public string ProgressRight { get => _progressRight; private set => Set(ref _progressRight, value); }
    public string ProgressFile { get => _progressFile; private set => Set(ref _progressFile, value); }
    public IReadOnlyList<DestinationProgressRow> DestinationProgress { get; private set; } = [];
    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (Set(ref _isPaused, value)) Raise(nameof(PauseText));
        }
    }
    public string PauseText => IsPaused ? "Continue" : "Pause";

    private async Task StartAsync()
    {
        if (_plan is not { CanRun: true } plan) return;
        if (_otherJobRunning())
        {
            _dialogs.Inform(WpfDialogs.Caption, "A sort is in progress in this window. Wait until it finishes, or stop it. Then start the backup.");
            return;
        }
        ClearResult();
        SaveSettings(); // the destinations are remembered once a backup really goes to them
        await RunAsync(options => BackupRunner.Start(plan, options), (runner, ct) => runner.Run(ct), "Backup in progress");
    }

    private async Task RunExistingAsync(bool close)
    {
        if (ResumableJob is not { } job) return;
        if (_otherJobRunning())
        {
            _dialogs.Inform(WpfDialogs.Caption, "A sort is in progress in this window. Before you continue, wait until it finishes, or stop it.");
            return;
        }
        ClearPlan(null);
        ClearResult();
        SetSummary(job.Header.SourceLabel, job.Header.Targets);
        await RunAsync(options => BackupRunner.Open(job.JournalPath, options), (runner, ct) => close ? runner.Close(ct) : runner.Run(ct),
            close ? "The app ends the backup" : "The backup continues");
    }

    private async Task CloseJobAsync()
    {
        if (!_dialogs.Confirm("Stop here",
                "Stop this backup here? The app will not copy the rest of the files.\n\nThe app keeps the files that it copied and checked. It deletes nothing. "
                + "The rest of the files are not in the backup, so do not format the card. Later, you can back up the card again into a new folder.\n\n"
                + "You do not need to connect the card for this."))
            return;
        await RunExistingAsync(close: true);
    }

    private async Task RunAsync(Func<BackupOptions, BackupRunner> open, Func<BackupRunner, CancellationToken, BackupResult> run, string verb)
    {
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _pause = new PauseGate();
        // Faults: test hooks, enabled only by environment variables a test script sets (as in the command line).
        var options = new BackupOptions { Progress = new Progress<BackupProgress>(UpdateProgress), Pause = _pause, Faults = TestFaults.FromEnvironment() };
        IsPaused = false;
        Progress = 0;
        ProgressLeft = ProgressRight = ProgressFile = "";
        DestinationProgress = [];
        Raise(nameof(DestinationProgress));
        State = BackupState.Running;
        Status = verb + "...";
        BackupResult? result = null;
        string? journal = null;
        try
        {
            Task<BackupResult> task = Task.Run(() =>
            {
                using BackupRunner runner = open(options);
                journal = runner.Journals[0];
                return run(runner, ct);
            });
            _activeRun = task;
            result = await task;
        }
        catch (Exception e)
        {
            // Whatever went wrong, the result card says so (never a crash or a stuck window): the logs hold every finished copy.
            SetResult(new ResultView(ResultTone.Attention, "The backup could not continue", "", [],
                e.Message + (journal is null ? "" : "\n\nThe job log records every finished copy, so you lose nothing. You can resume the backup."),
                BackupTexts.KeepTheCard, "The backup could not continue. For the reason, see the message above."));
        }
        finally
        {
            _activeRun = null;
            _pause = null;
            IsPaused = false;
        }
        if (journal is not null) _lastJournal = journal;
        if (result is not null)
        {
            _lastJournal = result.JournalPath;
            _result = result;
            SetResult(BackupTexts.Describe(result));
        }
        await RefreshBannerAsync();
        State = BackupState.Finished;
        Raise(nameof(FirstDestination));
        Raise(nameof(CanSortThis));
        Raise(nameof(CanSortThisNow));
        Raise(nameof(CanSortThisLater));
    }

    private void UpdateProgress(BackupProgress p)
    {
        Progress = p.Fraction;
        ProgressLeft = $"{p.ItemsFinished:N0} of {p.ItemsTotal:N0} files";
        ProgressRight = p.BytesPerSecond > 0 ? $"{Format.Rate(p.BytesPerSecond)}{(p.Remaining is { } r ? $"  ·  {Format.Duration(r)} left" : "")}" : "";
        ProgressFile = p.CurrentFile is null ? p.Phase : $"{p.Phase}: {p.CurrentFile}";
        DestinationProgress = p.Destinations.Select(d => new DestinationProgressRow(d.DriveName, d.Folder,
            p.ItemsTotal == 0 ? 0 : (double)d.Verified / p.ItemsTotal,
            d.Offline ?? $"{d.Verified:N0} of {p.ItemsTotal:N0} checked{(d.Failed > 0 ? $", {d.Failed:N0} failed" : "")}", d.Offline is not null)).ToList();
        Raise(nameof(DestinationProgress));
    }

    private void TogglePause()
    {
        if (_pause is null) return;
        if (_pause.IsPaused)
        {
            _pause.Resume();
            IsPaused = false;
            Status = "Backup in progress...";
        }
        else
        {
            _pause.Pause();
            IsPaused = true;
            Status = "Paused. Do not disconnect the card or the drives while the backup is paused. If you must disconnect them, click Stop first.";
        }
    }

    private void Stop()
    {
        _pause?.Resume();
        IsPaused = false;
        _cts?.Cancel();
        Status = State == BackupState.Verifying ? "The check stops safely after this step..." : "The backup stops safely after this step...";
    }

    /// <summary>Called by the window when it is closed during a backup.</summary>
    public async Task StopForCloseAsync()
    {
        Stop();
        if (_activeRun is { } run) await run.ContinueWith(_ => { });
    }

    // ---- Result ----------------------------------------------------------------------------------------------------------

    public ResultTone ResultTone { get; private set; }
    public bool ResultIsGood => HasResult && ResultTone == ResultTone.Good;
    public bool ResultIsAttention => HasResult && ResultTone == ResultTone.Attention;
    public string ResultTitle { get; private set; } = "";
    public string ResultLeftLine { get; private set; } = "";
    public IReadOnlyList<string> ResultReasons { get; private set; } = [];
    public string ResultDetail { get; private set; } = "";
    public string ResultAlarm { get; private set; } = "";
    /// <summary>Checksums, checksum files and messages as the system gave them: behind "Details".</summary>
    public IReadOnlyList<string> ResultTechnical { get; private set; } = [];
    public bool HasResult => ResultTitle.Length > 0;
    public bool HasResultReasons => ResultReasons.Count > 0;
    public bool HasResultTechnical => ResultTechnical.Count > 0;

    private bool _showMore;
    /// <summary>The result's less common actions (check again, the report, and "Sort this backup" when it is not green), behind "More".</summary>
    public bool ShowMore
    {
        get => _showMore;
        set => Set(ref _showMore, value);
    }

    /// <summary>"Sort this backup" among the main actions: only after a green backup (sorting changes the backup).</summary>
    public bool CanSortThisNow => CanSortThis && BackupWasGreen;
    /// <summary>"Sort this backup" under "More": after a backup that is not green.</summary>
    public bool CanSortThisLater => CanSortThis && !BackupWasGreen;
    /// <summary>The last backup (not a later "check again") was green: every file checked, every copy on a drive of its own.</summary>
    private bool BackupWasGreen => _result is { AllVerified: true } r && r.SeparateDrives >= r.Destinations.Count;

    /// <summary>The backup folder on the first destination of the last backup, when it is there.</summary>
    public string? FirstDestination => _lastJournal is { } j && JobPaths.RootOfJournal(j) is { } root && Directory.Exists(root) ? root : null;

    /// <summary>"Sort this backup" is offered once the last backup has a complete verified copy.</summary>
    public bool CanSortThis => FirstDestination is not null && _result is { AllVerified: true };

    private void SetResult(ResultView view)
    {
        ResultTone = view.Tone;
        ResultTitle = view.Title;
        ResultLeftLine = view.LeftLine;
        ResultReasons = view.Reasons;
        ResultDetail = view.Detail;
        ResultAlarm = view.Alarm ?? "";
        ResultTechnical = view.Technical;
        Status = view.Status;
        RaiseResult();
    }

    private void ClearResult()
    {
        ResultTitle = ResultLeftLine = ResultDetail = ResultAlarm = "";
        ResultReasons = ResultTechnical = [];
        _result = null;
        ShowMore = false;
        RaiseResult();
    }

    private void RaiseResult()
    {
        foreach (string p in new[] { nameof(ResultTone), nameof(ResultIsGood), nameof(ResultIsAttention), nameof(ResultTitle), nameof(ResultLeftLine),
                     nameof(ResultReasons), nameof(SummaryVerb), nameof(ResultDetail), nameof(ResultAlarm), nameof(HasResult), nameof(HasResultReasons),
                     nameof(IsSetupCompact), nameof(IsSetupFull), nameof(FirstDestination), nameof(CanSortThis), nameof(CanSortThisNow),
                     nameof(CanSortThisLater), nameof(ResultTechnical), nameof(HasResultTechnical) })
            Raise(p);
    }

    private async Task VerifyAsync()
    {
        if (_lastJournal is not { } journal) return;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        State = BackupState.Verifying;
        Status = "Check of the copies in progress...";
        var progress = new Progress<(int Done, int Total, string File)>(p => Status = $"Check of the copies in progress: {p.Done:N0} of {p.Total:N0}");
        try
        {
            BackupVerifyResult v = await Task.Run(() => BackupVerifier.Verify(journal, progress, ct));
            SetResult(BackupTexts.DescribeVerify(v));
        }
        catch (OperationCanceledException)
        {
            Status = "The check of the copies stopped.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
            SetResult(new ResultView(ResultTone.Attention, "The app could not check the copies again", "", [], e.Message, BackupTexts.KeepTheCard,
                "The app could not check the copies again. For the reason, see above."));
        }
        State = BackupState.Finished;
    }

    private void NewBackup()
    {
        ClearResult();
        ClearPlan(null);
        _lastJournal = null;
        State = BackupState.Idle;
        // The next card: a new description (its name is made again, with "_2" when this one's folder is taken).
        _description = "";
        Raise(nameof(Description));
        SuggestName();
        Status = SetupStatus();
        _ = RefreshCardsAsync();
    }

    private void OpenLog()
    {
        if (_lastJournal is null) return;
        string summary = BackupPaths.SummaryPath(_lastJournal);
        string file = File.Exists(summary) ? summary : _lastJournal;
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static void OpenInExplorer(string? folder)
    {
        if (folder is not null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ---- Unfinished backups -------------------------------------------------------------------------------------------

    /// <summary>An unfinished backup with at least one log that can be reached (the one Resume continues).</summary>
    public JobState? ResumableJob { get; private set; }
    /// <summary>An unfinished backup none of whose destinations is connected.</summary>
    public RecentBackup? OfflineJob { get; private set; }
    public bool HasBanner => ResumableJob is not null || OfflineJob is not null;
    public bool BannerOffline => ResumableJob is null && OfflineJob is not null;
    public string BannerTitle { get; private set; } = "";
    public string BannerText { get; private set; } = "";

    /// <summary>Looks for unfinished backups (on startup, after a run, and on "Check again").</summary>
    public async Task RefreshBannerAsync()
    {
        (JobState? job, List<JobState> siblings, RecentBackup? offline, int more) = await Task.Run(FindUnfinished);
        ResumableJob = job;
        OfflineJob = job is null ? offline : null;
        if (job is not null) (BannerTitle, BannerText) = BackupTexts.Banner(job, siblings, more);
        else if (offline is not null)
        {
            BannerTitle = BackupTexts.OfflineBanner(offline);
            BannerText = "“Forget this backup” only removes it from this list. Its job logs stay on the backup drives.";
        }
        else BannerTitle = BannerText = "";
        foreach (string p in new[] { nameof(ResumableJob), nameof(OfflineJob), nameof(HasBanner), nameof(BannerOffline), nameof(BannerTitle), nameof(BannerText), nameof(ShowStart) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    private static (JobState?, List<JobState>, RecentBackup?, int) FindUnfinished()
    {
        RecentBackup? offline = null;
        var resumable = new List<(JobState Job, List<JobState> Siblings)>();
        foreach (RecentBackup b in RecentBackups.Unfinished())
        {
            var states = new List<JobState>();
            foreach (string journal in b.Connected)
            {
                try
                {
                    states.Add(JournalReader.Read(journal));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
                {
                }
            }
            if (states.Count > 0 && states.All(s => s.IsEnded) && states.Count == b.Journals.Count)
            {
                // Finished meanwhile (e.g. from the command line): remember that.
                RecentBackups.Remember(b.JobId, b.Journals, b.Source, b.SourceName, b.Created, ended: true);
                continue;
            }
            JobState? open = states.FirstOrDefault(s => !s.IsEnded);
            if (open is not null) resumable.Add((open, states));
            else offline ??= b; // every copy that can be reached is finished; the one on a drive that is not connected is not
        }
        return resumable.Count > 0 ? (resumable[0].Job, resumable[0].Siblings, null, resumable.Count - 1) : (null, [], offline, 0);
    }

    /// <summary>Remembers unfinished backups whose logs were found in a destination folder, so the banner offers them.</summary>
    private static void RememberFound(IEnumerable<string> journals)
    {
        foreach (string journal in journals)
        {
            try
            {
                JobHeader h = JournalReader.Read(journal).Header;
                IReadOnlyList<string> targets = h.Targets.Count > 0 ? h.Targets : [h.Target];
                var logs = targets.Select((t, k) => BackupPaths.JournalPath(BackupPaths.FindDestination(t, h.TargetSerials.ElementAtOrDefault(k), h.Id), h.Id)).ToList();
                // The log that was found is where it is now (its drive may have another letter than when it was recorded).
                int here = logs.FindIndex(l => JobPaths.SamePath(l, journal));
                if (here < 0) logs.Add(journal);
                RecentBackups.Remember(h.Id, logs, h.Source, h.SourceLabel, h.Created, ended: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
            {
            }
        }
    }

    private void Forget()
    {
        if (OfflineJob is not { } b) return;
        RecentBackups.Forget(b.JobId);
        _ = RefreshBannerAsync();
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------------

    private sealed record Settings(List<string> Destinations, bool Reread, string? NameTemplate = null);

    private const string SettingsFile = "backup-settings.json";

    private void LoadSettings()
    {
        try
        {
            string path = Path.Join(RecentJobs.AppDataFolder, SettingsFile);
            if (!File.Exists(path) || JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) is not { } s) return;
            _saved = s with { Destinations = s.Destinations ?? [] };
            _reread = s.Reread;
            if (s.NameTemplate is { } stored && BackupPlanner.UpgradeTemplate(stored, saved: true) is var t && BackupPlanner.ValidateTemplate(t) is null) _template = _templateText = t;
            // Kept even when a drive is not connected now: the preview then says so, and the user decides (never a silent
            // drop from two copies to one).
            var existing = _saved.Destinations.Where(p => p.Trim().Length > 0).Take(BackupPlanner.MaxTargets).ToList();
            for (int i = 0; i < existing.Count; i++)
            {
                if (i >= Destinations.Count) Destinations.Add(new DestinationSlot(this, i));
                Destinations[i].Path = existing[i];
            }
            RaiseDestinations();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void SaveSettings() => WriteSettings(new Settings(Destinations.Select(d => d.Path.Trim()).Where(p => p.Length > 0).ToList(), Reread, _template));

    /// <summary>A new pattern is remembered at once; the destinations only once a backup really goes to them.</summary>
    private void SaveTemplate() => WriteSettings(_saved with { NameTemplate = _template });

    private void WriteSettings(Settings settings)
    {
        _saved = settings;
        try
        {
            Directory.CreateDirectory(RecentJobs.AppDataFolder);
            File.WriteAllText(Path.Join(RecentJobs.AppDataFolder, SettingsFile), JsonSerializer.Serialize(settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ShowError(Exception e) => _dialogs.Inform(WpfDialogs.Caption, e.Message, isError: true);
}
