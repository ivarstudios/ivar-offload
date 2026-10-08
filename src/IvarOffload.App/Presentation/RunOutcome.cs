using System.IO;
using IvarOffload.Core;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>How a result is shown. Good (green) only when nothing that should have moved is left behind.</summary>
public enum ResultTone { Good, Attention, Checking }

/// <summary>The result card after a job, in plain words.</summary>
/// <param name="LeftLine">"Left in the source: ..." - always there, so the user never has to guess.</param>
/// <param name="Alarm">A red line (for example: do not format the source), or null.</param>
/// <param name="Status">The one-line status next to the action buttons.</param>
public sealed record ResultView(ResultTone Tone, string Title, string LeftLine, IReadOnlyList<string> Reasons, string Detail, string? Alarm, string Status)
{
    public bool IsGood => Tone == ResultTone.Good;

    /// <summary>Checksums, checksum files and error messages as the system gave them: behind "Details", one click away.</summary>
    public IReadOnlyList<string> Technical { get; init; } = [];
}

/// <summary>
/// Decides what the result card says after a sort, a resume, an ended job or an undo. It is green only when the job
/// ended with nothing planned left behind, the fresh scan of the source finds nothing that should move, and the source
/// is not a memory card or camera drive (which still holds files that may exist nowhere else).
/// </summary>
public static class RunOutcome
{
    public const int MaxReasons = 6;

