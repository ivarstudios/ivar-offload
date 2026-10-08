using IvarOffload.App;
using IvarOffload.Core.Backup;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// Backup copies a card bit for bit to every destination, verifies every copy from disk, never changes the card, keeps
/// going on the healthy destinations when one fails, and survives a crash at every step.
/// </summary>
public class BackupTests
{
    // ---- The copy -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_file_is_copied_bit_for_bit_to_every_destination_and_the_card_is_not_changed()
    {
        using var t = new BackupTree();
        t.TypicalCard(large: true);
        Directory.CreateDirectory(Path.Join(t.Card, ".Trashes"));
        File.WriteAllText(Path.Join(t.Card, ".Trashes", "junk"), "x");
        var card = t.CardFingerprints();
        card.Remove(@".Trashes\junk");
        var metadata = t.CardMetadata();

        BackupPlan plan = t.Plan(destinations: 3);
        Assert.True(plan.CanRun, string.Join(" ", plan.Messages.Select(m => m.Text)));
        BackupResult result = t.Run(plan);

        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.Equal(card.Count, result.Files);
        Assert.All(result.Destinations, d => Assert.Equal(card.Count, d.Verified));
        for (int k = 0; k < 3; k++)
        {
            t.AssertExactCopy(k, card);
            Assert.False(Directory.Exists(Path.Join(t.Dest(k), ".Trashes")), "OS clutter was copied");
            Assert.Single(Directory.EnumerateFiles(Path.Join(t.Dest(k), AscMhl.FolderName), "*.mhl"));
        }
        // Not a single byte, date or attribute of the card changed - not even a last-access time.
        Assert.Equal(metadata, t.CardMetadata());
        Assert.Empty(Directory.EnumerateFileSystemEntries(t.Card, JobPaths.LogFolderName, SearchOption.AllDirectories));
    }

    [Fact]
    public void A_write_protected_card_is_backed_up_like_any_other()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        BackupPlan plan = t.Plan();
        t.WriteProtect(true);
        BackupResult result = t.Run(plan);
        t.WriteProtect(false);

