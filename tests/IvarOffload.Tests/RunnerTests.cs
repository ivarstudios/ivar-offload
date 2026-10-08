using System.Text;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

public class RunnerTests
{
    private const int Chunk = SafeFile.ChunkSize;

    /// <summary>A plan that uses copy + verify + delete even though source and target share a drive.</summary>
    private static MovePlan AsCopy(MovePlan p) => new()
    {
        Scan = p.Scan,
        SourceRoot = p.SourceRoot,
        TargetRoot = p.TargetRoot,
        Mode = p.Mode,
        Method = TransferMethod.Copy,
        VerifyChecksums = true,
        SourceVolume = p.SourceVolume,
        TargetVolume = p.TargetVolume,
        ToMove = p.ToMove,
        Staying = p.Staying,
        Conflicts = p.Conflicts,
        Messages = p.Messages,
        ByType = p.ByType,
        ByFolder = p.ByFolder,
    };

    private static List<string> SampleFiles(TestTree t)
    {
        t.Add(@"Stills\shoot\DCIM\100MEDIA\DJI_0001.MOV", 3 * 1024 * 1024);
        t.Add(@"Stills\shoot\DCIM\100MEDIA\DJI_0001.DNG", 2048);
        t.Add(@"Stills\shoot\MISC\THM\100\DJI_0001.THM", 512);
        t.Add(@"Stills\shoot\ro.MOV", 1000, FileAttributes.ReadOnly);
        t.Add(@"Stills\shoot\hidden.MP4", 1000, FileAttributes.Hidden);
        t.Add(@"Stills\shoot\empty.mov", 0);
        t.Add(@"Stills\shoot\exact.mov", Chunk);
        t.Add(@"Stills\shoot\plus.mov", Chunk + 1);
        t.Add("Stills\\sjön \U0001F3A5\\clip.mov", 10_000);
        t.Add(@"Stills\" + string.Join('\\', Enumerable.Repeat("a-folder-name-that-is-rather-long-to-build-a-deep-path", 5)) + @"\LONG.MOV", 5000);
        return ["DJI_0001.MOV", "DJI_0001.THM", "ro.MOV", "hidden.MP4", "empty.mov", "exact.mov", "plus.mov", "clip.mov", "LONG.MOV"];
    }

    /// <summary>Every video-side file is in the target with an identical fingerprint and gone from the source; the rest is untouched.</summary>
    private static void AssertMoved(TestTree t, Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> before)
    {
        foreach ((string rel, var fingerprint) in before)
        {
            bool isVideo = !rel.EndsWith(".DNG", StringComparison.Ordinal);
            string expectedAt = Path.Join(isVideo ? t.Target : t.Source, rel);
            string notAt = Path.Join(isVideo ? t.Source : t.Target, rel);
            Assert.True(File.Exists(expectedAt), $"missing: {expectedAt}");
            Assert.False(File.Exists(notAt), $"should not exist: {notAt}");
            Assert.Equal(fingerprint, TestTree.Fingerprint(expectedAt));
        }
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
    }

    private static Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> Fingerprints(TestTree t) =>
        Directory.EnumerateFiles(t.Source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(f => Path.GetRelativePath(t.Source, f), TestTree.Fingerprint);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moves_are_exact_including_timestamps_attributes_long_and_unicode_paths(bool copy)
    {
        using var t = new TestTree();
        SampleFiles(t);
        var before = Fingerprints(t);
        MovePlan plan = t.Plan();
        RunResult result = t.Run(copy ? AsCopy(plan) : plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(9, result.Moved);
        AssertMoved(t, before);
        Assert.True(JobVerifier.Verify(t.Journal).AllGood);
        AssertMoved(t, before); // verification did not touch any timestamp
    }

    public static TheoryData<bool, string, int> CrashPoints => new()
    {
        { false, "after-pre", 1 }, { false, "after-pre", 5 }, { false, "after-rename", 1 }, { false, "after-rename", 9 },
        { true, "after-copy-journal", 2 }, { true, "mid-copy", 1 }, { true, "mid-copy", 3 }, { true, "after-copy", 2 },
        { true, "after-copied", 2 }, { true, "after-place-rename", 2 }, { true, "after-placed", 2 }, { true, "after-delete", 2 },
        { true, "after-delete", 9 },
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void Resume_after_a_crash_at_any_step_finishes_exactly(bool copy, string point, int occurrence)
    {
        using var t = new TestTree();
        SampleFiles(t);
        var before = Fingerprints(t);
        MovePlan plan = t.Plan();
        Assert.Throws<SimulatedCrash>(() => t.Run(copy ? AsCopy(plan) : plan, new CrashAt(point, occurrence)));
        RunResult result = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(9, result.Moved);
        AssertMoved(t, before);
    }

    [Fact]
    public void Source_changed_after_the_preview_is_skipped_and_left_alone()
    {
        using var t = new TestTree();
        string changed = t.Add(@"a\changed.MOV");
        t.Add(@"a\fine.MOV");
        MovePlan plan = t.Plan();
        File.AppendAllText(changed, "edited after the preview");
        RunResult result = t.Run(plan);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(changed));
        Assert.Contains("changed after the preview", JournalReader.Read(t.Journal).Items.Single(i => i.Rel.EndsWith("changed.MOV")).Note);
    }

    [Fact]
    public void Existing_target_files_are_never_overwritten()
    {
        using var t = new TestTree();
        string same = t.Add(@"a\same.MOV");
        string different = t.Add(@"a\different.MOV");
        string late = t.Add(@"a\late.MOV");
        t.Add(@"a\new.MOV");
        Directory.CreateDirectory(Path.Join(t.Target, "a"));
        File.Copy(same, Path.Join(t.Target, @"a\same.MOV"));
        File.WriteAllText(Path.Join(t.Target, @"a\different.MOV"), "keep me");
        MovePlan plan = t.Plan();
        // The preview keeps a file whose name is taken by a DIFFERENT file out of the job; the identical copy is planned
        // and skipped at run time. A clash that appears after the preview is only found at run time.
        Assert.Equal("same.MOV", Assert.Single(plan.Conflicts).Name);
        Assert.Equal("different.MOV", Assert.Single(plan.DifferentConflicts).Name);
        Assert.DoesNotContain(plan.ToMove, f => f.Name == "different.MOV");
        File.WriteAllText(Path.Join(t.Target, @"a\late.MOV"), "appeared after the preview");
        RunResult result = t.Run(plan);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(same) && File.Exists(different) && File.Exists(late));
        Assert.Equal("keep me", File.ReadAllText(Path.Join(t.Target, @"a\different.MOV")));
        Assert.Equal("appeared after the preview", File.ReadAllText(Path.Join(t.Target, @"a\late.MOV")));
        JobState state = JournalReader.Read(t.Journal);
        Assert.Contains(state.Items, i => i.Note == SkipReasons.IdenticalInTarget);
        Assert.Contains(state.Items, i => i.Note == SkipReasons.DifferentInTarget);
        // The identical copy is a spare; the late different file is really still (only) in the source, and the result says so.
        // Changed deliberately: the file the preview held back (a DIFFERENT file has its name in the target) was left out
        // of the count. It is still in the source too, so it counts (and is listed apart in the summary).
        Assert.Equal(1, result.IdenticalInTarget);
        Assert.Equal(1, result.HeldBack);
        Assert.Equal(2, result.StillInSource);
        Assert.False(result.NothingLeftBehind);
        Assert.Contains("Left in the source (1 file,", JobReports.Summary(state));
        Assert.Contains(@"Left in the source (not part of this sort) (1 file,", JobReports.Summary(state));
    }

    [Fact]
    public void A_plan_with_only_conflicts_cannot_be_started()
    {
        using var t = new TestTree();
        string only = t.Add(@"a\only.MOV");
        Directory.CreateDirectory(Path.Join(t.Target, "a"));
        File.Copy(only, Path.Join(t.Target, @"a\only.MOV"));
        MovePlan plan = t.Plan();
        Assert.False(plan.CanRun);
        Assert.Throws<InvalidOperationException>(() => t.Run(plan));
    }

    [Fact]
    public void Verification_detects_a_file_damaged_after_the_move()
    {
        using var t = new TestTree();
        t.Add(@"a\clip.MOV", 100_000);
        t.Run(t.Plan());
        string moved = Path.Join(t.Target, @"a\clip.MOV");
        using (var s = new FileStream(moved, FileMode.Open, FileAccess.ReadWrite))
        {
            s.Position = 5000;
            int b = s.ReadByte();
            s.Position = 5000;
            s.WriteByte((byte)(b ^ 0xFF));
        }
        VerifyResult result = JobVerifier.Verify(t.Journal);
        Assert.False(result.AllGood);
        Assert.Contains(result.Problems, p => p.Contains("CHECKSUM MISMATCH"));
    }

    [Fact]
    public void Closing_an_interrupted_job_rolls_back_the_file_in_flight_and_ends_the_job()
    {
        using var t = new TestTree();
        SampleFiles(t);
        var before = Fingerprints(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copy-journal", 3)));
        RunResult result;
        using (JobRunner runner = JobRunner.Open(t.Journal)) result = runner.Close();
        // Changed deliberately: a close used to report Completed with NotStarted always 0. It now says Closed, and how
        // many files it left in the source, so nobody mistakes an ended job for a finished one.
        Assert.Equal(RunStatus.Closed, result.Status);
        Assert.Equal(2, result.Moved);
        Assert.Equal(7, result.NotStarted);
        Assert.Equal(7, result.StillInSource);
        Assert.False(result.NothingLeftBehind);
        JobState state = JournalReader.Read(t.Journal);
        Assert.True(state.IsEnded);
        Assert.Equal("closed", state.End!.What);
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
        foreach ((string rel, var fingerprint) in before)
        {
            bool inTarget = File.Exists(Path.Join(t.Target, rel)), inSource = File.Exists(Path.Join(t.Source, rel));
            Assert.True(inTarget ^ inSource, $"{rel} must be in exactly one place");
            Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(inTarget ? t.Target : t.Source, rel)));
        }
        Assert.Throws<JournalException>(() => JobRunner.Open(t.Journal)); // an ended job cannot be run again
    }

    [Fact]
    public void Empty_source_folders_are_found_and_only_empty_ones_removed()
    {
        using var t = new TestTree();
        t.Add(@"only-video\sub\clip.MOV");
        t.Add(@"mixed\clip.MOV");
        t.Add(@"mixed\photo.JPG");
        t.Run(t.Plan());
        JobState state = JournalReader.Read(t.Journal);
        List<string> empty = EmptyFolders.Find(state);
        Assert.Equal(new List<string> { @"only-video\sub", "only-video" }, empty);
        Assert.Equal(2, EmptyFolders.Remove(t.Journal, empty));
        Assert.False(Directory.Exists(Path.Join(t.Source, "only-video")));
        Assert.True(File.Exists(Path.Join(t.Source, @"mixed\photo.JPG")));
    }

    [Fact]
    public void Target_folders_get_the_dates_of_their_source_folders()
    {
        using var t = new TestTree();
        t.Add(@"2023 shoot\clip.MOV");
        var when = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        Directory.SetCreationTimeUtc(Path.Join(t.Source, "2023 shoot"), when);
        Directory.SetLastWriteTimeUtc(Path.Join(t.Source, "2023 shoot"), when);
        t.Run(t.Plan());
        Assert.Equal(when, Directory.GetCreationTimeUtc(Path.Join(t.Target, "2023 shoot")));
        Assert.Equal(when, Directory.GetLastWriteTimeUtc(Path.Join(t.Target, "2023 shoot")));
    }

    [Fact]
    public void Reading_a_file_to_hash_it_does_not_change_its_last_access_time()
    {
        using var t = new TestTree();
        string path = t.Add(@"a\clip.MOV", 50_000);
        DateTime before = File.GetLastAccessTimeUtc(path);
        SafeFile.HashFile(path, 50_000, null, CancellationToken.None);
        Assert.Equal(before, File.GetLastAccessTimeUtc(path));
    }
}

public class JournalTests
{
    private static string WriteJournal(TestTree t, params string[] extraLines)
    {
        t.Add(@"a\clip.MOV");
        t.Add(@"a\clip2.MOV");
        string journal = JobFactory.CreateJournal(t.Plan());
        File.AppendAllText(journal, string.Concat(extraLines), new UTF8Encoding(false));
        return journal;
    }

