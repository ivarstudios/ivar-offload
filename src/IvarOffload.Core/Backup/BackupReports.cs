using System.Globalization;
using System.Text;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;

namespace IvarOffload.Core.Backup;

/// <summary>
/// Why a file has no verified copy on a destination, in plain words. Written to the backup's logs and shown to the
/// user, so the texts must stay stable.
/// </summary>
public static class BackupReasons
{
    public const string NotCopiedEnded = "not copied - the backup was ended before this file was copied";
    public const string ChangedOnCard = "changed on the card after the preview - not copied; start a new backup to copy it";
    public const string GoneFromCard = "no longer on the card (removed after the preview) - not copied";
    public const string ChangedWhileCopied = "The file changed on the card while it was being copied. The copy was removed; Resume copies it again.";
    public const string CardReadTwiceDiffers =
        "The card returned different data when this file was read twice. The copies were removed; Resume copies it again. Check the card, reader, cable or port.";
    public const string NameTakenInDestination =
        "A file with this name appeared in the destination during the backup. It was not replaced - check it, then Resume.";
    public const string DifferentInDestination =
        "The file in the destination does not match the card any more. It was not replaced - check the destination drive.";
    public const string TooLargeForDestination =
        "This file is 4 GB or larger and the destination drive (FAT32) cannot store files that big. Use an NTFS or exFAT drive.";
    public const string StreamsNotSupported =
        "This file carries extra hidden metadata (alternate data streams) that the destination drive cannot store. Use an NTFS drive.";
    public const string CopyMismatchTwice = "The copy did not match the card twice (checksum mismatch) - check the destination drive.";
    public const string DiffersFromOtherCopies =
        "The card now returns different content for this file than when it was copied to another destination. The new copy was removed; "
        + "check the card and the copies already made.";

    // Top-ups (adding a card's new files to its earlier backup).
    public const string NotCheckedEnded =
        "not read again - the backup was ended before this file was checked (it is still in the backup folder, as the earlier backup verified it)";
    public const string KeptGone = "no longer on the card, and no longer in this backup folder (moved out by a sort, or deleted)";
    public const string CardDiffersFromBackup =
        "The card returned other data for this file than the earlier backup verified (same size and date), and different data again when it "
        + "was read a second time. The copy in the backup was kept; check the card, reader, cable or port, then Resume.";
    /// <summary>Why a file in the backup folder was moved into _IVAROffload\replaced (the card's version is copied in its place).</summary>
    public const string AsideChanged = "changed on the card since the earlier backup - this is the older version";
    public const string AsideDamaged = "no longer matched the checksum the earlier backup verified - damaged since";
    public const string AsideNotFromBackup = "was in the backup folder, but not from the earlier backup";
    public const string AsideEdited = "changed in the backup folder since the earlier backup (another size or date - edited by another program?)";
    public const string KeptEdited =
        "no longer on the card, and changed in the backup folder since the earlier backup (edited by another program?) - left as it is, not verified";

    public const string KeptDiffers =
        "no longer on the card, and the copy here no longer matches its checksum (same size and date, read alike twice - damaged on this drive since the earlier backup?) - left as it is";
    public const string KeptReadDiffers =
        "no longer on the card, and the copy here read differently twice - check this drive, its cable or port, then Resume";

    private const string CardErrorPrefix ="the card stopped responding while this file was being read (";
    private const string DestinationErrorPrefix = "the destination stopped responding while this file was being written (";

    public static string CardError(string detail) => $"{CardErrorPrefix}{detail})";

    public static bool IsCardError(string? reason) => reason?.StartsWith(CardErrorPrefix, StringComparison.Ordinal) == true;

    public static string DestinationError(string detail) => $"{DestinationErrorPrefix}{detail})";
}

