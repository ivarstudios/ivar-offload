using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// A file whose move was interrupted is reconciled without ever deleting what may be its only copy: when the original
/// disappears meanwhile, or when the job is ended while its source cannot be reached, every copy in the target is kept
/// (and recorded), never reported as "still in the source".
/// </summary>
public class InFlightTests
{
    private static void Clips(TestTree t)
    {
        t.Add(@"a\C0001.MOV", 300_000);
        t.Add(@"a\C0002.MOV", 400_000);
        t.Add(@"b\C0003.MOV", 500_000);
        t.Add(@"b\C0004.MOV", 200_000);
        t.Add(@"a\DSC0001.JPG", 3000);
    }

    /// <summary>The file whose move was interrupted.</summary>
    private static JobItem InFlight(string journal) =>
        JournalReader.Read(journal).Items.Single(i => i.Stage is ItemStage.Prepared or ItemStage.Copying or ItemStage.Copied or ItemStage.Placed);

    /// <summary>Files under a folder (outside the log folders) with this content, wherever and under whatever name.</summary>
    private static List<string> CopiesOf(string root, string sha) =>
        JobTestKit.Files(root).Where(r => TestTree.Sha(Path.Join(root, r)) == sha).ToList();

    private static RunResult ResumeOrClose(string journal, bool close)
    {
        using JobRunner runner = JobRunner.Open(journal);
        return close ? runner.Close() : runner.Run();
    }

    private static readonly string[] CopySteps = ["after-copy-journal", "mid-copy", "after-copy", "after-copied", "after-place-rename", "after-placed", "after-delete"];

    public static TheoryData<string, bool> Steps()
    {
        var data = new TheoryData<string, bool>();
        foreach (string step in CopySteps)
        {
            data.Add(step, false);
            data.Add(step, true);
        }
        return data;
    }

    /// <summary>A complete copy of the file exists in the target after a crash at this step.</summary>
    private static bool CompleteCopyAt(string step) => step is not ("after-copy-journal" or "mid-copy");

