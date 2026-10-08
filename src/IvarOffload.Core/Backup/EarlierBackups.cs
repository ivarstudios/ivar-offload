using System.Globalization;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>A file an earlier backup verified: its checksums, and the size and dates it had on the card then.</summary>
public sealed record EarlierFile(string Rel, long Size, long CreationTime, long LastWriteTime, string Sha256, string Xxh64);

/// <summary>The earlier backup of the card in one destination folder, and what a top-up changes there.</summary>
public sealed class TopUpFolder
{
    /// <summary>The destination folder the user picked (E:\Cards).</summary>
    public required string Parent { get; init; }
    /// <summary>The earlier backup's folder in it (E:\Cards\2026-09-28_SONY_A), which the top-up adds to.</summary>
    public required string Folder { get; init; }
    /// <summary>The earlier backup's log in that folder (the newest backup of this card there).</summary>
    public required JobState Earlier { get; init; }
    /// <summary>Card files that are written into this folder: new on the card, changed since, or no longer in the folder.</summary>
    public required IReadOnlyList<SourceFile> ToCopy { get; init; }
    /// <summary>Files of the earlier backup that are still on the card, unchanged, but no longer in this folder (moved out by a sort, deleted).</summary>
    public int Missing { get; init; }
    /// <summary>Files of the earlier backup that are no longer on the card and still in this folder: they stay.</summary>
    public int Kept { get; init; }
    public long MissingBytes { get; init; }
    /// <summary>Files of the earlier backup, still on the card unchanged, that are in this folder with another size (changed there since).</summary>
    public int Edited { get; init; }
    /// <summary>The files this folder's earlier backup verified (relative paths); what a top-up compares with there.</summary>
    public required IReadOnlySet<string> Verified { get; init; }
    /// <summary>
    /// The ASC MHL chain files the card brought along carry this folder's own generations (a backup of this card in the
    /// folder continued them), so they differ from the card's: the card's are checked, the folder's kept.
    /// </summary>
    public bool ChainsCarryOwnHistory { get; init; }
}

/// <summary>
/// An earlier backup of this card on every chosen destination, that a top-up can add the card's new files to: only what is
/// new or changed on the card is written, then every file of the card is verified again, so the folder ends up holding a
/// verified copy of the card as it is now. Nothing in the folder is overwritten or deleted: an older version of a changed
/// file is moved into _IVAROffload\replaced, and files no longer on the card stay.
/// </summary>
public sealed class TopUpOffer
{
    public required string JobId { get; init; }
    /// <summary>When the earlier backup was made (its log's time stamp).</summary>
    public string? Created { get; init; }
    /// <summary>One per chosen destination, in the order they were chosen.</summary>
    public required IReadOnlyList<TopUpFolder> Folders { get; init; }
    /// <summary>Card files the earlier backup does not have.</summary>
    public int New { get; init; }
    /// <summary>Card files the earlier backup has in an older version (their old version is moved aside).</summary>
    public int Changed { get; init; }
    /// <summary>Card files the earlier backup has as they are now (read again and compared, not written where they are there).</summary>
    public int Unchanged { get; init; }
    public long NewBytes { get; init; }
    /// <summary>What the earlier backup verified, for every card file it holds (by relative path).</summary>
    public required IReadOnlyDictionary<string, EarlierFile> Earlier { get; init; }
    /// <summary>Files of the earlier backup that are no longer on the card (they stay in the folder, and their copies are checked).</summary>
    public required IReadOnlyList<EarlierFile> Kept { get; init; }
    /// <summary>
    /// The whole quarter hours by which the card's dates are read off from the earlier backup's (a FAT card read after a
    /// daylight-saving change or in another time zone), in FILETIME ticks; usually 0.
    /// </summary>
    public long Shift { get; init; }

    /// <summary>The card file is as the earlier backup found it: same size and date (allowing <see cref="Shift"/>).</summary>
    public bool IsUnchanged(SourceFile f, EarlierFile e) => f.Size == e.Size && f.LastWriteTime - e.LastWriteTime == Shift;