/// <summary>
/// The summary and the checksum list each destination keeps next to its log (in _IVAROffload), so the backup can be
/// checked without the app. Rewritten while the backup runs and when it ends.
/// </summary>
public static class BackupReports
{
    public static void TryWrite(JobState state, IReadOnlyList<string> destinations)
    {
        try
        {
            WriteAtomically(BackupPaths.SummaryPath(state.JournalPath), Summary(state, destinations));
            WriteAtomically(BackupPaths.ManifestPath(state.JournalPath), Manifest(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is the record; the reports are a convenience and are written again later.
        }
    }

    public static string Summary(JobState state, IReadOnlyList<string>? destinations = null)
    {
        JobHeader h = state.Header;
        var sb = new StringBuilder();
        sb.AppendLine($"IVAR Offload backup {h.Id}");
        sb.AppendLine($"Card:         {h.SourceLabel} ({h.Source})");
        sb.AppendLine($"This copy:    {h.Target}");
        IReadOnlyList<string> all = destinations ?? h.Targets;
        if (all.Count > 1) sb.AppendLine($"All copies:   {string.Join(" | ", all)}");
        sb.AppendLine($"Started:      {h.Created} on {h.Machine} ({h.Tool})");
        if (h.AddsTo is not null) sb.AppendLine($"Adds to:      backup {h.AddsTo} in this folder (this backup adds the card's new files to it and reads every file of the card again; the Status line says how far it got)");
        sb.AppendLine($"Status:       {Status(state)}");
        List<JobItem> card = state.Items.Where(i => i.Why != BackupWhy.Kept).ToList();
        List<JobItem> kept = state.Items.Where(i => i.Why == BackupWhy.Kept).ToList();
        List<JobItem> done = card.Where(i => i.Stage == ItemStage.Done).ToList();
        sb.AppendLine($"Verified:     {done.Count:N0} of {card.Count:N0} files ({Format.Bytes(done.Sum(i => i.Size))} of {Format.Bytes(card.Sum(i => i.Size))})"
            + " - each copy read back from this drive and compared by SHA-256" + (h.Reread ? ", and each card file read twice." : "."));
        if (h.AddsTo is not null)
        {
            int copied = done.Count(i => i.How == "copy"), again = done.Count(i => i.How == "verify");
            sb.AppendLine($"              {copied:N0} copied now; {again:N0} already in the backup, read again from the card and from the copy (both matched the earlier checksum).");
        }
        if (state.Mhl is not null) sb.AppendLine($"ASC MHL:      {state.Mhl}");
        if (state.MhlRestarted is not null && state.Mhl is not null)
            sb.AppendLine($"              a new ASC MHL history was started: the earlier one lists files that changed or are gone since (an ASC MHL history can't record that). It was moved to {state.MhlRestarted}.");
        if (state.MhlSkipped is not null) sb.AppendLine($"ASC MHL:      not written - {state.MhlSkipped}");
        var failed = state.Items.Where(i => i.Failed && !i.IsFinished).ToList();
        var notCopied = card.Where(i => i.Stage == ItemStage.Skipped).ToList();
        var pending = card.Where(i => !i.IsFinished && !i.Failed).ToList();
        var aside = state.Items.Where(i => i.SetAside is not null && i.Stage == ItemStage.Done).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Failed ({failed.Count:N0}) - Resume tries them again:");
            foreach (JobItem i in failed) sb.AppendLine($"  {i.Rel}  -  {i.Note}");
        }
        if (notCopied.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Not copied ({notCopied.Count:N0}):");
            foreach (JobItem i in notCopied) sb.AppendLine($"  {i.Rel}  -  {i.Note}");
        }
        if (pending.Count > 0 && state.IsEnded is false)
        {
            sb.AppendLine();
            sb.AppendLine($"Not copied yet: {pending.Count:N0} files ({Format.Bytes(pending.Sum(i => i.Size))}).");
        }
        if (aside.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Moved aside ({aside.Count:N0}) - the files that were in the backup folder under these names, kept (never overwritten or deleted):");
            foreach (JobItem i in aside) sb.AppendLine($"  {i.Rel}  ->  {i.SetAside}  -  {i.SetAsideWhy}");
        }
        if (kept.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"No longer on the card ({kept.Count:N0}) - files of the earlier backup, kept in it:");
            foreach (JobItem i in kept)
                sb.AppendLine($"  {i.Rel}  -  {(i.Stage == ItemStage.Done ? "still in the backup, verified" : i.Note ?? (i.Failed ? "failed" : "not checked yet"))}");
        }
        if (state.HeldBack.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Left out of the backup ({state.HeldBack.Count:N0}):");
            foreach (HeldFile f in state.HeldBack) sb.AppendLine($"  {f.Rel}  -  {f.Why}");
        }
        sb.AppendLine();
        sb.AppendLine("The card was only read, never written to. A verified backup is not a reason to format the card by itself: "
            + "keep it until the footage is safe in at least two places you trust.");
        return sb.ToString();
    }

