using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// Where an undo puts the files: only into the folder they came from, proven by the sort's receipt (or, for older sorts,
/// by what the sort recorded) - never into another folder that merely has its name or letter. Also: finding a sort again
/// after its folders were renamed, and undo of clips whose companions are gone.
/// </summary>
public class UndoWhereTests
{
    private static void Sample(TestTree t)
    {
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.MOV", 200_000);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.JPG", 4000);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0002.MOV", 150_000);
        t.Add(@"Card1\ZOOM0001.WAV", 30_000);
        t.Add(@"Other\C0001.MP4", 64_000);
    }

    private static void AssertBackIn(string folder, Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> before)
    {
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), JobTestKit.Files(folder).Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string rel, var fingerprint) in before) Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(folder, rel)));
    }

    // ---- Renamed and reused source folders (the files never go into another shoot) -------------------------------------

    [Fact]
    public void Undo_goes_to_the_renamed_folder_not_to_a_new_folder_that_took_its_name()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        // Monday's folder is renamed after the shoot, and Tuesday's card is dumped into a new folder with the old name.
        string monday = Path.Join(t.Root, "Smith-2023-03-19");
        MoveFolder(t.Source, monday);
        Directory.CreateDirectory(Path.Join(t.Source, "Other"));
        File.WriteAllText(Path.Join(t.Source, @"Other\C0001.MP4"), "Tuesday's clip with Monday's name");
        File.WriteAllText(Path.Join(t.Source, "TUESDAY.MOV"), "Tuesday");
        var tuesday = Fingerprints(t.Source);

        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Null(preview.Blocked);
        Assert.True(JobPaths.SamePath(monday, preview.To), preview.To);
        // Opened from the receipt in the renamed folder: the same place, and the note does not claim Tuesday's folder is it.
        UndoPreview opened = UndoFactory.Preview(t.Journal, startedFrom: JobPaths.ReceiptTextPath(monday, id));
        Assert.True(JobPaths.SamePath(monday, opened.To), opened.To);
        Assert.Contains($"{t.Source} can't be confirmed as the folder the files came from", opened.ToNote);
        Assert.False(preview.DriveLetterChanged);
        Assert.False(preview.CreatesFolder);
        Assert.Contains("which holds this sort's receipt", preview.ToNote);
        Assert.Contains(preview.ToNote!, preview.Describe());
        Assert.Empty(preview.PlaceTaken); // Tuesday's C0001.MP4 is not in the way: it is in another folder

        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(monday, before);
        Assert.Equal(tuesday, Fingerprints(t.Source)); // Tuesday's folder is untouched...
        Assert.False(Directory.Exists(JobPaths.LogFolder(t.Source)), "...and gets no logs or receipts of Monday's sort");
        string receipt = File.ReadAllText(JobPaths.ReceiptTextPath(monday, id)); // brought up to date where it is, not in the new folder
        Assert.Contains("This sort was undone on", receipt);
        Assert.Contains("its log is next to this file", receipt);
    }

    [Fact]
    public void Undo_started_from_the_receipt_in_a_renamed_folder_goes_back_there()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        // Renamed into another folder (not next to where it was): only the receipt the user opened can say where it is.
        string archive = Path.Join(t.Root, @"Archive\2023-03-19 Smith");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        MoveFolder(t.Source, archive);
        string receipt = JobPaths.ReceiptTextPath(archive, id);

        string journal = UndoFactory.ResolveJournal(receipt)!;
        Assert.Equal(Path.GetFullPath(t.Journal), journal);
        // Not knowing where it went, the undo would recreate the folder (and says so); opened from the receipt, it goes there.
        UndoPreview blind = UndoFactory.Preview(journal);
        Assert.True(blind.CreatesFolder);
        Assert.Contains($"{t.Source} no longer exists; it will be created", string.Join("\n", blind.Describe()));
        UndoPreview preview = UndoFactory.Preview(journal, startedFrom: receipt);
        Assert.Null(preview.Blocked);
        Assert.True(JobPaths.SamePath(archive, preview.To));
        Assert.False(preview.CreatesFolder);

        using (JobRunner runner = UndoFactory.Start(journal, receipt, null)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(archive, before);
        Assert.False(Directory.Exists(t.Source), "the old folder is not recreated");
    }

    [Fact]
    public void Undo_started_from_the_receipt_in_a_copy_of_the_folder_puts_the_files_back_into_the_original()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        // A backup or archive copy of the card folder: its receipt is copied with it. The original is still in its place.
        string copy = Path.Join(t.Root, "INGEST - Copy");
        CopyFolder(t.Source, copy);
        var copyBefore = Fingerprints(copy);
        string receipt = JobPaths.ReceiptTextPath(copy, id);
        Assert.True(File.Exists(receipt));
        string journal = UndoFactory.ResolveJournal(receipt)!;
        Assert.Equal(Path.GetFullPath(t.Journal), journal);

        UndoPreview preview = UndoFactory.Preview(journal, startedFrom: receipt);
        Assert.Null(preview.Blocked);
        Assert.True(JobPaths.SamePath(t.Source, preview.To), preview.To);
        Assert.Contains($"{copy} holds a copy of the receipt", preview.ToNote);
        Assert.DoesNotContain("renamed or moved", preview.ToNote);
        Assert.Equal(preview.GoingBack.Count, UndoFactory.Preview(journal).GoingBack.Count);

        using (JobRunner runner = UndoFactory.Start(journal, receipt, null)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(t.Source, before);
        Assert.Equal(copyBefore, Fingerprints(copy)); // the copy is left as it is
        Assert.Contains("This sort was undone on", File.ReadAllText(JobPaths.ReceiptTextPath(t.Source, id)));
        Assert.DoesNotContain("This sort was undone on", File.ReadAllText(receipt));
    }

    [Fact]
    public void Two_folders_that_could_each_be_the_original_are_left_to_the_user()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        string copy = Path.Join(t.Root, "INGEST - Copy");
        CopyFolder(t.Source, copy);
        // The original lost its receipt; it still matches what the sort recorded, and the copy holds the receipt.
        Directory.Delete(JobPaths.LogFolder(t.Source), recursive: true);
        var copyBefore = Fingerprints(copy);
        string receipt = JobPaths.ReceiptTextPath(copy, id);

        Assert.True(JobPaths.SamePath(t.Source, UndoFactory.Preview(t.Journal).To)); // not started from the copy: the original, as before
        UndoPreview refused = UndoFactory.Preview(t.Journal, startedFrom: receipt);
        Assert.True(refused.NeedsFolder, refused.Blocked);
        Assert.Contains("Two folders could be the one the files came from", refused.Blocked);
        Assert.Contains(copy, refused.Blocked);
        Assert.Contains(t.Source, refused.Blocked);
        Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal, receipt));

        using (JobRunner runner = UndoFactory.Start(t.Journal, receipt, t.Source)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(t.Source, before);
        Assert.Equal(copyBefore, Fingerprints(copy));
    }

    [Fact]
    public void A_folder_with_the_old_name_but_no_proof_is_refused_and_the_user_can_choose_the_folder()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string elsewhere = Path.Join(t.Root, @"Archive\Monday");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        MoveFolder(t.Source, elsewhere);
        Directory.CreateDirectory(t.Source); // a new, empty folder with the old name
        Directory.SetCreationTimeUtc(t.Source, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        UndoPreview refused = UndoFactory.Preview(t.Journal);
        Assert.True(refused.NeedsFolder);
        Assert.Contains($"The folder {t.Source} is not the one the files came from", refused.Blocked);
        Assert.Contains("Choose the folder the files came from", refused.Blocked);
        Assert.Contains("is not the one the files came from", Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal)).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(t.Source)); // nothing was written into it

        UndoPreview chosen = UndoFactory.Preview(t.Journal, startedFrom: null, putBackTo: elsewhere);
        Assert.Null(chosen.Blocked);
        Assert.Contains("the folder you chose", chosen.ToNote);
        Assert.Contains("the sorted folder", UndoFactory.Preview(t.Journal, null, putBackTo: t.Target).Blocked);
        using (JobRunner runner = UndoFactory.Start(t.Journal, null, elsewhere)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(elsewhere, before);
    }

    // ---- Sorts that recorded no drive serial number (older sorts, subst drives) ---------------------------------------

    /// <summary>What a sort made by Ingest Sorter, or from a subst drive, leaves: no serial number for the source drive.</summary>
    private static void ForgetSourceSerial(string journal) => RewriteHeader(journal, h => h.AsObject().Remove("srcSerial"));

    [Fact]
    public void Without_a_serial_number_a_missing_folder_is_never_created_on_whatever_drive_has_the_letter()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        Directory.Delete(t.Source, recursive: true); // the folder is gone (or another card sits under the letter)

        // With the serial number the drive is recognised, so the folder can be recreated there.
        UndoPreview matched = UndoFactory.Preview(t.Journal);
        Assert.Null(matched.Blocked);
        Assert.True(matched.CreatesFolder);
        Assert.Contains("(on the drive the files came from)", string.Join("\n", matched.Describe()));

        ForgetSourceSerial(t.Journal);
        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.False(preview.CreatesFolder);
        Assert.True(preview.NeedsFolder);
        Assert.Contains("can't be confirmed as the drive the files came from", preview.Blocked);
        Assert.Throws<JournalException>(() => UndoFactory.CreateUndoJournal(t.Journal));
        Assert.False(Directory.Exists(t.Source), "the folder was created on an unmatched drive");
    }

    [Fact]
    public void Without_a_serial_number_the_folder_must_prove_it_is_the_one_the_files_came_from()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        ForgetSourceSerial(t.Journal);

        // The receipt proves it.
        Assert.Null(UndoFactory.Preview(t.Journal).Blocked);

        // Without the receipt (a sort by Ingest Sorter left none), the folders the sort recorded, with their original
        // creation times, prove it too.
        string receipts = JobPaths.LogFolder(t.Source);
        MoveFolder(receipts, Path.Join(t.Root, "receipts-aside"));
        Assert.Null(UndoFactory.Preview(t.Journal).Blocked);

        // A folder that only has the same name (recreated, with other creation times) proves nothing.
        string aside = Path.Join(t.Root, "INGEST-real");
        MoveFolder(t.Source, aside);
        foreach (string rel in new[] { @"Card1\DCIM\100MEDIA", "Other" })
        {
            Directory.CreateDirectory(Path.Join(t.Source, rel));
            for (string? d = rel; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                Directory.SetCreationTimeUtc(Path.Join(t.Source, d), new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        }
        UndoPreview refused = UndoFactory.Preview(t.Journal);
        Assert.True(refused.NeedsFolder, refused.Blocked);
        Assert.Contains("is not the one the files came from", refused.Blocked);

        Directory.Delete(t.Source, recursive: true);
        MoveFolder(aside, t.Source);
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(t.Source, before);
    }

    // ---- Groups: every file that can go back goes back ---------------------------------------------------------------

    [Fact]
    public void Undo_puts_back_every_file_that_can_go_back_when_companions_of_a_clip_are_gone()
    {
        using var t = new TestTree();
        foreach (string clip in new[] { "DJI_0001", "DJI_0002", "DJI_0003" })
        {
            t.Add($@"Drone\DCIM\100MEDIA\{clip}.MP4", 60_000);
            t.Add($@"Drone\DCIM\100MEDIA\{clip}.LRF", 20_000);
            t.Add($@"Drone\DCIM\100MEDIA\{clip}.SRT", 900);
        }
        foreach (string rel in new[] { @"CLIP\C0001.MP4", @"CLIP\C0001M01.XML", @"CLIP\C0002.MP4", @"CLIP\C0002M01.XML", "MEDIAPRO.XML", "STATUS.BIN", @"THMBNL\C0001T01.JPG" })
            t.Add($@"Sony\PRIVATE\M4ROOT\{rel}", rel.EndsWith(".MP4", StringComparison.Ordinal) ? 70_000 : 800);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        JobState sort = JournalReader.Read(t.Journal);
        Assert.Equal(before.Count, sort.DoneCount);
        Assert.Contains(sort.Items, i => i.Rel.EndsWith("DJI_0001.MP4", StringComparison.Ordinal) && i.Group.Length > 0);

        // The DJI proxies were deleted after the sort (common), and one clip XML of the Sony card structure too.
        string[] gone = [@"Drone\DCIM\100MEDIA\DJI_0001.LRF", @"Drone\DCIM\100MEDIA\DJI_0002.LRF", @"Drone\DCIM\100MEDIA\DJI_0003.LRF", @"Sony\PRIVATE\M4ROOT\CLIP\C0001M01.XML"];
        foreach (string rel in gone) File.Delete(Path.Join(t.Target, rel));

        UndoPreview preview = UndoFactory.Preview(t.Journal);
        Assert.Equal(before.Count - gone.Length, preview.GoingBack.Count);
        Assert.Equal(gone.Length, preview.CannotGoBack.Count);
        RunResult undo;
        using (JobRunner runner = UndoFactory.Start(t.Journal)) undo = runner.Run();
        Assert.Equal(RunStatus.Completed, undo.Status);
        Assert.Equal(preview.GoingBack.Count, undo.Moved); // what the question promised is what happens
        Assert.Empty(JobTestKit.Files(t.Target)); // nothing that could go back stayed behind
        foreach ((string rel, var fingerprint) in before.Where(b => !gone.Contains(b.Key)))
            Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(t.Source, rel)));

        // The sort is "partly undone", and the rest can be tried again (the preview then says why nothing can go back).
        JobListing listing = JobCatalog.ForFolder(t.Target).Single(j => j.Kind == JobKind.Sort);
        Assert.True(listing.PartlyUndone);
        Assert.True(listing.CanUndo);
        Assert.Equal(gone.Length, listing.UndoneBy!.NotMovedBack);
        Assert.False(UndoFactory.Preview(t.Journal).CanRun);
    }

    [Fact]
    public void A_complete_undo_leaves_the_sort_undone_and_not_offered_again()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        using (JobRunner runner = UndoFactory.Start(t.Journal)) Assert.True(runner.Run().NothingLeftBehind);
        JobListing listing = JobCatalog.ForFolder(t.Target).Single(j => j.Kind == JobKind.Sort);
        Assert.False(listing.PartlyUndone);
        Assert.False(listing.CanUndo);
        Assert.Equal(0, listing.UndoneBy!.NotMovedBack);
    }

    // ---- Finding a sort after its sorted folder was renamed ------------------------------------------------------------

    [Fact]
    public void A_sort_is_found_from_its_receipt_after_the_sorted_folder_was_renamed()
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        string renamed = Path.Join(t.Root, "Smith-Video");
        MoveFolder(t.Target, renamed);
        string journal = Path.Join(renamed, JobPaths.LogFolderName, id + JobPaths.JournalSuffix);

        string receipt = JobPaths.ReceiptTextPath(t.Source, id);
        Assert.Equal(journal, UndoFactory.ResolveJournal(receipt));
        JobListing listing = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.True(listing.IsConnected);
        Assert.True(listing.CanUndo);
        Assert.Equal(journal, listing.JournalPath);

        using (JobRunner runner = UndoFactory.Start(journal, receipt, null)) Assert.True(runner.Run().NothingLeftBehind);
        AssertBackIn(t.Source, before);
    }

    [Fact]
    public void A_sorted_folder_that_is_gone_while_its_drive_is_there_is_never_called_not_connected()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        string journal = t.Journal;
        // Moved somewhere it can't be found (not next to where it was).
        Directory.CreateDirectory(Path.Join(t.Source, @"Card1\deep\inside"));
        MoveFolder(t.Target, Path.Join(t.Source, @"Card1\deep\inside\Sorted"));

        string receipt = JobPaths.ReceiptTextPath(t.Source, id);
        Assert.Null(UndoFactory.ResolveJournal(receipt, out string? why));
        Assert.Contains($"The job log is no longer at {journal}", why);
        Assert.Contains("is connected, so the sorted folder was probably renamed, moved or deleted", why);
        Assert.DoesNotContain("not connected", why);

        JobListing listing = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.False(listing.IsConnected);
        Assert.True(listing.LogMissing);
        Assert.Equal("log not found", listing.Status);
        Assert.Equal(id, listing.Id);
        Assert.DoesNotContain("not connected", listing.WhyNotReachable);

        uint serial = JournalReader.Read(Path.Join(t.Source, @"Card1\deep\inside\Sorted", JobPaths.LogFolderName, id + JobPaths.JournalSuffix)).Header.TargetSerial;
        var remembered = new RecentJob { JournalPath = journal, JobId = id, Target = t.Target, TargetSerial = serial };
        Assert.True(remembered.LogMissing);
        Assert.Contains("is connected, so the sorted folder was probably renamed, moved or deleted", remembered.WhyNotReachable);
        // Remembered without the drive's serial number (an older entry): the drive at the letter may be another one.
        var older = new RecentJob { JournalPath = journal, JobId = id, Target = t.Target };
        Assert.Contains("it is not known whether it is the drive that holds the log", older.WhyNotReachable);
        Assert.Contains("renamed, moved or deleted", older.WhyNotReachable);
        Assert.DoesNotContain("is connected, so", older.WhyNotReachable);
        // Another drive with the log's letter: the log's drive is not connected.
        var otherDrive = new RecentJob { JournalPath = journal, JobId = id, Target = t.Target, TargetSerial = serial ^ 0x5A5A5A5A };
        Assert.False(otherDrive.LogMissing);
        Assert.Contains("is not connected", otherDrive.WhyNotReachable);
        var away = new RecentJob { JournalPath = OnMissingDrive(journal), JobId = id, Target = OnMissingDrive(t.Target), TargetLabel = "T7" };
        Assert.False(away.LogMissing);
        Assert.Contains("(T7) is not connected", away.WhyNotReachable);
    }
}