    /// <summary>The earlier backup's folders, "E:\Cards\2026-09-28_SONY_A and F:\Cards\2026-09-28_SONY_A".</summary>
    public string Where => string.Join(" and ", Folders.Select(f => f.Folder));

    /// <summary>
    /// Where older versions go in the backup folder: "replaced" in the earlier backup's log folder, which the top-up logs
    /// in too (IVAR Ingest's _IVARIngest for a backup it made).
    /// </summary>
    public string ReplacedFolder =>
        $"{(Folders.Count > 0 ? JobPaths.LogFolderNameOf(Folders[0].Earlier.JournalPath) : JobPaths.LogFolderName)}\\{BackupRunner.ReplacedFolderName}";

    /// <summary>Files of the earlier backup that are no longer in a folder but still on the card (the most of any destination).</summary>
    public int Missing => Folders.Count == 0 ? 0 : Folders.Max(f => f.Missing);

    /// <summary>Files of the earlier backup that are no longer on the card but still in a folder (the most of any destination).</summary>
    public int KeptInFolder => Folders.Count == 0 ? 0 : Folders.Max(f => f.Kept);

    /// <summary>Files of the earlier backup changed in a folder since (another size; the most of any destination).</summary>
    public int EditedInFolder => Folders.Count == 0 ? 0 : Folders.Max(f => f.Edited);

    /// <summary>
    /// Card files, other than camera index files, that the earlier backup holds with another size or date: usually a
    /// different shot whose number the camera used again after a deletion, or a file edited in the camera.
    /// </summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>The earlier backup was ended before every file was copied (its missing files are what a top-up adds).</summary>
    public bool EndedEarly { get; init; }

    /// <summary>
    /// What the preview says about the earlier backup: when it offers to add to it ("This card was backed up on ...; the
    /// card has 38 new files since then ..."), or when the top-up is chosen ("Adding to the backup of ...: 38 new files
    /// are copied, ...").
    /// </summary>
    public string Describe(bool adding, int cardFiles)
    {
        string when = Created is { Length: >= 16 } at ? at[..16].Replace('T', ' ') : "an earlier day";
        // Files a finished backup does not have were added to the card since; one that was ended early may just not have got to them.
        string added = EndedEarly ? $"{Count(New, "file")} of the card {(New == 1 ? "is" : "are")} not in that backup" : $"the card now has {Count(New, "new file")}";
        string missing = Missing > 0 ? $"{Count(Missing, "file")} of that backup {(Missing == 1 ? "is" : "are")} no longer in its folder (because of a sort or a deletion)" : "";
        string kept = KeptInFolder > 0 ? $"{Count(KeptInFolder, "file")} of that backup {(KeptInFolder == 1 ? "is" : "are")} no longer on the card" : "";
        if (!adding)
        {
            var what = new List<string>();
            if (New > 0) what.Add($"{added} ({Format.Bytes(NewBytes)})");
            if (Changed > 0) what.Add($"{Count(Changed, "file")} changed on the card after that backup");
            if (missing.Length > 0) what.Add(missing);
            if (kept.Length > 0) what.Add(kept);
            string state = what.Count == 0 ? "It contains all files of the card as they are now" : string.Join(". ", what.Select(Capitalized));
            string offer = New + Changed + Missing == 0
                ? "You can check the whole card against that backup (this copies no files again) instead of a new full copy."
                : "You can add what is missing to that backup and check the whole card, instead of a new full backup.";
            return $"There is a backup of this card from {when} in {Where}{(EndedEarly ? " (that backup ended before it copied all files)" : "")}. {state}. {offer}";
        }
        var steps = new List<string>
        {
            New == 0 ? "No file is missing from it."
                : EndedEarly ? $"It copies the {Count(New, "file")} that {(New == 1 ? "is" : "are")} not in it yet."
                : $"It copies {Count(New, "new file")}.",
        };
        if (Changed > 0)
        {
            int index = Changed - ChangedFiles.Count;
            steps.Add($"It copies {Count(Changed, "changed file")} again.");
            if (ChangedFiles.Count > 0)
                steps.Add($"{(ChangedFiles.Count == 1 ? "The earlier photo or clip stays" : "The earlier photos and clips stay")} next to the new {(ChangedFiles.Count == 1 ? "one" : "ones")} as \"name (earlier)\".");
            if (index > 0) steps.Add($"{(index == 1 ? "The older camera index file goes" : "The older camera index files go")} to {ReplacedFolder}.");
        }
        if (Missing > 0)
            steps.Add($"{Count(Missing, "file")} {(Missing == 1 ? "is" : "are")} no longer in the backup folder (because of a sort or a deletion). "
                      + $"It copies {(Missing == 1 ? "this file" : "these files")} again from the card.");
        steps.Add($"Then it reads all {Count(cardFiles, "file")} on the card again and compares them with the backup.");
        return $"This backup adds files to the backup from {when} in {Where}. {string.Join(" ", steps)}"
               + (kept.Length > 0 ? $" {Capitalized(kept)}: {(KeptInFolder == 1 ? "it stays" : "they stay")} in the backup." : "");
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n:N0} {what}s";
}

