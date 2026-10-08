using IvarOffload.App;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// A top-up adds a card's new files to its earlier backup (a card backed up, shot on without formatting, then backed up
/// again): only what is new or changed is written, then every file of the card is verified, so the folder holds a
/// verified copy of the card as it is now. Nothing in the folder is ever overwritten or deleted.
/// </summary>
public class BackupTopUpTests
{
    private const string MediaPro = @"PRIVATE\M4ROOT\MEDIAPRO.XML";
    private const string Jpg = @"DCIM\100MSDCF\DSC00001.JPG";

    private static BackupPlan TopUp(BackupTree t, int destinations = 2, bool reread = true, params string[] parents) =>
        BackupPlanner.Build(BackupScanner.Scan(t.Card), parents.Length > 0 ? parents : t.Parents.Take(destinations).ToList(), "ignored", reread, topUp: true);

    private static BackupPlan Full(BackupTree t, string name, int destinations = 2) =>
        BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(destinations).ToList(), name);

    /// <summary>The top-up's log in a backup folder (the one that adds to an earlier backup; the newest when there are several).</summary>
    private static string TopUpJournal(string folder) =>
        BackupPaths.FindJournals(folder).Select(l => (Log: l, Header: JournalReader.TryReadHeader(l)!))
            .Where(l => l.Header.AddsTo is not null).OrderByDescending(l => l.Header.Created, StringComparer.Ordinal).First().Log;

    private static string Messages(BackupPlan plan) => string.Join(" | ", plan.Messages.Select(m => m.Text));

    /// <summary>Deletes a card file (a shot deleted in camera) and gives its folder a fixed date, as a camera would.</summary>
    internal static void DeleteFromCard(BackupTree t, string rel)
    {
        string path = Path.Join(t.Card, rel);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        Thread.Sleep(100); // the folder's date settles after the delete
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, new DateTime(2024, 5, 8, 10, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>The files moved into _IVAROffload\replaced (the folder's earlier ASC MHL history, when it was moved there too, aside).</summary>
    private static List<string> Replaced(string folder)
    {
        string replaced = Path.Join(folder, JobPaths.LogFolderName, BackupRunner.ReplacedFolderName);
        return !Directory.Exists(replaced) ? [] : Directory.EnumerateFiles(replaced, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(replaced, f).Split(Path.DirectorySeparatorChar).Contains(AscMhl.FolderName)).ToList();
    }

    /// <summary>A card backed up to two destinations, then shot on: two new files.</summary>
    private static BackupTree BackedUpThenShotOn(bool large = false)
    {
        var t = new BackupTree();
        t.TypicalCard(large);
        Assert.True(t.Run(t.Plan()).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        t.Add(@"PRIVATE\M4ROOT\CLIP\C0002.MP4", 120_000);
        return t;
    }

    /// <summary>Rewrites a card file with other content and a later date (as a camera rewrites its index), keeping its attributes.</summary>
    private static byte[] Rewrite(BackupTree t, string rel, int size = 1200)
    {
        string path = Path.Join(t.Card, rel);
        byte[] before = File.ReadAllBytes(path);
        FileAttributes attributes = File.GetAttributes(path);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 5, 7, 9, 0, 0, DateTimeKind.Utc));
        File.SetAttributes(path, attributes);
        return before;
    }

    // ---- The offer --------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_shot_on_since_its_backup_is_offered_a_top_up_in_the_preview()
    {
        using BackupTree t = BackedUpThenShotOn();
        BackupPlan plan = Full(t, "second");
        TopUpOffer offer = Assert.IsType<TopUpOffer>(plan.TopUp);
        Assert.False(plan.IsTopUp);
        Assert.Equal(2, offer.New);
        Assert.Equal(0, offer.Changed);
        Assert.Equal(7, offer.Unchanged);
        Assert.Equal([t.Dest(0), t.Dest(1)], offer.Folders.Select(f => f.Folder));
        string text = offer.Describe(adding: false, plan.Files.Count);
        Assert.Contains("the card has 2 new files since then", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add what is missing to that backup and verify the whole card", text);
        // The earlier backup is described once (by the offer), not again in the notes.
        Assert.DoesNotContain(plan.Messages, m => m.Text.Contains("already backed up"));

        BackupPlan adding = TopUp(t);
        Assert.True(adding.CanRun, Messages(adding));
        Assert.True(adding.IsTopUp);
        Assert.Equal([t.Dest(0), t.Dest(1)], adding.Targets.Select(x => x.Folder));
        Assert.Contains("2 new files are copied", adding.TopUp!.Describe(adding: true, adding.Files.Count));
        Assert.Contains("all 9 files on the card are read again", adding.TopUp.Describe(adding: true, adding.Files.Count));
    }

    [Fact]
    public void An_index_file_rewritten_and_a_shot_deleted_in_camera_still_count_as_the_same_card()
    {
        using BackupTree t = BackedUpThenShotOn();
        Rewrite(t, MediaPro);
        DeleteFromCard(t, Jpg);
        TopUpOffer offer = Assert.IsType<TopUpOffer>(Full(t, "second").TopUp);
        Assert.Equal(1, offer.Changed);
        Assert.Equal(1, offer.KeptInFolder);
        Assert.Equal(Jpg, Assert.Single(offer.Kept).Rel);
    }

    [Fact]
    public void A_card_formatted_and_shot_on_again_is_not_offered_a_top_up()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        foreach (string f in Directory.EnumerateFiles(t.Card, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        t.Add(@"DCIM\100MSDCF\DSC00001.JPG", 31_000); // the camera numbers the new card's first shot alike
        BackupPlan plan = Full(t, "second");
        Assert.Null(plan.TopUp);
        BackupPlan adding = TopUp(t);
        Assert.False(adding.CanRun);
        Assert.Contains(adding.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("no earlier backup of this card"));
    }

    [Fact]
    public void No_top_up_when_a_destination_has_no_earlier_backup_or_they_hold_different_ones()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(Full(t, BackupTree.Name, 1)).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 5000);

        BackupPlan one = Full(t, "second", 2);
        Assert.Null(one.TopUp);
        Assert.Contains("has none of this card", one.TopUpRefusal);
        Assert.Contains(one.Messages, m => m.Level == MessageLevel.Info && m.Text == one.TopUpRefusal);

        // Another full backup of the card to the second destination only: the two hold different backups.
        Assert.True(t.Run(BackupPlanner.Build(BackupScanner.Scan(t.Card), [t.Parents[1]], "other")).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00003.JPG", 5000);
        BackupPlan two = Full(t, "third", 2);
        Assert.Null(two.TopUp);
        Assert.Contains("different earlier backups", two.TopUpRefusal);
        Assert.False(TopUp(t).CanRun);
    }

    [Fact]
    public void An_unfinished_backup_of_the_card_is_resumed_not_added_to()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-done", 2)));
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 5000);
        BackupPlan plan = Full(t, "second");
        Assert.Null(plan.TopUp);
        Assert.Contains("unfinished backup of this card", plan.TopUpRefusal);
    }

    // ---- The run ----------------------------------------------------------------------------------------------

    [Fact]
    public void New_files_are_added_and_every_file_of_the_card_is_verified_without_writing_the_others()
    {
        using BackupTree t = BackedUpThenShotOn(large: true);
        var card = t.CardFingerprints();
        string clip = Path.Join(t.Dest(0), @"PRIVATE\M4ROOT\CLIP\C0001.MP4");
        DateTime written = File.GetLastWriteTimeUtc(clip);
        long fileId = new FileInfo(clip).Length;
        var metadata = t.CardMetadata();

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.Equal(9, result.Files);
        Assert.NotNull(result.AddsTo);
        foreach (BackupDestinationResult d in result.Destinations)
        {
            Assert.Equal(9, d.Verified);
            Assert.Equal(2, d.Copied);
            Assert.Equal(7, d.Rechecked);
            Assert.Equal(0, d.Replaced + d.Repaired + d.Restored + d.Kept);
            Assert.True(d.MhlWritten);
            Assert.Null(d.MhlRestarted);
        }
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
        Assert.Equal(written, File.GetLastWriteTimeUtc(clip)); // not written again
        Assert.Equal(2, BackupPaths.FindJournals(t.Dest(0)).Count());
        Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(0))); // the folder's history got a second generation
        Assert.Equal(metadata, t.CardMetadata()); // the card was only read
        Assert.False(Directory.Exists(Path.Join(t.Dest(0), JobPaths.LogFolderName, BackupRunner.ReplacedFolderName)));
        Assert.Contains("already in the backup, read again", File.ReadAllText(BackupPaths.SummaryPath(TopUpJournal(t.Dest(0)))));
    }

    [Fact]
    public void A_file_changed_on_the_card_is_copied_again_and_its_old_version_moved_aside()
    {
        using BackupTree t = BackedUpThenShotOn();
        byte[] old = Rewrite(t, MediaPro);
        var card = t.CardFingerprints();

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, card);
            string aside = Assert.Single(Replaced(t.Dest(k)));
            Assert.EndsWith(MediaPro, aside);
            Assert.Equal(old, File.ReadAllBytes(aside));
            BackupDestinationResult d = result.Destinations[k];
            Assert.Equal(1, d.Replaced);
            Assert.Equal(3, d.Copied); // two new, one changed
            // The folder's own ASC MHL history listed the old version: it is kept aside, and a new one describes the folder now.
            Assert.NotNull(d.MhlRestarted);
            Assert.True(File.Exists(Path.Join(t.Dest(k), d.MhlRestarted, AscMhl.ChainFileName)));
            Assert.Equal(1, AscMhl.GenerationsIn(t.Dest(k)));
        }
        JobItem item = JournalReader.Read(TopUpJournal(t.Dest(0))).Items.Single(i => i.Rel == MediaPro);
        Assert.Equal(BackupWhy.Changed, item.Why);
        Assert.Equal(BackupReasons.AsideChanged, item.SetAsideWhy);
    }

    [Fact]
    public void Files_deleted_from_the_card_stay_in_the_backup_and_are_verified()
    {
        using BackupTree t = BackedUpThenShotOn();
        var expected = t.CardFingerprints();
        DeleteFromCard(t, Jpg);

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.Equal(8, result.Files);
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, expected); // the card now, and the deleted shot
            Assert.Equal(1, result.Destinations[k].Kept);
        }
        JobState state = JournalReader.Read(TopUpJournal(t.Dest(0)));
        Assert.Equal(9, state.Items.Count);
        Assert.Equal(BackupWhy.Kept, state.Items[^1].Why);
        Assert.Equal("kept", state.Items[^1].How);
    }

    [Fact]
    public void Files_a_sort_moved_out_are_copied_again_from_the_card()
    {
        using BackupTree t = BackedUpThenShotOn();
        var card = t.CardFingerprints();
        // "Sort this backup" moved the clips of the first destination out.
        Directory.Move(Path.Join(t.Dest(0), @"PRIVATE\M4ROOT\CLIP"), Path.Join(t.Root, "sorted-out"));

        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, Messages(plan));
        Assert.Equal(2, plan.TopUp!.Missing);
        Assert.Contains("no longer in the backup folder", plan.TopUp.Describe(adding: true, plan.Files.Count));
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
        Assert.Equal(2, result.Destinations[0].Restored);
        Assert.Equal(0, result.Destinations[1].Restored);
    }

    [Fact]
    public void A_copy_damaged_since_the_earlier_backup_is_moved_aside_and_copied_again()
    {
        using BackupTree t = BackedUpThenShotOn();
        var card = t.CardFingerprints();
        string damaged = Path.Join(t.Dest(1), @"DCIM\100MSDCF\DSC00001.ARW");
        JobTestKit.FlipByte(damaged, 500); // same size and dates, other content

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(1, card);
        Assert.Equal(1, result.Destinations[1].Repaired);
        Assert.Equal(0, result.Destinations[0].Repaired);
        JobItem item = JournalReader.Read(TopUpJournal(t.Dest(1))).Items.Single(i => i.Rel.EndsWith("DSC00001.ARW"));
        Assert.Equal(BackupReasons.AsideDamaged, item.SetAsideWhy);
        Assert.True(File.Exists(Path.Join(t.Dest(1), item.SetAside)));
    }

    [Fact]
    public void A_damaged_copy_of_a_file_no_longer_on_the_card_is_left_in_place_and_never_green()
    {
        using BackupTree t = BackedUpThenShotOn();
        DeleteFromCard(t, Jpg);
        JobTestKit.FlipByte(Path.Join(t.Dest(0), Jpg), 100); // same size and dates, other content

        BackupResult result = t.Run(TopUp(t));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.False(result.AllVerified);
        Assert.Equal(Jpg, Assert.Single(result.Destinations[0].KeptDamaged));
        Assert.Empty(result.Destinations[1].KeptDamaged);
        Assert.True(File.Exists(Path.Join(t.Dest(0), Jpg))); // the only copy there is: never moved
        Assert.Empty(Replaced(t.Dest(0)));
    }

    [Fact]
    public void A_file_no_longer_on_the_card_that_was_edited_in_the_folder_stays_as_it_is()
    {
        using BackupTree t = BackedUpThenShotOn();
        DeleteFromCard(t, Jpg);
        string edited = Path.Join(t.Dest(0), Jpg);
        File.AppendAllText(edited, "<x:xmpmeta rating=5/>"); // culled in place by a photo program
        byte[] now = File.ReadAllBytes(edited);

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.Equal(1, result.Destinations[0].KeptEdited);
        Assert.Equal(now, File.ReadAllBytes(edited));
        Assert.Contains("edited by another program", string.Join("\n", BackupTexts.TopUpLines(result)));
    }

    [Fact]
    public void A_file_edited_in_the_backup_folder_is_called_edited_not_damaged()
    {
        using BackupTree t = BackedUpThenShotOn();
        var card = t.CardFingerprints();
        string arw = Path.Join(t.Dest(0), @"DCIM\100MSDCF\DSC00001.ARW");
        File.AppendAllText(arw, "edited");

        BackupPlan plan = TopUp(t);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("changed there since the earlier backup (edited by another program?)"));
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(0, card);
        Assert.Equal((1, 0), (result.Destinations[0].Edited, result.Destinations[0].Repaired));
        Assert.Equal(BackupReasons.AsideEdited, JournalReader.Read(TopUpJournal(t.Dest(0))).Items.Single(i => i.Rel.EndsWith("DSC00001.ARW")).SetAsideWhy);
    }

    [Fact]
    public void A_different_shot_with_a_reused_name_is_named_in_a_warning()
    {
        using BackupTree t = BackedUpThenShotOn();
        Rewrite(t, Jpg, 40_000); // the camera numbered a new shot like one deleted in camera
        Rewrite(t, MediaPro); // an index file: expected to change, not named
        BackupPlan plan = TopUp(t);
        PlanMessage warning = Assert.Single(plan.Messages, m => m.Text.Contains("number the camera used again"));
        Assert.Equal(MessageLevel.Warning, warning.Level);
        Assert.StartsWith("1 file on the card has the name", warning.Text);
        Assert.Contains(Jpg, warning.Text);
        Assert.Contains("stays next to the card's, renamed \"DSC00001 (earlier).JPG\"", warning.Text);
    }

    private const string Earlier = @"DCIM\100MSDCF\DSC00001 (earlier).JPG";

    [Fact]
    public void The_earlier_shot_with_a_reused_name_stays_next_to_the_new_one()
    {
        using BackupTree t = BackedUpThenShotOn();
        var before = BackupTree.Fingerprints(t.Dest(0));
        Rewrite(t, Jpg, 40_000);
        Rewrite(t, MediaPro);
        var expected = t.CardFingerprints();
        expected[Earlier] = before[Jpg]; // the earlier shot, bit for bit, with its dates

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, expected);
            Assert.Equal(Earlier, Assert.Single(result.Destinations[k].KeptBeside));
            Assert.Equal(1, result.Destinations[k].Replaced); // MEDIAPRO.XML: into the log folder
            Assert.EndsWith("MEDIAPRO.XML", Assert.Single(Replaced(t.Dest(k))));
        }
        string all = string.Join("\n", BackupTexts.TopUpLines(result));
        Assert.Contains("1 photo or clip changed on the card since the earlier backup (usually another shot with a reused number)", all);
        Assert.Contains(Earlier, all);
        Assert.StartsWith("2 new files copied, 2 copied again, all 9 files checked", BackupTexts.Describe(result).LeftLine);

        // Verify again reads it too, and a later top-up keeps it like any file no longer on the card.
        Assert.True(BackupVerifier.Verify(TopUpJournal(t.Dest(0))).AllGood);
        JobTestKit.FlipByte(Path.Join(t.Dest(1), Earlier), 50);
        Assert.False(BackupVerifier.Verify(TopUpJournal(t.Dest(0))).AllGood);
        JobTestKit.FlipByte(Path.Join(t.Dest(1), Earlier), 50);
        t.Add(@"DCIM\100MSDCF\DSC00003.JPG", 9000);
        expected = t.CardFingerprints();
        expected[Earlier] = before[Jpg];
        BackupPlan third = TopUp(t);
        Assert.Contains(third.TopUp!.Kept, k => k.Rel == Earlier);
        Assert.True(t.Run(third).AllVerified);
        t.AssertExactCopy(0, expected);
    }

    [Theory]
    [InlineData("after-aside-journal", 1)]
    [InlineData("after-aside-journal", 2)]
    [InlineData("after-aside", 1)]
    [InlineData("after-aside", 2)]
    [InlineData("after-place-rename", 3)]
    public void A_crash_while_the_earlier_shot_is_renamed_is_resumed_with_both_in_place(string point, int occurrence)
    {
        using BackupTree t = BackedUpThenShotOn();
        var before = BackupTree.Fingerprints(t.Dest(0));
        Rewrite(t, Jpg, 40_000);
        var expected = t.CardFingerprints();
        expected[Earlier] = before[Jpg];
        Assert.Throws<SimulatedCrash>(() => t.Run(TopUp(t), new CrashAt(point, occurrence)));
        BackupResult result = BackupTree.Resume(TopUpJournal(t.Dest(0)));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, expected);
            Assert.False(File.Exists(Path.Join(t.Dest(k), @"DCIM\100MSDCF\DSC00001 (earlier 2).JPG")));
        }
    }

    [Fact]
    public void A_file_that_leaves_the_card_after_the_preview_leaves_the_old_version_where_it_is()
    {
        using BackupTree t = BackedUpThenShotOn();
        byte[] old = File.ReadAllBytes(Path.Join(t.Dest(0), MediaPro));
        Rewrite(t, MediaPro);
        BackupPlan plan = TopUp(t);
        DeleteFromCard(t, MediaPro); // deleted in camera between the preview and the run

        BackupResult result = t.Run(plan);
        Assert.False(result.AllVerified);
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(old, File.ReadAllBytes(Path.Join(t.Dest(k), MediaPro))); // never moved away without its replacement
            Assert.Empty(Replaced(t.Dest(k)));
            Assert.Equal(BackupReasons.GoneFromCard, JournalReader.Read(TopUpJournal(t.Dest(k))).Items.Single(i => i.Rel == MediaPro).Note);
        }
    }

    [Fact]
    public void Ending_a_top_up_before_a_replacement_is_copied_leaves_the_old_file_in_place()
    {
        using BackupTree t = BackedUpThenShotOn(large: true);
        byte[] old = File.ReadAllBytes(Path.Join(t.Dest(0), @"PRIVATE\M4ROOT\CLIP\C0001.MP4"));
        string clip = Path.Join(t.Card, @"PRIVATE\M4ROOT\CLIP\C0001.MP4");
        File.AppendAllText(clip, "more");
        BackupPlan plan = TopUp(t);
        // Stopped while the new version is being copied: then ended.
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt("mid-copy", 1)));
        BackupTree.Close(TopUpJournal(t.Dest(0)));
        for (int k = 0; k < 2; k++) Assert.Equal(old, File.ReadAllBytes(Path.Join(t.Dest(k), @"PRIVATE\M4ROOT\CLIP\C0001.MP4")));
    }

    [Fact]
    public void A_file_that_is_not_from_the_earlier_backup_is_moved_aside_not_replaced()
    {
        using BackupTree t = BackedUpThenShotOn();
        var card = t.CardFingerprints();
        string intruder = Path.Join(t.Dest(0), @"DCIM\100MSDCF\DSC00002.JPG"); // a new card file's name, put there by hand
        File.WriteAllText(intruder, "someone else's");

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(0, card);
        string aside = Assert.Single(Replaced(t.Dest(0)));
        Assert.Equal("someone else's", File.ReadAllText(aside));
    }

    [Fact]
    public void A_file_rewritten_on_the_card_with_the_same_size_and_date_is_copied_again()
    {
        using BackupTree t = BackedUpThenShotOn();
        string arw = Path.Join(t.Card, @"DCIM\100MSDCF\DSC00001.ARW");
        var before = BackupTree.Fingerprints(t.Dest(0));
        JobTestKit.FlipByte(arw, 900); // read alike twice: changed on the card in place
        var card = t.CardFingerprints();
        card[@"DCIM\100MSDCF\DSC00001 (earlier).ARW"] = before[@"DCIM\100MSDCF\DSC00001.ARW"]; // the earlier version stays next to it

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, card);
            Assert.Equal(BackupReasons.AsideChanged, JournalReader.Read(TopUpJournal(t.Dest(k))).Items.Single(i => i.Rel.EndsWith("DSC00001.ARW")).SetAsideWhy);
        }
    }

    [Fact]
    public void A_card_that_returns_other_data_each_time_keeps_the_copy_and_fails_the_file()
    {
        using BackupTree t = BackedUpThenShotOn();
        string arw = Path.Join(t.Card, @"DCIM\100MSDCF\DSC00001.ARW");
        byte[] copy = File.ReadAllBytes(Path.Join(t.Dest(0), @"DCIM\100MSDCF\DSC00001.ARW"));
        JobTestKit.FlipByte(arw, 900);
        // A flaky reader: the second read returns the data the backup verified.
        BackupResult result = t.Run(TopUp(t), new ActionAt("card-differs-from-backup", _ => JobTestKit.FlipByte(arw, 900)));
        Assert.Equal(RunStatus.CompletedWithFailures, result.Status);
        Assert.False(result.AllVerified);
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(copy, File.ReadAllBytes(Path.Join(t.Dest(k), @"DCIM\100MSDCF\DSC00001.ARW"))); // kept as it was
            Assert.Equal(BackupReasons.CardDiffersFromBackup, JournalReader.Read(TopUpJournal(t.Dest(k))).Items.Single(i => i.Rel.EndsWith("DSC00001.ARW")).Note);
        }
    }

    [Fact]
    public void A_complete_backup_is_added_to_rather_than_a_newer_one_that_was_ended_early()
    {
        using BackupTree t = BackedUpThenShotOn();
        Assert.Throws<SimulatedCrash>(() => t.Run(Full(t, "second", 1), new CrashAt("after-done", 1)));
        BackupTree.Close(BackupPaths.FindJournals(Path.Join(t.Parents[0], "second")).Single());
        TopUpOffer offer = Assert.IsType<TopUpOffer>(Full(t, "third", 1).TopUp);
        Assert.Equal(t.Dest(0), Assert.Single(offer.Folders).Folder);
    }

    [Fact]
    public void A_top_up_of_a_top_up_carries_the_files_no_longer_on_the_card_along()
    {
        using BackupTree t = BackedUpThenShotOn();
        var withJpg = t.CardFingerprints();
        DeleteFromCard(t, Jpg);
        Assert.True(t.Run(TopUp(t)).AllVerified);

        t.Add(@"DCIM\100MSDCF\DSC00003.JPG", 9000);
        var expected = t.CardFingerprints();
        expected[Jpg] = withJpg[Jpg];
        BackupPlan third = TopUp(t);
        Assert.True(third.CanRun, Messages(third));
        Assert.Equal(1, third.TopUp!.New);
        Assert.Equal(1, third.TopUp.KeptInFolder);
        BackupResult result = t.Run(third);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        t.AssertExactCopy(0, expected);
        Assert.Equal(3, BackupPaths.FindJournals(t.Dest(0)).Count());
        Assert.Equal(3, AscMhl.GenerationsIn(t.Dest(0)));
    }

    [Fact]
    public void Nothing_new_on_the_card_still_verifies_the_whole_card_against_the_backup()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, Messages(plan));
        Assert.Contains("verify the whole card against that backup (nothing is copied again)", Full(t, "second").TopUp!.Describe(adding: false, plan.Files.Count));
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified);
        Assert.All(result.Destinations, d => Assert.Equal((0, 7), (d.Copied, d.Rechecked)));
    }

    [Fact]
    public void Ending_a_top_up_early_keeps_every_file_in_the_folder_and_a_later_top_up_finishes_it()
    {
        using BackupTree t = BackedUpThenShotOn();
        Rewrite(t, MediaPro);
        var card = t.CardFingerprints();
        Assert.Throws<SimulatedCrash>(() => t.Run(TopUp(t), new CrashAt("after-aside", 1)));
        BackupResult closed = BackupTree.Close(TopUpJournal(t.Dest(0)));
        Assert.Equal(RunStatus.Closed, closed.Status);
        // The old MEDIAPRO.XML is aside, the new one not copied: the log says both.
        string summary = File.ReadAllText(BackupPaths.SummaryPath(TopUpJournal(t.Dest(0))));
        Assert.Contains("Moved aside", summary);
        Assert.Contains(BackupReasons.NotCheckedEnded, summary);

        BackupPlan again = TopUp(t);
        Assert.True(again.CanRun, Messages(again));
        Assert.True(t.Run(again).AllVerified);
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
    }

    // ---- Crashes, drives and cards ------------------------------------------------------------------------------

    public static TheoryData<string, int> CrashPoints() => new()
    {
        { "after-aside-journal", 1 },
        { "after-aside-journal", 3 },
        { "after-aside", 1 },
        { "after-aside", 2 },
        { "before-recheck-done", 1 },
        { "before-recheck-done", 5 },
        { "after-copied", 2 },
        { "after-done", 3 },
        { "before-mhl", 1 },
        { "before-mhl-restart", 1 },
        { "after-mhl-restart", 1 },
        { "after-mhl-restart", 2 },
        { "after-mhl", 1 },
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void A_crash_at_any_step_of_a_top_up_is_resumed_to_the_card_as_it_is_now(string point, int occurrence)
    {
        using BackupTree t = BackedUpThenShotOn(large: true);
        var withJpg = t.CardFingerprints();
        byte[] old = Rewrite(t, MediaPro);
        DeleteFromCard(t, Jpg);
        JobTestKit.FlipByte(Path.Join(t.Dest(1), @"DCIM\100MSDCF\DSC00001.ARW"), 500);
        var expected = t.CardFingerprints();
        expected[Jpg] = withJpg[Jpg];

        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, Messages(plan));
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt(point, occurrence)));
        BackupResult result = BackupTree.Resume(TopUpJournal(t.Dest(1)));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, expected);
            string replaced = Path.Join(t.Dest(k), JobPaths.LogFolderName, BackupRunner.ReplacedFolderName);
            Assert.Equal(old, File.ReadAllBytes(Assert.Single(Directory.EnumerateFiles(replaced, "MEDIAPRO*.XML", SearchOption.AllDirectories))));
            Assert.Equal(1, AscMhl.GenerationsIn(t.Dest(k)));
            Assert.Single(Directory.EnumerateFiles(Path.Join(t.Dest(k), AscMhl.FolderName), "*.mhl"));
        }
        Assert.Throws<JournalException>(() => BackupRunner.Open(TopUpJournal(t.Dest(0))));
    }

    [Fact]
    public void A_destination_unplugged_mid_top_up_drops_out_and_resume_completes_it()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        string link = Path.Join(t.Root, "D2link");
        JobTestKit.Junction(link, t.Parents[1]);
        Assert.True(t.Run(BackupPlanner.Build(BackupScanner.Scan(t.Card), [t.Parents[0], link], BackupTree.Name)).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        Rewrite(t, MediaPro);
        var card = t.CardFingerprints();

        BackupPlan plan = TopUp(t, parents: [t.Parents[0], link]);
        Assert.True(plan.CanRun, Messages(plan));
        BackupResult first = t.Run(plan, new ActionAt("before-recheck-done", _ => Directory.Delete(link), 4));
        Assert.Equal(RunStatus.CompletedWithFailures, first.Status);
        t.AssertExactCopy(0, card);
        Assert.Equal(HaltReason.TargetNotConnected, first.Destinations[1].Halt);

        JobTestKit.Junction(link, t.Parents[1]);
        BackupResult resumed = BackupTree.Resume(TopUpJournal(t.Dest(0)));
        Assert.True(resumed.AllVerified, resumed.Message);
        t.AssertExactCopy(1, card);
    }

    [Fact]
    public void A_card_pulled_mid_top_up_halts_and_resume_finishes()
    {
        using BackupTree t = BackedUpThenShotOn();
        Rewrite(t, MediaPro);
        var card = t.CardFingerprints();
        string away = t.Card + "-pulled";
        BackupResult halted = t.Run(TopUp(t), new ActionAt("before-recheck-done", _ => JobTestKit.MoveFolder(t.Card, away), 3));
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.SourceNotConnected, halted.Halt);
        for (int k = 0; k < 2; k++) Assert.Equal(0, JobTestKit.Lines(TopUpJournal(t.Dest(k)), "skip"));

        JobTestKit.MoveFolder(away, t.Card);
        BackupResult resumed = BackupTree.Resume(TopUpJournal(t.Dest(0)));
        Assert.True(resumed.AllVerified, resumed.Message);
        t.AssertExactCopy(0, card);
        t.AssertExactCopy(1, card);
    }

    [Fact]
    public void Verify_again_after_files_left_both_the_card_and_the_folder_is_still_green()
    {
        using BackupTree t = BackedUpThenShotOn();
        DeleteFromCard(t, Jpg);
        File.Delete(Path.Join(t.Dest(0), Jpg)); // sorted out, then deleted in camera
        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.Equal(1, result.Destinations[0].KeptGone);
        BackupVerifyResult v = BackupVerifier.Verify(TopUpJournal(t.Dest(0)));
        Assert.True(v.AllGood, string.Join(" ", v.Destinations.SelectMany(d => d.Problems)));
    }

    [Fact]
    public void Verify_again_after_a_top_up_checks_every_file_in_the_folder()
    {
        using BackupTree t = BackedUpThenShotOn();
        DeleteFromCard(t, Jpg);
        Assert.True(t.Run(TopUp(t)).AllVerified);
        BackupVerifyResult v = BackupVerifier.Verify(TopUpJournal(t.Dest(0)));
        Assert.True(v.AllGood, string.Join(" ", v.Destinations.SelectMany(d => d.Problems)));
        Assert.Equal(9, v.Files);
    }
}