/// <summary>Tests that read and write the per-user recent-jobs list; run on their own (the list's folder is process-wide).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecentJobsCollection
{
    public const string Name = "recent jobs";
}

[Collection(RecentJobsCollection.Name)]
public class RecentJobsTests
{
    [Fact]
    public void An_unfinished_job_whose_sorted_folder_was_renamed_is_found_again_not_shown_as_not_connected()
    {
        using var t = new TestTree();
        t.Add(@"a\C0001.MOV", 50_000);
        t.Add(@"a\C0002.MOV", 60_000);
        t.Add(@"a\C0003.MOV", 70_000);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        string id = JournalReader.Read(t.Journal).Header.Id;
        string renamed = Path.Join(t.Root, "Shoot-Video");
        MoveFolder(t.Target, renamed);
        string journal = Path.Join(renamed, JobPaths.LogFolderName, id + JobPaths.JournalSuffix);

        Assert.DoesNotContain(RecentJobs.NotConnected(), e => e.JobId == id);
        JobState found = Assert.Single(RecentJobs.Unfinished(), s => s.Header.Id == id);
        Assert.Equal(journal, found.JournalPath);
        Assert.Equal(journal, RecentJobs.Entries().Single(e => e.JobId == id).JournalPath); // remembered at its new place

        // Resuming it says exactly what happened (and nothing is recreated in the old place).
        RunResult halted = TestTree.Resume(journal);
        Assert.Equal(HaltReason.LogMoved, halted.Halt);
        Assert.Contains($"its log is now in {renamed}", halted.Message);
        Assert.False(Directory.Exists(t.Target));
    }

