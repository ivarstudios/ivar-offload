using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// The engine never records a missing drive or folder as a skipped file, halts instead of failing file after file,
/// never deletes an original that did not read the same twice, and keeps groups of files together.
/// </summary>
public class SafetyTests
{
    private static string[] SomeClips(TestTree t)
    {
        t.Add(@"a\C0001.MOV", 50_000);
        t.Add(@"a\C0002.MOV", 60_000);
        t.Add(@"b\C0003.MOV", 70_000);
        t.Add(@"b\c\C0004.MOV", 80_000);
        t.Add("C0005.MOV", 90_000);
        t.Add(@"a\DSC0001.JPG", 3000);
        return [@"a\C0001.MOV", @"a\C0002.MOV", @"b\C0003.MOV", @"b\c\C0004.MOV", "C0005.MOV"];
    }

    private static void AssertAllMoved(TestTree t, string[] videos, Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> before)
    {
        foreach (string rel in videos)
        {
            Assert.False(File.Exists(Path.Join(t.Source, rel)), $"still in source: {rel}");
            Assert.Equal(before[rel], TestTree.Fingerprint(Path.Join(t.Target, rel)));
        }
        Assert.True(File.Exists(Path.Join(t.Source, @"a\DSC0001.JPG")));
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
    }

    // ---- Missing drive or folder: halt, never skip ------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_vanishing_mid_job_halts_without_a_single_skip_and_resume_finishes(bool copy)
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        MovePlan plan = copy ? AsCopy(t.Plan()) : t.Plan();
        string away = t.Source + "-unplugged";
        var unplug = new ActionAt(copy ? "after-delete" : "after-rename", _ => MoveFolder(t.Source, away), 2);

        RunResult halted = Run(plan, new RunOptions { Faults = unplug });
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        // Changed deliberately: the drive is still there (only the folder went away), so the message says that
        // instead of "not connected".
        Assert.StartsWith($"The source folder is missing: {t.Source}. Its drive (", halted.Message);
        Assert.Contains("is connected, so someone probably renamed, moved or deleted the folder", halted.Message);
        Assert.DoesNotContain("not connected", halted.Message);
        Assert.Equal(0, halted.Skipped);
        Assert.Equal(0, Lines(t.Journal, "skip"));
        Assert.Equal(2, halted.Moved);
        Assert.False(JournalReader.Read(t.Journal).IsEnded);