/// <summary>
/// Finds earlier backups of the same card next to the chosen destinations (in the other folders of each destination
/// folder, e.g. E:\Cards\2026-09-27_SONY_A when backing up to E:\Cards), so the preview can say the card was already
/// backed up there, or that most of it was. A card is the same when its files are: the same names, sizes and dates.
/// What a log says is not trusted alone: the files it verified must still be in that folder.
/// </summary>
internal static class EarlierBackups
{
    /// <summary>At most this many folders are looked into per destination folder (a drive root can hold thousands).</summary>
    private const int MaxFolders = 2000;

    /// <summary>FILETIME ticks in 15 minutes: time zones and daylight saving move FAT times by whole quarter hours.</summary>
    private const long QuarterHour = 15L * 60 * 10_000_000;

    private const long MaxShift = 14 * 4 * QuarterHour;

    private sealed record Found(JobState State, string Folder, int Matched, int Gone);

    /// <summary>
    /// Camera index and catalog files that cameras rewrite with every shot, so a card that was shot on since its backup
    /// still counts as the same card when only they changed. From camera documentation; to be confirmed on real cards.
    /// </summary>
    private static readonly string[] IndexFileNames =
    [
        "MEDIAPRO.XML", "STATUS.BIN", "CUEUP.XML", // Sony XAVC (PRIVATE\M4ROOT)
        "INDEX.BDM", "MOVIEOBJ.BDM", // AVCHD (PRIVATE\AVCHD\BDMV)
        "NC_FLLST.DAT", // Nikon
        "SONYCARD.IND", "AVIN0001.BNP", "AVIN0001.INP", "AVIN0001.INT", // Sony index files
    ];

    private static readonly string[] IndexExtensions = [".CPI", ".MPL", ".CTG", ".BNP", ".INP", ".INT"]; // AVCHD clip and playlist info, Canon catalogs

    private static readonly string[] IndexFolders = ["CANONMSC", "PANA_GRP", "PANA_EXT", "MISC"]; // Canon, Panasonic, generic camera folders

