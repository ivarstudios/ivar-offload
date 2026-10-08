using System.Text.Json;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>
/// Where a backup's logs live: in every destination's _IVAROffload folder, one log per destination, all with the same
/// job id. They are named *.backup.jsonl, never *.journal.jsonl, so the sort tools (resume, undo, the job lists) never
/// pick up a backup: a backup must never be run by the engine that removes originals.
/// </summary>
public static class BackupPaths
{
    public const string JournalSuffix = ".backup.jsonl";

    /// <summary>
    /// The log folders a backup can be logged in: the current one, and IVAR Ingest's (this app's earlier name), which a
    /// backup started by IVAR Ingest keeps, top-ups of it included. Ingest Sorter made no backups.
    /// </summary>
    public static readonly IReadOnlyList<string> LogFolderNames = [JobPaths.LogFolderName, JobPaths.IvarIngestLogFolderName];

    /// <summary>
    /// The ASC MHL ignore patterns for the backup's own log folders, _IVAROffload and IVAR Ingest's _IVARIngest, wherever
    /// they are: the logs change after the manifest is written. The whole folders are left out, so the directory hashes of
    /// a card's earlier generations still match; a card's own log folders (logs of an earlier sort) are copied and
    /// verified, but not in the manifest. Both names are always ignored, so histories IVAR Ingest wrote keep matching.
    /// </summary>
    public static readonly IReadOnlyList<string> MhlIgnorePatterns = [.. LogFolderNames.Select(name => name + "/")];

    /// <summary>The log of a backup in a destination folder: in whichever log folder holds it, else in the current one.</summary>
    public static string JournalPath(string destination, string jobId) =>
        LogFolderNames.Select(name => Path.Join(destination, name, jobId + JournalSuffix)).FirstOrDefault(File.Exists)
        ?? Path.Join(destination, JobPaths.LogFolderName, jobId + JournalSuffix);

    public static string SummaryPath(string journalPath) => BasePath(journalPath) + ".summary.txt";

    public static string ManifestPath(string journalPath) => BasePath(journalPath) + ".manifest.csv";

    private static string BasePath(string journalPath) =>
        journalPath.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase) ? journalPath[..^JournalSuffix.Length] : journalPath;

    /// <summary>
    /// Where a destination is now: the recorded folder when the backup's log is there, else the same folder on the drive
    /// with the recorded serial number under another letter (when its log is there). The recorded folder otherwise.
    /// </summary>
    public static string FindDestination(string folder, uint serial, string jobId)
    {
        if (File.Exists(JournalPath(folder, jobId)) || serial == 0) return folder;
        return Drives.Elsewhere(folder, serial).FirstOrDefault(f => File.Exists(JournalPath(f, jobId))) ?? folder;
    }

    /// <summary>The backup logs in a destination folder's log folders (_IVAROffload, then IVAR Ingest's _IVARIngest).</summary>
    public static IEnumerable<string> FindJournals(string destination) =>
        LogFolderNames.Select(name => Path.Join(destination, name)).Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*" + JournalSuffix));
}

/// <summary>
/// What an item of a backup's log is (its "why"): a card file copied as usual, or, in a top-up, a card file the earlier
/// backup already holds, one changed on the card since, an ASC MHL chain file, or a file of the earlier backup that is no
/// longer on the card.
/// </summary>
public static class BackupWhy
{
    /// <summary>A card file that is copied (in a top-up: one that is new on the card).</summary>
    public const string Copied = "copied";
    /// <summary>Top-ups: a card file the earlier backup verified, unchanged since: read again and compared, not written.</summary>
    public const string BackedUp = "backed-up";
    /// <summary>Top-ups: a card file the earlier backup holds in an older version: that one is moved aside, this one copied.</summary>
    public const string Changed = "changed";
    /// <summary>
    /// Top-ups: a card file that is an ASC MHL chain, whose copy in the backup folder the earlier backup continued with its own
    /// generation: the card file is checked against the earlier checksum, and the copy (this folder's history) is kept.
    /// </summary>
    public const string Chain = "chain";
    /// <summary>Top-ups: a file of the earlier backup that is no longer on the card: it stays, and its copy is checked.</summary>
    public const string Kept = "kept";
}