        Assert.True(result.AllVerified, result.Message);
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
        Assert.Equal(card.Keys.Order(), t.CardFingerprints().Keys.Order());
        Assert.All(t.CardFingerprints(), f => Assert.Equal(card[f.Key].Item1, f.Value.Item1));
    }

    [Fact]
    public void Without_the_second_card_read_every_copy_is_still_read_back()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        BackupResult result = t.Run(t.Plan(reread: false));
        Assert.True(result.AllVerified);
        t.AssertExactCopy(0, card);
        Assert.False(JournalReader.Read(t.Journal(0)).Header.Reread);
    }

    [Fact]
    public void A_linked_folder_is_left_out_and_the_result_is_not_complete()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        string elsewhere = Path.Join(t.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Join(elsewhere, "x.txt"), "x");
        JobTestKit.Junction(Path.Join(t.Card, "LINK"), elsewhere);

        BackupPlan plan = t.Plan();
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("LINK"));
        BackupResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.LeftOut);
        Assert.False(result.AllVerified);
        Assert.False(Directory.Exists(Path.Join(t.Dest(0), "LINK")));
        Assert.Contains("LINK", File.ReadAllText(BackupPaths.SummaryPath(t.Journal(0))));
    }

    [Fact]
    public void Files_added_to_the_card_after_the_preview_keep_the_result_from_being_green()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        BackupPlan plan = t.Plan();
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 5000);
        BackupResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains(result.NotInBackup, f => f.Contains("DSC00002.JPG") && f.Contains("added after the preview"));
        Assert.False(result.AllVerified);
    }

    [Fact]
    public void A_file_changed_on_the_card_after_the_preview_is_not_copied()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        BackupPlan plan = t.Plan();
        string clip = Path.Join(t.Card, @"PRIVATE\M4ROOT\CLIP\C0001.MP4");
        File.AppendAllText(clip, "more");
        BackupResult result = t.Run(plan);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.False(result.AllVerified);
        Assert.False(File.Exists(Path.Join(t.Dest(0), @"PRIVATE\M4ROOT\CLIP\C0001.MP4")));
        Assert.Equal(BackupReasons.ChangedOnCard, JournalReader.Read(t.Journal(0)).Items.Single(i => i.Rel.EndsWith("C0001.MP4")).Note);
        Assert.All(result.Destinations, d => Assert.False(d.MhlWritten)); // a manifest only describes a complete copy
    }

    [Fact]
    public void A_card_with_a_damaged_mhl_history_is_backed_up_as_it_is_without_a_manifest()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Add(@"VIDEO\CardA\ascmhl\0001_CardA_2025-09-10_120000.mhl", 3000); // random bytes: not XML at all
        t.Add(@"VIDEO\CardA\ascmhl\ascmhl_chain.xml", 800);
        var card = t.CardFingerprints();

        BackupResult result = t.Run(t.Plan());
        Assert.True(result.AllVerified, result.Message);
        Assert.All(result.Destinations, d =>
        {
            Assert.False(d.MhlWritten);
            Assert.Contains("can't be read", d.MhlSkipped);
        });
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, card); // the damaged history too, byte for byte
            Assert.False(Directory.Exists(Path.Join(t.Dest(k), AscMhl.FolderName)), "a manifest was written next to an unreadable history");
            Assert.Contains("no ASC MHL", BackupTexts.MhlLine(result.Destinations[k]));
        }
        Assert.True(BackupVerifier.Verify(t.Journal(0)).AllGood);
    }

    // ---- Crashes ----------------------------------------------------------------------------------------------

    public static TheoryData<string, int> CrashPoints() => new()
    {
        { "after-copy-journal", 2 },
        { "mid-copy", 3 },
        { "after-copy", 2 },
        { "after-copied", 2 },
        { "after-copied", 3 },
        { "after-place-rename", 2 },
        { "after-place-rename", 3 },
        { "after-done", 3 },
        { "before-mhl", 1 },
        { "after-mhl", 1 },
        { "after-mhl", 2 },
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void A_crash_at_any_step_is_resumed_to_an_exact_verified_copy(string point, int occurrence)
    {
        using var t = new BackupTree();
        t.TypicalCard(large: true);
        var card = t.CardFingerprints();
        BackupPlan plan = t.Plan();
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt(point, occurrence)));

        BackupResult result = BackupTree.Resume(t.Journal(1));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
        for (int k = 0; k < 2; k++)
        {
            // Exactly one generation, and the official chain is intact.
            Assert.Equal(1, AscMhl.GenerationsIn(t.Dest(k)));
            Assert.Single(Directory.EnumerateFiles(Path.Join(t.Dest(k), AscMhl.FolderName), "*.mhl"));
        }
        Assert.Throws<JournalException>(() => BackupRunner.Open(t.Journal(0)));
    }

    [Theory]
    [InlineData("after-copy", 2)]
    [InlineData("after-copied", 2)]
    [InlineData("after-place-rename", 2)]
    public void Ending_a_backup_after_a_crash_keeps_verified_copies_and_leaves_no_partial_files(string point, int occurrence)
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt(point, occurrence)));
        BackupResult closed = BackupTree.Close(t.Journal(0));
        Assert.Equal(RunStatus.Closed, closed.Status);
        Assert.False(closed.AllVerified);
        for (int k = 0; k < 2; k++)
        {
            Assert.Empty(Directory.EnumerateFiles(t.Dest(k), "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
            JobState s = JournalReader.Read(t.Journal(k));
            Assert.True(s.IsEnded);
            foreach (JobItem item in s.Items.Where(i => i.Stage == ItemStage.Done))
                Assert.Equal(item.Sha256, TestTree.Sha(Path.Join(t.Dest(k), item.Rel)));
            Assert.All(s.Items, i => Assert.True(i.IsFinished));
        }
    }

    // ---- One destination fails, the others go on ------------------------------------------------------------

    [Fact]
    public void A_full_destination_drops_out_and_the_others_finish_then_resume_completes_it()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        int copies = 0;
        var counter = new ActionAt("after-done", _ => copies++);
        long? Free(string folder) => folder.StartsWith(t.Parents[1], StringComparison.OrdinalIgnoreCase) && copies >= 4 ? 1000 : null;

        BackupResult first = t.Run(t.Plan(), counter, Free);
        Assert.Equal(RunStatus.CompletedWithFailures, first.Status);
        Assert.False(first.AllVerified);
        Assert.True(first.Destinations[0].Ended && first.Destinations[0].MhlWritten);
        t.AssertExactCopy(0, card);
        Assert.Equal(HaltReason.TargetFull, first.Destinations[1].Halt);
        Assert.Contains("is full", first.Destinations[1].Problem);
        Assert.False(first.Destinations[1].Ended);

        BackupResult resumed = BackupTree.Resume(t.Journal(1));
        Assert.True(resumed.AllVerified, resumed.Message);
        t.AssertExactCopy(1, card);
    }

    [Fact]
    public void An_unplugged_destination_drops_out_and_resume_completes_it_when_it_is_back()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        // The second destination is reached through a junction; removing it takes the destination away mid-backup
        // (the way an unplugged drive does) while its log stays open.
        string link = Path.Join(t.Root, "D2link");
        JobTestKit.Junction(link, t.Parents[1]);
        BackupPlan plan = BackupPlanner.Build(BackupScanner.Scan(t.Card), [t.Parents[0], link], BackupTree.Name);
        Assert.True(plan.CanRun, string.Join(" ", plan.Messages.Select(m => m.Text)));
        var unplug = new ActionAt("after-done", _ => Directory.Delete(link), 4);

        BackupResult first = t.Run(plan, unplug);
        Assert.Equal(RunStatus.CompletedWithFailures, first.Status);
        t.AssertExactCopy(0, card);
        Assert.True(first.Destinations[0].MhlWritten);
        Assert.Equal(HaltReason.TargetNotConnected, first.Destinations[1].Halt);
        Assert.Contains("missing", first.Destinations[1].Problem);
        Assert.False(first.Destinations[1].Ended);

        JobTestKit.Junction(link, t.Parents[1]);
        BackupResult resumed = BackupTree.Resume(t.Journal(0));
        Assert.True(resumed.AllVerified, resumed.Message);
        t.AssertExactCopy(1, card);
    }

    [Fact]
    public void A_destination_that_damages_copies_is_given_a_second_chance_then_dropped()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        BackupPlan plan = t.Plan();
        string d1 = t.Dest(0);
        // Damage destination 1's copy of the second file once: it is copied again and matches.
        var once = new ActionAt("after-copy", _ => DamageTemps(d1), 2);
        BackupResult result = t.Run(plan, once);
        Assert.True(result.AllVerified, result.Message);
        t.AssertExactCopy(0, card);
    }

    [Fact]
    public void A_destination_that_damages_every_copy_is_dropped_and_the_other_finishes()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        string d1 = t.Dest(0);
        BackupResult result = t.Run(t.Plan(), new ActionAt("after-copy", _ => DamageTemps(d1)));
        Assert.Equal(HaltReason.TargetDamagingFiles, result.Destinations[0].Halt);
        Assert.True(result.Destinations[1].Ended && result.Destinations[1].MhlWritten);
        t.AssertExactCopy(1, card);
        Assert.False(result.AllVerified);
    }

    private static void DamageTemps(string destination)
    {
        foreach (string temp in Directory.EnumerateFiles(destination, "*" + JobPaths.TempExtension, SearchOption.AllDirectories))
            if (new FileInfo(temp).Length > 1000) JobTestKit.FlipByte(temp, 500);
    }

    [Fact]
    public void A_file_that_appears_in_a_destination_is_never_replaced()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        string rel = @"DCIM\100MSDCF\DSC00001.ARW";
        string intruder = Path.Join(t.Dest(1), rel);
        var appear = new ActionAt("after-copy-journal", i =>
        {
            if (!File.Exists(intruder) && Directory.Exists(Path.GetDirectoryName(intruder))) File.WriteAllText(intruder, "someone else's");
        });
        BackupResult result = t.Run(t.Plan(), appear);
        Assert.Equal("someone else's", File.ReadAllText(intruder));
        Assert.Equal(BackupReasons.NameTakenInDestination, JournalReader.Read(t.Journal(1)).Items.Single(i => i.Rel == rel).Note);
        Assert.True(result.Destinations[0].Ended);
        Assert.False(result.AllVerified);
    }

    // ---- The card -------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_pulled_mid_backup_halts_without_skipping_anything_and_resume_finishes()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        BackupPlan plan = t.Plan();
        string away = t.Card + "-pulled";
        BackupResult halted = t.Run(plan, new ActionAt("after-done", _ => JobTestKit.MoveFolder(t.Card, away), 4));
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        Assert.Contains("card", halted.Message);
        for (int k = 0; k < 2; k++) Assert.Equal(0, JobTestKit.Lines(t.Journal(k), "skip"));

        JobTestKit.MoveFolder(away, t.Card);
        BackupResult resumed = BackupTree.Resume(t.Journal(0));
        Assert.True(resumed.AllVerified, resumed.Message);
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
    }

    [Fact]
    public void A_card_that_reads_differently_the_second_time_is_never_trusted()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        BackupPlan plan = t.Plan();
        string clip = Path.Join(t.Card, @"PRIVATE\M4ROOT\CLIP\C0001.MP4");
        // The card "returns other data" for the clip after it was copied: same size and dates, other content.
        BackupResult first = t.Run(plan, new ActionAt("after-copy", i =>
        {
            if (plan.Files[i].RelativePath.EndsWith("C0001.MP4")) JobTestKit.FlipByte(clip, 777);
        }, 1, 2, 3, 4, 5, 6, 7));
        Assert.Equal(RunStatus.CompletedWithFailures, first.Status);
        for (int k = 0; k < 2; k++)
        {
            JobItem item = JournalReader.Read(t.Journal(k)).Items.Single(i => i.Rel.EndsWith("C0001.MP4"));
            Assert.Equal(BackupReasons.CardReadTwiceDiffers, item.Note);
            Assert.False(File.Exists(Path.Join(t.Dest(k), item.Rel)));
        }
        Assert.Empty(Directory.EnumerateFiles(t.Root, "*" + JobPaths.TempExtension, SearchOption.AllDirectories));

        BackupResult resumed = BackupTree.Resume(t.Journal(0));
        Assert.True(resumed.AllVerified, resumed.Message);
        Assert.Equal(TestTree.Sha(clip), TestTree.Sha(Path.Join(t.Dest(1), @"PRIVATE\M4ROOT\CLIP\C0001.MP4")));
    }

    // ---- Verify again -----------------------------------------------------------------------------------------

    [Fact]
    public void Verify_again_finds_a_copy_damaged_after_the_backup_on_that_destination_only()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        Assert.True(BackupVerifier.Verify(t.Journal(0)).AllGood);

        JobTestKit.FlipByte(Path.Join(t.Dest(1), @"DCIM\100MSDCF\DSC00001.ARW"));
        BackupVerifyResult result = BackupVerifier.Verify(t.Journal(0));
        Assert.False(result.AllGood);
        Assert.True(result.Destinations[0].AllGood);
        Assert.Contains(result.Destinations[1].Problems, p => p.Contains("DSC00001.ARW") && p.Contains("MISMATCH"));
    }

    // ---- Kept apart from Sort -----------------------------------------------------------------------------------

    [Fact]
    public void The_sort_tools_never_see_or_run_a_backup()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-done", 2)));
        string journal = t.Journal(0);

        Assert.Empty(JobPaths.FindJournals(t.Dest(0)));
        Assert.Empty(JobPaths.FindUnfinished(t.Dest(0)));
        // Tests running at the same time share the settings folder (a process-wide variable), so only this backup's id is
        // looked for, and only its absence from the sort jobs' list can be checked reliably.
        string id = JournalReader.Read(journal).Header.Id;
        Assert.DoesNotContain(RecentJobs.Entries(), e => e.JobId == id || string.Equals(e.JournalPath, journal, StringComparison.OrdinalIgnoreCase));
        JournalException refused = Assert.Throws<JournalException>(() => JobRunner.Open(journal));
        Assert.Contains("backup", refused.Message);
        Assert.Equal(JobKind.Backup, JournalReader.Read(journal).Header.Kind);
        Assert.Null(UndoFactory.ResolveJournal(journal));
        Assert.Null(UndoFactory.ResolveJournal(BackupPaths.SummaryPath(journal)));
    }

    // ---- The plan ---------------------------------------------------------------------------------------------

    [Fact]
    public void Default_name_is_the_date_and_the_folder_or_card_label()
    {
        using var t = new BackupTree();
        Assert.Equal("260928_CARD", BackupPlanner.DefaultName(t.Card, new DateTime(2026, 9, 28)));
        Assert.Null(BackupPlanner.ValidateName("2026-09-28_SONY A"));
        Assert.NotNull(BackupPlanner.ValidateName(""));
        Assert.NotNull(BackupPlanner.ValidateName("a/b"));
        Assert.NotNull(BackupPlanner.ValidateName("x:"));
        Assert.NotNull(BackupPlanner.ValidateName("CON"));
        Assert.NotNull(BackupPlanner.ValidateName("name."));
    }

    [Fact]
    public void A_destination_folder_that_is_not_empty_is_refused_so_two_cards_never_mix()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Directory.CreateDirectory(t.Dest(1));
        File.WriteAllText(Path.Join(t.Dest(1), "other card.txt"), "x");
        BackupPlan plan = t.Plan();
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("not empty"));

        Directory.CreateDirectory(t.Dest(0)); // an empty folder is fine
        Assert.DoesNotContain(t.Plan(1).Messages, m => m.Level == MessageLevel.Error);
    }

    [Fact]
    public void A_folder_with_an_unfinished_backup_of_the_same_card_says_to_resume_it()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-done", 2)));
        BackupPlan again = t.Plan();
        Assert.Contains(again.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("unfinished backup of this card") && m.Text.Contains("Resume it"));
        // The logs found are handed to the app, which offers to resume them (also on another PC).
        Assert.Equal(2, again.UnfinishedJournals.Count);

        // Another card in the same folder name: never called "this card".
        using var other = new BackupTree();
        other.TypicalCard();
        var parents = t.Parents.Take(2).ToList();
        BackupPlan wrong = BackupPlanner.Build(BackupScanner.Scan(other.Card), parents, BackupTree.Name);
        Assert.DoesNotContain(wrong.Messages, m => m.Text.Contains("of this card"));
        Assert.Contains(wrong.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("unfinished backup of another card"));
    }

    [Fact]
    public void The_default_name_gets_a_number_when_that_folder_is_taken_on_a_destination()
    {
        using var t = new BackupTree();
        var today = new DateTime(2026, 9, 28);
        Assert.Equal("260928_CARD", BackupPlanner.DefaultName(t.Card, today, t.Parents));
        Directory.CreateDirectory(Path.Join(t.Parents[1], "260928_CARD"));
        Assert.Equal("260928_CARD", BackupPlanner.DefaultName(t.Card, today, t.Parents)); // an empty folder is fine
        File.WriteAllText(Path.Join(t.Parents[1], "260928_CARD", "x.txt"), "first card");
        Assert.Equal("260928_CARD_2", BackupPlanner.DefaultName(t.Card, today, t.Parents));
    }

    // ---- Every copy the same, and finished copies looked at again ------------------------------------------------

    [Fact]
    public void A_file_the_card_returns_differently_on_resume_never_makes_the_copies_disagree()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        string clip = @"PRIVATE\M4ROOT\CLIP\C0001.MP4";
        // Destination 2 is full from the start; destination 1 finishes.
        long? Free(string folder) => folder.StartsWith(t.Parents[1], StringComparison.OrdinalIgnoreCase) ? 1000 : null;
        BackupResult first = t.Run(t.Plan(), freeBytes: Free);
        Assert.True(first.Destinations[0].Ended);
        // Then the card returns other content for the clip (same size and dates), consistently.
        JobTestKit.FlipByte(Path.Join(t.Card, clip), 4321);

        BackupResult resumed = BackupTree.Resume(t.Journal(0));
        Assert.False(resumed.AllVerified);
        JobItem item = JournalReader.Read(t.Journal(1)).Items.Single(i => i.Rel == clip);
        Assert.Equal(BackupReasons.DiffersFromOtherCopies, item.Note);
        Assert.False(File.Exists(Path.Join(t.Dest(1), clip)));
    }

    [Fact]
    public void A_destination_finished_in_an_earlier_run_is_looked_at_again_before_the_backup_counts_as_complete()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        int done = 0;
        long? Free(string folder) => folder.StartsWith(t.Parents[1], StringComparison.OrdinalIgnoreCase) && done >= 3 ? 1000 : null;
        BackupResult first = t.Run(t.Plan(), new ActionAt("after-done", _ => done++), Free);
        Assert.True(first.Destinations[0].Ended);
        // Meanwhile a file disappears from the finished copy (sorted out, deleted by hand).
        File.SetAttributes(Path.Join(t.Dest(0), @"DCIM\100MSDCF\DSC00001.JPG"), FileAttributes.Normal);
        File.Delete(Path.Join(t.Dest(0), @"DCIM\100MSDCF\DSC00001.JPG"));

        BackupResult resumed = BackupTree.Resume(t.Journal(1));
        Assert.True(resumed.Destinations[1].Complete(resumed.Files));
        Assert.Contains("missing or changed now", resumed.Destinations[0].Problem);
        Assert.False(resumed.AllVerified);
    }

    [Fact]
    public void A_damaged_log_on_one_destination_does_not_keep_the_others_from_resuming()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-done", 3)));
        // A bad sector in the middle of destination 2's log.
        string[] lines = File.ReadAllLines(t.Journal(1));
        lines[lines.Length / 2] = "{\"t\":\"done\",\"i\":GARBAGE";
        File.WriteAllText(t.Journal(1), string.Join("\n", lines) + "\n");

        BackupResult resumed = BackupTree.Resume(t.Journal(0));
        Assert.True(resumed.Destinations[0].Complete(resumed.Files));
        t.AssertExactCopy(0, card);
        Assert.Contains("can't be read", resumed.Destinations[1].Problem);
        Assert.False(resumed.AllVerified);
    }

    [Fact]
    public void Ending_a_backup_whose_files_are_all_verified_finishes_it_properly()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("before-mhl", 1)));
        BackupResult closed = BackupTree.Close(t.Journal(0));
        Assert.Equal(RunStatus.Completed, closed.Status);
        Assert.All(closed.Destinations, d => Assert.True(d.MhlWritten));
        Assert.True(closed.AllVerified, closed.Message);
    }

    [Fact]
    public void Verify_again_after_sorting_the_backup_reports_moved_files_as_moved_not_as_damage()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan(destinations: 1)).AllVerified);
        // Sort the backup: the videos (the Sony clip folder) move out of it.
        string sorted = Path.Join(t.Root, "SORTED");
        MovePlan sort = Planner.Build(Scanner.Scan(t.Dest(0)), sorted, MoveMode.Videos, verifyChecksums: true);
        Assert.True(sort.CanRun, string.Join(" ", sort.Messages.Select(m => m.Text)));
        using (JobRunner runner = JobRunner.Start(sort)) Assert.Equal(RunStatus.Completed, runner.Run().Status);

        BackupVerifyResult v = BackupVerifier.Verify(t.Journal(0));
        Assert.True(v.Destinations[0].MovedOut > 0);
        Assert.Empty(v.Destinations[0].Problems);
        Assert.True(v.AllGood);
    }

    [Fact]
    public void Destinations_inside_the_card_twice_or_more_than_three_are_refused()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        BackupScan scan = BackupScanner.Scan(t.Card);
        Assert.Contains(BackupPlanner.Build(scan, [t.Card], "x").Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("inside the folder being backed up"));
        Assert.Contains(BackupPlanner.Build(scan, [Path.Join(t.Card, "DCIM")], "x").Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("inside the folder"));
        Assert.Contains(BackupPlanner.Build(scan, [t.Parents[0], t.Parents[0] + "\\"], "x").Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("chosen twice"));
        Assert.Contains(BackupPlanner.Build(scan, [.. t.Parents, Path.Join(t.Root, "D4")], "x").Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("at most 3"));
        Assert.Contains(BackupPlanner.Build(scan, [], "x").Messages, m => m.Level == MessageLevel.Error);
        Assert.Contains(BackupPlanner.Build(scan, [t.Parents[0] + "," + t.Parents[1]], "x").Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("not valid"));
        // Two destinations on one drive: a warning that they are not separate copies.
        Assert.Contains(BackupPlanner.Build(scan, [t.Parents[0], t.Parents[1]], "x").Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("same drive"));
    }

    [Fact]
    public void A_fat32_destination_refuses_files_of_4_GB_and_space_counts_every_copy_on_one_drive()
    {
        using var t = new BackupTree();
        var scan = new BackupScan
        {
            SourceRoot = t.Card,
            Files =
            [
                new SourceFile { RelativePath = "big.MP4", Size = 5L * 1024 * 1024 * 1024, CreationTime = 0, LastWriteTime = 0, Attributes = 0 },
                new SourceFile { RelativePath = "small.JPG", Size = 1000, CreationTime = 0, LastWriteTime = 0, Attributes = 0 },
            ],
            Folders = [],
            LeftOut = [],
            SkippedFolders = [],
            Problems = [],
        };
        var fat = new VolumeInfo(@"Y:\", 42, "FAT32", 100L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024);
        var card = new VolumeInfo(@"Z:\", 7, "exFAT", 0, 64L * 1024 * 1024 * 1024) { Kind = DriveKind.Removable };
        var env = new PlanEnvironment { VolumeOf = p => p.StartsWith(t.Card, StringComparison.OrdinalIgnoreCase) ? card : fat, ClusterSizeOf = _ => 32768, SyncOf = _ => null };
        BackupPlan plan = BackupPlanner.Build(scan, [t.Parents[0]], "x", true, env);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("4 GB"));

        var ntfs = new VolumeInfo(@"Y:\", 42, "NTFS", 8L * 1024 * 1024 * 1024, 200L * 1024 * 1024 * 1024);
        env = new PlanEnvironment { VolumeOf = p => p.StartsWith(t.Card, StringComparison.OrdinalIgnoreCase) ? card : ntfs, ClusterSizeOf = _ => 4096, SyncOf = _ => null };
        Assert.DoesNotContain(BackupPlanner.Build(scan, [t.Parents[0]], "x", true, env).Messages, m => m.Level == MessageLevel.Error);
        Assert.Contains(BackupPlanner.Build(scan, [t.Parents[0], t.Parents[1]], "x", true, env).Messages,
            m => m.Level == MessageLevel.Error && m.Text.Contains("for 2 copies"));
    }

    [Fact]
    public void A_destination_on_the_card_itself_is_refused()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = new VolumeInfo(@"Z:\", 7, "exFAT", 50L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024) { Kind = DriveKind.Removable };
        var env = new PlanEnvironment { VolumeOf = _ => card, ClusterSizeOf = _ => 4096, SyncOf = _ => null, TestCard = "Z: TEST_CARD" };
        BackupPlan plan = BackupPlanner.Build(BackupScanner.Scan(t.Card), [t.Parents[0]], "x", true, env);
        Assert.True(plan.IsCard);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("on the card itself"));
    }
}