    public static ResultView Describe(RunResult r, JobState? job, SourceCheck? check, bool checking = false)
    {
        bool undo = r.Kind == JobKind.Undo || job?.IsUndo == true;
        MoveMode mode = job?.Header.Mode ?? MoveMode.Videos;
        string there = undo ? "the target folder" : "the source folder";
        string? card = undo ? null : check?.MemoryCard;
        IReadOnlyList<LeftFile> left = check?.Left ?? [];
        IReadOnlyList<LeftFile> missing = check?.Missing ?? [];
        int missingCount = Math.Max(missing.Count, r.MissingFromSource);
        int leftCount = Math.Max(left.Count, r.StillInSource);
        int leftPrimaries = left.Count == leftCount ? SourceCheck.Primaries(left) : 0;
        long leftBytes = left.Count == leftCount ? left.Sum(f => f.Size) : r.StillInSourceBytes;
        int moved = job?.DoneCount ?? r.Moved;
        int movedPrimaries = job is null ? 0 : MediaRules.CountPrimaries(job.Items.Where(i => i.Stage == ItemStage.Done).Select(i => i.Rel), mode);
        string movedText = moved == 0 ? "no files" : Things(moved, movedPrimaries, mode);
        string leftText = Things(leftCount, leftPrimaries, mode);
        string are = IsAre(leftCount, leftPrimaries);

        // Missing files (removed from the source by something else) are in neither folder: never green while there are any.
        ResultTone tone = !r.NothingLeftBehind || missingCount > 0 ? ResultTone.Attention
            : checking ? ResultTone.Checking
            : check is { IsChecked: true } && leftCount == 0 && card is null ? ResultTone.Good
            : ResultTone.Attention;

        string gone = missingCount == 0 ? "" : $", and {Files(missingCount)} {(missingCount == 1 ? "is" : "are")} missing from {there}";
        string movedAre = IsAre(moved, movedPrimaries);
        string title = r.Status switch
        {
            RunStatus.Halted => r.Message ?? "Stopped: the job cannot continue",
            RunStatus.Stopped => $"Stopped: {movedText} {(undo ? "returned" : "moved")} so far",
            _ when tone == ResultTone.Good && undo => $"Undone: {movedText} {movedAre} back in {job?.Header.Target}",
            _ when tone == ResultTone.Good => $"{(r.Status == RunStatus.Closed ? "Ended" : "Done")}: {movedText} moved",
            _ when leftCount == 0 && undo => $"Undone: {movedText} {movedAre} back in {job?.Header.Target}{gone}",
            _ when leftCount == 0 => $"{(r.Status == RunStatus.Closed ? "Ended" : "Done")}: {movedText} moved{gone}",
            RunStatus.Closed => undo
                ? $"Ended: {leftText} did not return and {are} still in the target folder{gone}"
                : $"Ended: {leftText} did not move and {are} still in the source folder{gone}",
            // Files that failed to move keep the job unfinished (the banner offers Resume): never "Finished" then.
            RunStatus.CompletedWithFailures => undo
                ? $"Not finished: {leftText} did not return{gone}"
                : $"Not finished: {leftText} {are} still in the source folder{gone}",
            _ => undo ? $"Finished: {leftText} did not return{gone}" : $"Finished: {leftText} {are} still in the source folder{gone}",
        };

        string label = undo ? "Left in the target folder: " : "Left in the source folder: ";
        string leftLine;
        if (tone == ResultTone.Checking) leftLine = $"The app now checks what is left in {there}...";
        else if (leftCount == 0)
        {
            string looks = Looks(check);
            leftLine = check switch
            {
                { NotChecked: { } why } => $"{label}nothing that needs to move, as far as the job knows. But the app could not check {there} again ({why}).",
                { IsChecked: false } => $"{label}nothing that needs to move in the folders that the app could read. But {check.UnreadableText}. "
                    + $"Check {(check.Unreadable.Count == 1 ? "it" : "them")} before you delete or format anything.",
                _ => label + (undo ? "nothing" : "nothing that needs to move")
                    + (looks.Length > 0 ? ". " + looks : card is not null ? $". Check {there} before you delete or format anything." : ""),
            };
        }
        else
        {
            string also = check is { Unrecognized: > 0 } c ? $", {Count(c.Unrecognized, "unrecognized file")}" : "";
            bool one = leftCount == 1 && also.Length == 0;
            string these = one ? "this file" : "these files";
            string tail = r.Status is RunStatus.Stopped or RunStatus.Halted ? $". The job did not move {these} yet. Resume continues from the point where the job stopped."
                : undo ? $". {(one ? "This file" : "These files")} did not return."
                : $". Check {these} before you delete or format anything.";
            leftLine = $"{label}{Things(leftCount, leftPrimaries, mode, leftBytes)}{also}{tail}";
        }
        if (missingCount > 0 && tone != ResultTone.Checking)
            leftLine += (leftLine.EndsWith('.') ? "" : ".") + $" Missing: something else removed {Files(missingCount)} from {there} before the job could move "
                + $"{(missingCount == 1 ? "it" : "them")}. For details, see below.";

        var reasons = new List<string>();
        int identical = job?.IdenticalInTargetCount ?? r.IdenticalInTarget;
        if (identical > 0)
            reasons.Add(undo
                ? $"{Files(identical)}: already in the original location"
                : $"{Files(identical)}: an identical copy was already in the target folder (skipped, nothing is missing)");
        // Files that are in neither folder come first, and are never summed up with other reasons.
        foreach (var g in missing.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()))
            reasons.Add($"{Files(g.Count())}: {g.Key}");
        if (missingCount > missing.Count)
            reasons.Add($"{Files(missingCount - missing.Count)}: {LeftReasons.MissingFromText(undo)}. For details, see the report.");
        var groups = left.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()).ToList();
        foreach (var g in groups.Take(groups.Count > MaxReasons ? MaxReasons - 1 : MaxReasons))
            reasons.Add($"{Files(g.Count())}: {g.Key}");
        if (groups.Count > MaxReasons)
            reasons.Add($"{Files(groups.Skip(MaxReasons - 1).Sum(g => g.Count()))}: other reasons (see “{ShowLeftText(undo)}”)");

        var detail = new List<string>();
        switch (r.Status)
        {
            case RunStatus.Stopped:
                detail.Add("It is now safe to disconnect the drives. Resume continues the job from the point where it stopped.");
                break;
            case RunStatus.Halted:
                detail.Add("Everything that the job did so far is safe and in the log. Fix the problem. Then click Resume"
                    + (r.Halt == HaltReason.OriginalsNotRemovable ? ", or end the job and keep the remaining files where they are." : "."));
                break;
            case RunStatus.CompletedWithFailures:
                detail.Add("Fix the cause (for example, close the program that uses a file). Then click Resume to try again, "
                    + "or end the job and keep the remaining files where they are.");
                break;
        }
        // How the files moved, and where the log and the receipt are: behind "Details".
        var technical = new List<string>();
        if (moved > 0 && job is not null && r.Status is not (RunStatus.Stopped or RunStatus.Halted)) technical.Add(Proof(job));
        if (job is not null)
            technical.Add(undo ? $"The log of the undo is in {Path.GetDirectoryName(job.JournalPath)}."
                : $"The log and a list of every file are in {Path.GetDirectoryName(job.JournalPath)}. A receipt is in {JobPaths.ReceiptFolder(job.Header.Source, job.JournalPath)}.");
        if (!undo && check is { MovableNow: > 0 } && r.Status is RunStatus.Completed or RunStatus.Closed)
            detail.Add("The list below shows what is still in the source folder. The button at the bottom right moves the files that can move now, as a new job.");
        if (!undo && check is { OnlineOnly: > 0 })
            detail.Add("Online-only files are placeholders for files that are in the cloud, so the job could not move them. To move them, right-click them "
                + "(or their folder) > “Always keep on this device”. Wait until the download is complete. Then scan the source folder again.");
        if (missingCount > 0)
            detail.Add($"Another program or person removed the missing files from {there} while the job ran (or during an interruption). "
                + $"They are not in {(undo ? "the source folder" : "the target folder")} either, unless the job kept a copy there. The report lists them.");
        if (check is { KeptCopies.Count: > 0 } kept && job is not null)
            detail.Add($"The job kept {(kept.KeptCopies.Count == 1 ? "a copy" : "copies")} in {(undo ? "the source folder" : "the target folder")} with a different name. "
                + $"{(kept.KeptCopies.Count == 1 ? "It can be the last copy of a file, so check it" : "They can be the last copies of some files, so check them")} before you delete anything: "
                + string.Join(", ", kept.KeptCopies.Take(3).Select(c => Path.Join(job.Header.Target, c)))
                + (kept.KeptCopies.Count > 3 ? $" (+{kept.KeptCopies.Count - 3:N0} more in the report)." : "."));
        if (check is { IsChecked: false } && leftCount > 0 && r.Status is not (RunStatus.Stopped or RunStatus.Halted))
            detail.Add(check.NotChecked is { } why
                ? $"The app could not check {there} again ({why})."
                : $"In {there}, {check.UnreadableText}. The app did not check the files in {(check.Unreadable.Count == 1 ? "this folder" : "these folders")}.");

        string? alarm = r.Halt == HaltReason.SourceInconsistent
            ? $"Do NOT format or delete {there}: some files read differently the second time, and the job kept their originals."
            : card is not null ? CardAlarm(card, job, mode)
            : null;

        string status = r.Status switch
        {
            RunStatus.Stopped => "Stopped. It is now safe to disconnect the drives. Resume continues the job from the point where it stopped.",
            RunStatus.Halted => "The job stopped by itself. Nothing is lost. For details, see the message above.",
            RunStatus.CompletedWithFailures => "The job could not move some files. Resume tries them again.",
            _ when tone == ResultTone.Checking => $"The app now checks what is left in {there}...",
            _ when tone == ResultTone.Good => "Finished.",
            // Everything planned moved, but the fresh scan could not look at (all of) the source.
            _ when leftCount == 0 && check is { IsChecked: false } => (r.Status == RunStatus.Closed ? "Ended. " : "Finished. ")
                + (check.NotChecked is not null ? $"The app could not check {there} again. For details, see above."
                    : $"The app could not read some folders in {there} again. For details, see above."),
            _ when leftCount == 0 && card is not null => (r.Status == RunStatus.Closed ? "Ended. " : "Finished. ")
                + $"Back up {card} before you format it. For details, see above.",
            _ when leftCount == 0 && missingCount > 0 => (r.Status == RunStatus.Closed ? "Ended. " : "Finished. ")
                + $"{Files(missingCount)} {(missingCount == 1 ? "was" : "were")} missing from {there}. For details, see above.",
            RunStatus.Closed => $"The job ended. The files that did not move are still in {there}.",
            _ => $"Finished, but some files are still in {there} (see above).",
        };

        return new ResultView(tone, title, leftLine, reasons, string.Join("\n", detail), alarm, status) { Technical = technical };
    }

    /// <summary>
    /// The red line after sorting a memory card or camera drive: it still holds what did not move (and, when the target
    /// is on it too, everything), maybe as the only copy. Sorting it anyway is allowed, formatting it next must not be.
    /// </summary>
    public static string CardAlarm(string card, JobState? job, MoveMode mode)
    {
        string moved = Planner.Word(mode), stayed = Planner.Word(mode == MoveMode.Videos ? MoveMode.Photos : MoveMode.Videos);
        return job?.Header.Method == TransferMethod.Rename // renamed within the drive: the target is on the card too
            ? $"{card} looks like a memory card or camera drive, and the {moved} only moved to {job.Header.Target} on the same drive. "
              + "Everything is still only on the card, unless you copied it to a different drive. "
              + "Before you format the card, copy it to a different drive. Then check the copy."
            : $"{card} looks like a memory card or camera drive. The {stayed} and everything else that stayed are still only on the card, "
              + "unless you copied them to a different drive. Before you format the card, copy it to a different drive. Then check the copy.";
    }

    /// <summary>The button that shows what a job left behind.</summary>
    public static string ShowLeftText(bool undo) => undo ? "Show files still in the target folder" : "Show files still in the source folder";

    /// <summary>How the files were moved, and what that proves.</summary>
    public static string Proof(JobState job) => job.Header.Method == TransferMethod.Copy
        ? "Between drives, the app read every copy again from the disk and read every original a second time. "
          + "Both matched the SHA-256 checksum before the app removed the original."
        : "The app renamed the files on the same drive, so it never wrote their data again. The app made sure that each move was complete."
          + (job.Header.Verify ? " A SHA-256 checksum of every file is in the log." : "");

    /// <summary>"412 videos", "5 videos (17 files)" or "12 files".</summary>
    public static string Things(int files, int primaries, MoveMode mode)
    {
        string plural = Planner.Word(mode), single = plural[..^1];
        if (files == primaries) return $"{files:N0} {(files == 1 ? single : plural)}";
        if (primaries == 0) return Files(files);
        return $"{primaries:N0} {(primaries == 1 ? single : plural)} ({Files(files)})";
    }

    /// <summary>"412 videos (38 GB)", "5 videos (17 files, 38 GB)" or "12 files (1 GB)".</summary>
    public static string Things(int files, int primaries, MoveMode mode, long bytes)
    {
        string size = Format.Bytes(bytes);
        if (files == primaries || primaries == 0) return $"{Things(files, primaries, mode)} ({size})";
        string plural = Planner.Word(mode), single = plural[..^1];
        return $"{primaries:N0} {(primaries == 1 ? single : plural)} ({Files(files)}, {size})";
    }

    public static string Files(int count) => Count(count, "file");

    /// <summary>"1 file", "3 files"; <paramref name="many"/> for irregular plurals ("libraries").</summary>
    public static string Count(int count, string what, string? many = null) => Format.Count(count, what, many);

    /// <summary>The verb after <see cref="Things"/>: it agrees with the first number of the phrase.</summary>
    private static string IsAre(int files, int primaries) => (primaries > 0 ? primaries : files) == 1 ? "is" : "are";

    private static string Looks(SourceCheck? check)
    {
        if (check is null || check.Unrecognized + check.OtherLooks == 0) return "";
        var parts = new List<string>();
        if (check.Unrecognized > 0) parts.Add(Count(check.Unrecognized, "unrecognized file"));
        if (check.OtherLooks > 0) parts.Add(Count(check.OtherLooks, "other file") + " that need" + (check.OtherLooks == 1 ? "s" : "") + " a look");
        bool one = check.Unrecognized + check.OtherLooks == 1;
        return $"{string.Join(" and ", parts)} {(one ? "stays" : "stay")} there. Check {(one ? "it" : "them")} before you delete or format anything.";
    }
}

