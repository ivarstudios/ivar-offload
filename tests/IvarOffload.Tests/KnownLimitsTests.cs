using System.Diagnostics;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// The limits the final review of Sort left: the job's summary counts what the check of the source
/// after the job finds, as the result does; an ended job never calls an original it could not look at missing; a source
/// on a subst drive letter records its drive; a moved job log is found further away; the reports are brought up to date
/// after a crash; a same-drive move keeps the attributes (a rename sets Archive).
/// </summary>
public class KnownLimitsTests
{
    private static void Clips(TestTree t)
    {
        t.Add(@"a\C0001.MOV", 50_000);
        t.Add(@"a\C0002.MOV", 60_000);
        t.Add(@"b\C0003.MOV", 70_000);
        t.Add(@"b\C0004.MOV", 80_000);
        t.Add(@"a\DSC0001.JPG", 3000);
    }

    // ---- The check of the source after a job, in the job's summary ------------------------------------------------------

    [Fact]
    public void The_summary_lists_what_the_check_after_the_job_found_as_the_result_does()
    {
        using var t = new TestTree();
        Clips(t);
        t.Add(@"a\C0005.MOV", 40_000, FileAttributes.Offline); // a cloud placeholder: never read, so it stays
        RunResult r = t.Run(t.Plan());
        Assert.True(r.NothingLeftBehind); // the job moved all it planned ...
        t.Add(@"b\C0006.MOV", 30_000); // ... and a clip was added after the preview

        JobState job = JournalReader.Read(t.Journal);
        (SourceCheck check, _) = SourceCheck.Rescan(job, null, default);
        Assert.Equal([@"a\C0005.MOV", @"b\C0006.MOV"], check.FoundAgain.Select(f => f.Rel).Order());
        SourceCheck.Record(job, check);

        JobState recorded = JournalReader.Read(t.Journal);
        Assert.True(recorded.IsEnded);
        RescanRecord rescan = Assert.IsType<RescanRecord>(recorded.Rescan);
        Assert.Null(rescan.NotChecked);
        Assert.Equal(1, rescan.MovableNow); // the new clip; the placeholder can't move until it is downloaded
        Assert.Equal(SourceCheck.OnlineOnlyReason, rescan.Left.Single(f => f.Rel == @"a\C0005.MOV").Why);
        Assert.Equal(SourceCheck.NotInThisSort, rescan.Left.Single(f => f.Rel == @"b\C0006.MOV").Why);

        // The summary next to the log (and the command line's status, which prints it) says what the result says.
        string summary = File.ReadAllText(JobPaths.SummaryPath(t.Journal));
        Assert.Equal(JobReports.Summary(recorded), summary.TrimStart('\uFEFF'));
        Assert.Contains("Still in the source folder: none of the files that this sort planned to move.", summary);
        Assert.Contains("Also still in the source folder: 2 files", summary);
        Assert.Contains("A new sort of the folder can move 1 file now.", summary);
        Assert.Contains("In the source folder at the check after the job (2 files", summary);
        Assert.Contains($"  {SourceCheck.OnlineOnlyReason} - 1 file", summary);
        Assert.Contains(@"    b\C0006.MOV", summary);

        // The last check counts: once both are gone, the summary says the source was checked and nothing was found.
        File.Delete(Path.Join(t.Source, @"a\C0005.MOV"));
        File.Delete(Path.Join(t.Source, @"b\C0006.MOV"));
        (SourceCheck clean, _) = SourceCheck.Rescan(recorded, null, default);
        SourceCheck.Record(recorded, clean);
        summary = File.ReadAllText(JobPaths.SummaryPath(t.Journal));
        Assert.Contains("and found nothing else to move.", summary);
        Assert.Contains("Still in the source folder: no files to move.", summary);
        Assert.DoesNotContain("In the source folder at the check after the job", summary);
    }

    [Fact]
    public void A_check_that_could_not_reach_the_source_is_recorded_as_such_and_an_unfinished_job_gets_none()
    {
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        JobState unfinished = JournalReader.Read(t.Journal);
        SourceCheck.Record(unfinished, SourceCheck.ForJob(unfinished));
        Assert.Equal(0, Lines(t.Journal, "rescan")); // only an ended job is checked again

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        string away = t.Source + "-away";
        MoveFolder(t.Source, away);
        JobState job = JournalReader.Read(t.Journal);
        (SourceCheck check, MovePlan? plan) = SourceCheck.Rescan(job, null, default);
        Assert.Null(plan);
        Assert.False(check.IsChecked);
        SourceCheck.Record(job, check);
        Assert.Equal("the source folder is not available", JournalReader.Read(t.Journal).Rescan?.NotChecked);
        Assert.Contains("could not check the source folder again after the job (the source folder is not available)",
            File.ReadAllText(JobPaths.SummaryPath(t.Journal)));
    }