/// <summary>The ASC MHL histories a backup writes pass the ASC's own reference tool (pip install ascmhl).</summary>
[Trait("Category", "ascmhl")]
public class BackupAscMhlTests
{
    [Fact]
    public void Every_destination_passes_ascmhl_verify_including_directory_hashes()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Add(".DS_Store", 10);
        Assert.True(t.Run(t.Plan()).AllVerified);
        for (int k = 0; k < 2; k++)
        {
            (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", t.Dest(k));
            Assert.True(exit == 0, output);
            (exit, output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", "-dh", t.Dest(k));
            Assert.True(exit == 0, output);
        }
    }

    [Fact]
    public void A_card_with_its_own_mhl_histories_gets_a_new_generation_in_each()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Add(@"OFFLOAD\A001\clip1.mov", 20_000);
        t.Add(@"OFFLOAD\A001\sub\clip2.mov", 20_000);
        // An earlier offload recorded a history for a subfolder, and then for the whole card.
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", Path.Join(t.Card, @"OFFLOAD\A001")).Exit);
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit);
        // The ascmhl tool leaves a delayed last-access update on the folder it wrote; let it settle before the snapshot.
        var metadata = t.CardMetadata();
        for (int wait = 0; wait < 5; wait++)
        {
            Thread.Sleep(1000);
            var now = t.CardMetadata();
            if (now.All(m => metadata[m.Key] == m.Value)) break;
            metadata = now;
        }
        AscMhl.GenerationsIn(t.Card);
        Assert.Equal(metadata, t.CardMetadata()); // reading the card's history changes nothing on it