    private static string Status(JobState state) => state.End?.What switch
    {
        "completed" when state.Items.All(i => i.Why == BackupWhy.Kept || i.Stage == ItemStage.Done) && state.HeldBack.Count == 0 => "finished - every file copied and verified",
        "completed" => "finished - some files were not copied (see below)",
        "closed" => "ended early - not every file was copied",
        _ => state.Items.Any(i => i.Stage != ItemStage.Pending || i.Failed) ? "unfinished - press Resume to continue" : "not started",
    };

    private static string Manifest(JobState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,size,modified_utc,sha256,xxh64,status");
        foreach (JobItem i in state.Items)
        {
            string status = i.Stage switch
            {
                ItemStage.Done when i.How == "kept" => "no longer on the card: kept in the backup, verified",
                ItemStage.Done when i.How == "verify" => "verified (already in the backup, read again)",
                ItemStage.Done => "verified",
                ItemStage.Skipped when i.Why == BackupWhy.Kept => i.Note ?? "",
                ItemStage.Skipped => "not copied: " + i.Note,
                _ => i.Failed ? "failed: " + i.Note : "not copied yet",
            };
            sb.Append(Csv(i.Rel)).Append(',').Append(i.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(DateTime.FromFileTimeUtc(i.LastWriteTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.Stage == ItemStage.Done ? i.Sha256 : "").Append(',').Append(i.Stage == ItemStage.Done ? i.Xxh64 : "").Append(',')
              .AppendLine(Csv(status));
        }
        return sb.ToString();
    }

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static void WriteAtomically(string path, string text)
    {
        string temp = path + ".partial";
        File.WriteAllText(temp, text, new UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The result of reading a backup's copies again on one destination.</summary>
/// <param name="MovedOut">Copies no longer in the backup folder because a sort moved them out (its receipt says where to).</param>
/// <param name="NotKept">Top-ups: files no longer on the card that the top-up found gone from this folder, changed or damaged (it said so then).</param>
public sealed record BackupVerifyDestination(string Folder, string DriveName, int Checked, int Matched, IReadOnlyList<string> Problems, string? NotReachable,
    int MovedOut = 0, int NotKept = 0)
{
    public bool AllGood => NotReachable is null && Problems.Count == 0;
}

public sealed record BackupVerifyResult(int Files, IReadOnlyList<BackupVerifyDestination> Destinations)
{
    /// <summary>
    /// Every destination is reachable, and every copy there matches its checksum: every file of the backup, except the
    /// ones a sort moved out of the backup folder since (<see cref="BackupVerifyDestination.MovedOut"/>).
    /// </summary>
    public bool AllGood => Destinations.Count > 0 && Destinations.All(d => d.AllGood && d.Matched + d.MovedOut + d.NotKept == Files);
}

/// <summary>Reads every copy of a backup back from its destination (bypassing the cache) and compares its SHA-256 with the log.</summary>
public static class BackupVerifier
{
    public static BackupVerifyResult Verify(string journalPath, IProgress<(int Done, int Total, string File)>? progress = null, CancellationToken ct = default)
    {
        JobState first = JournalReader.Read(journalPath);
        if (!first.IsBackup) throw new JournalException("This is not the log of a backup.");
        JobHeader h = first.Header;
        IReadOnlyList<string> targets = h.Targets.Count > 0 ? h.Targets : [h.Target];
        int total = first.Items.Count * targets.Count, done = 0;
        var results = new List<BackupVerifyDestination>();
        for (int k = 0; k < targets.Count; k++)
        {
            string folder = BackupPaths.FindDestination(targets[k], h.TargetSerials.ElementAtOrDefault(k), h.Id); // also under another letter
            string label = h.TargetLabels.ElementAtOrDefault(k) ?? "";
            string drive = label.Length > 0 ? $"{Drives.Letter(folder)} ({label})" : Drives.Letter(folder);
            string journal = BackupPaths.JournalPath(folder, h.Id);
            JobState state;
            try
            {
                state = JournalReader.Read(journal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
            {
                results.Add(new BackupVerifyDestination(folder, drive, 0, 0, [], File.Exists(journal) ? e.Message : $"{drive} is not connected (or the backup folder was renamed): {folder}"));
                done += first.Items.Count;
                continue;
            }
            int matched = 0, checkedCount = 0, movedOut = 0, notKept = 0;
            HashSet<string> sorted = MovedOutBySort(folder);
            var problems = new List<string>();
            foreach (JobItem item in state.Items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, total, item.Rel));
                if (item.Stage == ItemStage.Done && BackupRunner.IsBeside(item) && item.Beside is { } beside)
                {
                    // The earlier version kept next to it is part of the folder too.
                    string besidePath = Path.Join(folder, item.SetAside);
                    try
                    {
                        if (SafeFile.TrySnapshot(besidePath) is not { IsDirectory: false } b) problems.Add($"{item.SetAside}: missing from the destination");
                        else if (b.Size != beside.Size || SafeFile.ToHex(SafeFile.HashFile(besidePath, b.Size, null, ct)) != beside.Sha256)
                            problems.Add($"{item.SetAside}: CHECKSUM MISMATCH - the copy is damaged");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        problems.Add($"{item.SetAside}: could not be read ({e.Message})");
                    }
                }
                if (item.Stage != ItemStage.Done && item.Why == BackupWhy.Kept && item.IsFinished)
                {
                    notKept++; // not on the card, and not in this folder as the earlier backup verified it: the top-up said so
                    continue;
                }
                if (item.Stage != ItemStage.Done)
                {
                    problems.Add($"{item.Rel}: no verified copy on this destination ({item.Note ?? "not copied yet"})");
                    continue;
                }
                checkedCount++;
                string path = Path.Join(folder, item.Rel);
                try
                {
                    if (SafeFile.TrySnapshot(path) is not { IsDirectory: false } s)
                    {
                        if (sorted.Contains(item.Rel)) movedOut++; // "Sort this backup" moved it on; the sort's receipt says where
                        else problems.Add($"{item.Rel}: missing from the destination");
                    }
                    else if ((state.Mhl is not null || item.Why == BackupWhy.Chain) && IsUpdatedChain(item.Rel)) matched++; // the backup (or the one it added to) added its ASC MHL generation to it
                    else if (s.Size != item.Size) problems.Add($"{item.Rel}: size changed ({s.Size:N0} bytes, the card's was {item.Size:N0})");
                    else if (SafeFile.ToHex(SafeFile.HashFile(path, s.Size, null, ct)) != item.Sha256) problems.Add($"{item.Rel}: CHECKSUM MISMATCH - the copy is damaged");
                    else matched++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{item.Rel}: could not be read ({e.Message})");
                }
            }
            results.Add(new BackupVerifyDestination(folder, drive, checkedCount - movedOut, matched, problems, null, movedOut, notKept));
        }
        return new BackupVerifyResult(first.Items.Count, results);
    }

    /// <summary>
    /// The files (relative to the backup folder) that sorts moved out of it: each sort leaves a receipt in the folder's
    /// _IVAROffload folder that names every file that left by its full path there.
    /// </summary>
    private static HashSet<string> MovedOutBySort(string folder)
    {
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string prefix = Path.TrimEndingDirectorySeparator(folder) + "\\";
            // Receipts of sorts by this app or an earlier version of it (_IVARIngest, _IngestSorter).
            foreach (string logs in JobPaths.LogFolderNames.Select(name => Path.Join(folder, name)).Where(Directory.Exists))
            foreach (string receipt in Directory.EnumerateFiles(logs, "*" + JobPaths.ReceiptCsvSuffix))
                foreach (string line in File.ReadLines(receipt))
                    foreach (string cell in line.Split([',', ';', '\t']))
                    {
                        string path = cell.Trim().Trim('"');
                        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) moved.Add(path[prefix.Length..]);
                    }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return moved;
    }

    /// <summary>An ASC MHL chain file copied from the card, to which the backup added its own generation.</summary>
    private static bool IsUpdatedChain(string rel) =>
        Path.GetFileName(rel) == AscMhl.ChainFileName && Path.GetFileName(Path.GetDirectoryName(rel)) == AscMhl.FolderName;
}