    [Fact]
    public void Leftovers_that_are_added_to_a_plan_keep_its_online_only_files_counted()
    {
        using var t = new TestTree();
        for (int n = 0; n < 12; n++) t.Add($@"Video\A001_C003\A001_C003_{n:D6}.dng", 300);
        t.Add(@"Video\A001_C003\A001_C003.wav", 300); // sound named after the clip: the frames are one CinemaDNG clip
        t.Add(@"Video\C0009.MOV", 20_000, FileAttributes.Offline);
        // "End job without moving the rest" after 9 of the 12 frames: on their own the last 3 look like photos.
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 9)));
        using (JobRunner runner = JobRunner.Open(t.Journal)) Assert.Equal(RunStatus.Closed, runner.Close().Status);

        MovePlan plan = Leftovers.WithEarlierSorts(t.Plan());
        Assert.Contains(plan.ToMove, f => f.Reason.Contains("earlier sort", StringComparison.Ordinal)); // the frames an ended job left
        Assert.Contains(plan.MovingSideStaying, f => f.RelativePath == @"Video\C0009.MOV"); // used to be dropped with them
    }

    // ---- Ending a job whose source can't be reached: nothing is called missing ---------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ending_a_job_without_its_source_never_calls_a_kept_copy_missing(bool nameTaken)
    {
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copied", 1)));
        JobItem item = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Copied);
        string dst = Path.Join(t.Target, item.Rel);
        if (nameTaken) File.WriteAllText(dst, "another program put a file here");
        else FlipByte(Path.Join(Path.GetDirectoryName(dst)!, item.Temp!), 10); // the copy no longer matches its checksum
        string away = t.Source + "-away";
        MoveFolder(t.Source, away); // the source is not there when the job is ended

        using (JobRunner runner = JobRunner.Open(t.Journal)) Assert.Equal(RunStatus.Closed, runner.Close().Status);
        JobState state = JournalReader.Read(t.Journal);
        JobItem after = state.Items[item.Index];
        string suffix = nameTaken ? JobPaths.VerifiedCopySuffix : JobPaths.DamagedCopySuffix;
        Assert.True(FailReasons.IsMadeBy(after.Note, nameTaken ? FailReasons.VerifiedCopyKeptNotChecked : FailReasons.DamagedCopyKeptNotChecked), after.Note);
        Assert.True(File.Exists(dst + suffix), "the copy is kept");
        Assert.True(File.Exists(Path.Join(away, item.Rel)), "the original was not touched");
        // It could not be looked at, so it is still in the source as far as anyone knows - never "missing".
        Assert.False(after.IsMissing);
        Assert.True(after.IsStillInSource);
        Assert.Equal(0, state.MissingCount);
        SourceCheck check = SourceCheck.ForJob(state);
        Assert.Empty(check.Missing);
        Assert.Contains("not moved because you ended the job when the source folder was not available", check.Left.Single(f => f.Rel == item.Rel).Reason);
        Assert.Contains("You ended the job when it could not check their originals", JobReports.Summary(state));
        Assert.DoesNotContain("Missing:", JobReports.Summary(state));
    }

    // ---- A same-drive move keeps the attributes -------------------------------------------------------------------------

    [Fact]
    public void A_same_drive_move_and_its_undo_keep_the_attributes_a_rename_would_change()
    {
        using var t = new TestTree();
        t.Add(@"a\C0001.MOV", attributes: FileAttributes.Normal); // no Archive attribute: a rename sets it
        t.Add(@"a\C0002.MOV", attributes: FileAttributes.ReadOnly);
        t.Add(@"a\C0003.MOV", attributes: FileAttributes.Archive | FileAttributes.Hidden);
        t.Add(@"a\C0004.MOV", attributes: FileAttributes.Normal);
        var before = Directory.EnumerateFiles(t.Source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(t.Source, f), File.GetAttributes);

        // A crash right after a rename: resume puts the attributes back too (the log recorded them before the rename).
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 4)));
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        Assert.Equal(TransferMethod.Rename, JournalReader.Read(t.Journal).Header.Method);
        foreach ((string rel, FileAttributes attributes) in before)
            Assert.Equal(attributes, File.GetAttributes(Path.Join(t.Target, rel)));

        using (JobRunner undo = UndoFactory.Start(t.Journal)) Assert.Equal(RunStatus.Completed, undo.Run().Status);
        foreach ((string rel, FileAttributes attributes) in before)
            Assert.Equal(attributes, File.GetAttributes(Path.Join(t.Source, rel)));
    }

    // ---- Reports after a crash ------------------------------------------------------------------------------------------

    [Fact]
    public void After_a_crash_the_receipt_and_the_reports_are_brought_up_to_date_from_the_log()
    {
        using var t = new TestTree();
        Clips(t);
        // The reports are written when the first file has left, then about every quarter of a minute: a crash at the
        // third file leaves a receipt that still lists the second one as not moved.
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 3)));
        JobState state = JournalReader.Read(t.Journal);
        JobItem second = state.Items[1];
        Assert.Equal(ItemStage.Done, second.Stage);
        string csv = JobPaths.ReceiptCsvPath(t.Source, state.Header.Id), receipt = JobPaths.ReceiptTextPath(t.Source, state.Header.Id);
        Assert.Contains(File.ReadAllLines(csv), r => r.StartsWith("not moved", StringComparison.Ordinal) && r.Contains(Path.Join(t.Source, second.Rel)));

        // Nobody may run the job while it is brought up to date: a job open elsewhere keeps its reports as they are.
        string written = File.ReadAllText(receipt);
        using (JournalWriter.OpenForAppend(t.Journal, out _)) JobReports.TryRefresh(t.Journal);
        Assert.Equal(written, File.ReadAllText(receipt));

        JobReports.TryRefresh(t.Journal);
        Assert.Contains(File.ReadAllLines(csv), r => r.StartsWith("moved", StringComparison.Ordinal) && r.Contains(Path.Join(t.Source, second.Rel)));
        Assert.Contains(File.ReadAllLines(csv), r => r.StartsWith("not moved", StringComparison.Ordinal) && r.Contains("the job stopped before the end. Resume continues it"));
        Assert.DoesNotContain(File.ReadAllLines(csv), r => r.Contains("The file can be in the target folder now"));
        string text = File.ReadAllText(receipt);
        Assert.Contains("it stopped before the end. You can resume it or end it", text);
        Assert.DoesNotContain(", during the job.", text);
        // The fourth clip was not reached; the third was being renamed: it is here or already in the target.
        Assert.Contains("that the job planned to move is still in this folder", text);
        Assert.Contains($"The job stopped during the move of 1 file (\"interrupted\" in the list). This file is in this folder or already in {t.Target}", text);
        Assert.Contains(File.ReadAllLines(csv), r => r.StartsWith("interrupted", StringComparison.Ordinal) && r.Contains("Resume finishes the move"));
        Assert.Contains(File.ReadAllLines(JobPaths.ManifestPath(t.Journal)), r => r.StartsWith("moved", StringComparison.Ordinal) && r.Contains(second.Rel));

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        Assert.DoesNotContain("interrupted", File.ReadAllText(receipt));
        Assert.DoesNotContain("stopped", File.ReadAllText(receipt));
    }

    // ---- A job log whose folder was moved further away -------------------------------------------------------------------

    [Fact]
    public void A_sorted_folder_moved_into_an_archive_folder_is_found_again()
    {
        using var t = new TestTree();
        Clips(t);
        RunResult r = t.Run(t.Plan());
        string recorded = r.JournalPath, id = JournalReader.Read(recorded).Header.Id;
        string archived = Path.Join(t.Root, "Archive", "INGEST-Video");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        MoveFolder(t.Target, archived);

        string expected = Path.Join(archived, JobPaths.LogFolderName, Path.GetFileName(recorded));
        Assert.Equal(expected, JobPaths.FindMovedJournal(recorded, id));
        // From the receipt in the source too (what "Open a log file..." and "undo --journal <receipt>" do).
        Assert.Equal(expected, UndoFactory.ResolveJournal(JobPaths.ReceiptTextPath(t.Source, id), out _));
        Assert.True(UndoFactory.Preview(expected).CanRun);

        // Another job's log with the same name is never taken for it.
        Assert.Null(JobPaths.FindMovedJournal(recorded, id + "-other"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_of_the_sorted_folder_is_never_taken_for_it(bool copy)
    {
        using var t = new TestTree();
        Clips(t);
        RunResult r = t.Run(copy ? AsCopy(t.Plan()) : t.Plan()); // rename: file ids decide; copy: creation times do
        string recorded = r.JournalPath, id = JournalReader.Read(recorded).Header.Id;
        // A delivery copy nearby (Explorer's copy keeps the files' dates, but they get new file ids and creation times) ...
        CopyFolder(t.Target, Path.Join(t.Root, "Delivery", "INGEST-Video"));
        CopyFolder(t.Target, t.Target + " - Copy");
        // ... while the sorted folder itself went further away than the search looks.
        string far = Path.Join(t.Root, @"x1\x2\x3\x4\x5\INGEST-Video");
        Directory.CreateDirectory(Path.GetDirectoryName(far)!);
        MoveFolder(t.Target, far);
        Assert.Null(JobPaths.FindMovedJournal(recorded, id));
        Assert.Null(UndoFactory.ResolveJournal(JobPaths.ReceiptTextPath(t.Source, id), out _));

        // Renamed where it was, it is found - never the copy next to it.
        MoveFolder(far, t.Target + "_2026");
        Assert.Equal(Path.Join(t.Target + "_2026", JobPaths.LogFolderName, Path.GetFileName(recorded)), JobPaths.FindMovedJournal(recorded, id));
    }

    [Fact]
    public void The_wider_search_for_a_log_looks_nearest_first_and_stops_at_its_limit()
    {
        using var t = new TestTree();
        Clips(t);
        RunResult r = t.Run(t.Plan());
        string recorded = r.JournalPath, id = JournalReader.Read(recorded).Header.Id, name = Path.GetFileName(recorded);
        string deep = Path.Join(t.Root, "x1", "x2", "x3", "x4", "INGEST-Video"); // deeper than the search goes
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        MoveFolder(t.Target, deep);
        Assert.Null(JobPaths.SearchFurther(t.Target, name, id, [(t.Root, 1)], TimeSpan.FromSeconds(5), 1000));

        string near = Path.Join(t.Root, "x1", "x2", "x3", "INGEST-Video"); // moved into an archive, a few levels down
        MoveFolder(deep, near);
        Assert.Equal(Path.Join(near, JobPaths.LogFolderName, name), JobPaths.SearchFurther(t.Target, name, id, [(t.Root, 1)], TimeSpan.FromSeconds(5), 1000));
        Assert.Null(JobPaths.SearchFurther(t.Target, name, id, [(t.Root, 1)], TimeSpan.Zero, 1000)); // out of time
        Assert.Null(JobPaths.SearchFurther(t.Target, name, id, [(t.Root, 1)], TimeSpan.FromSeconds(5), 0)); // out of folders
    }

    // ---- A source on a subst drive letter ---------------------------------------------------------------------------------

    [Fact]
    public void A_subst_drive_letter_gets_the_drive_of_the_folder_it_stands_for()
    {
        using var t = new TestTree();
        using var subst = new SubstDrive(t.Root);
        string letter = subst.Root;

        Assert.Equal(t.Root, VolumeInfo.SubstTarget(letter));
        Assert.Null(VolumeInfo.SubstTarget(Path.GetPathRoot(t.Root)!));
        Assert.Equal(Path.Join(t.Root, "INGEST", "a"), VolumeInfo.Unsubst(Path.Join(letter, "INGEST", "a")));
        VolumeInfo real = VolumeInfo.Of(t.Root), viaSubst = VolumeInfo.Of(letter);
        Assert.NotEqual(0u, viaSubst.SerialNumber);
        Assert.Equal((real.SerialNumber, real.Root, real.FileSystem), (viaSubst.SerialNumber, viaSubst.Root, viaSubst.FileSystem));
        Assert.Equal(real.SerialNumber, Drives.SerialOf(Path.Join(letter, "INGEST")));

        // A subst letter is a folder, not another letter of a drive: never offered as one, and a path on it has none.
        Assert.DoesNotContain(Drives.OnOtherLetters(t.Source), p => p.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Drives.OnOtherLetters(Path.Join(letter, "INGEST")));
    }

    [Fact]
    public void A_sort_through_a_subst_drive_letter_records_its_drive_and_is_undone_after_the_letter_is_gone()
    {
        using var t = new TestTree();
        Clips(t);
        var before = Fingerprints(t.Source);
        string journal;
        using (var subst = new SubstDrive(t.Root))
        {
            string source = Path.Join(subst.Root, "INGEST"), target = Path.Join(subst.Root, "INGEST-Video");
            MovePlan plan = Planner.Build(Scanner.Scan(source), target, MoveMode.Videos, true);
            Assert.True(plan.CanRun, string.Join(" ", plan.Messages.Select(m => m.Text)));
            Assert.Equal(TransferMethod.Rename, plan.Method);
            RunResult r = t.Run(plan);
            Assert.Equal(RunStatus.Completed, r.Status);
            JobHeader h = JournalReader.Read(r.JournalPath).Header;
            Assert.Equal(VolumeInfo.Of(t.Root).SerialNumber, h.SourceSerial); // used to be 0: no drive checks at all
            Assert.Equal(t.Source, h.SourceReal);
            Assert.Equal(t.Target, h.TargetReal);
            journal = Path.Join(t.Target, JobPaths.LogFolderName, Path.GetFileName(r.JournalPath));
        }

        // The letter is gone: the undo finds the folder the letter stood for (it holds the receipt) and puts the files back.
        UndoPreview preview = UndoFactory.Preview(journal);
        Assert.True(preview.CanRun, preview.Blocked);
        using (JobRunner undo = UndoFactory.Start(journal)) Assert.Equal(RunStatus.Completed, undo.Run().Status);
        foreach ((string rel, var fingerprint) in before) Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(t.Source, rel)));
    }

    [Fact]
    public void A_subst_drive_letter_given_another_folder_is_not_taken_for_the_one_the_job_was_made_with()
    {
        using var t = new TestTree();
        Clips(t);
        string other = Path.Join(t.Root, "OtherProject");
        Directory.CreateDirectory(Path.Join(other, "INGEST")); // the letter will lead to a folder with the same name
        using var subst = new SubstDrive(t.Root);
        string source = Path.Join(subst.Root, "INGEST"); // the target is named by its own path
        Assert.Throws<SimulatedCrash>(() => t.Run(Planner.Build(Scanner.Scan(source), t.Target, MoveMode.Videos, true), new CrashAt("after-rename", 2)));
        string journal = t.Journal;

        subst.PointAt(other);
        // Resume halts instead of working in the other folder ...
        using (JobRunner runner = JobRunner.Open(journal))
        {
            RunResult halted = runner.Run();
            Assert.Equal(RunStatus.Halted, halted.Status);
            Assert.Contains("which now shows a different folder", halted.Message);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(other, "INGEST")));

        // ... and an undo puts the files back where the letter led when the job was made, never into the other folder.
        subst.PointAt(t.Root);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(journal).Status);
        subst.PointAt(other);
        UndoPreview preview = UndoFactory.Preview(journal);
        Assert.True(preview.CanRun, preview.Blocked);
        Assert.Equal(t.Source, preview.To);
    }

    [Fact]
    public void A_subst_drive_letter_for_the_Windows_drive_is_refused_like_the_drive_itself()
    {
        string windows = Path.GetPathRoot(Environment.SystemDirectory)!;
        using var subst = new SubstDrive(windows);
        Assert.Equal(windows, VolumeInfo.SubstTarget(subst.Root)); // "C:\", not the drive-relative "C:"
        Assert.Equal(windows, Drives.RealIfSubst(subst.Root));
        PlanMessage? refused = SourceGuards.CheckBroad(subst.Root, VolumeInfo.Of(subst.Root), SystemFolders.Current, MoveMode.Videos);
        Assert.Equal(MessageLevel.Error, refused?.Level);
    }

    /// <summary>A free drive letter made to stand for a folder with <c>subst</c> for the length of a test.</summary>
    private sealed class SubstDrive : IDisposable
    {
        /// <summary>"W:\".</summary>
        public string Root { get; }

        public SubstDrive(string folder)
        {
            char letter = Enumerable.Range('G', 20).Select(c => (char)c).Reverse()
                .First(c => !Directory.Exists($"{c}:\\") && VolumeInfo.SubstTarget($"{c}:") is null && !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
            Root = $"{letter}:\\";
            // A drive root is passed as it is: in quotes, its backslash would escape the closing quote.
            Subst($"{letter}: " + (folder.Length == 3 && folder.EndsWith(":\\", StringComparison.Ordinal) ? folder : $"\"{folder.TrimEnd('\\')}\""));
            Assert.True(Directory.Exists(Root), "subst did not make the drive letter");
        }

        /// <summary>Gives the letter another folder (as <c>subst</c> allows at any time, after removing it).</summary>
        public void PointAt(string folder)
        {
            Subst($"{Root[..2]} /D");
            Subst($"{Root[..2]} \"{folder.TrimEnd('\\')}\"");
        }

        public void Dispose() => Subst($"{Root[..2]} /D");

        private static void Subst(string arguments)
        {
            using Process p = Process.Start(new ProcessStartInfo("subst.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
        }
    }
}
