using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// The written record of a job is complete and honest while it runs and after it ends: the receipt and reports exist
/// from the first moved file on, and files the preview held back are counted and listed as still in the source.
/// </summary>
public class ReportsTests
{
    private static void Clips(TestTree t)
    {
        t.Add(@"a\C0001.MOV", 50_000);
        t.Add(@"a\C0002.MOV", 60_000);
        t.Add(@"b\C0003.MOV", 70_000);
        t.Add(@"b\C0004.MOV", 80_000);
        t.Add(@"a\DSC0001.JPG", 3000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_crash_leaves_a_receipt_in_the_source_and_the_reports_next_to_the_log(bool copy)
    {
        using var t = new TestTree();
        Clips(t);
        MovePlan plan = copy ? AsCopy(t.Plan()) : t.Plan();
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt(copy ? "after-delete" : "after-rename", 3)));
        JobState state = JournalReader.Read(t.Journal);
        string id = state.Header.Id;

        // A power cut or a kill leaves files moved out of the source: the receipt that traces them is already there.
        string receipt = JobPaths.ReceiptTextPath(t.Source, id), csv = JobPaths.ReceiptCsvPath(t.Source, id);
        Assert.True(File.Exists(receipt) && File.Exists(csv), "no receipt in the source after a crash");
        string text = File.ReadAllText(receipt);
        Assert.Contains("The job is not finished. ", text);
        // Written while the job ran: it says when, and that files it lists as not moved may have moved since.
        Assert.Contains("IVAR Offload wrote this receipt at ", text);
        Assert.Contains($"but they can be in {t.Target} now", text);
        Assert.DoesNotContain("are still in this folder", text);
        Assert.Contains(Path.GetFullPath(t.Journal), text);
        Assert.Contains($"Job log drive serial number: {state.Header.TargetSerial:X8}", text);
        string[] rows = File.ReadAllLines(csv);
        Assert.Equal(1 + state.Items.Count, rows.Length); // every planned file, with where it goes
        JobItem first = state.Items[0];
        Assert.Contains(rows, r => r.StartsWith("moved", StringComparison.Ordinal) && r.Contains(Path.Join(t.Target, first.Rel)));
        Assert.Contains(rows, r => r.StartsWith("not moved", StringComparison.Ordinal) && r.Contains("The file can be in the target folder now"));
        Assert.DoesNotContain(rows, r => r.StartsWith("moved", StringComparison.Ordinal) && (r.Contains("status at ") || r.Contains("not moved yet at ")));
        Assert.True(File.Exists(JobPaths.ManifestPath(t.Journal)));
        Assert.Contains("not finished (you can resume it)", File.ReadAllText(JobPaths.SummaryPath(t.Journal)));
        // Written whole or not at all: nothing half-written is left behind.
        Assert.Empty(Directory.EnumerateFiles(t.Root, "*.writing", SearchOption.AllDirectories));

        Assert.Equal(RunStatus.Completed, TestTree.Resume(t.Journal).Status);
        text = File.ReadAllText(receipt);
        Assert.Contains("moved 4 files", text);
        Assert.DoesNotContain("IVAR Offload wrote this receipt at ", text);
        Assert.DoesNotContain(File.ReadAllLines(csv), r => r.Contains("status at ") || r.Contains("not moved yet at "));
    }

