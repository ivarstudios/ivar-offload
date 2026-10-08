using System.Globalization;
using IvarOffload.Core;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

// Command-line front end for scripted use and for the interrupt/resume tests. The GUI uses the same engine.
return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        IVAR Offload (command line)

          preview --source <folder> --target <folder> [--mode videos|photos] [--no-checksums] [--list]
          run     --source <folder> --target <folder> [--mode videos|photos] [--no-checksums] --yes
          resume  --journal <file> | --target <folder>
          close   --journal <file> | --target <folder>
                                            finish or roll back files in flight, then end the job (the rest stays in the source)
          verify  --journal <file> | --target <folder>
                                            re-read every moved file and compare checksums; also lists files that did not move
          status  --journal <file> | --target <folder>
          jobs    --target <folder>         list the jobs of a folder (sorted into it, or out of it)
          undo    --journal <file> [--to <folder>] [--yes] [--list]
                                            move the files of a finished sort back (without --yes: show what would happen;
                                            --list names the files that are already back or cannot go back)
          undo    --target <folder> [--job <id>] [--to <folder>] [--yes] [--list]
                                            the most recent sort job of that folder, or the one with that id
                                            (--journal also accepts the job's manifest, summary or a .moved-out receipt;
                                            --to puts the files back into that folder, when the one they came from
                                            cannot be confirmed)

        Backup (copies a card to one to three destinations; the card is only ever read):
          backup  --source <card> --target <folder> [--target <folder> ...] [--name <name> | --name-template <pattern>] [--no-source-reread] [--top-up] [--list] [--yes]
                                            without --yes: show the preview. The copy goes into <folder>\<name> on every
                                            destination (default name: YYMMDD_<card label>; a folder that is not empty is refused).
                                            --name-template: the default name's pattern, of {card} {camera} and date and
                                            time codes in braces: YYYY or YY, MM, DD, HH, MM (minutes, after HH or before
                                            SS), SS, e.g. "{YYMMDD}_{camera}_{card}" or "{YYYY-MM-DD}_{HHMMSS}_{card}"
                                            --top-up: add the card's new files to its earlier backup (found in every
                                            <folder>, the same backup on all of them) and verify the whole card, instead
                                            of a new full backup. Nothing there is overwritten or deleted: older versions
                                            of changed files go into _IVAROffload\replaced; files no longer on the card stay.
          backup-resume  --journal <file> | --target <backup folder>
          backup-close   --journal <file> | --target <backup folder>
                                            finish or remove the copies in flight, then end the backup (the rest is not copied)
          backup-verify  --journal <file> | --target <backup folder>
                                            re-read every copy on every destination and compare checksums
          backup-status  --journal <file> | --target <backup folder>

        Exit codes: 0 done and nothing that should move is still in the source, 1 finished with failures, 2 stopped or
                    halted, 3 usage error, 4 plan (or undo) cannot run, 5 done or closed with files still in the source
                    (also files held back by the preview, files the check of the source after the job found - added
                    after the preview, online-only - and files that disappeared from the source), or the source could
                    not be checked again after the job.
                    Backups: 0 every file verified on every destination, 1 finished with failures (or a destination
                    dropped out), 2 stopped or halted, 5 finished but not everything on the card is in the backup.
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 3 : 0;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("Stopping safely after the current step... (the job can be resumed)");
            cts.Cancel();
        };
        try
        {
            var options = ParseOptions(args.Skip(1).ToArray(), out List<string> targets);
            return args[0] switch
            {
                "preview" => Preview(options, run: false, cts.Token),
                "run" => Preview(options, run: true, cts.Token),
                "resume" => Resume(options, close: false, cts.Token),
                "close" => Resume(options, close: true, cts.Token),
                "verify" => Verify(options, cts.Token),
                "status" => Status(options),
                "jobs" => Jobs(options),
                "undo" => Undo(options, cts.Token),
                "backup" => Backup(options, targets, cts.Token),
                "backup-resume" => BackupResume(options, close: false, cts.Token),
                "backup-close" => BackupResume(options, close: true, cts.Token),
                "backup-verify" => BackupVerify(options, cts.Token),
                "backup-status" => BackupStatus(options),
                _ => UsageError($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException e)
        {
            return UsageError(e.Message);
        }
        catch (JournalException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Preview(Dictionary<string, string?> o, bool run, CancellationToken ct)
    {
        string source = Required(o, "source");
        string target = Required(o, "target");
        MoveMode mode = o.TryGetValue("mode", out string? m) && m?.StartsWith("photo", StringComparison.OrdinalIgnoreCase) == true ? MoveMode.Photos : MoveMode.Videos;

        ScanResult scan = Scanner.Scan(source, ct: ct, target: target);
        // Also what earlier sorts of this folder into this target left behind (the app's "Move the remaining").
        MovePlan plan = Leftovers.WithEarlierSorts(Planner.Build(scan, target, mode, verifyChecksums: !o.ContainsKey("no-checksums")));
        Console.WriteLine($"Scanned {scan.Files.Count:N0} files in {scan.Folders.Count:N0} folders ({scan.Duration.TotalSeconds:0.0}s)");
        Console.WriteLine($"Move:  {plan.ToMove.Count:N0} files, {Format.Bytes(plan.BytesToMove)}  ({Planner.Word(mode)} and their companion files)");
        Console.WriteLine($"Stay:  {plan.Staying.Count:N0} files, {Format.Bytes(plan.BytesStaying)}");
        foreach (PlanMessage message in plan.Messages) Console.WriteLine($"[{message.Level}] {message.Text}");
        Console.WriteLine();
        Console.WriteLine("By type:");
        foreach (TypeSummary t in plan.ByType)
            Console.WriteLine($"  {(t.Moves ? "MOVE" : "stay"),-5} {t.Extension,-12} {t.Classification,-22} {t.Files,8:N0}  {Format.Bytes(t.Bytes),10}");
        if (o.ContainsKey("list"))
        {
            Console.WriteLine();
            foreach (SourceFile f in plan.ToMove) Console.WriteLine($"  MOVE {f.RelativePath}  [{f.Reason}]");
        }
        if (!run) return plan.CanRun ? 0 : 4;
        if (!plan.CanRun)
        {
            Console.Error.WriteLine("The plan cannot be run.");
            return 4;
        }
        if (!o.ContainsKey("yes")) throw new UsageException("Add --yes to confirm the move.");

        RunResult result;
        using (JobRunner runner = JobRunner.Start(plan, RunOptions()))
        {
            Console.WriteLine($"Job log: {runner.State.JournalPath}");
            result = runner.Run(ct);
        }
        return Report(result, ct);
    }

    private static int Resume(Dictionary<string, string?> o, bool close, CancellationToken ct)
    {
        string journal = ResolveJournal(o);
        RunResult result;
        using (JobRunner runner = JobRunner.Open(journal, RunOptions()))
        {
            JobState s = runner.State;
            Console.WriteLine($"{(close ? "Closing" : "Resuming")} job {s.Header.Id}: {s.DoneCount:N0} of {s.Items.Count:N0} files already moved");
            result = close ? runner.Close(ct) : runner.Run(ct);
        }
        return Report(result, ct);
    }

    private static int Verify(Dictionary<string, string?> o, CancellationToken ct)
    {
        string journal = ResolveJournal(o);
        VerifyResult result = JobVerifier.Verify(journal, ProgressPrinter(), ct);
        Console.WriteLine($"Verified {result.Checked:N0} files: {result.Matched:N0} match, {result.Problems.Count:N0} problems"
            + (result.NotMoved > 0 ? $" ({result.NotMoved:N0} planned files are still in the source)" : ""));
        foreach (string problem in result.Problems) Console.WriteLine("  " + problem);
        // The moved files speak only for themselves: an ended sort's source is checked again too, as after the job (files
        // added since, online-only ones), so "everything checked out" never hides what is still only in the source.
        SourceCheck? check = JournalReader.Read(journal) is { IsEnded: true, Header.Kind: JobKind.Sort } ? CheckSource(journal, ct) : null;
        return result.AllGood && (check is null || check.IsChecked && !check.FoundAgain.Any() && check.MemoryCard is null) ? 0 : 1;
    }

    private static int Status(Dictionary<string, string?> o)
    {
        string journal = ResolveJournal(o);
        JobReports.TryRefresh(journal); // after a crash, the reports written while the job ran can be a little behind its log
        JobState s = JournalReader.Read(journal);
        Console.WriteLine(JobReports.Summary(s));
        return 0;
    }

    private static int Jobs(Dictionary<string, string?> o)
    {
        string folder = Required(o, "target");
        List<JobListing> jobs = JobCatalog.ForFolder(folder);
        if (jobs.Count == 0)
        {
            Console.WriteLine($"No jobs found for {folder}.");
            return 0;
        }
        Console.WriteLine($"{"id",-33} {"date",-17} {"kind",-5} {"mode",-6} {"status",-13} {"moved",7} {"still",6}  undone / source -> target");
        foreach (JobListing j in jobs)
        {
            string undone = j.UndoneBy is { } u ? $"{(j.PartlyUndone ? "partly undone" : "undone")} ({u.Status}, {u.JobId})" : j.Kind == JobKind.Sort && j.IsConnected ? "-" : "";
            Console.WriteLine($"{j.Id,-33} {Date(j.Created),-17} {(j.Kind == JobKind.Undo ? "undo" : "sort"),-5} {Planner.Word(j.Mode),-6} {j.Status,-13} "
                + $"{(j.IsConnected ? j.Moved.ToString("N0", CultureInfo.CurrentCulture) : "?"),7} {(j.IsConnected ? j.StillInSource.ToString("N0", CultureInfo.CurrentCulture) : "?"),6}  "
                + $"{undone}  {j.Source} -> {j.Target}");
            if (j.WhyNotReachable is { } why) Console.WriteLine($"  {why}");
        }
        return 0;
    }

    private static int Undo(Dictionary<string, string?> o, CancellationToken ct)
    {
        string journal, startedFrom;
        if (o.TryGetValue("journal", out string? given) && given is not null)
        {
            if (UndoFactory.ResolveJournal(given, out string? whyNot) is not { } found)
            {
                Console.Error.WriteLine($"No job log found for {given}: {whyNot}");
                return 4;
            }
            journal = found;
            startedFrom = given;
        }
        else
        {
            string folder = Required(o, "target");
            List<JobListing> all = JobCatalog.ForFolder(folder).Where(j => j.Kind == JobKind.Sort).ToList();
            List<JobListing> sorts = all.Where(j => j.IsConnected).ToList();
            if (sorts.Count == 0 && all.FirstOrDefault(j => j.WhyNotReachable is not null) is { } unreachable)
            {
                Console.Error.WriteLine($"Sort job {unreachable.Id} was found for {folder}, but its log can't be opened. {unreachable.WhyNotReachable}");
                Console.Error.WriteLine("Then undo with:  undo --target <the sorted folder>   or   undo --journal <its job log>");
                return 4;
            }
            JobListing chosen = o.TryGetValue("job", out string? id) && id is not null
                ? sorts.FirstOrDefault(j => j.Id == id) ?? Unique(sorts.Where(j => j.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList(), id)
                : sorts.FirstOrDefault() ?? throw new UsageException($"No sort jobs found for {folder}.");
            journal = chosen.JournalPath;
            startedFrom = folder;
        }
        string? putBackTo = o.TryGetValue("to", out string? to) ? to ?? throw new UsageException("Missing folder after --to.") : null;

        UndoPreview preview = UndoFactory.Preview(journal, startedFrom, putBackTo);
        Console.WriteLine($"Undo of sort job {preview.Original.Header.Id} ({Date(preview.Original.Header.Created)}, {Planner.Word(preview.Original.Header.Mode)})");
        if (preview.Blocked is not null)
        {
            Console.Error.WriteLine(preview.Blocked);
            if (preview.NeedsFolder) Console.Error.WriteLine("To put the files back into a folder of your choice, add:  --to <folder>");
            return 4;
        }
        foreach (string line in preview.Describe()) Console.WriteLine("  " + line);
        if (o.ContainsKey("list"))
        {
            foreach (JobItem item in preview.AlreadyBack) Console.WriteLine($"  BACK  {item.Rel}");
            foreach (UndoIssue issue in preview.CannotGoBack) Console.WriteLine($"  STAYS {issue.Item.Rel}  [{issue.Reason}]");
        }
        if (!preview.CanRun) return 4;
        if (!o.ContainsKey("yes"))
        {
            Console.WriteLine("Add --yes to move the files back.");
            return 0;
        }

        string undoJournal;
        try
        {
            undoJournal = UndoFactory.CreateUndoJournal(journal, startedFrom, putBackTo);
        }
        catch (JournalException e)
        {
            Console.Error.WriteLine(e.Message); // something changed since the preview (or the sort is open elsewhere)
            return 4;
        }
        RunResult result;
        using (JobRunner runner = JobRunner.Open(undoJournal, RunOptions()))
        {
            Console.WriteLine($"Undo job log: {runner.State.JournalPath}");
            result = runner.Run(ct);
        }
        return Report(result, ct);
    }

    private static int Backup(Dictionary<string, string?> o, List<string> targets, CancellationToken ct)
    {
        string source = Required(o, "source");
        if (targets.Count == 0) throw new UsageException("Missing --target.");
        // Patterns written for earlier versions ({date}, {year}, ...) still work.
        string? template = o.TryGetValue("name-template", out string? pattern) ? BackupPlanner.UpgradeTemplate(pattern ?? "") : null;
        if (template is not null && BackupPlanner.ValidateTemplate(template) is { } templateError) throw new UsageException(templateError);
        // Never "_2" here (unlike the app): running the same command again stops at the folder the first run made.
        string name = o.TryGetValue("name", out string? n) && !string.IsNullOrWhiteSpace(n) ? n : BackupPlanner.DefaultName(source, DateTime.Now, template: template);
        BackupScan scan = BackupScanner.Scan(source, ct: ct);
        BackupPlan plan = BackupPlanner.Build(scan, targets, name, reread: !o.ContainsKey("no-source-reread"), topUp: o.ContainsKey("top-up"));
        Console.WriteLine($"Backup of {plan.SourceName}{(plan.IsCard ? " (memory card or camera drive)" : "")}: {plan.Files.Count:N0} files, {Format.Bytes(plan.Bytes)}, "
            + $"{scan.Folders.Count:N0} folders ({scan.Duration.TotalSeconds:0.0}s)");
        foreach (BackupTarget t in plan.Targets)
            Console.WriteLine($"  to {t.Folder}  ({t.DriveName}{(t.Volume is { } v ? $", {v.FileSystem}, {Format.Bytes(v.FreeBytes)} free" : "")})");
        Console.WriteLine($"Estimated time: about {Format.Duration(plan.Estimate())} at a typical card-reader speed");
        if (plan.TopUp is { } offer)
            Console.WriteLine(plan.IsTopUp ? offer.Describe(adding: true, plan.Files.Count) : $"[Top-up] {offer.Describe(adding: false, plan.Files.Count)} (add --top-up)");
        foreach (PlanMessage message in plan.Messages) Console.WriteLine($"[{message.Level}] {message.Text}");
        if (o.ContainsKey("list") && plan.IsTopUp)
            foreach (BackupTarget t in plan.Targets)
            {
                Console.WriteLine($"  In {t.Folder}:");
                var toCopy = t.Earlier!.ToCopy.ToHashSet();
                foreach (SourceFile f in plan.Files)
                    Console.WriteLine($"    {(!toCopy.Contains(f) ? "VERIFY" : plan.TopUp!.Earlier.TryGetValue(f.RelativePath, out EarlierFile? e) && !plan.TopUp.IsUnchanged(f, e) ? "REPLACE" : "COPY")} "
                        + $"{f.RelativePath}  ({Format.Bytes(f.Size)})");
                foreach (EarlierFile k in plan.TopUp!.Kept) Console.WriteLine($"    KEEP {k.Rel}  ({Format.Bytes(k.Size)}, no longer on the card)");
            }
        else if (o.ContainsKey("list"))
            foreach (SourceFile f in plan.Files) Console.WriteLine($"  COPY {f.RelativePath}  ({Format.Bytes(f.Size)})");
        if (!plan.CanRun)
        {
            Console.Error.WriteLine("The backup cannot be started.");
            return 4;
        }
        if (!o.ContainsKey("yes"))
        {
            Console.WriteLine("Add --yes to start the backup.");
            return 0;
        }
        using BackupRunner runner = BackupRunner.Start(plan, BackupRunOptions());
        Console.WriteLine($"Backup {runner.Header.Id}; logs: {string.Join(" | ", runner.Journals)}");
        return Report(runner.Run(ct));
    }

    private static int BackupResume(Dictionary<string, string?> o, bool close, CancellationToken ct)
    {
        using BackupRunner runner = BackupRunner.Open(ResolveBackupJournal(o, unfinished: true), BackupRunOptions());
        Console.WriteLine($"{(close ? "Ending" : "Resuming")} backup {runner.Header.Id} of {runner.Header.SourceLabel}");
        return Report(close ? runner.Close(ct) : runner.Run(ct));
    }

    private static int BackupVerify(Dictionary<string, string?> o, CancellationToken ct)
    {
        BackupVerifyResult result = BackupVerifier.Verify(ResolveBackupJournal(o), ct: ct);
        foreach (BackupVerifyDestination d in result.Destinations)
        {
            Console.WriteLine(d.NotReachable is { } why
                ? $"{d.Folder}: not checked - {why}"
                : $"{d.Folder}: {d.Matched:N0} of {result.Files:N0} files match, {d.Problems.Count:N0} problems");
            foreach (string problem in d.Problems) Console.WriteLine("  " + problem);
        }
        return result.AllGood ? 0 : 1;
    }

    private static int BackupStatus(Dictionary<string, string?> o)
    {
        Console.WriteLine(BackupReports.Summary(JournalReader.Read(ResolveBackupJournal(o))));
        return 0;
    }

    /// <summary>
    /// The log given, or the backup log in the folder given. A folder a top-up added to holds several: the newest (it
    /// describes the whole folder), or with <paramref name="unfinished"/> the one that is not finished.
    /// </summary>
    private static string ResolveBackupJournal(Dictionary<string, string?> o, bool unfinished = false)
    {
        if (o.TryGetValue("journal", out string? journal) && journal is not null) return journal;
        string folder = Required(o, "target");
        List<string> logs = BackupPaths.FindJournals(folder)
            .Select(l => (Log: l, Header: JournalReader.TryReadHeader(l)))
            .OrderByDescending(l => l.Header?.Created, StringComparer.Ordinal).Select(l => l.Log).ToList();
        if (logs.Count == 0) throw new UsageException($"No backup log found in {Path.Join(folder, JobPaths.LogFolderName)} (or {JobPaths.IvarIngestLogFolderName}).");
        if (logs.Count == 1) return logs[0];
        if (!unfinished) return logs[0];
        List<string> open = logs.Where(l => !JournalReader.Read(l).IsEnded).ToList();
        return open.Count == 1 ? open[0]
            : throw new UsageException((open.Count == 0 ? "Every backup in this folder has finished" : "Several unfinished backups found") + "; pass --journal:\n  " + string.Join("\n  ", logs));
    }

    private static BackupOptions BackupRunOptions()
    {
        DateTime last = DateTime.MinValue;
        return new BackupOptions
        {
            Faults = TestFaults.FromEnvironment(),
            Progress = new SyncProgress<BackupProgress>(p =>
            {
                if ((DateTime.UtcNow - last).TotalSeconds < 1 && p.ItemsFinished < p.ItemsTotal) return;
                last = DateTime.UtcNow;
                Console.Error.WriteLine($"  {p.Fraction,6:P1}  {p.ItemsFinished:N0}/{p.ItemsTotal:N0} files  {Format.Rate(p.BytesPerSecond)}  {p.Phase}: {p.CurrentFile}");
            }),
        };
    }

    private static int Report(BackupResult r)
    {
        Console.WriteLine($"{r.Status}: {r.Files:N0} files ({Format.Bytes(r.Bytes)})");
        foreach (BackupDestinationResult d in r.Destinations)
        {
            Console.WriteLine($"  {d.Folder}: {d.Verified:N0} verified, {d.Failed:N0} failed, {d.NotCopied:N0} not copied"
                + (d.MhlWritten ? ", ASC MHL written" : d.MhlSkipped is { } m ? $", no ASC MHL: {m}" : "") + (d.Ended ? "" : ", unfinished") + (d.Problem is { } p ? $" - {p}" : ""));
            if (r.AddsTo is not null)
                Console.WriteLine($"    added to the earlier backup: {d.Copied:N0} copied ({d.Restored:N0} of them no longer in the folder, {d.Replaced + d.Repaired + d.Edited + d.KeptBeside.Count:N0} replacing a file moved aside, "
                    + $"{d.KeptBeside.Count:N0} of those kept next to the new one as \"... (earlier)\"), "
                    + $"{d.Rechecked:N0} already there and read again; {d.Kept:N0} no longer on the card kept"
                    + (d.KeptGone > 0 ? $", {d.KeptGone:N0} no longer on the card nor in the folder" : "")
                    + (d.MhlRestarted is { } restarted ? $"; a new ASC MHL history was started (the earlier one is in {restarted})" : ""));
            foreach (string damaged in d.KeptDamaged) Console.WriteLine($"    DAMAGED {damaged} (no longer on the card; the copy here no longer matches its checksum - left as it is)");
        }
        if (r.LeftOut > 0) Console.WriteLine($"{r.LeftOut:N0} files or folders on the card were left out of the backup (see the preview's warnings and the summary).");
        foreach (string f in r.NotInBackup.Take(20)) Console.WriteLine($"  NOT IN BACKUP {f}");
        if (r.NotInBackup.Count > 20) Console.WriteLine($"  ... and {r.NotInBackup.Count - 20:N0} more not in the backup");
        if (r.CardNotRescanned) Console.WriteLine("The card could not be scanned again, so files added to it after the preview can't be ruled out.");
        if (r.Message is not null) Console.WriteLine(r.Message);
        if (r.AllVerified && r.AddsTo is not null)
            Console.WriteLine($"{r.Destinations.Max(d => d.Copied):N0} files copied, all {r.Files:N0} verified on {r.Destinations.Count} destination{(r.Destinations.Count == 1 ? "" : "s")}. The card was not changed.");
        else if (r.AllVerified)
            Console.WriteLine($"{r.Files:N0} files copied and verified to {r.Destinations.Count} destination{(r.Destinations.Count == 1 ? "" : "s")}. The card was not changed.");
        return r.AllVerified ? 0
            : r.Status is RunStatus.Stopped or RunStatus.Halted ? 2
            : r.Status == RunStatus.CompletedWithFailures || r.Destinations.Any(d => d.Failed > 0) ? 1
            : 5;
    }

    private static JobListing Unique(List<JobListing> matches, string id) => matches.Count switch
    {
        1 => matches[0],
        0 => throw new UsageException($"No sort job with id '{id}' found."),
        _ => throw new UsageException($"Several sort jobs match '{id}':\n  " + string.Join("\n  ", matches.Select(j => j.Id))),
    };

    private static string ResolveJournal(Dictionary<string, string?> o)
    {
        if (o.TryGetValue("journal", out string? journal) && journal is not null) return journal;
        string target = Required(o, "target");
        List<JobState> unfinished = JobPaths.FindUnfinished(target);
        if (unfinished.Count == 1) return unfinished[0].JournalPath;
        if (unfinished.Count == 0)
        {
            string? latest = JobPaths.FindJournals(target).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault(); // job ids start with the date
            return latest ?? throw new UsageException($"No job logs found in {JobPaths.LogFolder(target)} (or {string.Join(" or ", JobPaths.LegacyLogFolderNames)}).");
        }
        throw new UsageException("Several unfinished jobs found; pass --journal:\n  " + string.Join("\n  ", unfinished.Select(u => u.JournalPath)));
    }

    private static RunOptions RunOptions() => new()
    {
        Progress = ProgressPrinter(),
        Faults = TestFaults.FromEnvironment(),
    };

    private static IProgress<RunProgress> ProgressPrinter()
    {
        DateTime last = DateTime.MinValue;
        // Synchronous progress: printed on the worker thread, throttled to once a second.
        return new SyncProgress<RunProgress>(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 1 && p.ItemsFinished < p.ItemsTotal) return;
            last = DateTime.UtcNow;
            Console.Error.WriteLine($"  {p.Fraction,6:P1}  {p.ItemsFinished:N0}/{p.ItemsTotal:N0} files  {Format.Rate(p.BytesPerSecond)}  {p.Phase}: {p.CurrentFile}");
        });
    }

    private static int Report(RunResult r, CancellationToken ct)
    {
        Console.WriteLine($"{r.Status}: moved {r.Moved:N0}, skipped {r.Skipped:N0}, failed {r.Failed:N0}, not started {r.NotStarted:N0}, still in source {r.StillInSource:N0}"
            + (r.HeldBack > 0 ? $" ({r.HeldBack:N0} held back by the preview)" : "")
            + (r.MissingFromSource > 0 ? $", missing from the source {r.MissingFromSource:N0}" : ""));
        if (r.HeldBack > 0)
            Console.WriteLine($"{r.HeldBack:N0} files were held back by the preview and are still in the source (a DIFFERENT file with the same name is already in the target, or they belong with one). The summary lists them.");
        if (r.MissingFromSource > 0)
            Console.WriteLine($"{r.MissingFromSource:N0} files disappeared from the source before they could be moved (removed by something else): they are not in the target either. The summary lists them.");
        if (r.Message is not null) Console.WriteLine(r.Message);
        // An ended sort: the source is scanned again, as the app does (files added after the preview, online-only ones,
        // what earlier sorts left), and what it finds is also written into the job's summary.
        SourceCheck? check = r.Kind == JobKind.Sort && r.Status is RunStatus.Completed or RunStatus.Closed ? CheckSource(r.JournalPath, ct) : null;
        Console.WriteLine($"Log: {r.JournalPath}");
        return ExitCode(r, check);
    }

    /// <summary>The check of the source after a sort ended (see <see cref="SourceCheck"/>): printed, and recorded in the job's log.</summary>
    private static SourceCheck? CheckSource(string journal, CancellationToken ct)
    {
        JobState job;
        try
        {
            job = JournalReader.Read(journal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
            Console.WriteLine($"The source was not checked again: the job log can't be read ({e.Message}).");
            return new SourceCheck { NotChecked = $"the job log can't be read ({e.Message})" };
        }
        Console.Error.WriteLine("Checking the source again...");
        DateTime last = DateTime.MinValue;
        var progress = new SyncProgress<ScanProgress>(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 1) return;
            last = DateTime.UtcNow;
            Console.Error.WriteLine($"  {p.Files:N0} files in {p.Folders:N0} folders");
        });
        (SourceCheck check, _) = SourceCheck.Rescan(job, progress, ct);
        SourceCheck.Record(job, check);
        if (check.NotChecked is { } why)
            Console.WriteLine($"The source could not be checked again ({why}), so files added to it after the preview, or online-only ones, were not looked for.");
        else if (check.Unreadable.Count > 0)
            Console.WriteLine($"When the source was checked again, {check.UnreadableText}: what is in them was not checked.");
        List<LeftFile> found = check.FoundAgain.ToList();
        if (found.Count > 0)
        {
            Console.WriteLine($"Also still in the source, found when it was checked again (not part of this job): {found.Count:N0} files - "
                + string.Join("; ", found.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()).Select(g => $"{g.Count():N0} {g.Key}")) + ".");
            foreach (LeftFile f in found.Take(20)) Console.WriteLine($"  STILL IN SOURCE {f.Rel}  [{f.Reason}]");
            if (found.Count > 20) Console.WriteLine($"  ... and {found.Count - 20:N0} more (listed in the job's summary)");
        }
        if (check.OnlineOnly > 0)
            Console.WriteLine("Online-only files are cloud placeholders that are not downloaded: make them available on this device, then sort again.");
        if (check.MovableNow > 0)
            Console.WriteLine($"{check.MovableNow:N0} files can move now: run the same preview and run again (it also takes what this job left behind).");
        if (check.MemoryCard is { } card)
            Console.WriteLine($"{card} looks like a memory card or camera drive: it still holds the only copy of everything that stayed (of everything, when the "
                + "target is on it too), unless it was copied elsewhere. Copy it to another drive and check the copy before you format it.");
        return check;
    }

    /// <summary>
    /// 0 only when everything that should have moved is in the target; 5 when files are left behind (also those the
    /// check of the source after the job found), are missing, when the source could not be checked again for them, or
    /// when the source is a memory card or camera drive (it still holds the only copy of what stayed, as the app says).
    /// </summary>
    internal static int ExitCode(RunResult r, SourceCheck? check = null)
    {
        int left = check is null ? r.StillInSource : Math.Max(check.Left.Count, r.StillInSource);
        bool unsure = check is { IsChecked: false } or { MemoryCard: not null };
        return r.Status switch
        {
            RunStatus.Completed => left + r.MissingFromSource > 0 || unsure ? 5 : 0,
            RunStatus.Closed => r.Failed > 0 ? 1 : left + r.MissingFromSource > 0 || unsure ? 5 : 0,
            RunStatus.CompletedWithFailures => 1,
            _ => 2,
        };
    }

    private static string Date(string? at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t)
            ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : at ?? "";

    /// <param name="targets">Every --target given (a backup takes several; other commands use the last one).</param>
    private static Dictionary<string, string?> ParseOptions(string[] args, out List<string> targets)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        targets = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new UsageException($"Unexpected argument '{args[i]}'.");
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result[key] = value;
            if (key.Equals("target", StringComparison.OrdinalIgnoreCase) && value is not null) targets.Add(value);
        }
        return result;
    }

    private static string Required(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new UsageException($"Missing --{key}.");

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 3;
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