/// <summary>What the recent-backups list remembers about a backup, so an unfinished one can be offered at startup.</summary>
public sealed class RecentBackup
{
    public string JobId { get; set; } = "";
    /// <summary>The log of every destination (the place each was last seen).</summary>
    public List<string> Journals { get; set; } = [];
    public string Source { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string? Created { get; set; }
    /// <summary>Every destination's log had ended the last time this PC saw them.</summary>
    public bool Ended { get; set; }

    /// <summary>The logs that can be reached right now.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<string> Connected => Journals.Where(File.Exists);
}

/// <summary>
/// A small per-user list of recent backups (separate from the sort jobs' list, so no sort tool ever sees a backup),
/// so an unfinished backup can be offered for resume at startup, also while one of its drives is not connected.
/// </summary>
public static class RecentBackups
{
    private const int MaxEntries = 50;
    private const string FileName = "recent-backups.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Lock = new();

    private static string FilePath => Path.Join(RecentJobs.AppDataFolder, FileName);

    /// <summary>Every remembered backup, newest first.</summary>
    public static List<RecentBackup> Entries() => Load(out _);

    private static List<RecentBackup> Load(out bool damaged)
    {
        damaged = false;
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<RecentBackup>>(File.ReadAllText(FilePath), JsonOptions) ?? [] : [];
        }
        catch (JsonException)
        {
            damaged = true;
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the list through a temporary file, so a power cut never leaves half of it. A list that can't be read is
    /// kept next to it (.damaged) rather than silently replaced.
    /// </summary>
    private static void Save(List<RecentBackup> list, bool damaged)
    {
        Directory.CreateDirectory(RecentJobs.AppDataFolder);
        if (damaged) File.Copy(FilePath, FilePath + ".damaged", overwrite: true);
        string temp = FilePath + ".partial";
        File.WriteAllText(temp, JsonSerializer.Serialize(list.Take(MaxEntries).ToList(), JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Remembered backups that had not ended the last time they were seen.</summary>
    public static List<RecentBackup> Unfinished() => Entries().Where(e => !e.Ended).ToList();

    /// <summary>Remembers a backup (or updates what is known about it).</summary>
    public static void Remember(string jobId, IEnumerable<string> journals, string source, string sourceName, string? created, bool ended)
    {
        lock (Lock)
        {
            try
            {
                List<RecentBackup> list = Load(out bool damaged);
                list.RemoveAll(e => e.JobId == jobId);
                list.Insert(0, new RecentBackup
                {
                    JobId = jobId,
                    Journals = journals.Select(Path.GetFullPath).ToList(),
                    Source = source,
                    SourceName = sourceName,
                    Created = created,
                    Ended = ended,
                });
                Save(list, damaged);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Convenience only; the logs on the destinations are what matters.
            }
        }
    }

    /// <summary>Removes a backup from the list (its logs stay on the destinations).</summary>
    public static void Forget(string jobId)
    {
        lock (Lock)
        {
            try
            {
                List<RecentBackup> list = Load(out bool damaged);
                if (list.RemoveAll(e => e.JobId == jobId) > 0) Save(list, damaged);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Writes the logs of a confirmed backup plan: one per destination. The logs, not the plan, are what gets run.</summary>
public static class BackupFactory
{
    /// <summary>Creates every destination folder and its log; returns the log of the first destination.</summary>
    /// <exception cref="IOException">A destination could not be prepared; nothing is left behind on any destination.</exception>
    public static string Create(BackupPlan plan)
    {
        if (!plan.CanRun)
            throw new InvalidOperationException("You cannot start this backup: " + string.Join(" ", plan.Messages.Where(m => m.Level == MessageLevel.Error).Select(m => m.Text)));

        string id = JobFactory.NewJobId("backup");
        string created = JournalRecord.Now();
        var journals = new List<string>();
        var madeFolders = new List<string>();
        try
        {
            foreach (BackupTarget t in plan.Targets)
            {
                if (plan.IsTopUp) CheckStillTheEarlierBackup(t);
                // The preview found the folder missing or empty; something may have been put there since.
                else if (Directory.Exists(t.Folder) && Directory.EnumerateFileSystemEntries(t.Folder).Any())
                    throw new IOException($"{t.Folder} is no longer empty. Check the card again. Then change the name or the folder for the backup drive.");
                if (!Directory.Exists(t.Folder))
                {
                    Directory.CreateDirectory(t.Folder);
                    madeFolders.Add(t.Folder);
                }
                // A top-up logs next to the earlier backup's log: a backup started by IVAR Ingest keeps its _IVARIngest folder.
                string log = Path.Join(t.Folder, plan.IsTopUp && t.Earlier is { } earlier
                    ? JobPaths.LogFolderNameOf(earlier.Earlier.JournalPath)
                    : JobPaths.LogFolderName);
                if (!Directory.Exists(log))
                {
                    Directory.CreateDirectory(log);
                    madeFolders.Add(log);
                }
                string journal = Path.Join(log, id + BackupPaths.JournalSuffix);
                WriteJournal(journal, plan, t, id, created);
                journals.Add(journal);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
            foreach (string j in journals) TryDelete(() => File.Delete(j));
            foreach (string f in Enumerable.Reverse(madeFolders)) TryDelete(() => Directory.Delete(f));
            throw e as IOException ?? new IOException(e.Message, e);
        }
        RecentBackups.Remember(id, journals, plan.SourceRoot, plan.SourceName, created, ended: false);
        return journals[0];
    }

    /// <summary>
    /// A top-up goes into the earlier backup's folder: its log must still be there, finished, and nothing may have
    /// started another backup there since the preview.
    /// </summary>
    private static void CheckStillTheEarlierBackup(BackupTarget t)
    {
        const string Again = "Check the card again to see what you can add now.";
        if (t.Earlier is not { } earlier) throw new IOException($"{t.Folder}: there is no known earlier backup to add to. {Again}");
        if (!Directory.Exists(t.Folder)) throw new IOException($"{t.Folder} is no longer there. {Again}");
        foreach (string journal in BackupPaths.FindJournals(t.Folder))
        {
            JobState state = JournalReader.Read(journal);
            if (state.IsBackup && !state.IsEnded)
                throw new IOException($"{t.Folder} now contains an unfinished backup (started {state.Header.Created}). Resume it or end it first.");
        }
        if (!File.Exists(earlier.Earlier.JournalPath) || !JournalReader.Read(earlier.Earlier.JournalPath).IsEnded)
            throw new IOException($"The log of the earlier backup is no longer in {t.Folder}. {Again}");
    }

    private static void WriteJournal(string path, BackupPlan plan, BackupTarget target, string id, string created)
    {
        using JournalWriter writer = JournalWriter.CreateNew(path);
        writer.Write(new JournalRecord
        {
            Type = "job",
            Version = JobFactory.JournalVersion,
            JobId = id,
            Kind = "backup",
            At = created,
            Tool = JobFactory.ToolName,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            AddsTo = plan.IsTopUp ? plan.TopUp!.JobId : null,
            Source = plan.SourceRoot,
            Target = target.Folder,
            SourceSerial = plan.SourceVolume?.SerialNumber,
            SourceLabel = plan.SourceName,
            TargetSerial = target.Volume?.SerialNumber,
            TargetLabel = target.Volume?.Label,
            SourceReal = Drives.RealIfSubst(plan.SourceRoot),
            TargetReal = Drives.RealIfSubst(target.Folder),
            Targets = plan.Targets.Select(t => t.Folder).ToList(),
            TargetSerials = plan.Targets.Select(t => t.Volume?.SerialNumber ?? 0).ToList(),
            TargetLabels = plan.Targets.Select(t => t.Volume?.Label ?? "").ToList(),
            Reread = plan.Reread,
            Mode = nameof(MoveMode.Videos), // unused by backups; keeps older readers of the header working
            Method = nameof(TransferMethod.Copy),
            Verify = true,
            CompareIds = plan.SourceVolume?.HasStableFileIds ?? false,
            Count = plan.Files.Count,
            Bytes = plan.Bytes,
        }, flush: false);
        foreach ((string rel, FolderTimes t) in plan.Scan.Folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            writer.Write(new JournalRecord { Type = "dir", Rel = rel, CreationTime = t.CreationTime, LastWriteTime = t.LastWriteTime }, flush: false);
        int index = 0;
        TopUpOffer? topUp = plan.IsTopUp ? plan.TopUp : null;
        foreach (SourceFile f in plan.Files)
        {
            // A top-up: what the earlier backup verified for this file, when it holds it.
            EarlierFile? earlier = target.Earlier?.Verified.Contains(f.RelativePath) == true ? topUp?.Earlier.GetValueOrDefault(f.RelativePath) : null;
            writer.Write(new JournalRecord
            {
                Type = "item",
                Index = index++,
                Rel = f.RelativePath,
                Size = f.Size,
                CreationTime = f.CreationTime,
                LastWriteTime = f.LastWriteTime,
                Why = topUp is null || earlier is null ? BackupWhy.Copied
                    : !topUp.IsUnchanged(f, earlier) ? BackupWhy.Changed
                    : target.Earlier!.ChainsCarryOwnHistory && AscMhl.IsChainFile(f.RelativePath) ? BackupWhy.Chain
                    : BackupWhy.BackedUp,
                Sha256 = earlier?.Sha256,
                Xxh64 = earlier?.Xxh64,
            }, flush: false);
        }
        // A top-up's logs also list the earlier backup's files that are no longer on the card: they stay in the folder.
        foreach (EarlierFile f in topUp?.Kept ?? [])
            writer.Write(new JournalRecord
            {
                Type = "item",
                Index = index++,
                Rel = f.Rel,
                Size = f.Size,
                CreationTime = f.CreationTime,
                LastWriteTime = f.LastWriteTime,
                Why = BackupWhy.Kept,
                // Only what this folder's earlier backup verified is compared here (another destination's may have had it).
                Sha256 = target.Earlier?.Verified.Contains(f.Rel) == true ? f.Sha256 : null,
                Xxh64 = target.Earlier?.Verified.Contains(f.Rel) == true ? f.Xxh64 : null,
            }, flush: false);
        // Files the backup leaves out (links, online-only), and folders that could not be read: never part of the run,
        // but they keep the backup from being complete, and its reports say so.
        foreach (LeftOutFile f in plan.Scan.LeftOut)
            writer.Write(new JournalRecord { Type = "held", Rel = f.RelativePath, Size = f.Size, LastWriteTime = f.LastWriteTime, Why = f.Why }, flush: false);
        foreach (SkippedFolder f in plan.Scan.SkippedFolders.Where(f => !BackupScanner.OsClutter.ContainsKey(Path.GetFileName(f.RelativePath))))
            writer.Write(new JournalRecord { Type = "held", Rel = f.RelativePath + "\\", Size = 0, Why = f.Reason }, flush: false);
        foreach (string problem in plan.Scan.Problems)
            writer.Write(new JournalRecord { Type = "held", Rel = problem, Size = 0, Why = "the scan could not read this folder, so its files are not in the backup" }, flush: false);
        writer.Write(new JournalRecord { Type = "ready", Count = plan.Files.Count, Bytes = plan.Bytes });
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