/// <summary>The ASC MHL histories a top-up leaves pass the ASC's own reference tool (pip install ascmhl).</summary>
[Trait("Category", "ascmhl")]
public class BackupTopUpMhlTests
{
    /// <param name="directoryHashes">
    /// Also verify the folder hashes (-dh). The reference tool compares them with the first generation, so a history that
    /// got a generation for added files fails that check - also one the tool's own "ascmhl create" continued.
    /// </param>
    private static void AssertVerifies(string folder, bool directoryHashes = true)
    {
        (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", folder);
        Assert.True(exit == 0, output);
        if (!directoryHashes) return;
        (exit, output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", "-dh", folder);
        Assert.True(exit == 0, output);
    }

    private static BackupPlan TopUp(BackupTree t) =>
        BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "ignored", topUp: true);

    [Fact]
    public void A_top_up_with_new_and_deleted_files_continues_the_folders_history()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        BackupTopUpTests.DeleteFromCard(t, @"DCIM\100MSDCF\DSC00001.JPG");
        Assert.True(t.Run(TopUp(t)).AllVerified);
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(k)));
            AssertVerifies(t.Dest(k), directoryHashes: false);
        }
    }

    [Fact]
    public void A_top_up_with_a_changed_file_or_a_sorted_out_file_starts_a_history_that_verifies()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        string mediaPro = Path.Join(t.Card, @"PRIVATE\M4ROOT\MEDIAPRO.XML");
        File.SetAttributes(mediaPro, FileAttributes.Normal);
        File.WriteAllText(mediaPro, "<rewritten by the camera/>");
        // Sorted out of the first destination, then deleted in camera: in neither place the history expects.
        Directory.Move(Path.Join(t.Dest(0), @"DCIM\100MSDCF"), Path.Join(t.Root, "sorted"));
        BackupTopUpTests.DeleteFromCard(t, @"DCIM\100MSDCF\DSC00001.ARW");
        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            Assert.NotNull(result.Destinations[k].MhlRestarted);
            AssertVerifies(t.Dest(k));
        }
    }

    [Fact]
    public void A_card_with_its_own_history_gets_the_next_generation_on_top_of_the_backups()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit);
        Assert.True(t.Run(t.Plan()).AllVerified);
        Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(0)));
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);

        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, string.Join(" | ", plan.Messages.Select(m => m.Text)));
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(3, AscMhl.GenerationsIn(t.Dest(k)));
            AssertVerifies(t.Dest(k), directoryHashes: false);
        }
        Assert.Equal(1, AscMhl.GenerationsIn(t.Card)); // the card's own history was only read
    }

    [Fact]
    public void After_a_top_up_that_was_ended_early_the_folders_history_of_a_card_with_its_own_is_still_continued()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit);
        Assert.True(t.Run(t.Plan()).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        Assert.Throws<SimulatedCrash>(() => t.Run(TopUp(t), new CrashAt("after-done", 1)));
        string first = BackupPaths.FindJournals(t.Dest(0)).Select(l => (l, JournalReader.TryReadHeader(l)!)).Single(x => x.Item2.AddsTo is not null).l;
        BackupTree.Close(first); // no generation written: the chain in the folder still carries the first backup's

        t.Add(@"DCIM\100MSDCF\DSC00003.JPG", 25_000);
        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, string.Join(" | ", plan.Messages.Select(m => m.Text)));
        Assert.Equal(0, plan.TopUp!.Missing); // the folder's chain is its own, not "missing" or "edited"
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        Assert.All(result.Destinations, d => Assert.Equal(0, d.Repaired + d.Edited));
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(3, AscMhl.GenerationsIn(t.Dest(k)));
            AssertVerifies(t.Dest(k), directoryHashes: false);
        }
    }

    [Fact]
    public void The_earlier_shot_kept_next_to_a_new_one_is_in_the_history()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        string jpg = Path.Join(t.Card, @"DCIM\100MSDCF\DSC00001.JPG");
        File.WriteAllBytes(jpg, Enumerable.Range(0, 40_000).Select(i => (byte)(i * 13)).ToArray()); // another shot, same number
        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            Assert.True(File.Exists(Path.Join(t.Dest(k), @"DCIM\100MSDCF\DSC00001 (earlier).JPG")));
            AssertVerifies(t.Dest(k));
        }
    }

    [Fact]
    public void A_card_that_got_its_own_history_since_is_not_offered_a_top_up()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified); // the folder gets this app's own history
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit); // another tool wrote one onto the card

        BackupPlan plan = BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "second");
        Assert.Null(plan.TopUp);
        Assert.Contains("that it did not have when it was backed up", plan.TopUpRefusal);
    }

    [Fact]
    public void A_card_whose_own_history_changed_since_is_not_offered_a_top_up()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit);
        Assert.True(t.Run(t.Plan()).AllVerified);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        Thread.Sleep(1100); // a new generation file name has the time to the second
        Assert.Equal(0, AscMhlTool.Run("ascmhl.exe", "create", "-h", "xxh64", t.Card).Exit); // offloaded again elsewhere

        BackupPlan plan = BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "second");
        Assert.Null(plan.TopUp);
        Assert.Contains("ASC MHL history", plan.TopUpRefusal);
    }
}
