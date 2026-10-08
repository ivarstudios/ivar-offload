using System.Text.Json;
using System.Text.Json.Serialization;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>
/// Where job logs live: in the target folder, so the record of every move travels with the files. A short receipt
/// is also left in the source folder, so what left it can be traced from there too.
/// </summary>
public static class JobPaths
{
    /// <summary>The log folder new jobs write (in the target, and the receipt in the source).</summary>
    public const string LogFolderName = "_IVAROffload";
    /// <summary>The log folder of IVAR Ingest, this app's earlier name (sorts and backups).</summary>
    public const string IvarIngestLogFolderName = "_IVARIngest";
    /// <summary>The log folder of Ingest Sorter, the name before that (sorts only).</summary>
    public const string IngestSorterLogFolderName = "_IngestSorter";
    /// <summary>
    /// The log folders of this app's earlier names, newest first. Jobs logged there are found, resumed, verified, listed
    /// and undone like any other, and keep writing there; like the current one they are never scanned.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyLogFolderNames = [IvarIngestLogFolderName, IngestSorterLogFolderName];
    /// <summary>Every log-folder name, the current one first.</summary>
    public static readonly IReadOnlyList<string> LogFolderNames = [LogFolderName, .. LegacyLogFolderNames];
    public const string JournalSuffix = ".journal.jsonl";
    public const string TempExtension = ".offload-partial";
    /// <summary>The temporary copies of earlier versions: found as leftovers, never moved. Resume finds them by the name in the log.</summary>
    public const string LegacyTempExtension = ".isort-partial";
    /// <summary>Receipt in the source folder: every file that left, where it went, and its checksum (opens in Excel).</summary>
    public const string ReceiptCsvSuffix = ".moved-out.csv";
    /// <summary>Receipt in the source folder: a few lines in plain words, with the path of the job log.</summary>
    public const string ReceiptTextSuffix = ".moved-out.txt";
    /// <summary>Added to the name of a copy in the target that failed its checksum and was renamed out of the way.</summary>
    public const string DamagedCopySuffix = ".damaged-copy";
    /// <summary>
    /// Added to the name of a copy that was kept although it was never checked: its original disappeared while it was
    /// being copied, or the job was ended while the original could not be reached. Never deleted by the app.
    /// </summary>
    public const string UnverifiedCopySuffix = ".unverified-copy";
    /// <summary>Added to the name of a verified copy whose original is gone, when another file already has its name in the target.</summary>
    public const string VerifiedCopySuffix = ".verified-copy";

    public static string LogFolder(string targetRoot) => Path.Join(targetRoot, LogFolderName);