        BackupPlan plan = t.Plan();
        Assert.Equal(1, plan.MhlGenerations);
        Assert.True(t.Run(plan).AllVerified);
        var after = t.CardMetadata();
        Assert.True(metadata.Count == after.Count && metadata.All(m => after.TryGetValue(m.Key, out var a) && a == m.Value),
            string.Join("; ", metadata.Where(m => !after.TryGetValue(m.Key, out var a) || a != m.Value).Select(m => $"{m.Key}: {m.Value} -> {(after.TryGetValue(m.Key, out var a) ? a.ToString() : "gone")}")));
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(k)));
            Assert.Equal(3, AscMhl.GenerationsIn(Path.Join(t.Dest(k), @"OFFLOAD\A001"))); // the card's root generation added one there too
            (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", t.Dest(k));
            Assert.True(exit == 0, output);
            (exit, output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", "-dh", t.Dest(k));
            Assert.True(exit == 0, output);
            string manifest = File.ReadAllText(Directory.EnumerateFiles(Path.Join(t.Dest(k), AscMhl.FolderName), "0002_*.mhl").Single());
            Assert.Contains("action=\"verified\"", manifest);
            Assert.DoesNotContain("action=\"failed\"", manifest);
            Assert.Contains("<hashlistreference>", manifest);
        }
        // The copied chain files changed (a generation was added), and verify-again knows that.
        Assert.True(BackupVerifier.Verify(t.Journal(0)).AllGood);
    }

    [Fact]
    public void A_card_that_no_longer_matches_its_own_history_is_copied_but_never_green()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "md5", t.Card).Exit); // an older offload, in md5
        JobTestKit.FlipByte(Path.Join(t.Card, @"DCIM\100MSDCF\DSC00001.ARW"), 99);         // changed on the card since
        var card = t.CardFingerprints();

        BackupResult result = t.Run(t.Plan());
        Assert.False(result.AllVerified);
        Assert.All(result.Destinations, d =>
        {
            Assert.True(d.MhlMismatch);
            Assert.Contains("DSC00001.ARW", d.MhlSkipped);
        });
        t.AssertExactCopy(0, card); // the card as it is now, byte for byte
        Assert.Contains("no longer matches its own earlier checksums", BackupTexts.Describe(result).Title);
    }

    [Fact]
    public void A_card_whose_history_matches_in_another_hash_format_gets_a_verified_generation()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh128", t.Card).Exit); // the reference tool's default
        Assert.True(t.Run(t.Plan()).AllVerified);
        (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", t.Dest(0));
        Assert.True(exit == 0, output);
    }

    [Theory]
    [InlineData("missing manifest")]
    [InlineData("changed manifest")]
    [InlineData("custom ignore")]
    public void A_history_that_cannot_be_continued_safely_gets_no_generation(string damage)
    {
        using var t = new BackupTree();
        t.TypicalCard();
        string[] extra = damage == "custom ignore" ? ["-i", "*.XML"] : [];
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", ["create", "-h", "xxh64", .. extra, t.Card]).Exit);
        string manifest = Directory.EnumerateFiles(Path.Join(t.Card, AscMhl.FolderName), "*.mhl").Single();
        if (damage == "missing manifest") File.Delete(manifest);
        if (damage == "changed manifest") File.AppendAllText(manifest, "<!-- edited -->");

        BackupResult result = t.Run(t.Plan());
        Assert.All(result.Destinations, d =>
        {
            Assert.False(d.MhlWritten);
            Assert.NotNull(d.MhlSkipped);
            Assert.False(d.MhlMismatch);
        });
        Assert.True(result.AllVerified); // the files are verified; the result names why there is no ASC MHL
        Assert.Equal(1, Directory.EnumerateFiles(Path.Join(t.Dest(0), AscMhl.FolderName), "*.mhl").Count() + (damage == "missing manifest" ? 1 : 0));
    }

    [Fact]
    public void The_c4_id_matches_the_reference_tool()
    {
        using var t = new BackupTree();
        t.Add("x.bin", 1234);
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit);
        string chain = File.ReadAllText(Path.Join(t.Card, AscMhl.FolderName, AscMhl.ChainFileName));
        string manifest = Directory.EnumerateFiles(Path.Join(t.Card, AscMhl.FolderName), "*.mhl").Single();
        Assert.Contains(AscMhl.C4(File.ReadAllBytes(manifest)), chain);
    }
}