    /// <summary>A camera index or catalog file that the camera rewrites as it records (see <see cref="IndexFileNames"/>).</summary>
    internal static bool IsCameraIndex(string rel)
    {
        string name = Path.GetFileName(rel);
        if (IndexFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (IndexExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return true;
        return rel.Split('\\').SkipLast(1).Any(part => IndexFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The earlier backup of this card that a top-up can add to: the newest finished backup of the same card (same drive
    /// serial and folder below the root, and at least half of the files that backup verified - camera index files aside -
    /// still on the card with the same name, size and date) in every chosen destination folder, the same backup on all of
    /// them. Null when there is none; <paramref name="refusal"/> then says why, when there is an earlier backup that can't
    /// be added to.
    /// </summary>
    /// <param name="parents">The destination folders the user picked (E:\Cards), not the backup folders inside them.</param>
    internal static TopUpOffer? FindTopUp(BackupScan scan, uint sourceSerial, IReadOnlyList<string> parents, out string? refusal)
    {
        refusal = null;
        if (scan.Files.Count == 0 || parents.Count == 0) return null;
        var card = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile f in scan.Files) card[f.RelativePath] = f;

        var found = new List<(string Parent, string Folder, JobState State, long Shift)?>();
        foreach (string parent in parents)
        {
            var ofThisCard = new List<(string Journal, string Folder, JobHeader Header)>();
            foreach (string folder in Candidates([parent], []))
                foreach (string journal in Journals(folder))
                    if (JournalReader.TryReadHeader(journal) is { Kind: JobKind.Backup } h && SameCard(h, scan.SourceRoot, sourceSerial))
                        ofThisCard.Add((journal, folder, h));
            // In each folder the newest backup of the card describes it (a top-up's log lists the whole folder). A folder whose
            // newest backup is complete is preferred to one whose backup was ended early (which may hold only a few files).
            var newest = new List<(string Folder, JobState State, long Shift)>();
            foreach (var inFolder in ofThisCard.GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase))
                foreach (var c in inFolder.OrderByDescending(c => Created(c.Header)))
                {
                    JobState state;
                    try
                    {
                        state = JournalReader.Read(c.Journal);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
                    {
                        continue;
                    }
                    if (!state.PlanComplete) continue;
                    if (!Similar(state, card, out long shift)) continue; // another card (formatted since), or an older backup
                    if (!state.IsEnded)
                    {
                        refusal = $"{c.Folder} contains an unfinished backup of this card (started {Date(state.Header.Created)}). Resume it, or end it, "
                                  + "before you add new files to it. Until then, a new backup copies the whole card.";
                        return null;
                    }
                    newest.Add((c.Folder, state, shift));
                    break;
                }
            var best = newest.OrderByDescending(n => n.State.End?.What == "completed").ThenByDescending(n => Created(n.State.Header)).FirstOrDefault();
            found.Add(best.State is null ? null : (parent, best.Folder, best.State, best.Shift));
        }
        if (found.All(f => f is null)) return null;
        if (found.Any(f => f is null))
        {
            string none = string.Join(" and ", parents.Where((_, k) => found[k] is null));
            refusal = $"You can add new files only to an earlier backup that is on each backup drive. There is no backup of this card in {none}. "
                      + "A new backup copies the whole card.";
            return null;
        }
        var folders = found.Select(f => f!.Value).ToList();
        IEnumerable<string> ofThisCardIn(string folder) => Journals(folder).Where(j => JournalReader.TryReadHeader(j) is { Kind: JobKind.Backup } h && SameCard(h, scan.SourceRoot, sourceSerial));
        if (folders.Select(f => f.State.Header.Id).Distinct().Count() > 1)
        {
            refusal = $"The backup drives have different earlier backups of this card ({string.Join(" and ", folders.Select(f => f.Folder))}), "
                      + "so you cannot add new files to one of them. A new backup copies the whole card.";
            return null;
        }
        if (folders.Select(f => f.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() < folders.Count) return null; // the same destination twice: said elsewhere

        long common = folders[0].Shift;
        var earlier = new Dictionary<string, EarlierFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
            foreach ((string rel, EarlierFile e) in EarlierFiles(f.State))
                earlier.TryAdd(rel, e);

        // A card whose own ASC MHL history changed since (another tool offloaded it and added a generation) can't be added
        // to: the folder's copy of that history already carries this app's generation, and the two can't be joined.
        foreach ((string rel, EarlierFile e) in earlier)
            if (AscMhl.IsChainFile(rel) && card.TryGetValue(rel, out SourceFile? chain) && (chain.Size != e.Size || chain.LastWriteTime - e.LastWriteTime != common))
            {
                refusal = $"The ASC MHL history of the card ({rel}) changed after the backup to {string.Join(" and ", folders.Select(f => f.Folder))}, "
                          + "because a different offload added to it. You cannot add new files to that backup. A new backup copies the whole card.";
                return null;
            }

        // A card that has an ASC MHL history now that it had not then, where the folder has its own (this app's): the two
        // can't be joined either.
        foreach (SourceFile f in scan.Files)
            if (AscMhl.IsChainFile(f.RelativePath) && !earlier.ContainsKey(f.RelativePath) && folders.Any(x => File.Exists(Path.Join(x.Folder, f.RelativePath))))
            {
                refusal = $"The card has an ASC MHL history ({f.RelativePath}) that it did not have at the time of the backup to "
                          + $"{string.Join(" and ", folders.Select(x => x.Folder))}. A different offload wrote it. You cannot add new files to that backup. "
                          + "A new backup copies the whole card.";
                return null;
            }

        var offer = new TopUpOffer
        {
            JobId = folders[0].State.Header.Id,
            Created = folders[0].State.Header.Created,
            Folders = [],
            Earlier = earlier.Where(e => card.ContainsKey(e.Key)).ToDictionary(StringComparer.OrdinalIgnoreCase),
            Kept = earlier.Values.Where(e => !card.ContainsKey(e.Rel)).OrderBy(e => e.Rel, StringComparer.OrdinalIgnoreCase).ToList(),
            Shift = common,
        };
        int unchanged = 0, added = 0;
        long newBytes = 0;
        var changed = new List<string>();
        foreach (SourceFile f in scan.Files)
        {
            if (!offer.Earlier.TryGetValue(f.RelativePath, out EarlierFile? e))
            {
                added++;
                newBytes += f.Size;
            }
            else if (offer.IsUnchanged(f, e)) unchanged++;
            else changed.Add(f.RelativePath);
        }
        return new TopUpOffer
        {
            JobId = offer.JobId,
            Created = offer.Created,
            Earlier = offer.Earlier,
            Kept = offer.Kept,
            Shift = common,
            EndedEarly = folders.Any(f => f.State.End?.What == "closed"),
            New = added,
            Changed = changed.Count,
            ChangedFiles = changed.Where(c => !IsCameraIndex(c)).ToList(),
            Unchanged = unchanged,
            NewBytes = newBytes,
            Folders = folders.Select(f => Look(f.Parent, f.Folder, f.State, scan.Files, offer, ofThisCardIn(f.Folder))).ToList(),
        };
    }

    /// <summary>What a top-up writes into one earlier backup folder: every card file that is not there as the card has it now.</summary>
    /// <param name="logs">Every backup log of this card in the folder (the earlier backup's lineage).</param>
    private static TopUpFolder Look(string parent, string folder, JobState state, List<SourceFile> files, TopUpOffer offer, IEnumerable<string> logs)
    {
        var verified = EarlierFiles(state).Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ownChains = ChainsCarryOwnHistory(state, logs);
        var toCopy = new List<SourceFile>();
        int missing = 0, edited = 0;
        long missingBytes = 0;
        foreach (SourceFile f in files)
        {
            if (!verified.Contains(f.RelativePath) || !offer.Earlier.TryGetValue(f.RelativePath, out EarlierFile? e) || !offer.IsUnchanged(f, e)) toCopy.Add(f);
            else if (!InFolder(folder, e, anySize: ownChains && AscMhl.IsChainFile(e.Rel)))
            {
                toCopy.Add(f);
                if (InFolder(folder, e, anySize: true)) edited++;
                else
                {
                    missing++;
                    missingBytes += f.Size;
                }
            }
        }
        return new TopUpFolder
        {
            Parent = parent,
            Folder = folder,
            Earlier = state,
            ToCopy = toCopy,
            Missing = missing,
            MissingBytes = missingBytes,
            Edited = edited,
            Verified = verified,
            ChainsCarryOwnHistory = ownChains,
            Kept = offer.Kept.Count(k => verified.Contains(k.Rel) && InFolder(folder, k, anySize: false)),
        };
    }

    /// <summary>A backup of this card in the folder wrote an ASC MHL generation (into the chains copied from the card, too).</summary>
    private static bool ChainsCarryOwnHistory(JobState state, IEnumerable<string> logs)
    {
        if (state.Mhl is not null) return true;
        foreach (string log in logs)
        {
            try
            {
                if (JournalReader.Read(log).Mhl is not null) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
            {
            }
        }
        return false;
    }

    private static bool InFolder(string folder, EarlierFile f, bool anySize)
    {
        try
        {
            var info = new FileInfo(Path.Join(folder, f.Rel));
            return info.Exists && (anySize || info.Length == f.Size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a finished backup's log says its folder holds: every file it verified, and in a top-up that was ended early,
    /// the files it did not get to check again but that an earlier backup had verified there (still with those checksums).
    /// </summary>
    private static IEnumerable<(string Rel, EarlierFile File)> EarlierFiles(JobState state)
    {
        foreach (JobItem i in state.Items)
        {
            if (i.Stage == ItemStage.Done && i.Sha256 is { } sha && i.Xxh64 is { } xxh)
                yield return (i.Rel, new EarlierFile(i.Rel, i.Size, i.CreationTime, i.LastWriteTime, sha, xxh));
            // The earlier version of a file changed on the card, kept next to the new one: a file of the folder like any other.
            if (i.Stage == ItemStage.Done && BackupRunner.IsBeside(i) && i.Beside is { } b)
                yield return (i.SetAside!, new EarlierFile(i.SetAside!, b.Size, b.CreationTime, b.LastWriteTime, b.Sha256, b.Xxh64));
            else if (state.IsTopUp && i.Stage == ItemStage.Skipped && i.Note?.StartsWith(BackupReasons.NotCheckedEnded, StringComparison.Ordinal) == true
                     && i.ExpectedSha256 is { } expected && i.ExpectedXxh64 is { } expectedXxh)
                yield return (i.Rel, new EarlierFile(i.Rel, i.Size, i.CreationTime, i.LastWriteTime, expected, expectedXxh));
        }
    }

    /// <summary>
    /// The earlier backup is of this card as it is now: at least half of the files it verified (camera index files aside,
    /// which cameras rewrite as they record) are on the card with the same name, size and date. Files deleted in the
    /// camera and new shots since are fine. The dates may all be off by the same whole quarter hours (see <see cref="Matched"/>).
    /// </summary>
    private static bool Similar(JobState state, Dictionary<string, SourceFile> card, out long shift)
    {
        // Files no longer on the card, and earlier versions kept next to new ones, say nothing about which card this is.
        var kept = state.Items.Where(i => i.Why == BackupWhy.Kept).Select(i => i.Rel)
            .Concat(state.Items.Where(BackupRunner.IsBeside).Select(i => i.SetAside!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = EarlierFiles(state).Select(f => f.File).Where(f => !IsCameraIndex(f.Rel) && !kept.Contains(f.Rel)).ToList();
        // Every file of the earlier backup that is still on the card with its size says how far the card's dates are off.
        var shifts = new Dictionary<long, int>();
        foreach (EarlierFile f in files)
            if (card.TryGetValue(f.Rel, out SourceFile? now) && now.Size == f.Size)
            {
                long d = now.LastWriteTime - f.LastWriteTime;
                if (d % QuarterHour == 0 && Math.Abs(d) <= MaxShift) shifts[d] = shifts.GetValueOrDefault(d) + 1;
            }
        shift = shifts.Count == 0 ? 0 : shifts.OrderByDescending(s => s.Value).ThenBy(s => Math.Abs(s.Key)).First().Key;
        long common = shift;
        int matched = files.Count(f => card.TryGetValue(f.Rel, out SourceFile? now) && now.Size == f.Size && now.LastWriteTime - f.LastWriteTime == common);
        return matched > 0 && matched * 2 >= files.Count;
    }

    private static bool SameCard(JobHeader h, string source, uint sourceSerial) =>
        sourceSerial != 0 && h.SourceSerial != 0
            ? h.SourceSerial == sourceSerial && string.Equals(BelowRoot(h.Source), BelowRoot(source), StringComparison.OrdinalIgnoreCase)
            : JobPaths.SamePath(h.Source, source);

    /// <param name="parents">The destination folders the user picked (E:\Cards), not the backup folders inside them.</param>
    /// <param name="exclude">The backup folders of this backup (checked by <see cref="BackupPlanner.ValidateDestination"/>).</param>
    /// <param name="unfinished">Receives the logs of unfinished backups of this card that were found.</param>
    /// <param name="skipJob">
    /// The earlier backup the preview offers to add to: no message for it, nor for older finished backups of this card
    /// (unfinished ones are still named, to resume them).
    /// </param>
    public static IEnumerable<PlanMessage> Check(BackupScan scan, uint sourceSerial, IEnumerable<string> parents, IReadOnlyCollection<string> exclude,
        List<string> unfinished, string? skipJob = null)
    {
        if (scan.Files.Count == 0) return [];
        var card = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFile f in scan.Files) card[f.RelativePath] = f;
        long bytes = scan.Bytes;

        // Cheap first, from the logs' first lines: the whole card again (same count and size), or an earlier backup of
        // this very card (same drive serial and folder) that had fewer files. Only these logs are read in full, newest first.
        var candidates = new List<(string Journal, string Folder, JobHeader Header, bool Whole)>();
        foreach (string folder in Candidates(parents, exclude))
            foreach (string journal in Journals(folder))
            {
                if (JournalReader.TryReadHeader(journal) is not { Kind: JobKind.Backup } h) continue;
                if (unfinished.Any(u => JobPaths.SamePath(u, journal))) continue;
                bool whole = h.Count == scan.Files.Count && h.Bytes == bytes;
                bool earlierOfThisCard = sourceSerial != 0 && h.SourceSerial == sourceSerial && h.Count < scan.Files.Count && h.Bytes <= bytes
                                         && string.Equals(BelowRoot(h.Source), BelowRoot(scan.SourceRoot), StringComparison.OrdinalIgnoreCase);
                if (whole || earlierOfThisCard) candidates.Add((journal, folder, h, whole));
            }

        var found = new List<Found>();
        string? wholeJob = null, partJob = null; // the newest finished backup of each kind: one message each, however many there are
        foreach (var c in candidates.OrderByDescending(c => Created(c.Header)))
        {
            string? chosen = c.Whole ? wholeJob : partJob;
            if (chosen is not null && chosen != c.Header.Id) continue;
            JobState state;
            try
            {
                state = JournalReader.Read(c.Journal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
            {
                continue;
            }
            if (!state.PlanComplete || state.Items.Count == 0) continue;
            // The preview offers to add to the newest backup of this card: older ones are not described again.
            if (skipJob is not null && (state.Header.Id == skipJob || state.IsEnded)) continue;
            // A top-up's files that are no longer on the card are not part of what it copied from the card.
            List<JobItem> fromCard = state.Items.Where(i => i.Why != BackupWhy.Kept).ToList();
            int matched = Matched(fromCard, card);
            if (matched != fromCard.Count || matched == 0) continue; // not every file of that backup is on the card as it was
            if (state.IsEnded || !c.Whole)
            {
                if (c.Whole) wholeJob = c.Header.Id;
                else partJob = c.Header.Id;
            }
            found.Add(new Found(state, c.Folder, matched, Gone(state, c.Folder)));
        }
        return Describe(found, scan.Files.Count, unfinished);
    }

    private static List<PlanMessage> Describe(List<Found> found, int files, List<string> unfinished)
    {
        var messages = new List<PlanMessage>();
        // One message per earlier backup (its copies on several destinations are named together).
        foreach (var job in found.GroupBy(f => f.State.Header.Id))
        {
            JobState state = job.First().State;
            string when = Date(state.Header.Created);
            int fromCard = state.Items.Count(i => i.Why != BackupWhy.Kept);
            bool whole = fromCard == files;
            string where = string.Join(" and ", job.Select(f => f.Folder));
            if (!state.IsEnded)
            {
                if (!whole) continue; // an unfinished backup of fewer files: resuming it would not copy the new ones
                unfinished.AddRange(job.Select(f => f.State.JournalPath));
                messages.Add(new PlanMessage(MessageLevel.Warning,
                    $"This card has an unfinished backup in {where} (started {when}). If you do not want one more copy, resume that backup instead of a new one."));
                continue;
            }
            var intact = job.Where(f => f.Gone == 0).ToList();
            var lessened = job.Where(f => f.Gone > 0).ToList();
            // Files a sort moved out, or that were deleted: the log alone never counts as a copy.
            string gone = lessened.Count == 0 ? ""
                : $" {string.Join(" and ", lessened.Select(f => f.Folder))} no longer {(lessened.Count == 1 ? "contains" : "contain")} "
                  + $"{Count(lessened.Max(f => f.Gone))} of that backup (because of a sort, a change or a deletion).";
            bool allVerified = job.All(f => f.State.Items.Where(i => i.Why != BackupWhy.Kept).All(i => i.Stage == ItemStage.Done));
            int matched = job.First().Matched;
            if (whole)
                messages.Add(new PlanMessage(MessageLevel.Warning, !allVerified
                    ? $"There is a backup of this card from {when} in {where}, but that backup ended before it copied all files.{gone} A new backup copies the whole card."
                    : intact.Count > 0
                        ? $"There is already a backup of this card from {when}. {string.Join(" and ", intact.Select(f => f.Folder))} {(intact.Count == 1 ? "contains" : "contain")} "
                          + $"the same {Count(files)} (same names, sizes and dates). That backup checked all of them.{gone} A new backup makes one more copy."
                        : $"There is a backup of this card from {when} in {where}, but not all of it is there now:{gone} A new backup copies the whole card."));
            else
                messages.Add(new PlanMessage(lessened.Count > 0 ? MessageLevel.Warning : MessageLevel.Info,
                    $"{matched:N0} of the {Count(files)} on the card {(matched == 1 ? "is" : "are")} "
                    + $"{(allVerified ? "already in a backup" : "in a backup that ended early")} from {when} in {where}.{gone} "
                    + $"The card has {Count(files - matched)} that {(files - matched == 1 ? "is" : "are")} not in that backup. This backup copies the whole card."));
        }
        return messages;
    }

    /// <summary>
    /// How many of the backup's files are on the card with the same name, size and date, counted until the first that
    /// is not. The dates may all be off by the same whole quarter hours (a FAT card keeps local time, so a backup made
    /// before a daylight-saving change or in another time zone reads it shifted), but never by different amounts.
    /// </summary>
    private static int Matched(List<JobItem> items, Dictionary<string, SourceFile> card)
    {
        long? shift = null;
        int matched = 0;
        foreach (JobItem i in items)
        {
            if (!card.TryGetValue(i.Rel, out SourceFile? f) || f.Size != i.Size) return matched;
            long d = f.LastWriteTime - i.LastWriteTime;
            shift ??= d % QuarterHour == 0 && Math.Abs(d) <= MaxShift ? d : 0;
            if (d != shift) return matched;
            matched++;
        }
        return matched;
    }

    /// <summary>Files the backup verified that are no longer in its folder with their size.</summary>
    private static int Gone(JobState state, string folder)
    {
        int gone = 0;
        foreach (JobItem i in state.Items.Where(i => i.Stage == ItemStage.Done))
        {
            try
            {
                var info = new FileInfo(Path.Join(folder, i.Rel));
                if (!info.Exists || info.Length != i.Size) gone++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                gone++;
            }
        }
        return gone;
    }

    /// <summary>The folders next to where this backup goes, and each destination folder itself (it may be an earlier backup).</summary>
    private static IEnumerable<string> Candidates(IEnumerable<string> parents, IReadOnlyCollection<string> exclude)
    {
        var seen = new List<string>();
        foreach (string parent in parents)
        {
            if (seen.Any(s => JobPaths.SamePath(s, parent))) continue;
            seen.Add(parent);
            yield return parent;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(parent).Take(MaxFolders).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            foreach (string child in children)
                if (!exclude.Any(x => JobPaths.SamePath(x, child)))
                    yield return child;
        }
    }

    private static IEnumerable<string> Journals(string folder)
    {
        try
        {
            return BackupPaths.FindJournals(folder).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static DateTimeOffset Created(JobHeader h) =>
        DateTimeOffset.TryParse(h.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t) ? t : DateTimeOffset.MinValue;

    private static string BelowRoot(string path)
    {
        try
        {
            return Path.GetRelativePath(Path.GetPathRoot(path) ?? path, path).TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string Date(string? created) => created is { Length: >= 16 } at ? at[..16].Replace('T', ' ') : "an earlier day";

    private static string Count(int n) => n == 1 ? "1 file" : $"{n:N0} files";
}
