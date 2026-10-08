using IvarOffload.Core;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>
/// What the Backup tab says: the result card after a backup (green only when every file of the card has a verified
/// copy on every destination), verify-again, the unfinished-backup banner and the mode descriptions. Never "safe to
/// format": that is the user's decision, and a copy alone is not a reason to wipe a card.
/// </summary>
public static class BackupTexts
{
    /// <summary>Shown under the mode tabs.</summary>
    public const string BackupDescription =
        "Copies a memory card (or any folder) to one to three drives, bit for bit. Every copy is read back from its drive and compared by checksum, "
        + "and ASC MHL checksum files are written next to it. The card is only read, never changed.";

    public const string SortDescription =
        "Sorts a card backup: moves the videos (or the photos) into their own folder with the same sub-folders. Nothing is ever overwritten, "
        + "originals are only removed after a verified move, every step is logged on both sides, and any sort can be undone.";

    /// <summary>The red line kept on the result card while the card still holds files the backup does not have.</summary>
    public const string KeepTheCard = "Don't format the card: it still holds files that are not in the backup.";

    /// <summary>
    /// The green title: only what happened ("Copied to 2 drives"), never advice about the card. The drives counted are
    /// drives of their own; copies that share a drive are never green.
    /// </summary>
    public static string CopiedTitle(int drives) => $"Copied to {RunOutcome.Count(drives, "drive")}";

    /// <summary>"All 412 files were copied and checked. The card was not changed."</summary>
    public static string AllCheckedLine(int files) =>
        (files == 1 ? "The file was copied and checked." : $"All {RunOutcome.Count(files, "file")} were copied and checked.") + " The card was not changed.";

    /// <summary>
    /// A top-up: "38 new files copied, all 450 files checked. The card was not changed.", with the files that were copied
    /// again (changed on the card, or no longer in the backup folder) counted apart from the new ones.
    /// </summary>
    public static string TopUpCheckedLine(int newFiles, int again, int files) =>
        (newFiles == 0 && again == 0 ? "Nothing new to copy" : newFiles == 0 ? $"{RunOutcome.Count(again, "file")} copied again"
            : $"{RunOutcome.Count(newFiles, "new file")} copied" + (again > 0 ? $", {again:N0} copied again" : ""))
        + $", all {RunOutcome.Count(files, "file")} checked. The card was not changed.";

