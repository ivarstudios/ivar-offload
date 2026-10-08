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
                                            Finishes or rolls back the files in progress, then ends the job. The other
                                            files stay in the source folder.
          verify  --journal <file> | --target <folder>
                                            Reads every moved file again and compares the checksums. It also lists the
                                            files that did not move.
          status  --journal <file> | --target <folder>
          jobs    --target <folder>         Lists the jobs of a folder (jobs that moved files into it or out of it).
          undo    --journal <file> [--to <folder>] [--yes] [--list]
                                            Returns the files of a finished sort to its source folder. Without --yes, it
                                            only shows what will happen. --list names the files that are already back and
                                            the files that cannot return.
          undo    --target <folder> [--job <id>] [--to <folder>] [--yes] [--list]
                                            Undoes the most recent sort job of that folder, or the job with that id.
                                            --journal also accepts the manifest or the summary of the job, or a .moved-out
                                            receipt. --to returns the files to that folder. Use it when IVAR Offload cannot
                                            confirm the folder that the files came from.

        Backup: copies a card to one, two or three backup drives. IVAR Offload only reads the card.
          backup  --source <card> --target <folder> [--target <folder> ...] [--name <name> | --name-template <pattern>] [--no-source-reread] [--top-up] [--list] [--yes]
                                            Without --yes, it only shows the preview. On each backup drive, the backup
                                            folder is <folder>\<name>. The default name is YYMMDD_<card label>. IVAR Offload
                                            does not accept a backup folder that is not empty.
                                            --name-template sets the pattern of the default name. The pattern can contain
                                            {card}, {camera}, and date and time codes in braces: YYYY or YY, MM, DD, HH, MM
                                            (minutes, after HH or before SS), SS. For example: "{YYMMDD}_{camera}_{card}" or
                                            "{YYYY-MM-DD}_{HHMMSS}_{card}".
                                            --top-up adds the new files of the card to its earlier backup (the same backup
                                            in each <folder>). It also checks the whole card. It does not make a new full
                                            backup. The top-up does not overwrite or delete anything in the earlier backup.
                                            Older versions of changed files move into _IVAROffload\replaced. Files that are
                                            no longer on the card stay in the backup.
          backup-resume  --journal <file> | --target <backup folder>
          backup-close   --journal <file> | --target <backup folder>
                                            Finishes or removes the copies in progress, then ends the backup. IVAR Offload
                                            does not copy the other files.
          backup-verify  --journal <file> | --target <backup folder>
                                            Reads every copy on every backup drive again and compares the checksums.
          backup-status  --journal <file> | --target <backup folder>

        Exit codes: 0 done, and no file to move is still in the source folder. 1 finished with failures. 2 stopped (on
                    request or after a problem). 3 usage error. 4 the plan (or the undo) cannot run. 5 done or closed,
                    but files are still in the source folder. These include files that the preview kept, and files that
                    the check after the job found (files that came into the folder after the preview, or online-only
                    files). Code 5 can also mean that files disappeared from the source folder, or that IVAR Offload
                    could not check it again after the job.
                    Backups: 0 every file checked on every backup drive. 1 finished with failures (or the backup stopped
                    on one of the backup drives). 2 stopped (on request or after a problem). 5 finished, but the backup
                    does not contain everything that is on the card.
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
            Console.Error.WriteLine("The job stops safely after the current step. You can resume it.");
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
                _ => UsageError($"IVAR Offload does not know the command '{args[0]}'."),
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
        Console.WriteLine($"Source folder: {scan.Files.Count:N0} files in {scan.Folders.Count:N0} folders (scan time {scan.Duration.TotalSeconds:0.0}s)");
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
            Console.Error.WriteLine("IVAR Offload cannot run this plan.");
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
            Console.WriteLine($"IVAR Offload {(close ? "ends" : "resumes")} job {s.Header.Id}. {s.DoneCount:N0} of {s.Items.Count:N0} files already moved.");
            result = close ? runner.Close(ct) : runner.Run(ct);
        }
        return Report(result, ct);
    }

    private static int Verify(Dictionary<string, string?> o, CancellationToken ct)
    {
        string journal = ResolveJournal(o);
        VerifyResult result = JobVerifier.Verify(journal, ProgressPrinter(), ct);
        Console.WriteLine($"IVAR Offload checked {result.Checked:N0} files: {result.Matched:N0} match, {result.Problems.Count:N0} problems"
            + (result.NotMoved > 0 ? $" ({result.NotMoved:N0} planned files are still in the source folder)" : ""));
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
            Console.WriteLine($"IVAR Offload found no jobs for {folder}.");
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
                Console.Error.WriteLine($"IVAR Offload found no job log for {given}. {whyNot}");
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
                Console.Error.WriteLine($"IVAR Offload found sort job {unreachable.Id} for {folder}. It cannot open the log of this job. {unreachable.WhyNotReachable}");
                Console.Error.WriteLine("Then, to undo the sort, use:  undo --target <target folder>   or   undo --journal <the job log>");
                return 4;
            }
            JobListing chosen = o.TryGetValue("job", out string? id) && id is not null
                ? sorts.FirstOrDefault(j => j.Id == id) ?? Unique(sorts.Where(j => j.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).ToList(), id)
                : sorts.FirstOrDefault() ?? throw new UsageException($"IVAR Offload found no sort jobs for {folder}.");
            journal = chosen.JournalPath;
            startedFrom = folder;
        }
        string? putBackTo = o.TryGetValue("to", out string? to) ? to ?? throw new UsageException("The folder after --to is missing.") : null;

        UndoPreview preview = UndoFactory.Preview(journal, startedFrom, putBackTo);
        Console.WriteLine($"Undo of sort job {preview.Original.Header.Id} ({Date(preview.Original.Header.Created)}, {Planner.Word(preview.Original.Header.Mode)})");
        if (preview.Blocked is not null)
        {
            Console.Error.WriteLine(preview.Blocked);
            if (preview.NeedsFolder) Console.Error.WriteLine("To return the files to a folder that you choose, add:  --to <folder>");
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
            Console.WriteLine("Add --yes to return the files.");
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
        if (targets.Count == 0) throw new UsageException("--target is missing.");
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
        Console.WriteLine($"Estimated time: approximately {Format.Duration(plan.Estimate())} at a typical card-reader speed");
        if (plan.TopUp is { } offer)
            Console.WriteLine(plan.IsTopUp ? offer.Describe(adding: true, plan.Files.Count) : $"[Top-up] {offer.Describe(adding: false, plan.Files.Count)} To do this, add --top-up.");
        foreach (PlanMessage message in plan.Messages) Console.WriteLine($"[{message.Level}] {message.Text}");
        if (o.ContainsKey("list") && plan.IsTopUp)
            foreach (BackupTarget t in plan.Targets)
            {
                Console.WriteLine($"  In {t.Folder}:");
                var toCopy = t.Earlier!.ToCopy.ToHashSet();
                foreach (SourceFile f in plan.Files)
                    Console.WriteLine($"    {(!toCopy.Contains(f) ? "CHECK" : plan.TopUp!.Earlier.TryGetValue(f.RelativePath, out EarlierFile? e) && !plan.TopUp.IsUnchanged(f, e) ? "REPLACE" : "COPY")} "
                        + $"{f.RelativePath}  ({Format.Bytes(f.Size)})");
                foreach (EarlierFile k in plan.TopUp!.Kept) Console.WriteLine($"    KEEP {k.Rel}  ({Format.Bytes(k.Size)}, no longer on the card)");
            }
        else if (o.ContainsKey("list"))
            foreach (SourceFile f in plan.Files) Console.WriteLine($"  COPY {f.RelativePath}  ({Format.Bytes(f.Size)})");
        if (!plan.CanRun)
        {
            Console.Error.WriteLine("The backup cannot start.");
            return 4;
        }
        if (!o.ContainsKey("yes"))
        {
            Console.WriteLine("Add --yes to start the backup.");
            return 0;
        }
        using BackupRunner runner = BackupRunner.Start(plan, BackupRunOptions());
        Console.WriteLine($"Backup {runner.Header.Id}. Job logs: {string.Join(" | ", runner.Journals)}");
        return Report(runner.Run(ct));
    }

    private static int BackupResume(Dictionary<string, string?> o, bool close, CancellationToken ct)
    {
        using BackupRunner runner = BackupRunner.Open(ResolveBackupJournal(o, unfinished: true), BackupRunOptions());
        Console.WriteLine($"IVAR Offload {(close ? "ends" : "resumes")} backup {runner.Header.Id} of {runner.Header.SourceLabel}.");
        return Report(close ? runner.Close(ct) : runner.Run(ct));
    }

    private static int BackupVerify(Dictionary<string, string?> o, CancellationToken ct)
    {
        BackupVerifyResult result = BackupVerifier.Verify(ResolveBackupJournal(o), ct: ct);
        foreach (BackupVerifyDestination d in result.Destinations)
        {
            Console.WriteLine(d.NotReachable is { } why
                ? $"{d.Folder}: not checked. {why}"
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
        if (logs.Count == 0) throw new UsageException($"IVAR Offload found no backup log in {Path.Join(folder, JobPaths.LogFolderName)} (or in {JobPaths.IvarIngestLogFolderName}).");
        if (logs.Count == 1) return logs[0];
        if (!unfinished) return logs[0];
        List<string> open = logs.Where(l => !JournalReader.Read(l).IsEnded).ToList();
        return open.Count == 1 ? open[0]
            : throw new UsageException((open.Count == 0 ? "Every backup in this folder is finished." : "IVAR Offload found more than one unfinished backup.")
                + " Add --journal and one of these job logs:\n  " + string.Join("\n  ", logs));
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
            Console.WriteLine($"  {d.Folder}: {d.Verified:N0} checked, {d.Failed:N0} failed, {d.NotCopied:N0} not copied"
                + (d.MhlWritten ? ", ASC MHL written" : d.MhlSkipped is { } m ? $", no ASC MHL: {m}" : "") + (d.Ended ? "" : ", unfinished") + (d.Problem is { } p ? $". {p}" : ""));
            if (r.AddsTo is not null)
                Console.WriteLine($"    added to the earlier backup: {d.Copied:N0} copied ({d.Restored:N0} of them no longer in the folder, "
                    + $"{d.Replaced + d.Repaired + d.Edited + d.KeptBeside.Count:N0} in place of an earlier file that IVAR Offload moved and kept, "
                    + $"{d.KeptBeside.Count:N0} of those earlier files stay next to the new file as \"... (earlier)\"), "
                    + $"{d.Rechecked:N0} already there (IVAR Offload read them again). IVAR Offload kept {d.Kept:N0} files that are no longer on the card"
                    + (d.KeptGone > 0 ? $". {d.KeptGone:N0} files are no longer on the card or in the folder" : "")
                    + (d.MhlRestarted is { } restarted ? $". IVAR Offload started a new ASC MHL history. The earlier history is in {restarted}" : "")
                    + ".");
            foreach (string damaged in d.KeptDamaged)
                Console.WriteLine($"    DAMAGED {damaged}: the file is no longer on the card. The copy here no longer matches its checksum. IVAR Offload did not change the copy.");
        }
        if (r.LeftOut > 0) Console.WriteLine($"The backup did not copy {r.LeftOut:N0} files or folders on the card. For details, see the preview warnings and the summary.");
        foreach (string f in r.NotInBackup.Take(20)) Console.WriteLine($"  NOT IN BACKUP {f}");
        if (r.NotInBackup.Count > 20) Console.WriteLine($"  ... and {r.NotInBackup.Count - 20:N0} more files that are not in the backup");
        if (r.CardNotRescanned) Console.WriteLine("Because IVAR Offload could not scan the card again, it does not know if files came onto the card after the preview.");
        if (r.Message is not null) Console.WriteLine(r.Message);
        if (r.AllVerified && r.AddsTo is not null)
            Console.WriteLine($"IVAR Offload copied {r.Destinations.Max(d => d.Copied):N0} files and checked all {r.Files:N0} files on {r.Destinations.Count} backup drive{(r.Destinations.Count == 1 ? "" : "s")}. "
                + "It did not change the card.");
        else if (r.AllVerified)
            Console.WriteLine($"IVAR Offload copied {r.Files:N0} files to {r.Destinations.Count} backup drive{(r.Destinations.Count == 1 ? "" : "s")} and checked them. It did not change the card.");
        return r.AllVerified ? 0
            : r.Status is RunStatus.Stopped or RunStatus.Halted ? 2
            : r.Status == RunStatus.CompletedWithFailures || r.Destinations.Any(d => d.Failed > 0) ? 1
            : 5;
    }

    private static JobListing Unique(List<JobListing> matches, string id) => matches.Count switch
    {
        1 => matches[0],
        0 => throw new UsageException($"IVAR Offload found no sort job with the id '{id}'."),
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
            return latest ?? throw new UsageException($"IVAR Offload found no job logs in {JobPaths.LogFolder(target)} (or in {string.Join(" or ", JobPaths.LegacyLogFolderNames)}).");
        }
        throw new UsageException("IVAR Offload found more than one unfinished job. Add --journal and one of these job logs:\n  " + string.Join("\n  ", unfinished.Select(u => u.JournalPath)));
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
            + (r.HeldBack > 0 ? $" (the preview kept {r.HeldBack:N0} of them)" : "")
            + (r.MissingFromSource > 0 ? $", missing from the source {r.MissingFromSource:N0}" : ""));
        if (r.HeldBack > 0)
            Console.WriteLine($"The preview kept {r.HeldBack:N0} files in the source folder. A DIFFERENT file with the same name is already in the target folder, "
                + "or these files are companion files of a file like that. The summary lists them.");
        if (r.MissingFromSource > 0)
            Console.WriteLine($"{r.MissingFromSource:N0} files disappeared from the source folder before IVAR Offload could move them. Something else removed them. "
                + "They are not in the target folder either. The summary lists them.");
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
            Console.WriteLine($"IVAR Offload did not check the source folder again, because it could not read the job log ({e.Message}).");
            return new SourceCheck { NotChecked = $"IVAR Offload could not read the job log ({e.Message})" };
        }
        Console.Error.WriteLine("IVAR Offload checks the source folder again...");
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
            Console.WriteLine($"IVAR Offload could not check the source folder again ({why}). "
                + "It did not examine the folder for files that came into it after the preview, or for online-only files.");
        else if (check.Unreadable.Count > 0)
            Console.WriteLine($"When IVAR Offload checked the source folder again, {check.UnreadableText}. "
                + $"IVAR Offload did not check the files in {(check.Unreadable.Count == 1 ? "this folder" : "these folders")}.");
        List<LeftFile> found = check.FoundAgain.ToList();
        if (found.Count > 0)
        {
            Console.WriteLine($"When IVAR Offload checked the source folder again, it also found {Format.Count(found.Count, "file")} that {(found.Count == 1 ? "is" : "are")} "
                + $"still in it. {(found.Count == 1 ? "This file is" : "These files are")} not part of this job:");
            foreach (var reason in found.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {Format.Count(reason.Count(), "file")}: {reason.Key}");
            foreach (LeftFile f in found.Take(20)) Console.WriteLine($"  STILL IN SOURCE {f.Rel}  [{f.Reason}]");
            if (found.Count > 20) Console.WriteLine($"  ... and {found.Count - 20:N0} more files. The job summary lists them.");
        }
        if (check.OnlineOnly > 0)
            Console.WriteLine("Online-only files are cloud placeholders that are not downloaded. Make them available on this device. Then sort the source folder again.");
        if (check.MovableNow > 0)
            Console.WriteLine($"{check.MovableNow:N0} files can move now. To move them, use the same preview and run commands again. "
                + "The new job also includes the files that this job did not move.");
        if (check.MemoryCard is { } card)
            Console.WriteLine($"{card} looks like a memory card or camera drive. It still holds the only copy of everything that stayed (or of everything, if the "
                + "target folder is on it too), unless you already copied it to another drive. Copy the card to another drive. Then check the copy. "
                + "Do not format the card before you check the copy.");
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
            if (!args[i].StartsWith("--")) throw new UsageException($"IVAR Offload did not expect the argument '{args[i]}'.");
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result[key] = value;
            if (key.Equals("target", StringComparison.OrdinalIgnoreCase) && value is not null) targets.Add(value);
        }
        return result;
    }

    private static string Required(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new UsageException($"--{key} is missing.");

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