        MoveFolder(away, t.Source);
        RunResult resumed = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(5, resumed.Moved);
        Assert.Equal(0, resumed.StillInSource);
        Assert.True(resumed.NothingLeftBehind);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void A_source_missing_at_resume_halts_before_touching_anything()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 2)));
        string away = t.Source + "-away";
        MoveFolder(t.Source, away);
        int records = File.ReadAllLines(t.Journal).Length;

        RunResult halted = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        // Only the session record was added: no file was touched, skipped or failed.
        Assert.Equal(records + 1, File.ReadAllLines(t.Journal).Length);
        Assert.Equal(0, Lines(t.Journal, "skip") + Lines(t.Journal, "fail"));

        MoveFolder(away, t.Source);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void A_renamed_source_folder_fails_its_files_instead_of_skipping_them()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        MovePlan plan = t.Plan();
        MoveFolder(Path.Join(t.Source, "b"), Path.Join(t.Source, "b-renamed"));

        RunResult result = t.Run(plan);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, result.Failed);
        Assert.Equal(3, result.Moved);
        Assert.All(JournalReader.Read(t.Journal).Items.Where(i => i.Failed), i => Assert.Contains("is missing. Did someone rename it or disconnect its drive?", i.Note));

        MoveFolder(Path.Join(t.Source, "b-renamed"), Path.Join(t.Source, "b"));
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_renamed_target_halts_the_resume_and_nothing_is_recreated(bool copy)
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        MovePlan plan = copy ? AsCopy(t.Plan()) : t.Plan();
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt(copy ? "after-placed" : "after-rename", 2)));
        string renamed = t.Target + " renamed";
        MoveFolder(t.Target, renamed);
        string journal = JobPaths.FindJournals(renamed).Single();

        RunResult halted = TestTree.Resume(journal);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.LogMoved, halted.Halt);
        Assert.Contains("its log is now in", halted.Message);
        Assert.False(Directory.Exists(t.Target), "the old target folder must not be recreated");
        Assert.Equal(0, Lines(journal, "skip") + Lines(journal, "fail"));

        MoveFolder(renamed, t.Target);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void A_placed_copy_whose_source_folder_vanished_is_not_recorded_as_moved()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        string away = Path.Join(t.Root, "b-away");
        bool moved = false;
        // The folder of the file being copied disappears after its copy is placed, before the original is removed.
        var rename = new ActionAt("after-placed", i =>
        {
            if (moved || !JournalReader.Read(t.Journal).Items[i].Rel.StartsWith(@"b", StringComparison.Ordinal)) return;
            MoveFolder(Path.Join(t.Source, "b"), away);
            moved = true;
        });
        RunResult result = Run(AsCopy(t.Plan()), new RunOptions { Faults = rename });
        Assert.True(moved);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(2, result.Failed);
        JobState state = JournalReader.Read(t.Journal);
        Assert.DoesNotContain(state.Items, i => i.Stage == ItemStage.Done && i.Rel.StartsWith(@"b\", StringComparison.Ordinal));
        Assert.Equal(0, result.Skipped);

        MoveFolder(away, Path.Join(t.Source, "b"));
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    // ---- Drive errors, space, file-system limits --------------------------------------------------------------

    [Fact]
    public void Device_and_space_errors_are_recognised_from_both_kinds_of_exception()
    {
        Assert.True(IOErrors.IsDeviceGone(new Win32IOException(21, "Reading", "x")));
        Assert.True(IOErrors.IsDeviceGone(new IOException("not connected", unchecked((int)0x8007048F)))); // 1167
        Assert.True(IOErrors.IsDeviceGone(new IOException("gone", unchecked((int)0x80070037)))); // 55
        Assert.True(IOErrors.IsDeviceGone(new IOException("io", unchecked((int)0x8007045D)))); // 1117
        Assert.False(IOErrors.IsDeviceGone(new IOException("in use", unchecked((int)0x80070020)))); // sharing violation
        Assert.False(IOErrors.IsDeviceGone(new IOException("plain")));
        Assert.True(IOErrors.IsDiskFull(new IOException("full", unchecked((int)0x80070070))));
        Assert.True(IOErrors.IsDiskFull(new Win32IOException(39, "Writing", "x")));
        Assert.True(IOErrors.IsFileTooLarge(new IOException("too big", unchecked((int)0x800700DF))));
        Assert.True(IOErrors.IsAccessDenied(new UnauthorizedAccessException()));
        Assert.True(new Win32IOException(19, "Deleting", "x").IsAccessDenied);
    }

    [Fact]
    public void A_drive_that_stops_responding_halts_the_job_and_resume_finishes()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        var gone = new ThrowAt("mid-copy", () => new IOException("The device is not ready.", unchecked((int)0x80070015)), 2);
        RunResult halted = Run(AsCopy(t.Plan()), new RunOptions { Faults = gone });
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.DriveStoppedResponding, halted.Halt);
        Assert.Contains("did not respond", halted.Message);
        Assert.Equal(0, halted.Skipped);
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void A_file_the_drive_keeps_failing_on_does_not_hold_up_the_other_files()
    {
        using var t = new TestTree();
        SomeClips(t);
        // One file sits on a bad spot of the drive: every read of it ends in "I/O device error" while the drive still answers.
        var badSpot = new ActionAt("mid-copy", i =>
        {
            if (i == 1) throw new IOException("The request could not be performed because of an I/O device error.", unchecked((int)0x8007045D));
        });
        RunResult first = Run(AsCopy(t.Plan()), Quick(badSpot));
        Assert.Equal(RunStatus.Halted, first.Status); // the first time it may be a drive that dropped out for a moment
        Assert.Equal(HaltReason.DriveStoppedResponding, first.Halt);
        Assert.Equal(1, first.Moved);

        RunResult resumed = Resume(t.Journal, Quick(badSpot));
        Assert.Equal(RunStatus.CompletedWithFailures, resumed.Status);
        Assert.Equal(4, resumed.Moved);
        JobItem bad = JournalReader.Read(t.Journal).Items[1];
        Assert.True(FailReasons.IsDeviceError(bad.Note), bad.Note);
        Assert.True(File.Exists(Path.Join(t.Source, bad.Rel)));
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
        Assert.Equal(RunStatus.CompletedWithFailures, Resume(t.Journal, Quick(badSpot)).Status); // never a halt loop
    }

    [Fact]
    public void Another_drive_under_the_source_letter_halts_the_job_and_nothing_is_written_to_it()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        uint serial = JournalReader.Read(t.Journal).Header.SourceSerial;
        Assert.NotEqual(0u, serial);
        // The same folder path now answers from another drive (e.g. the second backup copy of the card got the letter).
        RewriteHeader(t.Journal, h => h["srcSerial"] = serial ^ 0x5A5A5A5A);
        // Changed deliberately: the receipt is now written as soon as the first file has left (it used to wait for the
        // end of the job), so it is there already. It must not be touched while the job halts.
        string receipt = JobPaths.ReceiptTextPath(t.Source, JournalReader.Read(t.Journal).Header.Id);
        string written = File.ReadAllText(receipt);
        File.SetLastWriteTimeUtc(receipt, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        RunResult halted = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        Assert.Equal(0, Lines(t.Journal, "skip") + Lines(t.Journal, "fail"));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(receipt)); // nothing is written onto a drive that is not the job's
        Assert.Equal(written, File.ReadAllText(receipt));

        RewriteHeader(t.Journal, h => h["srcSerial"] = serial);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
        Assert.True(File.Exists(JobPaths.ReceiptTextPath(t.Source, JournalReader.Read(t.Journal).Header.Id)));
    }

    [Fact]
    public void A_drive_that_came_back_under_another_letter_halts_with_a_message_that_says_so()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        JobHeader recorded = JournalReader.Read(t.Journal).Header;
        // As if the job had been started on another PC, where this very drive (same serial number) had another letter.
        RewriteHeader(t.Journal, h =>
        {
            h["source"] = OnMissingDrive(recorded.Source);
            h["target"] = OnMissingDrive(recorded.Target);
        });

        RunResult halted = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.LogMoved, halted.Halt);
        Assert.Contains("has a different letter now", halted.Message);
        Assert.Contains($"give the drive its old letter ({OnMissingDrive(t.Target)[..2]}) again", halted.Message);
        Assert.Equal(0, Lines(t.Journal, "skip") + Lines(t.Journal, "fail"));

        RewriteHeader(t.Journal, h =>
        {
            h["source"] = recorded.Source;
            h["target"] = recorded.Target;
        });
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void After_a_pause_in_the_middle_of_a_file_both_drives_are_checked_before_it_is_moved()
    {
        using var t = new TestTree();
        // The source is reached through a junction, so it can vanish while one of its files is open (as an unplugged drive does).
        string real = Path.Join(t.Root, "real-source");
        Directory.CreateDirectory(real);
        Directory.Delete(t.Source);
        Junction(t.Source, real);
        t.Add(@"b\C0002.MOV", 200_000);
        t.Add(@"b\DSC0002.JPG", 3000);
        var before = Fingerprints(t.Source);
        var gate = new PauseGate();
        bool paused = false;
        var pauseAtStart = new SyncProgress<RunProgress>(p =>
        {
            if (paused || p.Phase != "File check in progress") return;
            paused = true;
            gate.Pause(); // the user presses Pause as the file starts (its checksum is being taken)...
            Task.Run(() =>
            {
                Thread.Sleep(200);
                Directory.Delete(t.Source); // ...and the drive is unplugged while the job waits
                gate.Resume();
            });
        });
        RunResult halted;
        using (JobRunner runner = JobRunner.Start(t.Plan(), new RunOptions { Pause = gate, Progress = pauseAtStart })) halted = runner.Run();
        Assert.True(paused);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        Assert.Equal(0, Lines(t.Journal, "pre") + Lines(t.Journal, "skip") + Lines(t.Journal, "fail")); // checked before the move was even prepared

        Junction(t.Source, real);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        Assert.Equal(before[@"b\C0002.MOV"], TestTree.Fingerprint(Path.Join(t.Target, @"b\C0002.MOV")));
    }

    [Fact]
    public void A_full_target_reported_by_a_plain_IOException_halts()
    {
        using var t = new TestTree();
        SomeClips(t);
        var full = new ThrowAt("mid-copy", () => new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
        RunResult halted = Run(AsCopy(t.Plan()), new RunOptions { Faults = full });
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.TargetFull, halted.Halt);
        Assert.Equal(1, halted.Failed);
        Assert.Equal(0, halted.Moved);
    }

    [Fact]
    public void Free_space_is_checked_before_each_copy()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        RunResult halted = Run(AsCopy(t.Plan()), new RunOptions { FreeBytes = _ => 1000 });
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.TargetFull, halted.Halt);
        Assert.Contains("is full", halted.Message);
        Assert.Equal(0, Lines(t.Journal, "copy"));
        Assert.Equal(0, halted.Failed);

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    [Fact]
    public void A_file_too_large_for_the_target_fails_but_the_job_goes_on()
    {
        using var t = new TestTree();
        SomeClips(t);
        var tooLarge = new ThrowAt("mid-copy", () => new IOException("The file size exceeds the limit allowed and cannot be saved.", unchecked((int)0x800700DF)));
        RunResult result = Run(AsCopy(t.Plan()), new RunOptions { Faults = tooLarge });
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(4, result.Moved);
        Assert.Equal(FailReasons.TooLargeForTarget, JournalReader.Read(t.Journal).Items.Single(i => i.Failed).Note);
    }

    // ---- Second read of the original before it is deleted -----------------------------------------------------

    [Fact]
    public void An_original_that_reads_differently_the_second_time_is_kept()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        MovePlan plan = AsCopy(t.Plan());
        string? flipped = null;
        // Between the copy and the checks, the source returns different data (a flaky reader): same size and dates.
        var flaky = new ActionAt("after-copy", i =>
        {
            flipped = Path.Join(t.Source, JournalReader.Read(t.Journal).Items[i].Rel);
            FlipByte(flipped);
        }, 1);
        RunResult result = Run(plan, Quick(flaky));
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(4, result.Moved);
        JobItem kept = JournalReader.Read(t.Journal).Items.Single(i => i.Failed);
        Assert.Equal(FailReasons.SourceReadTwiceDiffers, kept.Note);
        Assert.True(File.Exists(flipped));
        Assert.False(File.Exists(Path.Join(t.Target, kept.Rel)), "no copy of an inconsistent original is placed");
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
    }

    [Fact]
    public void A_source_that_keeps_reading_differently_halts_the_job()
    {
        using var t = new TestTree();
        SomeClips(t);
        var flaky = new ActionAt("after-copy", i => FlipByte(Path.Join(t.Source, JournalReader.Read(t.Journal).Items[i].Rel)));
        RunResult result = Run(AsCopy(t.Plan()), Quick(flaky));
        Assert.Equal(RunStatus.Halted, result.Status);
        Assert.Equal(HaltReason.SourceInconsistent, result.Halt);
        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Moved);
        Assert.Equal(6, JobTestKit.Files(t.Source).Count);
    }

    [Fact]
    public void The_second_read_also_guards_a_resumed_delete()
    {
        using var t = new TestTree();
        SomeClips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 1)));
        JobItem placed = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Placed);
        string original = Path.Join(t.Source, placed.Rel);
        FlipByte(original);
        RunResult result = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(FailReasons.SourceReadTwiceDiffers, JournalReader.Read(t.Journal).Items[placed.Index].Note);
        Assert.True(File.Exists(original));
    }

    // ---- Originals that cannot be removed ----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Originals_that_cannot_be_removed_halt_the_job_after_two_files(bool copy)
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        MovePlan plan = copy ? AsCopy(t.Plan()) : t.Plan();
        string[] locked = videos.Select(v => Path.Join(t.Source, v)).ToArray();
        try
        {
            foreach (string file in locked) DenyDelete(file, deny: true);
            RunResult halted = Run(plan, Quick());
            Assert.Equal(RunStatus.Halted, halted.Status);
            Assert.Equal(HaltReason.OriginalsNotRemovable, halted.Halt);
            Assert.Equal(2, halted.Failed);
            Assert.Equal(0, halted.Moved);
            if (copy)
            {
                Assert.Contains("2 checked copies are in the target folder", halted.Message);
                Assert.Equal(2, JournalReader.Read(t.Journal).Items.Count(i => i.Failed && i.Stage == ItemStage.Placed));
            }
            else Assert.Contains("cannot move files out of the source folder", halted.Message);
            Assert.All(locked, f => Assert.True(File.Exists(f)));
        }
        finally
        {
            foreach (string file in locked) DenyDelete(file, deny: false);
        }
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    // ---- Close always ends the job ------------------------------------------------------------------------------

    /// <summary>Runs a copy job whose originals cannot be deleted until it halts with two verified copies in place.</summary>
    private static List<JobItem> HaltWithTwoPlacedCopies(TestTree t, string[] locked, bool keepDenied)
    {
        // Planned first: the preview's own delete-permission probe would (rightly) refuse a source it cannot move out of.
        MovePlan plan = AsCopy(t.Plan());
        foreach (string file in locked) DenyDelete(file, deny: true);
        try
        {
            Assert.Equal(HaltReason.OriginalsNotRemovable, Run(plan, Quick()).Halt);
        }
        finally
        {
            if (!keepDenied) foreach (string file in locked) DenyDelete(file, deny: false);
        }
        List<JobItem> placed = JournalReader.Read(t.Journal).Items.Where(i => i.Stage == ItemStage.Placed && i.Failed).ToList();
        Assert.Equal(2, placed.Count);
        return placed;
    }

    [Fact]
    public void Close_ends_a_job_whose_originals_cannot_be_removed()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        string[] locked = videos.Select(v => Path.Join(t.Source, v)).ToArray();
        try
        {
            List<JobItem> placed = HaltWithTwoPlacedCopies(t, locked, keepDenied: true);

            // The permission cannot be fixed (a read-only share, someone else's files): the user ends the job instead.
            RunResult closed;
            using (JobRunner runner = JobRunner.Open(t.Journal, Quick())) closed = runner.Close();
            Assert.Equal(RunStatus.Closed, closed.Status);
            Assert.Equal(HaltReason.None, closed.Halt);
            Assert.Equal(2, closed.Failed);
            Assert.Equal(3, closed.NotStarted);
            Assert.Equal(5, closed.StillInSource);
            JobState state = JournalReader.Read(t.Journal);
            Assert.True(state.IsEnded);
            foreach (JobItem p in placed)
            {
                JobItem item = state.Items[p.Index];
                Assert.Equal(FailReasons.OriginalNotRemovable, item.Note);
                Assert.Equal("copied, original kept", JobReports.Status(item));
                Assert.True(File.Exists(Path.Join(t.Target, item.Rel)), "the verified copy stays in the target");
            }
            Assert.All(locked, f => Assert.True(File.Exists(f)));
            Assert.Contains("2 of them also have a checked copy in the target folder", JobReports.Summary(state));
        }
        finally
        {
            foreach (string file in locked) DenyDelete(file, deny: false);
        }
    }

    [Fact]
    public void Close_ends_a_job_even_when_the_originals_in_flight_read_differently()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        List<JobItem> placed = HaltWithTwoPlacedCopies(t, videos.Select(v => Path.Join(t.Source, v)).ToArray(), keepDenied: false);
        foreach (JobItem item in placed) FlipByte(Path.Join(t.Source, item.Rel)); // a flaky reader: both now read differently

        RunResult closed;
        using (JobRunner runner = JobRunner.Open(t.Journal, Quick())) closed = runner.Close();
        Assert.Equal(RunStatus.Closed, closed.Status);
        JobState state = JournalReader.Read(t.Journal);
        Assert.True(state.IsEnded);
        foreach (JobItem p in placed)
        {
            Assert.Equal(FailReasons.SourceReadTwiceDiffers, state.Items[p.Index].Note);
            Assert.True(File.Exists(Path.Join(t.Source, p.Rel)), "an original that read differently is never deleted");
        }
    }

    // ---- Damaged copies in the target ----------------------------------------------------------------------------

    [Fact]
    public void A_copy_that_decayed_in_the_target_is_set_aside_and_made_again()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 1)));
        JobItem placed = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Placed);
        string copy = Path.Join(t.Target, placed.Rel);
        FlipByte(copy); // silent corruption: the size and every date stay exactly as the job set them

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
        Assert.True(File.Exists(copy + JobPaths.DamagedCopySuffix));
        JobState state = JournalReader.Read(t.Journal);
        Assert.Equal(1, Lines(t.Journal, "setaside"));
        Assert.Equal(placed.Rel + JobPaths.DamagedCopySuffix, state.Items[placed.Index].SetAside);
        Assert.Contains("Damaged copies with a different name in the target folder", JobReports.Summary(state));
    }

    [Fact]
    public void A_copy_changed_by_another_program_is_left_alone_with_its_original()
    {
        using var t = new TestTree();
        SomeClips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 1)));
        JobItem placed = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Placed);
        string copy = Path.Join(t.Target, placed.Rel);
        File.AppendAllText(copy, "edited by an app"); // a real edit: new size and date

        RunResult result = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(FailReasons.CopyChangedByOtherProgram, JournalReader.Read(t.Journal).Items[placed.Index].Note);
        Assert.True(File.Exists(Path.Join(t.Source, placed.Rel)));
        Assert.EndsWith("edited by an app", File.ReadAllText(copy));
        Assert.False(File.Exists(copy + JobPaths.DamagedCopySuffix));
    }

    [Fact]
    public void A_target_that_damages_the_fresh_copy_too_halts_the_job()
    {
        using var t = new TestTree();
        SomeClips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 1)));
        JobItem placed = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Placed);
        string copy = Path.Join(t.Target, placed.Rel);
        FlipByte(copy);
        var damageFreshCopy = new ActionAt("after-copy", i =>
            FlipByte(Directory.GetFiles(Path.GetDirectoryName(copy)!, "*" + JobPaths.TempExtension).Single()), 1);

        RunResult result = Resume(t.Journal, new RunOptions { Faults = damageFreshCopy });
        Assert.Equal(RunStatus.Halted, result.Status);
        Assert.Equal(HaltReason.TargetDamagingFiles, result.Halt);
        Assert.True(File.Exists(Path.Join(t.Source, placed.Rel)), "the original is kept");
        Assert.True(File.Exists(copy + JobPaths.DamagedCopySuffix));
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void A_damaged_copy_whose_original_has_disappeared_is_kept_never_deleted()
    {
        using var t = new TestTree();
        SomeClips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copied", 1)));
        JobItem copied = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Copied);
        string temp = Path.Join(Path.GetDirectoryName(Path.Join(t.Target, copied.Rel))!, copied.Temp!);
        File.Delete(Path.Join(t.Source, copied.Rel)); // someone removed the original meanwhile (its folder is still there)
        FlipByte(temp);                                // and the copy decayed

        RunResult result = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(4, result.Moved);
        string kept = Path.Join(t.Target, copied.Rel) + JobPaths.DamagedCopySuffix;
        Assert.True(File.Exists(kept), "the only copy left is kept");
        Assert.False(File.Exists(Path.Join(t.Target, copied.Rel)));
        JobItem item = JournalReader.Read(t.Journal).Items[copied.Index];
        Assert.True(item.Failed);
        Assert.Equal(copied.Rel + JobPaths.DamagedCopySuffix, item.SetAside);
        Assert.Contains(Path.GetFileName(kept), item.Note);
    }

    [Fact]
    public void A_placed_copy_that_went_missing_is_made_again_instead_of_failing_forever()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-placed", 1)));
        JobItem placed = JournalReader.Read(t.Journal).Items.Single(i => i.Stage == ItemStage.Placed);
        File.Delete(Path.Join(t.Target, placed.Rel));
        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        AssertAllMoved(t, videos, before);
    }

    // ---- Groups ------------------------------------------------------------------------------------------------

    private static MovePlan GroupedPlan(TestTree t)
    {
        t.Add(@"g\C0001.MP4", 40_000);
        t.Add(@"g\C0001.SRT", 900);
        t.Add(@"g\C0002.MP4", 40_000);
        t.Add(@"g\C0002.SRT", 900);
        MovePlan plan = t.Plan();
        foreach (SourceFile f in plan.ToMove) f.GroupKey = Path.Join(f.Directory, Path.GetFileNameWithoutExtension(f.Name));
        return plan;
    }

    [Fact]
    public void Journal_items_are_grouped_with_the_main_file_first()
    {
        using var t = new TestTree();
        MovePlan plan = GroupedPlan(t);
        SourceFile F(string name) => plan.ToMove.Single(f => f.Name == name);
        var shuffled = new List<SourceFile> { F("C0001.SRT"), F("C0002.MP4"), F("C0001.MP4"), F("C0002.SRT") };
        string journal = JobFactory.CreateJournal(WithToMove(plan, shuffled));
        JobState state = JournalReader.Read(journal);
        Assert.Equal([@"g\C0001.MP4", @"g\C0001.SRT", @"g\C0002.MP4", @"g\C0002.SRT"], state.Items.Select(i => i.Rel));
        Assert.Equal([@"g\C0001", @"g\C0001", @"g\C0002", @"g\C0002"], state.Items.Select(i => i.Group));
    }

    [Fact]
    public void Companions_of_a_clip_that_was_not_moved_stay_with_it()
    {
        using var t = new TestTree();
        MovePlan plan = GroupedPlan(t);
        File.AppendAllText(Path.Join(t.Source, @"g\C0001.MP4"), "changed after the preview");
        RunResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, result.StillInSource);
        JobState state = JournalReader.Read(t.Journal);
        Assert.Equal(SkipReasons.KeptWith("C0001.MP4"), state.Items.Single(i => i.Rel == @"g\C0001.SRT").Note);
        Assert.True(File.Exists(Path.Join(t.Source, @"g\C0001.SRT")));
        Assert.True(File.Exists(Path.Join(t.Target, @"g\C0002.SRT")));
    }

    [Fact]
    public void Companions_of_a_clip_that_failed_wait_for_it_and_move_on_resume()
    {
        using var t = new TestTree();
        MovePlan plan = GroupedPlan(t);
        RunResult result;
        using (new FileStream(Path.Join(t.Source, @"g\C0001.MP4"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Run(plan, Quick());
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(2, result.Failed);
        Assert.Equal(FailReasons.WaitingFor("C0001.MP4"), JournalReader.Read(t.Journal).Items.Single(i => i.Rel == @"g\C0001.SRT").Note);
        Assert.True(File.Exists(Path.Join(t.Source, @"g\C0001.SRT")));
        Assert.True(File.Exists(Path.Join(t.Target, @"g\C0002.SRT")));

        RunResult resumed = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(4, resumed.Moved);
    }

    [Fact]
    public void An_identical_copy_already_in_the_target_does_not_hold_back_its_companions()
    {
        using var t = new TestTree();
        MovePlan plan = GroupedPlan(t);
        string spare = Path.Join(t.Target, @"g\C0001.MP4");
        Directory.CreateDirectory(Path.GetDirectoryName(spare)!);
        File.Copy(Path.Join(t.Source, @"g\C0001.MP4"), spare); // e.g. from an earlier, interrupted sort of the same card
        RunResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.IdenticalInTarget);
        Assert.Equal(0, result.StillInSource);
        Assert.True(result.NothingLeftBehind);
        Assert.True(File.Exists(Path.Join(t.Target, @"g\C0001.SRT")), "the companion moves although its clip was skipped as an identical copy");
    }

    [Fact]
    public void Files_after_a_failed_member_wait_and_files_before_it_stay_moved()
    {
        using var t = new TestTree();
        foreach (string ext in new[] { "MP4", "LRF", "SRT", "THM", "WAV" }) t.Add($@"g\DJI_0001.{ext}", 20_000);
        MovePlan plan = t.Plan();
        foreach (SourceFile f in plan.ToMove) f.GroupKey = @"g\DJI_0001";
        string journal = JobFactory.CreateJournal(plan);
        List<JobItem> items = JournalReader.Read(journal).Items;
        JobItem blocker = items[2];
        RunResult result;
        using (new FileStream(Path.Join(t.Source, blocker.Rel), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Resume(journal, Quick());
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        JobState state = JournalReader.Read(journal);
        Assert.All(state.Items.Take(2), i => Assert.Equal(ItemStage.Done, i.Stage));
        Assert.All(state.Items.Skip(3), i => Assert.Equal(FailReasons.WaitingFor(Path.GetFileName(blocker.Rel)), i.Note));

        Assert.Equal(RunStatus.Completed, Resume(journal).Status);
        Assert.Equal(items.Count, JournalReader.Read(journal).DoneCount);
    }

    // ---- Alternate data streams --------------------------------------------------------------------------------

    private const string Tags = ":com.apple.metadata_kMDItemUserTags";

    [Fact]
    public void Named_streams_are_copied_and_verified_on_the_copy_path()
    {
        using var t = new TestTree();
        string clip = t.Add(@"a\A001_C001.MOV", 70_000);
        File.WriteAllText(clip + Tags, "Select\nClient A");
        File.WriteAllBytes(clip + ":AFP_AfpInfo", [1, 2, 3, 4, 5]);
        var before = TestTree.Fingerprint(clip);
        RunResult result = t.Run(AsCopy(t.Plan()));
        Assert.Equal(RunStatus.Completed, result.Status);
        string moved = Path.Join(t.Target, @"a\A001_C001.MOV");
        Assert.False(File.Exists(clip));
        Assert.Equal(before, TestTree.Fingerprint(moved));
        Assert.Equal("Select\nClient A", File.ReadAllText(moved + Tags));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(moved + ":AFP_AfpInfo"));
        Assert.Equal(2, SafeFile.NamedStreams(moved).Count);
    }

    [Fact]
    public void A_named_stream_that_does_not_read_back_is_copied_again()
    {
        using var t = new TestTree();
        string clip = t.Add(@"a\A001_C001.MOV", 70_000);
        File.WriteAllText(clip + Tags, "Select\nClient A");
        var damageStream = new ActionAt("after-copy", _ =>
            File.WriteAllText(Directory.GetFiles(Path.Join(t.Target, "a"), "*" + JobPaths.TempExtension).Single() + Tags, "Xelect\nClient A"), 1);
        RunResult result = Run(AsCopy(t.Plan()), new RunOptions { Faults = damageStream });
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, Lines(t.Journal, "copy")); // the first copy's stream did not match, so the file was copied again
        Assert.Equal("Select\nClient A", File.ReadAllText(Path.Join(t.Target, @"a\A001_C001.MOV") + Tags));
    }

    [Fact]
    public void A_file_whose_streams_the_target_cannot_store_stays_in_the_source()
    {
        using var t = new TestTree();
        string tagged = t.Add(@"a\tagged.MOV", 20_000);
        File.WriteAllText(tagged + Tags, "Select");
        string downloaded = t.Add(@"a\downloaded.MOV", 20_000);
        File.WriteAllText(downloaded + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3");
        RunResult result = Run(AsCopy(t.Plan()), new RunOptions { TargetStoresStreams = false });
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal(FailReasons.StreamsNotSupported, JournalReader.Read(t.Journal).Items.Single(i => i.Failed).Note);
        Assert.True(File.Exists(tagged));
        Assert.False(File.Exists(Path.Join(t.Target, @"a\tagged.MOV")));
        Assert.True(File.Exists(Path.Join(t.Target, @"a\downloaded.MOV")), "only the download mark is lost, which is harmless");
    }

    // ---- Honest results -------------------------------------------------------------------------------------------

    [Fact]
    public void Verify_lists_planned_files_that_did_not_move_and_is_never_all_good_then()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        MovePlan plan = t.Plan();
        File.AppendAllText(Path.Join(t.Source, videos[0]), "changed after the preview");
        Assert.Equal(RunStatus.Completed, t.Run(plan).Status);

        VerifyResult result = JobVerifier.Verify(t.Journal);
        Assert.False(result.AllGood);
        Assert.Equal(4, result.Checked);
        Assert.Equal(4, result.Matched);
        Assert.Equal(1, result.NotMoved);
        string problem = Assert.Single(result.Problems);
        Assert.StartsWith("not moved (still in the source folder): ", problem);
        Assert.Contains(videos[0], problem);
        Assert.Contains(SkipReasons.ChangedAfterPreview, problem);

        // A log written by an earlier version holds the earlier words of the same reason: the summary uses today's words.
        const string earlierWords = "the source file changed after the preview (left in place)";
        File.WriteAllText(t.Journal, File.ReadAllText(t.Journal).Replace(SkipReasons.ChangedAfterPreview, earlierWords));
        JobState earlier = JournalReader.Read(t.Journal);
        Assert.Equal(earlierWords, earlier.Items.Single(i => i.Stage == ItemStage.Skipped).Note);
        Assert.Contains($"skipped x1: {SkipReasons.ChangedAfterPreview}", JobReports.Summary(earlier));
    }

    [Fact]
    public void Nothing_is_written_into_the_source_when_no_file_left_it()
    {
        using var t = new TestTree();
        string[] videos = SomeClips(t);
        MovePlan plan = t.Plan();
        foreach (string v in videos) File.AppendAllText(Path.Join(t.Source, v), "changed after the preview");
        RunResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0, result.Moved);
        Assert.Equal(5, result.StillInSource);
        Assert.False(result.NothingLeftBehind);
        Assert.False(Directory.Exists(JobPaths.LogFolder(t.Source)), "no receipt (and no _IVAROffload folder) in a source nothing left");
    }
}