    [Theory]
    [MemberData(nameof(Steps))]
    public void An_original_that_disappears_during_a_crash_is_never_lost_by_resume_or_close(string step, bool close)
    {
        using var t = new TestTree();
        Clips(t);
        var shas = JobTestKit.Files(t.Source).ToDictionary(r => r, r => TestTree.Sha(Path.Join(t.Source, r)), StringComparer.OrdinalIgnoreCase);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt(step, 2)));
        JobItem item = InFlight(t.Journal);
        string original = Path.Join(t.Source, item.Rel);
        if (File.Exists(original)) File.Delete(original); // something else removes the original meanwhile (its folder stays)

        RunResult result = ResumeOrClose(t.Journal, close);
        if (close) Assert.Equal(RunStatus.Closed, result.Status);
        else Assert.Contains(result.Status, new[] { RunStatus.Completed, RunStatus.CompletedWithFailures });
        JobState state = JournalReader.Read(t.Journal);
        JobItem after = state.Items[item.Index];

        if (CompleteCopyAt(step)) Assert.NotEmpty(CopiesOf(t.Target, shas[item.Rel])); // the only copy left is kept
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));

        // It is in the target, or reported as missing - never as "still in the source", where it is not.
        Assert.False(after.IsStillInSource, $"{after.Stage} {after.Note}");
        Assert.True(after.Stage == ItemStage.Done || after.IsMissing, $"{after.Stage} {after.Note}");
        Assert.DoesNotContain(state.StillInSource, i => i.Index == item.Index);
        if (!close) Assert.Equal(0, result.StillInSource);
        string summary = JobReports.Summary(state);
        if (after.IsMissing)
        {
            Assert.Equal(1, result.MissingFromSource);
            Assert.False(result.NothingLeftBehind);
            Assert.Contains($"Missing from the source (removed by something else; not in the target either) (1 file, ", summary);
            Assert.DoesNotContain($"    {item.Rel}  (", summary[..summary.IndexOf("Missing from the source (", StringComparison.Ordinal)]);
        }
        // Every other file moved as usual.
        foreach (string rel in shas.Keys.Where(r => r.EndsWith(".MOV", StringComparison.Ordinal) && !r.Equals(item.Rel, StringComparison.OrdinalIgnoreCase)))
            Assert.True(close ? File.Exists(Path.Join(t.Target, rel)) || File.Exists(Path.Join(t.Source, rel)) : File.Exists(Path.Join(t.Target, rel)), rel);
    }

    [Fact]
    public void Ending_a_job_keeps_a_verified_copy_whose_original_has_disappeared()
    {
        // The case of the finding: a crash after the copy was verified, the original deleted, then "End job".
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copied", 1)));
        JobItem item = InFlight(t.Journal);
        string sha = TestTree.Sha(Path.Join(t.Source, item.Rel));
        File.Delete(Path.Join(t.Source, item.Rel));

        RunResult closed = ResumeOrClose(t.Journal, close: true);
        Assert.Equal(RunStatus.Closed, closed.Status);
        JobItem after = JournalReader.Read(t.Journal).Items[item.Index];
        Assert.Equal(ItemStage.Done, after.Stage); // the verified copy took its place: the file moved
        Assert.Equal(sha, TestTree.Sha(Path.Join(t.Target, item.Rel)));
        Assert.Equal(sha, after.Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_whose_original_disappeared_before_it_was_checked_is_kept_as_an_unverified_copy(bool close)
    {
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copy", 2)));
        JobItem item = InFlight(t.Journal);
        string sha = TestTree.Sha(Path.Join(t.Source, item.Rel));
        File.Delete(Path.Join(t.Source, item.Rel));

        RunResult result = ResumeOrClose(t.Journal, close);
        Assert.Equal(close ? RunStatus.Closed : RunStatus.CompletedWithFailures, result.Status);
        string kept = Path.Join(t.Target, item.Rel) + JobPaths.UnverifiedCopySuffix;
        Assert.Equal(sha, TestTree.Sha(kept));
        Assert.False(File.Exists(Path.Join(t.Target, item.Rel)), "an unchecked copy never takes the file's own name");
        JobState state = JournalReader.Read(t.Journal);
        JobItem after = state.Items[item.Index];
        Assert.True(after.Failed);
        Assert.Contains(Path.GetFileName(kept), after.Note);
        Assert.True(after.IsMissing);
        Assert.Equal(item.Rel + JobPaths.UnverifiedCopySuffix, after.SetAside);
        Assert.Contains("Unverified copies kept in the target", JobReports.Summary(state));
        Assert.Equal(1, result.MissingFromSource);
        Assert.DoesNotContain(state.StillInSource, i => i.Index == item.Index);

        if (close) return;
        // A later resume records it as gone from the source; the kept copy stays, and the reports still name it.
        RunResult again = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.Completed, again.Status);
        Assert.False(again.NothingLeftBehind);
        Assert.True(File.Exists(kept));
        JobState ended = JournalReader.Read(t.Journal);
        Assert.Contains($"a copy was kept in the target as {item.Rel}{JobPaths.UnverifiedCopySuffix}", JobReports.Summary(ended));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_partly_written_copy_is_kept_when_its_original_is_gone_and_removed_when_it_is_there(bool originalGone)
    {
        // What a power cut in the middle of a copy leaves: part of the file in the job's temporary file.
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copy-journal", 2)));
        JobItem item = InFlight(t.Journal);
        string original = Path.Join(t.Source, item.Rel);
        string temp = Path.Join(Path.GetDirectoryName(Path.Join(t.Target, item.Rel))!, item.Temp!);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, File.ReadAllBytes(original)[..100_000]);
        if (originalGone) File.Delete(original);

        RunResult result = TestTree.Resume(t.Journal);
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
        string kept = Path.Join(t.Target, item.Rel) + JobPaths.UnverifiedCopySuffix;
        if (originalGone)
        {
            Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
            Assert.Equal(100_000, new FileInfo(kept).Length); // all that is left of it
            Assert.Equal(FailReasons.OriginalGoneDuringCopy(Path.GetFileName(kept)), JournalReader.Read(t.Journal).Items[item.Index].Note);
        }
        else
        {
            Assert.Equal(RunStatus.Completed, result.Status); // the original is there: the partial copy is not needed
            Assert.False(File.Exists(kept));
            Assert.True(result.NothingLeftBehind);
        }
    }

    [Fact]
    public void A_verified_copy_whose_original_is_gone_and_whose_name_is_taken_is_kept_under_another_name()
    {
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(AsCopy(t.Plan()), new CrashAt("after-copied", 1)));
        JobItem item = InFlight(t.Journal);
        string sha = TestTree.Sha(Path.Join(t.Source, item.Rel));
        File.Delete(Path.Join(t.Source, item.Rel));
        File.WriteAllText(Path.Join(t.Target, item.Rel), "another program put a file here");

        RunResult result = TestTree.Resume(t.Journal);
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.Equal("another program put a file here", File.ReadAllText(Path.Join(t.Target, item.Rel)));
        Assert.Equal(sha, TestTree.Sha(Path.Join(t.Target, item.Rel) + JobPaths.VerifiedCopySuffix));
        Assert.True(JournalReader.Read(t.Journal).Items[item.Index].IsMissing);
    }

    public static TheoryData<bool, string> UndoSteps()
    {
        var data = new TheoryData<bool, string>();
        foreach (string step in CopySteps)
        {
            data.Add(false, step);
            data.Add(true, step);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UndoSteps))]
    public void An_undo_never_loses_a_file_that_disappears_from_the_sorted_folder_during_a_crash(bool close, string step)
    {
        using var t = new TestTree();
        Clips(t);
        var shas = JobTestKit.Files(t.Source).ToDictionary(r => r, r => TestTree.Sha(Path.Join(t.Source, r)), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string undo = UndoFactory.CreateUndoJournal(t.Journal, TransferMethod.Copy);
        Assert.Throws<SimulatedCrash>(() =>
        {
            using JobRunner runner = JobRunner.Open(undo, new RunOptions { Faults = new CrashAt(step, 2) });
            runner.Run();
        });
        JobItem item = InFlight(undo);
        string sorted = Path.Join(t.Target, item.Rel);
        if (File.Exists(sorted)) File.Delete(sorted); // removed from the sorted folder meanwhile

        RunResult result = ResumeOrClose(undo, close);
        Assert.NotEqual(RunStatus.Halted, result.Status);
        if (CompleteCopyAt(step)) Assert.NotEmpty(CopiesOf(t.Source, shas[item.Rel]));
        Assert.Empty(Directory.EnumerateFiles(t.Source, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
        JobItem after = JournalReader.Read(undo).Items[item.Index];
        // An undo knows the checksum each file must have: a complete copy that has it is put back in place.
        if (CompleteCopyAt(step) || (step == "mid-copy" && File.Exists(Path.Join(t.Source, item.Rel))))
        {
            Assert.Equal(ItemStage.Done, after.Stage);
            Assert.Equal(shas[item.Rel], TestTree.Sha(Path.Join(t.Source, item.Rel)));
        }
    }

    // ---- Ending a job whose source cannot be reached ----------------------------------------------------------------

    [Theory]
    [InlineData("after-pre")]
    [InlineData("after-rename")]
    [InlineData("after-copy")]
    [InlineData("after-copied")]
    [InlineData("after-place-rename")]
    [InlineData("after-placed")]
    public void A_job_whose_source_went_away_with_a_file_in_flight_can_be_ended_and_then_undone(string step)
    {
        using var t = new TestTree();
        Clips(t);
        var before = Fingerprints(t.Source);
        bool copy = step is not ("after-pre" or "after-rename");
        MovePlan plan = copy ? AsCopy(t.Plan()) : t.Plan();
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt(step, 2)));
        JobItem item = InFlight(t.Journal);
        string away = t.Source + "-renamed";
        MoveFolder(t.Source, away); // renamed, moved or lost while the file was in flight

        RunResult closed = ResumeOrClose(t.Journal, close: true);
        Assert.Equal(RunStatus.Closed, closed.Status); // used to halt again with "Reconnect it", so the job could never be ended
        JobState state = JournalReader.Read(t.Journal);
        Assert.True(state.IsEnded);
        JobItem after = state.Items[item.Index];
        string dst = Path.Join(t.Target, item.Rel);
        switch (step)
        {
            case "after-pre":
                Assert.Equal(SkipReasons.Closed, after.Note); // the rename never happened: the file is in the source
                Assert.True(File.Exists(Path.Join(away, item.Rel)));
                break;
            case "after-rename":
                Assert.Equal(ItemStage.Done, after.Stage); // it had moved
                Assert.Equal(before[item.Rel], TestTree.Fingerprint(dst));
                break;
            case "after-copy":
                Assert.Equal(FailReasons.UnfinishedCopyKept(Path.GetFileName(dst) + JobPaths.UnverifiedCopySuffix), after.Note);
                Assert.True(File.Exists(dst + JobPaths.UnverifiedCopySuffix), "the unchecked copy is kept, not deleted");
                Assert.True(after.IsStillInSource); // its original was never touched
                break;
            default:
                Assert.Equal(FailReasons.OriginalNotChecked, after.Note);
                Assert.Equal("copied, original kept", JobReports.Status(after));
                Assert.Equal(before[item.Rel].Item1, TestTree.Sha(dst));
                break;
        }
        Assert.True(File.Exists(Path.Join(away, item.Rel)) || step == "after-rename", "the original was not touched");
        Assert.Empty(Directory.EnumerateFiles(t.Target, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));

        // Once the folder is back, the files that did move can be undone.
        MoveFolder(away, t.Source);
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.True(preview.CanRun, preview.Blocked);
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.Equal(RunStatus.Completed, runner.Run().Status);
        foreach ((string rel, var fingerprint) in before)
            Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(t.Source, rel)));
    }
}