    [Fact]
    public void The_reports_are_written_when_a_job_starts_but_nothing_goes_into_the_source_before_a_file_left_it()
    {
        using var t = new TestTree();
        Clips(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-pre", 1)));
        Assert.True(File.Exists(JobPaths.ManifestPath(t.Journal)));
        Assert.True(File.Exists(JobPaths.SummaryPath(t.Journal)));
        Assert.False(Directory.Exists(JobPaths.LogFolder(t.Source)), "no receipt (and no log folder) in a source nothing has left yet");
    }

    [Fact]
    public void Files_the_preview_held_back_are_counted_and_listed_as_still_in_the_source()
    {
        using var t = new TestTree();
        // Day 2's card has restarted numbering: its C0001 is a DIFFERENT clip than day 1's C0001 in the target.
        t.Add(@"day\C0001.MP4", 50_000);
        t.Add(@"day\C0001.SRT", 900);
        t.Add(@"day\C0002.MP4", 60_000);
        Directory.CreateDirectory(Path.Join(t.Target, "day"));
        File.WriteAllText(Path.Join(t.Target, @"day\C0001.MP4"), "day 1's clip with the same name");
        MovePlan plan = t.Plan();
        Assert.NotEmpty(plan.HeldBack);
        int held = plan.HeldBack.Count;

        RunResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.Moved);
        Assert.Equal(held, result.HeldBack);
        Assert.Equal(held, result.StillInSource); // counted: the CLI exits with 5, and the result is not "all done"
        Assert.False(result.NothingLeftBehind);

        JobState state = JournalReader.Read(t.Journal);
        Assert.Equal(held, state.HeldBack.Count);
        Assert.Equal(held, Lines(t.Journal, "held"));
        Assert.Equal(held, state.StillInSourceCount);
        Assert.Empty(state.StillInSource); // never part of the run: nothing to resume, nothing to undo
        string summary = JobReports.Summary(state);
        Assert.DoesNotContain("Still in the source folder: no files to move.", summary);
        Assert.Contains($"Left in the source folder (not part of this sort) ({JobReports.Files(held)}, ", summary);
        Assert.Contains(@"day\C0001.MP4", summary);
        Assert.Contains("a DIFFERENT file with the same name is already in the target folder", summary);

        string receipt = File.ReadAllText(JobPaths.ReceiptTextPath(t.Source, state.Header.Id));
        Assert.Contains($"{JobReports.Files(held)} (", receipt);
        Assert.Contains("still in this folder", receipt);
        string[] csv = File.ReadAllLines(JobPaths.ReceiptCsvPath(t.Source, state.Header.Id));
        Assert.Equal(held, csv.Count(r => r.StartsWith("held back", StringComparison.Ordinal)));

        VerifyResult verify = JobVerifier.Verify(t.Journal);
        Assert.False(verify.AllGood);
        Assert.Equal(held, verify.HeldBack);
        Assert.Contains(verify.Problems, p => p.StartsWith("kept in the source folder by the preview: ", StringComparison.Ordinal) && p.Contains(@"day\C0001.MP4"));
        Assert.True(UndoFactory.Preview(t.Journal).CanRun);
        Assert.Single(UndoFactory.Preview(t.Journal).GoingBack);
    }

    [Fact]
    public void A_journal_with_held_records_is_read_by_the_rules_older_readers_follow()
    {
        using var t = new TestTree();
        t.Add(@"day\C0001.MP4", 50_000);
        t.Add(@"day\C0002.MP4", 60_000);
        Directory.CreateDirectory(Path.Join(t.Target, "day"));
        File.WriteAllText(Path.Join(t.Target, @"day\C0001.MP4"), "a different clip");
        string journal = JobFactory.CreateJournal(t.Plan());
        // The held records sit before "ready" and carry no item index, so a reader that ignores unknown record types
        // (as every version does) sees exactly the plan it would have seen without them.
        List<string> lines = File.ReadAllLines(journal).ToList();
        int ready = lines.FindIndex(l => l.Contains("\"t\":\"ready\"", StringComparison.Ordinal));
        Assert.True(lines.FindIndex(l => l.Contains("\"t\":\"held\"", StringComparison.Ordinal)) is var h && h > 0 && h < ready);
        Assert.DoesNotContain(lines.Where(l => l.Contains("\"t\":\"held\"", StringComparison.Ordinal)), l => l.Contains("\"i\":", StringComparison.Ordinal));
        File.WriteAllLines(journal, lines.Select(l => l.Replace("\"t\":\"held\"", "\"t\":\"someday\"")));
        JobState state = JournalReader.Read(journal);
        Assert.True(state.PlanComplete);
        Assert.Single(state.Items);
        Assert.Empty(state.HeldBack);
    }
}
