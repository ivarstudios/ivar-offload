using System.Text.Json;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>Undo of a sort (from its log), the receipt left in the source, and the job lists built from both.</summary>
public class UndoTests
{
    private static void Sample(TestTree t)
    {
        t.Add(@"Stills\shoot\DCIM\100MEDIA\DJI_0001.MOV", 3 * 1024 * 1024);
        t.Add(@"Stills\shoot\DCIM\100MEDIA\DJI_0001.DNG", 2048);
        t.Add(@"Stills\shoot\MISC\THM\100\DJI_0001.THM", 512);
        t.Add(@"Stills\shoot\ro.MOV", 1000, FileAttributes.ReadOnly);
        t.Add(@"Stills\shoot\hidden.MP4", 1000, FileAttributes.Hidden);
        t.Add(@"Stills\shoot\empty.mov", 0);
        t.Add("Stills\\sjön \U0001F3A5\\clip.mov", 10_000);
        t.Add(@"Stills\" + string.Join('\\', Enumerable.Repeat("a-folder-name-that-is-rather-long-to-build-a-deep-path", 5)) + @"\LONG.MOV", 5000);
        t.Add(@"Other\C0001.MP4", 64_000);
    }

    private static void AssertAllBack(TestTree t, Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> before)
    {
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), JobTestKit.Files(t.Source).Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string rel, var fingerprint) in before)
            Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(t.Source, rel)));
        Assert.Empty(JobTestKit.Files(t.Target));
    }

    [Fact]
    public void Undo_on_the_same_drive_puts_every_file_back_exactly()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        var folderTimes = Directory.GetLastWriteTimeUtc(Path.Join(t.Source, @"Stills\shoot"));
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string sort = t.Journal;

        UndoPreview preview = UndoFactory.Preview(sort);
        Assert.True(preview.CanRun);
        Assert.Equal(TransferMethod.Rename, preview.Method);
        Assert.Equal(8, preview.GoingBack.Count);

        RunResult result;
        using (JobRunner runner = UndoFactory.Start(sort)) result = runner.Run();
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(JobKind.Undo, result.Kind);
        Assert.Equal(8, result.Moved);
        Assert.True(result.NothingLeftBehind);
        AssertAllBack(t, before);
        Assert.Equal(folderTimes, Directory.GetLastWriteTimeUtc(Path.Join(t.Source, @"Stills\shoot")));

        JobState original = JournalReader.Read(sort);
        Assert.Equal("completed", original.UndoneBy?.Status);
        Assert.Equal(result.JournalPath, original.UndoneBy?.JournalPath);
        JobState undo = JournalReader.Read(result.JournalPath);
        Assert.True(undo.IsUndo);
        Assert.Equal(original.Header.Id, undo.Header.UndoesId);
        Assert.True(JobPaths.SamePath(JobPaths.LogFolder(t.Source), Path.GetDirectoryName(result.JournalPath)!), "the undo's log is in the original source");
        Assert.Contains("already undone", UndoFactory.Preview(sort).Blocked);
        // The sort's receipt says it was undone (in words, with a readable date) and no longer offers an undo.
        string receipt = File.ReadAllText(JobPaths.ReceiptTextPath(t.Source, original.Header.Id));
        Assert.Contains($"This sort was undone on {UndoFactory.Date(original.UndoneBy!.At)}", receipt);
        Assert.Contains("its log is next to this file", receipt);
        Assert.DoesNotContain("To undo:", receipt);
    }

    [Fact]
    public void Undo_between_drives_copies_back_and_restores_content_times_and_attributes()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(AsCopy(t.Plan())).Status);
        RunResult result;
        using (JobRunner runner = JobRunner.Open(UndoFactory.CreateUndoJournal(t.Journal, TransferMethod.Copy))) result = runner.Run();
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(8, result.Moved);
        AssertAllBack(t, before);
        JobState undo = JournalReader.Read(result.JournalPath);
        Assert.All(undo.Items, i => Assert.Equal(i.ExpectedSha256, i.Sha256)); // each copy matched the checksum recorded at sort time
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_leaves_files_that_changed_since_the_sort_where_they_are(bool copy)
    {
        using var t = new TestTree();
        Sample(t);
        MovePlan plan = t.Plan();
        Assert.Equal(RunStatus.Completed, t.Run(copy ? AsCopy(plan) : plan).Status);
        string grown = Path.Join(t.Target, @"Other\C0001.MP4");
        File.AppendAllText(grown, "trimmed and saved by an editor");
        string silent = Path.Join(t.Target, @"Stills\shoot\DCIM\100MEDIA\DJI_0001.MOV");
        FlipByte(silent); // same size and dates, different content: only the checksum can tell

        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Single(preview.CannotGoBack); // the preview reads no content: the silent change is caught when the undo runs

        string undoJournal = UndoFactory.CreateUndoJournal(t.Journal, copy ? TransferMethod.Copy : TransferMethod.Rename);
        RunResult result = TestTree.Resume(undoJournal);
        Assert.Equal(RunStatus.Completed, result.Status);
        // Changed deliberately: DJI_0001.THM (same clip group as DJI_0001.MOV) used to stay in the target with its
        // changed clip. An undo now puts back every file that can go back, each checked against its own checksum.
        Assert.Equal(6, result.Moved);
        Assert.Equal(2, result.StillInSource);
        JobState undo = JournalReader.Read(undoJournal);
        Assert.Equal(2, undo.Items.Count(i => i.Note == SkipReasons.ChangedSinceSorted));
        Assert.True(File.Exists(grown) && File.Exists(silent));
        Assert.True(File.Exists(Path.Join(t.Source, @"Stills\shoot\MISC\THM\100\DJI_0001.THM")));
        Assert.False(File.Exists(Path.Join(t.Source, @"Other\C0001.MP4")));
        Assert.False(File.Exists(Path.Join(t.Source, @"Stills\shoot\DCIM\100MEDIA\DJI_0001.MOV")));
    }

    [Fact]
    public void Undo_never_overwrites_a_file_that_took_the_original_place()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string taken = Path.Join(t.Source, @"Other\C0001.MP4");
        File.WriteAllText(taken, "a new file with the old name");
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Single(preview.PlaceTaken);

        RunResult result;
        using (JobRunner runner = UndoFactory.Start(t.Journal)) result = runner.Run();
        Assert.Equal(7, result.Moved);
        Assert.Equal(SkipReasons.AlreadyBackDifferent, JournalReader.Read(result.JournalPath).Items.Single(i => i.Stage == ItemStage.Skipped).Note);
        Assert.Equal("a new file with the old name", File.ReadAllText(taken));
        Assert.True(File.Exists(Path.Join(t.Target, @"Other\C0001.MP4")));
    }

    [Fact]
    public void Undo_is_refused_for_an_unfinished_sort_for_an_undo_and_a_second_time()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 3)));
        Assert.Contains("has not finished", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal)).Message);
        Assert.Contains("has not finished", UndoFactory.Preview(t.Journal).Blocked);

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        string undo = UndoFactory.CreateUndoJournal(t.Journal);
        Assert.Contains("already being undone", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal)).Message);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(undo).Status);
        Assert.Contains("already undone", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal)).Message);
        Assert.Contains("An undo can't be undone", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(undo)).Message);
    }

    [Fact]
    public void An_undo_interrupted_by_a_crash_resumes_to_the_end()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(AsCopy(t.Plan())).Status);
        string undo = UndoFactory.CreateUndoJournal(t.Journal, TransferMethod.Copy);
        Assert.Throws<SimulatedCrash>(() =>
        {
            using JobRunner runner = JobRunner.Open(undo, new RunOptions { Faults = new CrashAt("after-placed", 4) });
            runner.Run();
        });
        Assert.Contains("already being undone", UndoFactory.Preview(t.Journal).Blocked);

        Assert.Equal(RunStatus.Completed, TestTree.Resume(undo).Status);
        AssertAllBack(t, before);
    }

    [Fact]
    public void An_undo_ended_early_can_be_followed_by_another_undo_of_the_rest()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string undo = UndoFactory.CreateUndoJournal(t.Journal);
        Assert.Throws<SimulatedCrash>(() =>
        {
            using JobRunner runner = JobRunner.Open(undo, new RunOptions { Faults = new CrashAt("after-rename", 3) });
            runner.Run();
        });
        using (JobRunner runner = JobRunner.Open(undo)) Assert.Equal(RunStatus.Closed, runner.Close().Status);
        Assert.Equal("closed", JournalReader.Read(t.Journal).UndoneBy?.Status);

        // The files the first undo already put back are not part of the second one, and are never reported as left behind.
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Equal(3, preview.AlreadyBack.Count);
        Assert.Equal(5, preview.GoingBack.Count);
        Assert.Empty(preview.CannotGoBack);
        RunResult second;
        using (JobRunner runner = UndoFactory.Start(t.Journal)) second = runner.Run();
        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.Equal(5, second.Moved);
        Assert.Equal(0, second.StillInSource);
        Assert.True(second.NothingLeftBehind);
        AssertAllBack(t, before);
        Assert.Contains("already undone", UndoFactory.Preview(t.Journal).Blocked);
    }

    [Fact]
    public void Files_no_longer_in_the_sorted_folder_stay_out_of_the_undo_and_a_later_undo_takes_them()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string mine = Path.Join(t.Target, "Other-mine");
        MoveFolder(Path.Join(t.Target, "Other"), mine); // the user reorganised the sorted folder

        UndoIssue issue = Assert.Single(UndoFactory.Preview(t.Journal).CannotGoBack);
        Assert.Equal(SkipReasons.NotInSortedFolder, issue.Reason);
        RunResult first;
        using (JobRunner runner = UndoFactory.Start(t.Journal)) first = runner.Run();
        Assert.Equal(RunStatus.Completed, first.Status); // not a failure that repeats on every resume
        Assert.Equal(7, first.Moved);
        Assert.Equal(1, first.StillInSource);
        Assert.False(first.NothingLeftBehind);
        Assert.Equal(SkipReasons.NotInSortedFolder, JournalReader.Read(first.JournalPath).Items.Single(i => i.Stage == ItemStage.Skipped).Note);
        Assert.Contains("Not moved back: 1 file", JobReports.Summary(JournalReader.Read(first.JournalPath)));

        // The folder is put back: a new undo takes what the first one left.
        MoveFolder(mine, Path.Join(t.Target, "Other"));
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Null(preview.Blocked);
        Assert.Single(preview.GoingBack);
        Assert.Equal(7, preview.AlreadyBack.Count);
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        AssertAllBack(t, before);
    }

    [Fact]
    public void A_crash_while_the_undo_log_was_being_written_does_not_block_undo()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string sortLog = File.ReadAllText(t.Journal);
        string undo = UndoFactory.CreateUndoJournal(t.Journal);
        // What a kill before the plan's last line leaves: an undo log without "ready", and no mark in the sort's log.
        string[] lines = File.ReadAllLines(undo);
        File.WriteAllText(undo, string.Join("\n", lines.Take(lines.Length - 1)) + "\n");
        File.WriteAllText(t.Journal, sortLog);

        Assert.Null(UndoFactory.Preview(t.Journal).Blocked);
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        AssertAllBack(t, before);
    }

    [Fact]
    public void Undo_finds_the_drive_again_when_it_has_another_letter()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        // As if the sort had been made on another PC, where this very drive (same serial number) had another letter.
        RewriteHeader(t.Journal, h =>
        {
            h["source"] = OnMissingDrive(h["source"]!.GetValue<string>());
            h["target"] = OnMissingDrive(h["target"]!.GetValue<string>());
        });
        string receipt = JobPaths.ReceiptTextPath(t.Source, id);
        File.WriteAllLines(receipt, File.ReadAllLines(receipt).Select(l => l.StartsWith("Job log: ", StringComparison.Ordinal) ? "Job log: " + OnMissingDrive(l[9..]) : l));

        Assert.Equal(Path.GetFullPath(t.Journal), UndoFactory.ResolveJournal(receipt)); // "pick the receipt" still finds the log
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Null(preview.Blocked);
        Assert.True(preview.DriveLetterChanged);
        Assert.True(JobPaths.SamePath(t.Source, preview.To));
        Assert.Contains(preview.Describe(), l => l.Contains("another letter now"));
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        AssertAllBack(t, before);
    }

    [Fact]
    public void The_undo_preview_says_when_the_original_drive_has_no_room_for_the_files()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(AsCopy(t.Plan())).Status);
        UndoPreview preview = UndoFactory.Preview(t.Journal, TransferMethod.Copy, freeBytes: 1000);
        Assert.False(preview.CanRun);
        Assert.Contains("Not enough free space", preview.Blocked);
        Assert.Contains("Not enough free space", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal, TransferMethod.Copy, freeBytes: 1000)).Message);
        Assert.Null(JournalReader.Read(t.Journal).UndoneBy);
        Assert.Empty(JobPaths.FindJournals(t.Source));
    }

    // ---- Receipt, job lists, finding a job's log ---------------------------------------------------------------

    [Fact]
    public void A_receipt_in_the_source_says_what_left_where_it_went_and_how_to_undo()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        JobState state = JournalReader.Read(t.Journal);
        string csv = JobPaths.ReceiptCsvPath(t.Source, state.Header.Id), text = JobPaths.ReceiptTextPath(t.Source, state.Header.Id);
        Assert.True(File.Exists(csv) && File.Exists(text));
        string[] rows = File.ReadAllLines(csv);
        Assert.Equal(9, rows.Length); // header + 8 files
        foreach (JobItem item in state.Items)
            Assert.Contains(rows, r => r.StartsWith("moved", StringComparison.Ordinal) && r.Contains(Path.Join(t.Target, item.Rel)) && r.Contains(item.Sha256!));
        string receipt = File.ReadAllText(text);
        Assert.Contains("moved 8 files", receipt);
        Assert.Contains("Undo a previous sort", receipt);
        Assert.Equal(Path.GetFullPath(t.Journal), UndoFactory.ResolveJournal(csv));
        Assert.Equal(Path.GetFullPath(t.Journal), UndoFactory.ResolveJournal(text));
        Assert.Equal(Path.GetFullPath(t.Journal), UndoFactory.ResolveJournal(JobPaths.SummaryPath(t.Journal)));
        Assert.DoesNotContain(Scanner.Scan(t.Source).Files, f => f.RelativePath.StartsWith(JobPaths.LogFolderName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Jobs_are_listed_for_both_the_source_and_the_target_folder()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        JobListing fromTarget = Assert.Single(JobCatalog.ForFolder(t.Target));
        JobListing fromSource = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.Equal(id, fromTarget.Id);
        Assert.Equal(id, fromSource.Id);
        Assert.True(fromTarget.CanUndo);
        Assert.Equal("completed", fromTarget.Status);

        using (JobRunner runner = UndoFactory.Start(t.Journal)) runner.Run();
        List<JobListing> jobs = JobCatalog.ForFolder(t.Source);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(JobKind.Undo, jobs[0].Kind); // newest first
        JobListing sort = jobs.Single(j => j.Kind == JobKind.Sort);
        Assert.False(sort.CanUndo);
        Assert.Equal("completed", sort.UndoneBy?.Status);
    }

    [Fact]
    public void Recent_jobs_read_the_old_list_and_keep_jobs_whose_drive_is_not_connected()
    {
        string folder = Path.Join(Path.GetTempPath(), "offload-recent-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Join(folder, "recent-jobs.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { @"Q:\gone\_IngestSorter\old.journal.jsonl" }));
            RecentJob old = Assert.Single(RecentJobs.Load(file));
            Assert.Equal(@"Q:\gone\_IngestSorter\old.journal.jsonl", old.JournalPath);

            var entries = new List<RecentJob>
            {
                new() { JournalPath = @"Q:\Shoot-Video\_IngestSorter\a.journal.jsonl", JobId = "a", Target = @"Q:\Shoot-Video", TargetLabel = "SONY_SSD" },
                new() { JournalPath = @"Q:\Other\_IngestSorter\b.journal.jsonl", JobId = "b", Ended = true },
                old,
            };
            RecentJobs.Save(file, entries);
            List<RecentJob> loaded = RecentJobs.Load(file);
            Assert.Equal(3, loaded.Count);
            RecentJob notConnected = Assert.Single(RecentJobs.NotConnected(loaded));
            Assert.Equal("a", notConnected.JobId);
            Assert.Equal("SONY_SSD", notConnected.DriveName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
