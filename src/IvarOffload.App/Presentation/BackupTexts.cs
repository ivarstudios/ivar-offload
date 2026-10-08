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
        "A backup copies a memory card (or any folder) to one to three backup drives, bit for bit. The app reads each copy again from its drive "
        + "and compares the checksum with the checksum of the card file. It also writes ASC MHL checksum files next to the copies. "
        + "The app only reads the card and never changes it.";

    public const string SortDescription =
        "A sort moves the videos (or the photos) of a card backup into a target folder, with the same sub-folders. It never overwrites anything. "
        + "It removes the original file only after it checks the moved file. It records every step in the source folder and in the target folder. "
        + "You can undo any sort.";

    /// <summary>The red line kept on the result card while the card still holds files the backup does not have.</summary>
    public const string KeepTheCard = "Do not format the card: it still holds files that are not in the backup.";

    /// <summary>
    /// The green title: only what happened ("Copied to 2 drives"), never advice about the card. The drives counted are
    /// drives of their own; copies that share a drive are never green.
    /// </summary>
    public static string CopiedTitle(int drives) => $"Copied to {RunOutcome.Count(drives, "drive")}";

    /// <summary>"The app copied and checked all 412 files. It did not change the card."</summary>
    public static string AllCheckedLine(int files) =>
        (files == 1 ? "The app copied and checked the file." : $"The app copied and checked all {RunOutcome.Count(files, "file")}.") + " It did not change the card.";

    /// <summary>
    /// A top-up: "The app copied 38 new files and checked all 450 files. It did not change the card.", with the files
    /// that were copied again (changed on the card, or no longer in the backup folder) counted apart from the new ones.
    /// </summary>
    public static string TopUpCheckedLine(int newFiles, int again, int files)
    {
        string check = files == 1 ? "checked the file" : $"checked all {RunOutcome.Count(files, "file")}";
        string copied = newFiles == 0 ? $"copied {RunOutcome.Count(again, "file")} again"
            : $"copied {RunOutcome.Count(newFiles, "new file")}" + (again > 0 ? $", copied {RunOutcome.Count(again, "other file")} again" : "");
        return (newFiles == 0 && again == 0 ? $"There was nothing new to copy. The app {check}." : $"The app {copied} and {check}.")
               + " It did not change the card.";
    }

    /// <summary>A top-up: what happened to the files the earlier backup held, in plain words (none for a full backup).</summary>
    public static List<string> TopUpLines(BackupResult r)
    {
        var lines = new List<string>();
        if (r.AddsTo is null) return lines;
        int replaced = r.Destinations.Max(d => d.Replaced), restored = r.Destinations.Max(d => d.Restored);
        int kept = r.Destinations.Max(d => d.Kept), gone = r.Destinations.Max(d => d.KeptGone);
        string replacedFolder = $"{JobPaths.LogFolderNameOf(r.Destinations[0].JournalPath)}\\{BackupRunner.ReplacedFolderName}";
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptBeside.Count > 0).Take(1))
            lines.Add($"{RunOutcome.Count(d.KeptBeside.Count, "photo or clip", "photos or clips")} changed on the card after the earlier backup (usually another shot with a reused number). "
                      + $"The app copied {It(d.KeptBeside.Count)} and kept the earlier version from the backup next to "
                      + (d.KeptBeside.Count == 1 ? $"it: {d.KeptBeside[0]}." : $"each one, for example {d.KeptBeside[0]}."));
        if (replaced > 0)
            lines.Add($"{RunOutcome.Count(replaced, "camera index file")} changed on the card after the earlier backup. The app copied {It(replaced)} again "
                      + $"and kept the old version{(replaced == 1 ? "" : "s")} in {replacedFolder} in the backup folder.");
        if (restored > 0)
            lines.Add($"{RunOutcome.Count(restored, "file")} {(restored == 1 ? "was" : "were")} no longer in the backup folder (a sort moved {It(restored)} to a different folder, "
                      + $"or someone deleted {It(restored)}). The app copied {It(restored)} again from the card.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.Repaired > 0))
            lines.Add($"{RunOutcome.Count(d.Repaired, "copy", "copies")} in {d.Folder} no longer matched {(d.Repaired == 1 ? "its" : "their")} checksum: "
                      + $"{(d.Repaired == 1 ? "it" : "they")} became damaged on {d.DriveName} after the earlier backup. "
                      + $"The app moved {It(d.Repaired)} to {replacedFolder} and copied {It(d.Repaired)} again from the card. Check that drive.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.Edited > 0))
            lines.Add($"{RunOutcome.Count(d.Edited, "file")} in {d.Folder} changed there after the earlier backup (possibly because another program edited {It(d.Edited)}), "
                      + $"or {(d.Edited == 1 ? "it" : "they")} did not come from that backup. The app moved {It(d.Edited)} to {replacedFolder} "
                      + $"and copied the {(d.Edited == 1 ? "file" : "files")} from the card into {(d.Edited == 1 ? "its" : "their")} place.");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptEdited > 0))
            lines.Add($"{RunOutcome.Count(d.KeptEdited, "file")} in {d.Folder} that {(d.KeptEdited == 1 ? "is" : "are")} no longer on the card changed there after "
                      + $"the earlier backup (possibly because another program edited {It(d.KeptEdited)}). "
                      + $"The app kept {(d.KeptEdited == 1 ? "it as it is" : "them as they are")} and did not check {It(d.KeptEdited)}.");
        if (kept > 0)
            lines.Add($"{RunOutcome.Count(kept, "file")} in the backup {(kept == 1 ? "is" : "are")} no longer on the card. "
                      + $"{(kept == 1 ? "It stays" : "They stay")} in the backup, and the app checked {It(kept)}.");
        if (gone > 0)
            lines.Add($"{RunOutcome.Count(gone, "file")} from the earlier backup {(gone == 1 ? "is" : "are")} not on the card and not in the backup folder any more "
                      + $"(a sort moved {It(gone)} to a different folder, or someone deleted {It(gone)}).");
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.MhlRestarted is not null))
            lines.Add($"{d.Folder}: the app started a new ASC MHL history. The earlier history lists files that changed or that are missing now. "
                      + $"ASC MHL cannot record these changes. The earlier history is in {d.MhlRestarted}.");
        return lines;
    }

    /// <summary>"it" for one file, "them" for more.</summary>
    private static string It(int count) => count == 1 ? "it" : "them";

    public static ResultView Describe(BackupResult r)
    {
        int n = r.Destinations.Count;
        int complete = r.Destinations.Count(d => d.Complete(r.Files));
        var reasons = new List<string>();
        foreach (BackupDestinationResult d in r.Destinations) reasons.Add(DestinationLine(d, r.Files));
        reasons.AddRange(TopUpLines(r));
        foreach (BackupDestinationResult d in r.Destinations.Where(d => d.KeptDamaged.Count > 0))
        {
            bool one = d.KeptDamaged.Count == 1;
            reasons.Add($"DAMAGED on {d.DriveName}: {RunOutcome.Count(d.KeptDamaged.Count, "file")} no longer {(one ? "matches its" : "match their")} checksum, "
                        + $"and the card no longer holds {It(d.KeptDamaged.Count)}. (The size and the date are the same, and two reads gave the same result.) "
                        + $"{(one ? "The file is" : "For example:")} {d.KeptDamaged[0]}. The app kept {(one ? "it as it is" : "them as they are")}. "
                        + $"Check that drive and your other copies of {(one ? "this file" : "these files")}.");
        }
        if (r.LeftOut > 0)
            reasons.Add($"The app did not copy {RunOutcome.Count(r.LeftOut, "file or folder", "files or folders")} on the card (links, online-only files or folders that it could not read). "
                        + "For details, see the job log.");
        if (r.NotInBackup.Count > 0)
            reasons.Add($"{RunOutcome.Count(r.NotInBackup.Count, "file")} on the card {(r.NotInBackup.Count == 1 ? "is" : "are")} not in the backup (new or changed after the preview). "
                        + $"{(r.NotInBackup.Count == 1 ? "The file is" : "For example:")} {r.NotInBackup[0]}.");
        if (r.CardNotRescanned) reasons.Add("The app could not check the card again after the backup. It is possible that someone added files to the card after the preview.");

        var technical = new List<string> { $"Total size: {Format.Bytes(r.Bytes)}. The app read each copy from its own drive and compared it with the card by SHA-256." };
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
                    ? "Do not format the card yet: every copy is on the same drive as the files that you backed up."
                    : $"Do not format the card yet: only {separate} of {RunOutcome.Count(n, "copy", "copies")} {(separate == 1 ? "is" : "are")} on a drive of its own.",
                "Copied, but not to separate drives.") { Technical = technical };
        }

        if (r.Status == RunStatus.Completed && r.Destinations.All(d => d.Complete(r.Files) && !d.MhlMismatch) && r.Destinations.Any(d => d.KeptDamaged.Count > 0)
            && r.LeftOut == 0 && r.NotInBackup.Count == 0 && !r.CardNotRescanned)
            return new ResultView(ResultTone.Attention, "The app checked every file on the card, but the backup has damaged files that are no longer on the card",
                "Every file on the card has a checked copy on every drive.", reasons,
                "A file that is no longer on the card became damaged on the drive after the earlier backup. The app kept it as it is, because the card cannot replace it. "
                + "The line above shows the drive.",
                null, "The app checked the backup and found damaged files from the earlier backup.") { Technical = technical };

        if (r.Status == RunStatus.Completed && r.Destinations.All(d => d.Complete(r.Files)) && r.Destinations.Any(d => d.MhlMismatch))
            return new ResultView(ResultTone.Attention, "Copied and checked, but the card no longer matches its own earlier checksums",
                "Every file on the card has a checked copy, exactly as the card is now.", reasons,
                "The checksum history on the card comes from an earlier offload. It lists other versions of some files, or files that are no longer on the card. "
                + "These files changed on the card after that offload, or someone deleted them. The line for each drive shows these files.",
                "Do not format the card yet: it changed after its first offload. Check the files in the lines above.",
                "The app checked the backup, but the card does not match its earlier checksums.") { Technical = technical };

        string title = r.Status switch
        {
            RunStatus.Halted => "Backup stopped: " + FirstSentence(r.Message),
            RunStatus.Stopped => "Backup stopped. To continue, click Resume below.",
            RunStatus.Closed => "Backup ended early: not every file is in the backup",
            RunStatus.CompletedWithFailures when complete > 0 => $"Finished on {complete} of {RunOutcome.Count(n, "drive")}: {(n - complete == 1 ? "the other drive needs" : "the other drives need")} Resume",
            RunStatus.CompletedWithFailures => "Not finished: the app could not copy some files yet",
            _ => "Finished, but not everything on the card is in the backup",
        };
        string left = complete == n
            ? "Every drive holds the same checked files."
            : $"Complete copies: {complete} of {n}.";
        string detail = r.Status is RunStatus.Halted or RunStatus.Stopped or RunStatus.CompletedWithFailures
            ? (r.Message is { Length: > 0 } m && r.Status != RunStatus.Halted ? m + " " : "")
              + "The app keeps every finished copy. Resume continues the backup from the point where it stopped."
            : "The report on each drive lists each file that the app did not copy, and the reason.";
        return new ResultView(ResultTone.Attention, title, left, reasons, r.Status == RunStatus.Halted ? r.Message ?? detail : detail, KeepTheCard,
            r.Status == RunStatus.Halted ? r.Message ?? title : title) { Technical = technical };
    }

    /// <summary>"E:\Cards\260929_A: 412 of 412 checked", with what went wrong there (the checksum files are in <see cref="MhlLine"/>).</summary>
    public static string DestinationLine(BackupDestinationResult d, int files)
    {
        string line = $"{d.Folder}: {d.Verified:N0} of {files:N0} checked";
        if (d.Failed > 0) line += $", {RunOutcome.Count(d.Failed, "file")} failed";
        if (d.Problem is { } p) line += ". " + p;
        else if (!d.Ended && d.Verified < files) line += ", not finished";
        else if (!d.MhlWritten && d.Verified == files) line += ". The app did not write checksum files for other tools. For the reason, see Details.";
        return line;
    }

    /// <summary>Whether the ASC MHL checksum files were written on a destination, and if not, why (for "Details").</summary>
    public static string MhlLine(BackupDestinationResult d) =>
        d.MhlWritten ? $"{d.Folder}: the app wrote the ASC MHL checksum files."
        : d.MhlSkipped is { } why ? $"{d.Folder}: no ASC MHL checksum files. Reason: {why}"
        : $"{d.Folder}: no ASC MHL checksum files, because the backup is not finished there.";

    public static ResultView DescribeVerify(BackupVerifyResult v)
    {
        var reasons = new List<string>();
        int moved = v.Destinations.Sum(d => d.MovedOut);
        foreach (BackupVerifyDestination d in v.Destinations)
        {
            reasons.Add(d.NotReachable is { } why ? $"{d.Folder}: not checked. {why}"
                : $"{d.Folder}: {d.Matched:N0} of {v.Files:N0} files match"
                  + (d.MovedOut > 0 ? $". A sort moved {RunOutcome.Count(d.MovedOut, "file")} out of this folder. The receipt of that sort in the backup folder shows where." : ""));
            reasons.AddRange(d.Problems.Take(5).Select(p => "    " + p));
            if (d.Problems.Count > 5) reasons.Add($"    ... and {d.Problems.Count - 5:N0} more. For the full list, see the report.");
        }
        IReadOnlyList<string> technical = ["The app read every copy again from its drive. It compared each copy with the SHA-256 checksum that it recorded when it made the copy."];
        return v.AllGood
            ? new ResultView(ResultTone.Good, moved > 0
                    ? "Checked again: every copy that is still in the backup folders matches (this check did not include the files that a sort moved out)."
                    : $"Checked again: all {RunOutcome.Count(v.Files, "file")} match on {RunOutcome.Count(v.Destinations.Count, "drive")}.",
                "", reasons, "", null, "Checked again: every copy matches.") { Technical = technical }
            : new ResultView(ResultTone.Attention, "The check found problems", "", reasons,
                "A copy that does not match became damaged after the backup, or another program changed it. If you still have the card, make a new backup from it.",
                KeepTheCard, "The check found problems. For details, see above.") { Technical = technical };
    }

    /// <summary>The banner for an unfinished backup whose logs can be reached.</summary>
    public static (string Title, string Text) Banner(JobState job, IReadOnlyList<JobState> destinations, int more)
    {
        string where = string.Join(", ", destinations.Select(d => $"{d.DoneCount:N0} on {Drives(d)}"));
        int notCopied = job.Items.Count - (destinations.Count == 0 ? job.DoneCount : destinations.Min(d => d.DoneCount));
        return ($"A backup of {job.Header.SourceLabel} from {JobTexts.Date(job.Header.Created)} did not finish. "
                + $"Checked so far: {where} (of {RunOutcome.Count(job.Items.Count, "file")}).",
            (notCopied > 0 ? $"Do not format the card: {RunOutcome.Count(notCopied, "file")} {(notCopied == 1 ? "is" : "are")} not in the backup yet. " : "")
            + "Resume copies the rest. Make sure that the card is connected before you click Resume. “Stop here…” keeps the files that the app copied. It deletes nothing."
            + (more > 0 ? $"  (+{RunOutcome.Count(more, "more unfinished backup")}: the app shows {It(more)} when this one is done.)" : ""));
    }

    /// <summary>
    /// The banner for an unfinished backup whose unfinished copies can't be reached right now (their drive is not
    /// connected): it names those drives.
    /// </summary>
    public static string OfflineBanner(RecentBackup b)
    {
        var away = b.Journals.Where(j => !System.IO.File.Exists(j)).Select(Letter).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool many = away.Count > 1;
        string drives = away.Count == 0 ? "its backup drive" : string.Join(", ", away);
        return $"Unfinished backup of {b.SourceName} ({JobTexts.Date(b.Created)}): the {(many ? "copies" : "copy")} on {drives} {(many ? "are" : "is")} not finished. "
               + $"{(many ? "These drives are" : "That drive is")} not connected. Connect {(many ? "them" : "it")}. Then click “Check again”.";
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
                ? $"{(onSource.Count == 1 ? "The copy is" : $"All {onSource.Count} copies are")} on {drive}, the same drive as the files that you back up. "
                  + "If that drive fails, you lose everything."
                : $"{RunOutcome.Count(onSource.Count, "copy", "copies")} {(onSource.Count == 1 ? "is" : "are")} on {drive}, the same drive as the files that you back up. "
                  + (onSource.Count == 1 ? "It is not a separate copy." : "They are not separate copies."));
        }
        foreach (var group in shared)
            lines.Add($"{group.Count()} copies are on the same drive, {DriveText(group.First().Volume!.Root, group.First().Volume!.Label)}. "
                      + "If that drive fails, you lose all of them at the same time.");
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>A planner warning about a destination that is not on a drive of its own (shown as one line instead).</summary>
    public static bool IsSameDriveWarning(PlanMessage m) =>
        m.Level == MessageLevel.Warning && (m.Text.Contains(BackupPlanner.SameDriveAsSourceMarker, StringComparison.Ordinal)
                                            || m.Text.Contains(BackupPlanner.SharedDriveMarker, StringComparison.Ordinal));

    /// <summary>"approximately 12m 30s", from the plan's estimate.</summary>
    public static string Estimate(TimeSpan t) => t.TotalMinutes < 1 ? "less than a minute" : "approximately " + Format.Duration(t);

    public static string FirstSentence(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "the message below gives the reason";
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? text[..dot] : text.TrimEnd('.');
    }
}