    /// <summary>A top-up: what happened to the files the earlier backup held, in plain words (none for a full backup).</summary>
    public static List<string> TopUpLines(BackupResult r)
    {
        var lines = new List<string>();
        if (r.AddsTo is null) return lines;
        int replaced = r.Destinations.Max(d => d.Replaced), restored = r.Destinations.Max(d => d.Restored);
        int kept = r.Destinations.Max(d => d.Kept), gone = r.Destinations.Max(d => d.KeptGone);
        string replacedFolder = $"{JobPaths.LogFolderNameOf(r.Destinations[0].JournalPath)}\\{BackupRunner.ReplacedFolderName}";
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptBeside.Count > 0).Take(1))
            lines.Add($"{RunOutcome.Count(d.KeptBeside.Count, "photo or clip", "photos or clips")} changed on the card since the earlier backup (usually another shot with a reused number): "
                      + $"copied, and the backup's earlier version kept next to {(d.KeptBeside.Count == 1 ? "it" : "each")}, e.g. {d.KeptBeside[0]}.");
        if (replaced > 0)
            lines.Add($"{RunOutcome.Count(replaced, "camera index file")} changed on the card since the earlier backup: copied again. The old version{(replaced == 1 ? " is" : "s are")} kept in {replacedFolder} in the backup folder.");
        if (restored > 0)
            lines.Add($"{RunOutcome.Count(restored, "file")} no longer in the backup folder (moved out by a sort, or deleted) {(restored == 1 ? "was" : "were")} copied again from the card.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.Repaired > 0))
            lines.Add($"{RunOutcome.Count(d.Repaired, "copy", "copies")} in {d.Folder} no longer matched {(d.Repaired == 1 ? "its" : "their")} checksum (damaged on {d.DriveName} since the earlier backup): "
                      + $"moved to {replacedFolder} and copied again from the card. Check that drive.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.Edited > 0))
            lines.Add($"{RunOutcome.Count(d.Edited, "file")} in {d.Folder} had been changed there since the earlier backup (edited by another program?) or "
                      + $"{(d.Edited == 1 ? "was" : "were")} not from it: moved to {replacedFolder}, and the card's file copied in {(d.Edited == 1 ? "its" : "their")} place.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptEdited > 0))
            lines.Add($"{RunOutcome.Count(d.KeptEdited, "file")} in {d.Folder} that {(d.KeptEdited == 1 ? "is" : "are")} no longer on the card had been changed there since "
                      + "the earlier backup (edited by another program?): left as they are, not verified.");
        if (kept > 0) lines.Add($"{RunOutcome.Count(kept, "file")} in the backup {(kept == 1 ? "is" : "are")} no longer on the card: {(kept == 1 ? "it stays" : "they stay")} in the backup, verified.");
        if (gone > 0) lines.Add($"{RunOutcome.Count(gone, "file")} of the earlier backup {(gone == 1 ? "is" : "are")} neither on the card nor in the backup folder any more (moved out by a sort, or deleted).");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.MhlRestarted is not null))
            lines.Add($"{d.Folder}: a new ASC MHL history was started (the earlier one lists files that changed or are gone since, which ASC MHL can't record); the earlier one is in {d.MhlRestarted}.");
        return lines;
    }

    public static ResultView Describe(BackupResult r)
    {
        int n = r.Destinations.Count;
        int complete = r.Destinations.Count(d => d.Complete(r.Files));
        var reasons = new List<string>();
        foreach (BackupDestinationResult d in r.Destinations) reasons.Add(DestinationLine(d, r.Files));
        reasons.AddRange(TopUpLines(r));
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptDamaged.Count > 0))
            reasons.Add($"DAMAGED on {d.DriveName}: {RunOutcome.Count(d.KeptDamaged.Count, "file")} that {(d.KeptDamaged.Count == 1 ? "is" : "are")} no longer on the card no longer "
                        + $"{(d.KeptDamaged.Count == 1 ? "matches its" : "match their")} checksum (same size and date, read alike twice), e.g. {d.KeptDamaged[0]} - left as it is. "
                        + "Check that drive, and your other copies of these files.");
        if (r.LeftOut > 0) reasons.Add($"{RunOutcome.Count(r.LeftOut, "file or folder", "files or folders")} on the card {(r.LeftOut == 1 ? "was" : "were")} left out (links, online-only files or unreadable folders) - see the log.");
        if (r.NotInBackup.Count > 0)
            reasons.Add($"{RunOutcome.Count(r.NotInBackup.Count, "file")} on the card {(r.NotInBackup.Count == 1 ? "is" : "are")} not in the backup (added or changed after the preview), e.g. {r.NotInBackup[0]}.");
        if (r.CardNotRescanned) reasons.Add("The card could not be checked again afterwards, so files added to it after the preview can't be ruled out.");

        var technical = new List<string> { $"{Format.Bytes(r.Bytes)} in all. Each copy was read back from its own drive and compared with the card by SHA-256." };
        technical.AddRange(r.Destinations.Select(MhlLine));

        if (r.AllVerified)
        {
            int separate = r.SeparateDrives;
            string line = r.AddsTo is null ? AllCheckedLine(r.Files) : TopUpCheckedLine(
                r.Destinations.Max(d => d.Copied - d.Replaced - d.KeptBeside.Count - d.Repaired - d.Edited - d.Restored),
                r.Destinations.Max(d => d.Replaced + d.KeptBeside.Count + d.Repaired + d.Edited + d.Restored), r.Files);
            if (separate >= n)
                return new ResultView(ResultTone.Good, CopiedTitle(n), line, reasons, "", null, CopiedTitle(n) + ".") { Technical = technical };
            // Checked, but copies that share a drive (or share the drive being backed up) are lost together: never green.
            return new ResultView(ResultTone.Attention, "Copied, but not to separate drives", line, reasons, "",
                separate == 0
                    ? "Don't format the card yet: every copy is on the drive being backed up."
                    : $"Don't format the card yet: only {separate} of {RunOutcome.Count(n, "copy", "copies")} {(separate == 1 ? "is" : "are")} on a drive of its own.",
                "Copied, but not to separate drives.") { Technical = technical };
        }

        if (r.Status == RunStatus.Completed && r.Destinations.All(d => d.Complete(r.Files) && !d.MhlMismatch) && r.Destinations.Any(d => d.KeptDamaged.Count > 0)
            && r.LeftOut == 0 && r.NotInBackup.Count == 0 && !r.CardNotRescanned)
            return new ResultView(ResultTone.Attention, "Every file on the card is checked - but the backup had damaged files that are no longer on the card",
                "Every file on the card has a checked copy on every drive.", reasons,
                "A file that is no longer on the card was damaged on the drive since the earlier backup. It was left as it is: the card can't replace it. "
                + "The line above names the drive.",
                null, "Backup checked; damaged files found in the earlier backup.") { Technical = technical };

        if (r.Status == RunStatus.Completed && r.Destinations.All(d => d.Complete(r.Files)) && r.Destinations.Any(d => d.MhlMismatch))
            return new ResultView(ResultTone.Attention, "Copied and checked - but the card no longer matches its own earlier checksums",
                "Every file on the card has a checked copy, exactly as the card is now.", reasons,
                "The card's checksum history from an earlier offload lists other versions of some files, or files that are no longer on it: "
                + "they were changed or deleted on the card since. The line per drive names them.",
                "Don't format the card yet: it changed since it was first offloaded. Check the files named above.",
                "Backup checked; the card differs from its earlier checksums.") { Technical = technical };

        string title = r.Status switch
        {
            RunStatus.Halted => "Backup stopped: " + FirstSentence(r.Message),
            RunStatus.Stopped => "Backup stopped - Resume below continues it",
            RunStatus.Closed => "Backup ended early - not every file was copied",
            RunStatus.CompletedWithFailures when complete > 0 => $"Finished on {complete} of {RunOutcome.Count(n, "drive")} - the others need Resume",
            RunStatus.CompletedWithFailures => "Not finished - some files could not be copied yet",
            _ => "Finished - but not everything on the card is in the backup",
        };
        string left = complete == n
            ? "Every drive holds the same checked files."
            : $"Complete copies: {complete} of {n}.";
        string detail = r.Status is RunStatus.Halted or RunStatus.Stopped or RunStatus.CompletedWithFailures
            ? (r.Message is { Length: > 0 } m && r.Status != RunStatus.Halted ? m + " " : "") + "Every finished copy is kept, and Resume continues where the backup stopped."
            : "The report on each drive lists every file and why it was not copied.";
        return new ResultView(ResultTone.Attention, title, left, reasons, r.Status == RunStatus.Halted ? r.Message ?? detail : detail, KeepTheCard,
            r.Status == RunStatus.Halted ? r.Message ?? title : title) { Technical = technical };
    }

    /// <summary>"E:\Cards\260929_A: 412 of 412 checked", with what went wrong there (the checksum files are in <see cref="MhlLine"/>).</summary>
    public static string DestinationLine(BackupDestinationResult d, int files)
    {
        string line = $"{d.Folder}: {d.Verified:N0} of {files:N0} checked";
        if (d.Failed > 0) line += $", {RunOutcome.Count(d.Failed, "file")} failed";
        if (d.Problem is { } p) line += " - " + p;
        else if (!d.Ended && d.Verified < files) line += " - not finished";
        else if (!d.MhlWritten && d.Verified == files) line += " - no checksum files for other tools (see Details)";
        return line;
    }

    /// <summary>Whether the ASC MHL checksum files were written on a destination, and if not, why (for "Details").</summary>
    public static string MhlLine(BackupDestinationResult d) =>
        d.MhlWritten ? $"{d.Folder}: ASC MHL checksum files written."
        : d.MhlSkipped is { } why ? $"{d.Folder}: no ASC MHL checksum files - {why}"
        : $"{d.Folder}: no ASC MHL checksum files (the backup is not finished there).";

    public static ResultView DescribeVerify(BackupVerifyResult v)
    {
        var reasons = new List<string>();
        int moved = v.Destinations.Sum(d => d.MovedOut);
        foreach (BackupVerifyDestination d in v.Destinations)
        {
            reasons.Add(d.NotReachable is { } why ? $"{d.Folder}: not checked - {why}"
                : $"{d.Folder}: {d.Matched:N0} of {v.Files:N0} files match"
                  + (d.MovedOut > 0 ? $", {RunOutcome.Count(d.MovedOut, "file")} moved out by a sort (its receipt in the backup folder says where)" : ""));
            reasons.AddRange(d.Problems.Take(5).Select(p => "    " + p));
            if (d.Problems.Count > 5) reasons.Add($"    ... and {d.Problems.Count - 5:N0} more (see the report)");
        }
        IReadOnlyList<string> technical = ["Every copy was read again from its drive and compared with the SHA-256 checksum recorded when it was made."];
        return v.AllGood
            ? new ResultView(ResultTone.Good, moved > 0
                    ? "Checked again: every copy still in the backup folders matches (files a sort moved out were not checked here)."
                    : $"Checked again: all {RunOutcome.Count(v.Files, "file")} match on {RunOutcome.Count(v.Destinations.Count, "drive")}.",
                "", reasons, "", null, "Checked again - every copy matches.") { Technical = technical }
            : new ResultView(ResultTone.Attention, "Checking again found problems", "", reasons,
                "A copy that does not match was damaged after the backup (or changed by another program). Make a new backup from the card if you still have it.",
                KeepTheCard, "Checking again found problems - see above.") { Technical = technical };
    }

    /// <summary>The banner for an unfinished backup whose logs can be reached.</summary>
    public static (string Title, string Text) Banner(JobState job, IReadOnlyList<JobState> destinations, int more)
    {
        string where = string.Join(", ", destinations.Select(d => $"{d.DoneCount:N0} on {Drives(d)}"));
        int notCopied = job.Items.Count - (destinations.Count == 0 ? job.DoneCount : destinations.Min(d => d.DoneCount));
        return ($"A backup of {job.Header.SourceLabel} didn't finish: {RunOutcome.Count(job.Items.Count, "file")}, checked so far {where}. Started {JobTexts.Date(job.Header.Created)}.",
            (notCopied > 0 ? $"Don't format the card: {RunOutcome.Count(notCopied, "file")} {(notCopied == 1 ? "is" : "are")} not in the backup yet. " : "")
            + "Resume copies the rest (put the card back in first). “Stop here…” keeps what is copied - nothing is deleted."
            + (more > 0 ? $"  (+{RunOutcome.Count(more, "more unfinished backup")} - offered when this one is done.)" : ""));
    }

    /// <summary>
    /// The banner for an unfinished backup whose unfinished copies can't be reached right now (their drive is not
    /// connected): it names those drives.
    /// </summary>
    public static string OfflineBanner(RecentBackup b)
    {
        var away = b.Journals.Where(j => !System.IO.File.Exists(j)).Select(Letter).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string drives = away.Count == 0 ? "its destination" : string.Join(", ", away);
        return $"Unfinished backup of {b.SourceName} ({JobTexts.Date(b.Created)}): the copy on {drives} is not finished, and that drive is not connected. "
               + "Connect it, then press Check again.";
    }

    private static string Drives(JobState d) => d.Header.TargetLabel.Length > 0 ? $"{Letter(d.Header.Target)} ({d.Header.TargetLabel})" : Letter(d.Header.Target);

    private static string Letter(string path) => (System.IO.Path.GetPathRoot(path) ?? path).TrimEnd('\\');

    /// <summary>The button that starts a previewed backup.</summary>
    public static string StartButton(int files, int destinations) =>
        $"Back up {RunOutcome.Count(files, "file")} to {RunOutcome.Count(destinations, "drive")}";

    /// <summary>The button that starts a previewed top-up: "Add 38 files, check all 450".</summary>
    public static string TopUpStartButton(int toCopy, int files) =>
        toCopy == 0 ? $"Check all {RunOutcome.Count(files, "file")}" : $"Add {RunOutcome.Count(toCopy, "file")}, check all {files:N0}";

    /// <summary>"DATA (D:)", the way File Explorer names a drive; the letter alone when the drive has no name.</summary>
    public static string DriveText(string root, string label)
    {
        string letter = root.TrimEnd('\\');
        return label.Length > 0 ? $"{label} ({letter})" : letter;
    }

    /// <summary>
    /// The one line the preview shows for every copy that is not on a drive of its own (the planner warns per
    /// destination; those warnings are left out, see <see cref="IsSameDriveWarning"/>). Null when every copy is separate.
    /// </summary>
    public static string? SameDriveAlert(BackupPlan plan)
    {
        var targets = plan.Targets.Where(t => t.Volume is not null).ToList();
        var onSource = plan.SourceVolume is { } source && !plan.IsCard ? targets.Where(t => t.Volume!.IsSameVolume(source)).ToList() : [];
        var shared = targets.Except(onSource).GroupBy(t => (t.Volume!.SerialNumber, t.Volume.Root.ToUpperInvariant())).Where(g => g.Count() > 1).ToList();
        var lines = new List<string>();
        if (onSource.Count > 0)
        {
            string drive = DriveText(onSource[0].Volume!.Root, onSource[0].Volume!.Label);
            lines.Add(onSource.Count == plan.Targets.Count
                ? $"{(onSource.Count == 1 ? "The copy is" : $"All {onSource.Count} copies are")} on {drive}, the drive being backed up. If that drive fails, everything is lost."
                : $"{RunOutcome.Count(onSource.Count, "copy", "copies")} {(onSource.Count == 1 ? "is" : "are")} on {drive}, the drive being backed up: not a separate copy.");
        }
        foreach (var group in shared)
            lines.Add($"{group.Count()} copies are on the same drive, {DriveText(group.First().Volume!.Root, group.First().Volume!.Label)}. "
                      + "If that drive fails, they are lost together.");
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>A planner warning about a destination that is not on a drive of its own (shown as one line instead).</summary>
    public static bool IsSameDriveWarning(PlanMessage m) =>
        m.Level == MessageLevel.Warning && (m.Text.Contains(BackupPlanner.SameDriveAsSourceMarker, StringComparison.Ordinal)
                                            || m.Text.Contains(BackupPlanner.SharedDriveMarker, StringComparison.Ordinal));

    /// <summary>"about 12 min", from the plan's estimate.</summary>
    public static string Estimate(TimeSpan t) => t.TotalMinutes < 1 ? "under a minute" : "about " + Format.Duration(t);

    public static string FirstSentence(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "see the message below";
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? text[..dot] : text.TrimEnd('.');
    }
}