/// <summary>What the verification of a finished job says.</summary>
public static class VerifyOutcome
{
    /// <summary>
    /// The result card after "Verify again from disk". The verification speaks for the moved files only: what the job's
    /// result said about the source (the "Left in the source" line and its reasons) stays, and it is green only when
    /// every moved file is intact and that result was green too.
    /// </summary>
    public static ResultView Show(VerifyResult r, ResultView? before, bool undo = false)
    {
        (bool good, string title, string detail) = Describe(r, undo);
        // Only planned files that did not move: they are already on the card and in the list below, so no paths again.
        if (!good && before is not null && r.Matched == r.Checked && r.Problems.Count == r.NotMoved + r.HeldBack + r.Missing)
            detail = "The app read every moved file again from the disk. Each file matches the checksum that the job recorded when it moved the file. "
                + "The files that did not move are in the list below." + (r.Missing > 0 ? " The text above names the missing files." : "");
        return new ResultView(good && before is { IsGood: true } ? ResultTone.Good : ResultTone.Attention, title,
            before?.LeftLine ?? "", before?.Reasons ?? [], detail, before?.Alarm,
            good && before is { IsGood: false } ? "Check complete: the moved files did not change. For the files that did not move, see above." : "Check complete.");
    }

    public static (bool Good, string Title, string Detail) Describe(VerifyResult r, bool undo = false)
    {
        if (r.AllGood)
            return (true, r.Checked == 1 ? "Checked again: the moved file is the same as before" : $"Checked again: all {r.Checked:N0} moved files are the same as before",
                "The app read every moved file again from the disk. Each file matches the checksum that the job recorded when it moved the file.");
        string problems = string.Join("\n", r.Problems.Take(8)) + (r.Problems.Count > 8 ? "\n..." : "");
        string there = undo ? "the target folder" : "the source folder";
        int left = r.NotMoved + r.HeldBack;
        if (r.Matched == r.Checked && left + r.Missing > 0)
        {
            var parts = new List<string>();
            // Held-back files were never planned (the preview kept them), but they should have moved too.
            if (left > 0)
                parts.Add(r.HeldBack == 0
                    ? $"{RunOutcome.Count(left, "planned file")} {(left == 1 ? "is" : "are")} still in {there}"
                    : $"{RunOutcome.Files(left)} that {(left == 1 ? "needs" : "need")} to move {(left == 1 ? "is" : "are")} still in {there}");
            if (r.Missing > 0) parts.Add($"{RunOutcome.Files(r.Missing)} {(r.Missing == 1 ? "is" : "are")} missing from {there}");
            return (false, "The moved files did not change, but " + string.Join(", and ", parts), problems);
        }
        return (false, $"The check found {RunOutcome.Count(r.Problems.Count, "problem")}", problems);
    }
}