    [Fact]
    public void Without_the_log_drive_serial_number_a_missing_log_is_not_said_to_be_on_a_connected_drive()
    {
        using var t = new TestTree();
        t.Add(@"a\C0001.MOV", 50_000);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        Directory.CreateDirectory(Path.Join(t.Root, @"deep\er\than\the\search"));
        MoveFolder(t.Target, Path.Join(t.Root, @"deep\er\than\the\search\Sorted")); // further away than the search for it looks
        string receipt = JobPaths.ReceiptTextPath(t.Source, id);

        // The receipt names the log's drive: it is connected, so the folder was renamed or moved.
        Assert.Null(UndoFactory.ResolveJournal(receipt, out string? why));
        Assert.Contains("is connected, so the sorted folder was probably renamed, moved or deleted", why);

        // An older receipt without that line: this PC's recent-jobs list still knows the drive.
        File.WriteAllLines(receipt, File.ReadAllLines(receipt).Where(l => !l.StartsWith("Job log drive serial number: ", StringComparison.Ordinal)));
        Assert.Null(UndoFactory.ResolveJournal(receipt, out why));
        Assert.Contains("is connected, so the sorted folder was probably renamed, moved or deleted", why);

        // Nothing knows the drive: another drive may have the letter now, so both are said.
        RecentJobs.Forget(id);
        Assert.Null(UndoFactory.ResolveJournal(receipt, out why));
        Assert.Contains("it is not known whether it is the drive that holds the log", why);
        Assert.DoesNotContain("is connected, so", why);
        Assert.DoesNotContain("not connected", why);
        JobListing listing = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.Equal(why, listing.WhyNotReachable);
        Assert.Equal("log not found", listing.Status);
    }

    [Fact]
    public void The_list_of_a_folder_follows_a_renamed_sorted_folder()
    {
        using var t = new TestTree();
        t.Add(@"a\C0001.MOV", 50_000);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        Directory.Delete(JobPaths.LogFolder(t.Source), recursive: true); // no receipt: only the recent-jobs list knows the job
        string renamed = Path.Join(t.Root, "Renamed-Video");
        MoveFolder(t.Target, renamed);

        JobListing listing = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.Equal(id, listing.Id);
        Assert.True(listing.IsConnected);
        Assert.Equal(Path.Join(renamed, JobPaths.LogFolderName, id + JobPaths.JournalSuffix), listing.JournalPath);
    }
}