    [Fact]
    public void A_torn_last_line_is_ignored_and_truncated_on_resume()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t, "{\"t\":\"done\",\"i\":0,\"how\":\"ren");
        JobState state = JournalReader.Read(journal);
        Assert.Equal(ItemStage.Pending, state.Items[0].Stage);
        Assert.True(state.ValidLength < new FileInfo(journal).Length);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(journal).Status);
        Assert.Equal(2, JournalReader.Read(journal).DoneCount);
    }

    [Fact]
    public void A_zero_filled_tail_after_power_loss_is_ignored()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t, new string('\0', 300) + "\n" + new string('\0', 50));
        Assert.True(JournalReader.Read(journal).PlanComplete);
        Assert.Equal(RunStatus.Completed, TestTree.Resume(journal).Status);
    }

    [Fact]
    public void Damage_in_the_middle_is_reported_not_guessed()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t);
        List<string> lines = File.ReadAllLines(journal).ToList();
        lines.Insert(2, "{ this is not json");
        File.WriteAllLines(journal, lines);
        Assert.Throws<JournalException>(() => JournalReader.Read(journal));
    }

    [Fact]
    public void A_journal_whose_plan_was_not_completely_written_cannot_be_started()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t);
        List<string> lines = File.ReadAllLines(journal).Where(l => !l.Contains("\"t\":\"ready\"")).ToList();
        File.WriteAllLines(journal, lines);
        Assert.False(JournalReader.Read(journal).IsResumable);
        Assert.Throws<JournalException>(() => JobRunner.Open(journal));
    }

    [Fact]
    public void Only_one_process_can_run_a_job()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t);
        using JobRunner first = JobRunner.Open(journal);
        Assert.True(Assert.Throws<JournalException>(() => JobRunner.Open(journal)).InUse);
    }

    [Fact]
    public void Opening_to_append_reads_the_journal_under_the_lock_and_keeps_what_others_wrote()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t);
        const string written = "{\"t\":\"session\",\"what\":\"resume\"}\n";
        long before = new FileInfo(journal).Length;
        // Another process appended a complete record (and then crashed mid-line) after this one last read the journal.
        File.AppendAllText(journal, written + "{\"t\":\"done\",\"i\":0,\"how\":\"ren", new UTF8Encoding(false));
        using (JournalWriter.OpenForAppend(journal, out JobState state))
            Assert.Equal(before + written.Length, state.ValidLength);
        Assert.EndsWith(written, File.ReadAllText(journal)); // the torn tail is cut, the complete record kept
    }

    [Fact]
    public void Verify_and_folder_removal_refuse_a_job_that_is_open_elsewhere()
    {
        using var t = new TestTree();
        string journal = WriteJournal(t);
        using JobRunner running = JobRunner.Open(journal);
        Assert.True(Assert.Throws<JournalException>(() => JobVerifier.Verify(journal)).InUse);
        Assert.True(Assert.Throws<JournalException>(() => EmptyFolders.Remove(journal, [])).InUse);
    }

    [Fact]
    public void A_running_verify_holds_the_job_so_nothing_it_does_can_cut_off_another_writer()
    {
        using var t = new TestTree();
        t.Add(@"a\clip.MOV", 100_000);
        t.Run(t.Plan());
        JournalException? whileVerifying = null;
        VerifyResult result = JobVerifier.Verify(t.Journal, new SyncProgress<RunProgress>(p =>
        {
            if (p.Phase == "Verified") whileVerifying = Record.Exception(() => JobRunner.Open(t.Journal).Dispose()) as JournalException;
        }));
        Assert.True(result.AllGood);
        Assert.True(whileVerifying?.InUse);
        Assert.Equal(1, JobTestKit.Lines(t.Journal, "verify"));
        Assert.Equal(1, JobTestKit.Lines(t.Journal, "end"));
    }
}

public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
