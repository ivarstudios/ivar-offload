using IvarOffload.App;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// The app's words for what the engine now records: files that disappeared from the source (and the copies kept of
/// them), files the preview held back, videos that stay anyway (online-only, links, inside a project), the undo that
/// must be told where the files came from, and jobs whose log is not where it was.
/// </summary>
public partial class GuiLogicTests
{
    // ---- Files that disappeared from the source ------------------------------------------------------------------------

    [Fact]
    public void Missing_files_are_their_own_amber_group_and_the_copies_kept_of_them_are_named()
    {
        JobItem gone = Item(@"DCIM\B.MOV", ItemStage.Skipped, SkipReasons.SourceGone, size: 2000);
        JobItem kept = Item(@"DCIM\C.MOV", ItemStage.Pending, FailReasons.VerifiedCopyKept("C.MOV.verified-copy"), failed: true);
        kept.SetAside = @"DCIM\C.MOV.verified-copy";
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), gone, kept);
        Assert.False(job.StillInSource.Any()); // the engine does not count them as still in the source ...
        Assert.Equal(2, job.MissingCount);      // ... but as missing

        RunResult r = Result(job, RunStatus.Completed);
        SourceCheck check = SourceCheck.ForSort(job, NothingLeft);
        ResultView view = RunOutcome.Describe(r, job, check);

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Done - 1 video moved - 2 files are missing from the source", view.Title);
        Assert.Empty(check.Left);
        Assert.Equal([@"DCIM\B.MOV", @"DCIM\C.MOV"], check.Missing.Select(f => f.Rel));
        Assert.All(check.Missing, f => Assert.Equal("missing from the source", f.Status));
        Assert.Equal("Left in the source: nothing that should move. Missing: 2 files were removed from the source by something else before they could be moved - see below.",
            view.LeftLine);
        Assert.Contains("1 file: missing from the source (removed by something else) - not in the target either", view.Reasons);
        Assert.Contains("1 file: missing from the source (removed by something else) - its verified copy was kept in the target under another name "
            + "(.verified-copy), because a file with its name is there", view.Reasons);
        Assert.Contains(@"D:\Sorted\Card1-Video\DCIM\C.MOV.verified-copy", view.Detail);
        Assert.Contains("The job's summary lists them.", view.Detail);
        Assert.Equal("Finished. 2 files were missing from the source - see above.", view.Status);

        // Not green after "Verify again" either: the verification names them too.
        var verified = new VerifyResult(1, 1, ["missing (removed from the source before it was moved; not in the target either): x", "missing ...: y"]) { Missing = 2 };
        ResultView after = VerifyOutcome.Show(verified, view);
        Assert.Equal(ResultTone.Attention, after.Tone);
        Assert.Equal("Moved files are intact - 2 files are missing from the source", after.Title);
    }

    [Fact]
    public void Missing_files_never_hide_behind_what_is_left_in_the_source_and_are_never_summed_up()
    {
        var items = new List<JobItem> { Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\M.MOV", ItemStage.Skipped, SkipReasons.SourceGone) };
        for (int i = 0; i < 9; i++) items.Add(Item($@"DCIM\F{i}.MOV", ItemStage.Pending, $"problem number {i}", failed: true));
        JobState job = Job(null!, [.. items]);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.CompletedWithFailures), job, SourceCheck.ForJob(job));

        Assert.Equal("Not finished - 9 videos are still in the source - 1 file is missing from the source", view.Title);
        Assert.Contains("1 file: missing from the source (removed by something else) - not in the target either", view.Reasons);
        Assert.Contains("Missing: 1 file was removed from the source by something else", view.LeftLine);
    }

    [Fact]
    public void A_real_file_removed_before_it_was_moved_is_reported_missing_not_left_in_the_source()
    {
        using var t = new TestTree();
        t.Add(@"DCIM\A.MOV");
        string b = t.Add(@"DCIM\B.MOV");
        MovePlan plan = t.Plan();
        System.IO.File.Delete(b); // something else removes it after the preview

        RunResult r = t.Run(plan);
        JobState job = JournalReader.Read(r.JournalPath);
        Assert.Equal(1, r.MissingFromSource);
        Assert.False(r.NothingLeftBehind);
        (SourceCheck check, _) = SourceCheck.Rescan(job, null, default);
        ResultView view = RunOutcome.Describe(r, job, check);

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Done - 1 video moved - 1 file is missing from the source", view.Title);
        Assert.Empty(check.Left);
        Assert.Equal(@"DCIM\B.MOV", Assert.Single(check.Missing).Rel);
        Assert.Contains("1 file: missing from the source (removed by something else) - not in the target either", view.Reasons);
    }

    [Fact]
    public void Every_failure_that_keeps_a_copy_reads_as_plain_words()
    {
        JobItem Failed(string note, ItemStage stage = ItemStage.Pending) => Item(@"DCIM\A.MOV", stage, note, failed: true);

        // The source was not there when the job was ended: nothing was judged, and the original is probably still there.
        JobItem notChecked = Failed(FailReasons.OriginalNotChecked, ItemStage.Placed);
        Assert.False(notChecked.IsMissing);
        Assert.StartsWith("verified copy is in the target; the original could not be checked", LeftReasons.Reason(notChecked));
        Assert.Equal("copied, original not checked", LeftReasons.StatusText(notChecked, undo: false));
        Assert.Equal(FailReasons.OriginalNotChecked, LeftReasons.Detail(notChecked));
        JobItem unfinished = Failed(FailReasons.UnfinishedCopyKept("A.MOV.unverified-copy"));
        Assert.False(unfinished.IsMissing);
        Assert.Equal("not moved - the job was ended while it was being copied and the source was not there; the unfinished copy was kept in the target (.unverified-copy)",
            LeftReasons.Reason(unfinished));

        // The original is gone: what is left of it, and where.
        Assert.Equal("missing from the source (removed by something else) while it was being copied - the unchecked copy was kept in the target (.unverified-copy)",
            LeftReasons.MissingReason(Failed(FailReasons.OriginalGoneDuringCopy("A.MOV.unverified-copy")), undo: false));
        Assert.Equal("missing from the source (removed by something else) - its copy does not match the checksum and was kept in the target (.damaged-copy)",
            LeftReasons.MissingReason(Failed(FailReasons.DamagedCopyKept("A.MOV.damaged-copy")), undo: false));
        Assert.Equal("missing from the sorted folder (removed by something else) while it was being copied, and it had changed since the sort - "
            + "its copy was kept in the original folder (.unverified-copy)",
            LeftReasons.MissingReason(Failed(FailReasons.ChangedCopyKept("A.MOV.unverified-copy")), undo: true));
        Assert.Equal("missing from the source (removed by something else) - the move was interrupted, and it is not in the target either",
            LeftReasons.MissingReason(Failed(FailReasons.InNeitherPlace), undo: false));
        JobItem gone = Item(@"DCIM\A.MOV", ItemStage.Skipped, SkipReasons.SourceGone);
        Assert.Equal("missing from the source", LeftReasons.StatusText(gone, undo: false));
        Assert.Equal("missing from the sorted folder", LeftReasons.StatusText(gone, undo: true));
        Assert.Equal(LeftReasons.MissingReason(gone, undo: false), LeftReasons.Reason(gone));

        // A message only counts when it was made by that message's own words.
        Assert.True(FailReasons.IsMadeBy(FailReasons.VerifiedCopyKept("x.verified-copy-2"), FailReasons.VerifiedCopyKept));
        Assert.False(FailReasons.IsMadeBy(FailReasons.DamagedCopyKept("x"), FailReasons.VerifiedCopyKept));
        Assert.False(FailReasons.IsMadeBy(null, FailReasons.VerifiedCopyKept));
    }

    // ---- Files the preview held back --------------------------------------------------------------------------------------

    [Fact]
    public void Files_the_preview_held_back_are_listed_once_also_when_the_source_could_not_be_checked()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        job.HeldBack.Add(new HeldFile(@"DCIM\B.MOV", 3000, 0, "a DIFFERENT file with the same name is already in the target - stays"));
        job.HeldBack.Add(new HeldFile(@"DCIM\B.SRT", 10, 0, "stays with its clip"));
        RunResult r = Result(job, RunStatus.Completed);
        Assert.Equal(2, r.StillInSource);

        // The source can't be scanned again: the job's own record still names them.
        SourceCheck unreachable = SourceCheck.ForSort(job, null, "the source folder can't be reached");
        ResultView view = RunOutcome.Describe(r, job, unreachable);
        Assert.Equal([@"DCIM\B.MOV", @"DCIM\B.SRT"], unreachable.Left.Select(f => f.Rel));
        Assert.All(unreachable.Left, f => Assert.Equal((PlanFacts.ClashReason, SourceCheck.HeldBackStatus), (f.Reason, f.Status)));
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Finished - 1 video (2 files) is still in the source", view.Title);
        Assert.Equal($"2 files: {PlanFacts.ClashReason}", Assert.Single(view.Reasons));

        // Scanned again, the fresh plan holds them back too: still once each.
        MovePlan rescan = Plan([], heldBack: [File(@"DCIM\B.MOV", note: FileNote.DifferentInTarget), File(@"DCIM\B.SRT", role: FileRole.Sidecar, note: FileNote.HeldWithGroup)]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        Assert.Equal(2, check.Left.Count);
        Assert.Equal("Finished - 1 video (2 files) is still in the source", RunOutcome.Describe(r, job, check).Title);

        var verified = new VerifyResult(1, 1, ["held back by the preview (still in the source): x", "held back ...: y"]) { HeldBack = 2 };
        Assert.Equal("Moved files are intact - 2 files that should have moved are still in the source", VerifyOutcome.Describe(verified).Title);
    }

    // ---- Videos that stay anyway ----------------------------------------------------------------------------------------

    [Fact]
    public void Videos_inside_a_project_stay_by_design_and_an_undecided_online_clip_only_needs_a_look()
    {
        JobState job = Job("completed", Item(@"Card1\C0001.MP4", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [
            File(@"Edit\Media\C0002.MP4", MediaSide.Neutral, FileRole.Other, FileNote.InProject, "inside an editing/processing project (Edit.prproj) - stays", natural: MediaSide.Video),
            File(@"Edit\Edit.prproj", MediaSide.Neutral, FileRole.Other, FileNote.InProject),
            // A short clip next to a HEIC of its name, not downloaded: a Live Photo clip or a video - it can't be told.
            File(@"Phone\IMG_0501.MOV", MediaSide.Neutral, FileRole.Other, FileNote.OnlineOnly)]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, check);

        // The preview said the project keeps its media; that is not "left behind" and must not block a green result.
        Assert.Empty(check.Left);
        Assert.NotEqual(ResultTone.Attention, view.Tone);
        Assert.Equal(0, check.OnlineOnly);
        Assert.Equal(1, check.OtherLooks);
    }

    // ---- The preview's "needs a look" -----------------------------------------------------------------------------------

    [Fact]
    public void A_clip_named_like_a_photo_without_the_live_photo_tag_is_counted_in_needs_a_look()
    {
        MovePlan plan = Plan([File(@"Canon\IMG_0001.MOV", note: FileNote.NotLivePhoto)], staying: [File(@"Canon\IMG_0001.JPG", MediaSide.Photo)]);
        Attention a = PlanFacts.Attention(plan);
        Assert.Equal(1, a.Files);
        Assert.Equal("1 named like a photo but not a Live Photo clip (treated as videos)", a.Detail);
    }

    // ---- Undo: where the files go back to --------------------------------------------------------------------------------

    [Fact]
    public void An_undo_whose_folder_cannot_be_confirmed_offers_to_choose_it_and_then_says_where_the_files_go()
    {
        using var t = new TestTree();
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.MOV", 2000);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.JPG", 500);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string elsewhere = Path.Join(t.Root, @"Archive\Monday");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        JobTestKit.MoveFolder(t.Source, elsewhere);
        Directory.CreateDirectory(t.Source); // a new, empty folder with the old name
        Directory.SetCreationTimeUtc(t.Source, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        UndoPreview refused = UndoFactory.Preview(t.Journal, startedFrom: null);
        Assert.Equal(UndoStep.ChooseFolder, JobTexts.UndoNext(refused));
        (string question, string details) = JobTexts.UndoQuestion(refused);
        Assert.Equal("The folder the files came from can't be confirmed.", question);
        Assert.StartsWith(refused.Blocked!, details);
        Assert.EndsWith("you see what would go back before anything moves.", details);
        Assert.Equal("Choose the folder the files came from...", JobTexts.UndoChooseFolderButton);

        // Chosen: previewed again for that folder, and the question says why it is not the recorded one.
        UndoPreview chosen = UndoFactory.Preview(t.Journal, startedFrom: null, putBackTo: elsewhere);
        Assert.Equal(UndoStep.Ask, JobTexts.UndoNext(chosen));
        (question, details) = JobTexts.UndoQuestion(chosen);
        Assert.EndsWith($" back to {chosen.To}?", question);
        Assert.Contains(chosen.ToNote!, details);
        Assert.Contains("the folder you chose", details);
    }

    [Fact]
    public void An_undo_opened_from_the_receipt_in_a_renamed_folder_goes_back_there_and_says_so()
    {
        using var t = new TestTree();
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.MOV", 2000);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        string id = JournalReader.Read(t.Journal).Header.Id;
        string archive = Path.Join(t.Root, @"Archive\2023-03-19 Smith");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        JobTestKit.MoveFolder(t.Source, archive);
        var opened = new UndoChoice(UndoFactory.ResolveJournal(JobPaths.ReceiptTextPath(archive, id))!, JobPaths.ReceiptTextPath(archive, id));

        UndoPreview preview = UndoFactory.Preview(opened.Journal, opened.StartedFrom);
        Assert.Equal(UndoStep.Ask, JobTexts.UndoNext(preview));
        Assert.True(JobPaths.SamePath(archive, preview.To));
        (string question, string details) = JobTexts.UndoQuestion(preview);
        Assert.EndsWith($" back to {preview.To}?", question);
        if (preview.ToNote is { } note) Assert.Contains(note, details);
        Assert.DoesNotContain("will be created", details);
    }

    [Fact]
    public void Nothing_to_move_back_informs_and_a_blocked_undo_that_needs_no_folder_is_not_offered_a_folder()
    {
        JobState sort = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        var blocked = new UndoPreview { Original = sort, From = Target, To = Source, Blocked = "This sort was already undone." };
        Assert.Equal(UndoStep.Inform, JobTexts.UndoNext(blocked));
        Assert.Equal("This sort can't be undone right now.", JobTexts.UndoQuestion(blocked).Question);

        var needsFolder = new UndoPreview { Original = sort, From = Target, To = Source, Blocked = "The folder E:\\Card1 does not exist. Choose the folder the files came from.", NeedsFolder = true };
        Assert.Equal(UndoStep.ChooseFolder, JobTexts.UndoNext(needsFolder));
    }

    [Fact]
    public void A_sort_whose_undo_left_files_can_be_undone_again_and_is_listed_as_partly_undone()
    {
        JobListing Listing(JobState state) => new() { JournalPath = @"D:\x\_IVAROffload\j.journal.jsonl", Id = "j", Source = Source, Target = Target, State = state };
        JobState sort = Job("completed", Item("A.MOV", ItemStage.Done), Item("B.MOV", ItemStage.Done));

        sort.UndoneBy = new UndoMark("u", "u.journal.jsonl", null, "completed", NotMovedBack: 1); // an undo that could not move every file back
        Assert.True(sort.UndoneBy.IsPartial);
        Assert.Equal("partly undone - can undo the rest", JobTexts.ListingStatus(Listing(sort)));
        Assert.True(JobTexts.CanTryUndo(Listing(sort)));
        Assert.Null(JobTexts.WhyNotUndo(Listing(sort)));

        sort.UndoneBy = new UndoMark("u", "u.journal.jsonl", null, "completed", NotMovedBack: 0);
        Assert.Equal("undone", JobTexts.ListingStatus(Listing(sort)));
        Assert.False(JobTexts.CanTryUndo(Listing(sort)));
        Assert.StartsWith("This sort was already undone.", JobTexts.WhyNotUndo(Listing(sort)));

        sort.UndoneBy = new UndoMark("u", "u.journal.jsonl", null, "started");
        Assert.Equal("being undone", JobTexts.ListingStatus(Listing(sort)));
        Assert.False(JobTexts.CanTryUndo(Listing(sort)));
        Assert.Contains("has not ended", JobTexts.WhyNotUndo(Listing(sort)));
    }

    // ---- A job log that is not where it was --------------------------------------------------------------------------------

    [Fact]
    public void The_drive_serial_number_decides_whether_a_missing_log_is_on_a_connected_drive()
    {
        MissingLog ByLabel() => MissingLog.Either;
        Assert.Equal(MissingLog.FolderMoved, JobTexts.WhyLogMissing(0x1234ABCD, driveWithSerialIsThere: true, ByLabel));
        Assert.Equal(MissingLog.DriveAway, JobTexts.WhyLogMissing(0x1234ABCD, driveWithSerialIsThere: false, ByLabel)); // another drive at the letter
        Assert.Equal(MissingLog.Either, JobTexts.WhyLogMissing(0, driveWithSerialIsThere: true, ByLabel)); // unknown: the label decides

        // A real sort: the listing carries the serial number the job recorded, so the app can tell for sure.
        using var t = new TestTree();
        t.Add(@"DCIM\A.MOV");
        RunResult r = t.Run(t.Plan());
        JobState job = JournalReader.Read(r.JournalPath);
        JobListing listing = JobCatalog.Load(r.JournalPath)!;
        Assert.Equal(job.Header.TargetSerial, listing.TargetSerial);
        Assert.NotEqual(0u, listing.TargetSerial);

        // The sorted folder is renamed away: its drive is here, so it is not "not connected".
        Directory.CreateDirectory(Path.Join(t.Root, "elsewhere"));
        JobTestKit.MoveFolder(t.Target, Path.Join(t.Root, @"elsewhere\deeper"));
        var remembered = new RecentJob { JournalPath = r.JournalPath, JobId = job.Header.Id, Target = t.Target, TargetSerial = job.Header.TargetSerial };
        Assert.True(remembered.LogMissing);
        Assert.Equal(MissingLog.FolderMoved, JobTexts.WhyLogMissing(remembered.TargetSerial, remembered.LogMissing, ByLabel));
        var other = new RecentJob { JournalPath = r.JournalPath, JobId = job.Header.Id, Target = t.Target, TargetSerial = job.Header.TargetSerial ^ 0xFFFF };
        Assert.Equal(MissingLog.DriveAway, JobTexts.WhyLogMissing(other.TargetSerial, other.LogMissing, ByLabel));
    }
}