    /// <summary>A folder name that is one of the app's log folders (current or legacy).</summary>
    public static bool IsLogFolderName(string name) => LogFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The log-folder name a job uses on both sides: the one its journal is in. A job started by IVAR Ingest or Ingest
    /// Sorter keeps its _IVARIngest or _IngestSorter folders, so its receipt stays next to the earlier one; everything
    /// else uses the current name.
    /// </summary>
    public static string LogFolderNameOf(string journalPath)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(journalPath))) ?? "";
        return LegacyLogFolderNames.FirstOrDefault(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? LogFolderName;
    }

    /// <summary>A temporary copy this app (or an earlier version of it) makes while copying a file.</summary>
    public static bool IsTempExtension(string extension) =>
        extension.Equals(TempExtension, StringComparison.OrdinalIgnoreCase) || extension.Equals(LegacyTempExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder in the source that holds a job's receipt.</summary>
    public static string ReceiptFolder(string sourceRoot, string journalPath) => Path.Join(sourceRoot, LogFolderNameOf(journalPath));

    public static string ManifestPath(string journalPath) => BasePath(journalPath) + ".manifest.csv";

    public static string SummaryPath(string journalPath) => BasePath(journalPath) + ".summary.txt";

    public static string BasePath(string journalPath) =>
        journalPath.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase) ? journalPath[..^JournalSuffix.Length] : journalPath;

    /// <param name="logFolderName">The job's log-folder name (<see cref="LogFolderNameOf"/>); the current one by default.</param>
    public static string ReceiptCsvPath(string sourceRoot, string jobId, string logFolderName = LogFolderName) =>
        Path.Join(sourceRoot, logFolderName, jobId + ReceiptCsvSuffix);

    /// <param name="logFolderName">The job's log-folder name (<see cref="LogFolderNameOf"/>); the current one by default.</param>
    public static string ReceiptTextPath(string sourceRoot, string jobId, string logFolderName = LogFolderName) =>
        Path.Join(sourceRoot, logFolderName, jobId + ReceiptTextSuffix);

    /// <summary>
    /// The folder that holds this journal's log folder (_IVAROffload, or _IVARIngest or _IngestSorter for older jobs), i.e. where the
    /// job's target really is now. Null when the journal is not inside a log folder.
    /// </summary>
    public static string? RootOfJournal(string journalPath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(journalPath));
        if (folder is null || !IsLogFolderName(Path.GetFileName(folder))) return null;
        return Path.GetDirectoryName(folder);
    }

    /// <summary>Two paths name the same folder (full paths compared without case and trailing separator).</summary>
    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The job logs in a folder's log folders: _IVAROffload first, then IVAR Ingest's _IVARIngest and Ingest Sorter's _IngestSorter.</summary>
    public static IEnumerable<string> FindJournals(string targetRoot) =>
        LogFolderNames.Select(name => Path.Join(targetRoot, name)).Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*" + JournalSuffix));

    /// <summary>
    /// A job log that is no longer where it was recorded, while its drive is there: the sorted folder (the job's
    /// target) was renamed or moved. Looked for first beside the recorded folder (renamed where it was), then further
    /// away on the same drive, nearest first: in the folders around it a few levels up and down (moved into an archive
    /// folder, or its parent folder renamed too), in the folders of the other sorts this PC remembers on that drive, and
    /// below the drive's root - within a limit of time and folders, so a log that is gone for good costs a moment only.
    /// The log must hold the same job id. Null when not found.
    /// </summary>
    public static string? FindMovedJournal(string journalPath, string jobId)
    {
        if (jobId.Length == 0) return null;
        try
        {
            string? root = RootOfJournal(journalPath);
            if (root is null || Path.GetPathRoot(root) is not { Length: > 0 } driveRoot || !IO.SafeFile.DirectoryExists(driveRoot)) return null;
            string name = Path.GetFileName(journalPath);
            string? parent = Path.GetDirectoryName(root);
            if (parent is not null && IO.SafeFile.DirectoryExists(parent)
                && Directory.EnumerateDirectories(parent).Prepend(parent).Select(f => LogIn(f, root, name, jobId)).FirstOrDefault(l => l is not null) is { } beside)
                return beside;
            lock (FailedSearches)
                if (FailedSearches.TryGetValue(journalPath, out DateTime at) && DateTime.UtcNow - at < SearchAgainAfter) return null;
            string? found = SearchFurther(root, name, jobId, Anchors(root, driveRoot), FurtherSearchTime, FurtherSearchFolders);
            lock (FailedSearches)
                if (found is null) FailedSearches[journalPath] = DateTime.UtcNow;
                else FailedSearches.Remove(journalPath);
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>A wider search that found nothing is not repeated for this long (the banner asks every time it is refreshed).</summary>
    private static readonly TimeSpan SearchAgainAfter = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, DateTime> FailedSearches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FurtherSearchTime = TimeSpan.FromSeconds(2);
    private const int FurtherSearchFolders = 5000;
    /// <summary>How deep below each place it starts from the wider search looks.</summary>
    private const int FurtherSearchDepth = 4;

    /// <summary>Folders that never hold a sorted folder, or that are too big to look through: never entered.</summary>
    private static readonly HashSet<string> NotSearched = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "DCIM", "node_modules",
    };

    /// <summary>
    /// The job log with this name and id in a folder's log folders (never in <paramref name="notIn"/>, where it was), when
    /// that folder is the job's own sorted folder and not a copy of it (<see cref="IsJobsOwnFolder"/>); null otherwise.
    /// </summary>
    private static string? LogIn(string folder, string notIn, string name, string jobId)
    {
        if (SamePath(folder, notIn)) return null;
        foreach (string logFolder in LogFolderNames)
        {
            string candidate = Path.Join(folder, logFolder, name);
            if (File.Exists(candidate) && JournalReader.TryReadHeader(candidate)?.Id == jobId && IsJobsOwnFolder(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// The folder a job log was found in holds the files the job moved there, not copies of them: a copy of the sorted
    /// folder (Explorer's "Sorted - Copy", a delivery or backup copy) carries the log too, and undoing from it would move
    /// the files out of the copy. Up to 20 moved files, spread over the job, are looked at: each one still there must be
    /// the same file record the job moved (its file id, when the job recorded one), or at least have the size and the
    /// creation time it had (a copy is made with a new creation time). A job that moved nothing yet counts as its own.
    /// </summary>
    internal static bool IsJobsOwnFolder(string journal)
    {
        try
        {
            JobState state = JournalReader.Read(journal);
            string? root = RootOfJournal(journal);
            if (root is null) return false;
            List<JobItem> moved = state.Items.Where(i => i.Stage == ItemStage.Done).ToList();
            if (moved.Count == 0) return true;
            int step = Math.Max(1, moved.Count / 20), found = 0;
            long tolerance = 2 * TimeSpan.TicksPerSecond; // FAT and exFAT round times
            for (int k = 0; k < moved.Count; k += step)
            {
                JobItem item = moved[k];
                if (IO.SafeFile.TrySnapshot(Path.Join(root, item.Rel)) is not { IsDirectory: false } now) continue; // moved on or deleted since
                found++;
                bool sameRecord = item.How == "rename" && item.FileId is { } id && now.FileId is { } nowId
                    ? SameFileRecord(id, nowId)
                    : now.Size == item.Size && Math.Abs(now.CreationTime - (item.SeenCreationTime ?? item.CreationTime)) <= tolerance;
                if (!sameRecord) return false;
            }
            return found > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Two file ids ("volume-fileid", see SafeFile.Snapshot) name the same file record; the volume part is not compared (a subst or another letter reads the same).</summary>
    private static bool SameFileRecord(string a, string b) =>
        string.Equals(a[(a.IndexOf('-') + 1)..], b[(b.IndexOf('-') + 1)..], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the wider search starts, with how far each is from the recorded folder (a step per folder level): the
    /// folders above it that still exist, the folders the other sorts this PC remembers on the same drive are in now
    /// (two steps away: sorted folders are often kept together), and the drive's root.
    /// </summary>
    private static List<(string Folder, int Distance)> Anchors(string root, string driveRoot)
    {
        var anchors = new List<(string, int)>();
        int up = 0;
        for (string? p = Path.GetDirectoryName(root); p is not null; p = Path.GetDirectoryName(p))
        {
            up++;
            if (IO.SafeFile.DirectoryExists(p)) anchors.Add((p, up));
        }
        foreach (RecentJob other in RecentJobs.Entries())
            if (RootOfJournal(other.JournalPath) is { } there && Path.GetDirectoryName(there) is { } around
                && SamePath(Path.GetPathRoot(around) ?? "", driveRoot) && IO.SafeFile.DirectoryExists(around))
                anchors.Add((around, 2));
        anchors.Add((driveRoot, up));
        return anchors;
    }

    /// <summary>
    /// Looks for the log nearest first: a folder is as far as the steps from its anchor (<see cref="Anchors"/>) down to
    /// it, so a folder moved close by is found before a far one (of two as far, the one below the nearer anchor), down to <see cref="FurtherSearchDepth"/> levels below an
    /// anchor. Stops after <paramref name="limit"/> or <paramref name="maxFolders"/> folders. Hidden and system folders,
    /// links, log folders, application libraries and the folders in <see cref="NotSearched"/> are not entered.
    /// </summary>
    internal static string? SearchFurther(string notIn, string name, string jobId, IReadOnlyList<(string Folder, int Distance)> anchors, TimeSpan limit, int maxFolders)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Nearest first; of two as far, the one below the anchor nearer to where the log was (then the one found first).
        var queue = new PriorityQueue<(string Folder, int Depth, int Distance, int Anchor), (int Distance, int Anchor, int Order)>();
        int order = 0;
        foreach ((string folder, int distance) in anchors.OrderBy(a => a.Distance))
            if (seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))) queue.Enqueue((folder, 0, distance, distance), (distance, distance, order++));
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        int looked = 0;
        while (queue.TryDequeue(out var next, out _))
        {
            if (clock.Elapsed > limit || ++looked > maxFolders) return null;
            if (LogIn(next.Folder, notIn, name, jobId) is { } found) return found;
            if (next.Depth == FurtherSearchDepth) continue;
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(next.Folder, "*", options))
                {
                    string subName = Path.GetFileName(sub);
                    if (IsLogFolderName(subName) || NotSearched.Contains(subName) || subName.StartsWith('$') || subName.StartsWith('.')
                        || MediaRules.ExcludedFolders.ContainsKey(subName) || MediaRules.ApplicationLibraryKind(subName) is not null)
                        continue;
                    if (seen.Add(sub)) queue.Enqueue((sub, next.Depth + 1, next.Distance + 1, next.Anchor), (next.Distance + 1, next.Anchor, order++));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// The drive that holds a job log answers (and is the drive with <paramref name="serial"/> when that is known),
    /// so a missing log means a renamed, moved or deleted folder - not a drive that is not connected.
    /// </summary>
    public static bool DriveIsThere(string journalPath, uint serial) => Drives.IsOn(journalPath, serial);

    /// <summary>
    /// Why a job log cannot be reached, in plain words: its drive is not connected, or its folder is gone. Without the
    /// drive's serial number (<paramref name="serial"/> 0) a drive at the log's letter may be another drive, so the
    /// message says both are possible instead of claiming the log's drive is connected.
    /// </summary>
    public static string WhyNotReachable(string journalPath, uint serial, string driveName)
    {
        if (!DriveIsThere(journalPath, serial)) return $"The drive with this job's log ({driveName}) is not connected. Connect it, then try again.";
        string open = $"open the job log inside the renamed folder ({Path.Join(LogFolderNameOf(journalPath), Path.GetFileName(journalPath))}), or choose that folder.";
        if (serial != 0)
            return $"The job log is no longer at {journalPath}. Its drive ({driveName}) is connected, so the sorted folder was probably renamed, moved or deleted. "
                   + char.ToUpperInvariant(open[0]) + open[1..];
        string letter = Drives.Letter(journalPath);
        string which = string.Equals(driveName, letter, StringComparison.OrdinalIgnoreCase) ? "" : $" ({driveName})";
        return $"The job log is no longer at {journalPath}. A drive is connected at {letter}, but it is not known whether it is the drive that holds the log{which}: "
               + "the sorted folder may have been renamed, moved or deleted, or the drive with the log may be unplugged and another drive given its letter. "
               + $"Plug in the drive with the log, or {open}";
    }

    /// <summary>Jobs in a target folder that were started but not finished.</summary>
    public static List<JobState> FindUnfinished(string targetRoot)
    {
        var result = new List<JobState>();
        foreach (string journal in FindJournals(targetRoot))
        {
            try
            {
                JobState state = JournalReader.Read(journal);
                if (state.IsResumable) result.Add(state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
            {
                // A log we cannot read is reported when the user opens it, not here.
            }
        }
        return result;
    }
}

/// <summary>What the recent-jobs list remembers about a job, so it can be shown even while its drive is not connected.</summary>
public sealed class RecentJob
{
    public string JournalPath { get; set; } = "";
    public string JobId { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public JobKind Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public MoveMode Mode { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    /// <summary>Volume serial number of the drive with the job log (its target); 0 when unknown.</summary>
    public uint TargetSerial { get; set; }
    public string? Created { get; set; }
    /// <summary>The job had ended the last time this PC saw it.</summary>
    public bool Ended { get; set; }

    /// <summary>The job log can be reached right now (its drive is connected and the folder is where it was).</summary>
    [JsonIgnore] public bool IsConnected => JournalPath.Length > 0 && File.Exists(JournalPath);

    /// <summary>
    /// The drive with the job log is connected, but the log is not where it was (the sorted folder was renamed, moved
    /// or deleted). Such a job is never called "not connected".
    /// </summary>
    [JsonIgnore] public bool LogMissing => JournalPath.Length > 0 && !IsConnected && JobPaths.DriveIsThere(JournalPath, TargetSerial);

    /// <summary>Why the job log cannot be reached (null when it can), in plain words.</summary>
    [JsonIgnore] public string? WhyNotReachable => IsConnected ? null : JobPaths.WhyNotReachable(JournalPath, TargetSerial, DriveName);

    /// <summary>The drive that holds the job log, for messages: "SONY_SSD" or "F:".</summary>
    [JsonIgnore] public string DriveName => TargetLabel.Length > 0 ? TargetLabel : (Path.GetPathRoot(Target.Length > 0 ? Target : JournalPath) ?? Target).TrimEnd('\\');
}

/// <summary>
/// A small per-user list of recent jobs, so an interrupted job can be offered for resume at startup (also while its
/// drive is not connected) and earlier sorts can be offered for undo.
/// </summary>
public static class RecentJobs
{
    private const int MaxEntries = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string FileName = "recent-jobs.json";

    private static string FilePath => Path.Join(AppDataFolder, FileName);

    /// <summary>
    /// Per-user settings folder (%LOCALAPPDATA%\IVAROffload). IVAROFFLOAD_DATA overrides it (used by tests to stay out
    /// of the real profile).
    /// </summary>
    public static string AppDataFolder => DataOverride ?? Path.Join(LocalAppData, "IVAROffload");

    /// <summary>
    /// The settings folders of this app's earlier names, newest first (IVAR Ingest's, then Ingest Sorter's); none while
    /// IVAROFFLOAD_DATA overrides the folder.
    /// </summary>
    public static IReadOnlyList<string> LegacyAppDataFolders =>
        DataOverride is null ? [Path.Join(LocalAppData, "IVARIngest"), Path.Join(LocalAppData, "IngestSorter")] : [];

    private static string? DataOverride => Environment.GetEnvironmentVariable("IVAROFFLOAD_DATA") is { Length: > 0 } overridden ? overridden : null;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The file to read a per-user setting from (e.g. "settings.json"): the one in <see cref="AppDataFolder"/>, or, while
    /// that folder has none, the newest one an earlier version left in its own folder (IVAR Ingest, then Ingest Sorter).
    /// Files are always written to AppDataFolder, so an old one is read only until the first save.
    /// </summary>
    public static string AppDataFileToRead(string name) => FileToRead(name, AppDataFolder, LegacyAppDataFolders);

    internal static string FileToRead(string name, string folder, IReadOnlyList<string> legacyFolders)
    {
        string path = Path.Join(folder, name);
        if (File.Exists(path)) return path;
        return legacyFolders.Select(legacy => Path.Join(legacy, name)).FirstOrDefault(File.Exists) ?? path;
    }

    /// <summary>Remembers a job log (reads its header).</summary>
    public static void Add(string journalPath)
    {
        JobHeader? header = JournalReader.TryReadHeader(journalPath);
        Remember(journalPath, header, ended: null);
    }

    /// <summary>Remembers a job with its current state, replacing any older entry for the same job (e.g. at an old path).</summary>
    public static void Add(JobState state) => Remember(state.JournalPath, state.Header, state.IsEnded);

    /// <summary>Removes a job from the list (it is not deleted anywhere else).</summary>
    public static void Forget(string journalPathOrJobId)
    {
        try
        {
            List<RecentJob> list = Entries();
            if (list.RemoveAll(e => Matches(e, journalPathOrJobId) || e.JobId == journalPathOrJobId) > 0) Save(FilePath, list);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every remembered job, newest first.</summary>
    public static List<RecentJob> Entries() => Load(AppDataFileToRead(FileName));

    /// <summary>Paths of the remembered job logs, newest first.</summary>
    public static List<string> List() => Entries().Select(e => e.JournalPath).ToList();

    /// <summary>Recent jobs that can be resumed (also when their sorted folder was renamed next to where it was).</summary>
    public static List<JobState> Unfinished()
    {
        var result = new List<JobState>();
        foreach (RecentJob entry in Relocated(Entries()).Where(e => e.IsConnected))
        {
            try
            {
                JobState state = JournalReader.Read(entry.JournalPath);
                if (state.IsResumable) result.Add(state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
            {
            }
        }
        return result;
    }

    /// <summary>
    /// Unfinished jobs whose log cannot be reached right now (drive not connected, or the target folder was renamed),
    /// so the app can say "Unfinished sort on SONY_SSD (not connected) - connect it to continue".
    /// </summary>
    /// <remarks>
    /// A job whose drive is there but whose log is not (<see cref="RecentJob.LogMissing"/>) is first looked for next to
    /// where it was; one that is found is remembered at its new place and is not in this list. The others say why in
    /// <see cref="RecentJob.WhyNotReachable"/>.
    /// </remarks>
    public static List<RecentJob> NotConnected() => NotConnected(Relocated(Entries()));

    internal static List<RecentJob> NotConnected(IEnumerable<RecentJob> entries) =>
        entries.Where(e => !e.Ended && e.JobId.Length > 0 && !e.IsConnected).ToList();

    /// <summary>
    /// The job log of a remembered job that is no longer where it was while its drive is there, found next to its old
    /// place (the sorted folder was renamed) and remembered at its new place. Null when it cannot be found.
    /// </summary>
    public static string? FindMoved(RecentJob entry)
    {
        if (entry.IsConnected || entry.JobId.Length == 0 || !entry.LogMissing) return null;
        string? found = JobPaths.FindMovedJournal(entry.JournalPath, entry.JobId);
        if (found is not null) Add(found);
        return found;
    }

    /// <summary>The entries, with the logs of unfinished jobs whose sorted folder was renamed found again.</summary>
    private static List<RecentJob> Relocated(List<RecentJob> entries)
    {
        bool moved = false;
        foreach (RecentJob entry in entries.Where(e => !e.Ended && !e.IsConnected))
            moved |= FindMoved(entry) is not null;
        return moved ? Entries() : entries;
    }

    private static void Remember(string journalPath, JobHeader? header, bool? ended)
    {
        try
        {
            string path = Path.GetFullPath(journalPath);
            List<RecentJob> list = Entries();
            RecentJob? previous = list.FirstOrDefault(e => Matches(e, path))
                                  ?? list.FirstOrDefault(e => header is not null && header.Id.Length > 0 && e.JobId == header.Id); // found at a new place
            list.RemoveAll(e => Matches(e, path) || (header is not null && header.Id.Length > 0 && e.JobId == header.Id));
            list.Insert(0, header is null
                ? previous ?? new RecentJob { JournalPath = path }
                : new RecentJob
                {
                    JournalPath = path,
                    JobId = header.Id,
                    Kind = header.Kind,
                    Mode = header.Mode,
                    Source = header.Source,
                    Target = header.Target,
                    SourceLabel = header.SourceLabel,
                    TargetLabel = header.TargetLabel,
                    TargetSerial = header.TargetSerial,
                    Created = header.Created,
                    Ended = ended ?? previous?.Ended ?? false,
                });
            Save(FilePath, list);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Convenience only; the journal in the target folder is what matters.
        }
    }

    private static bool Matches(RecentJob entry, string journalPath) => string.Equals(entry.JournalPath, journalPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the list. Also reads the first version's format, a plain list of journal paths.</summary>
    internal static List<RecentJob> Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<RecentJob>();
            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } path)
                    result.Add(new RecentJob { JournalPath = path });
                else if (element.ValueKind == JsonValueKind.Object && element.Deserialize<RecentJob>(JsonOptions) is { JournalPath.Length: > 0 } entry)
                    result.Add(entry);
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    internal static void Save(string file, List<RecentJob> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(list.Take(MaxEntries).ToList(), JsonOptions));
    }
}
