using IvarOffload.App;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// Results that must not look finished while they are not: a memory card sorted anyway, files an ended job left of a
/// clip, online-only videos, a renamed job folder, and the undo questions.
/// </summary>
public partial class GuiLogicTests
{
    private const string Card = "F: SONY_A7IV";

    // ---- A memory card or camera drive that was sorted anyway ---------------------------------------------------------

    [Fact]
    public void A_memory_card_sorted_anyway_is_never_green_and_says_to_back_it_up_first()
    {
        // The videos went to another drive: the photos (and everything else) are still only on the card.
        JobState job = Job(JobKind.Sort, "completed", TransferMethod.Copy, Item(@"PRIVATE\M4ROOT\CLIP\C0001.MP4", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [File(@"DCIM\100MSDCF\DSC00001.ARW", MediaSide.Photo)], memoryCard: Card);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, rescan));

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Done - 1 video moved", view.Title);
        Assert.Equal("Left in the source: nothing that should move - check before deleting or formatting anything.", view.LeftLine);
        Assert.Equal("F: SONY_A7IV looks like a memory card or camera drive. The photos and everything else that stayed are still only on it, "
            + "unless you copied them elsewhere - copy the card to another drive and check the copy before you format it.", view.Alarm);
        Assert.Equal("Finished. Back up F: SONY_A7IV before you format it - see above.", view.Status);
    }

    [Fact]
    public void A_memory_card_sorted_into_a_folder_on_itself_says_nothing_left_the_card()
    {
        JobState job = Job(JobKind.Sort, "completed", TransferMethod.Rename, Item(@"PRIVATE\M4ROOT\CLIP\C0001.MP4", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [File(@"DCIM\100MSDCF\DSC00001.ARW", MediaSide.Photo)], memoryCard: Card, method: TransferMethod.Rename);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, rescan));

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal($"F: SONY_A7IV looks like a memory card or camera drive, and the videos were only moved to {Target} on the same drive. "
            + "Everything is still only on it, unless you copied it elsewhere - copy the card to another drive and check the copy before you format it.", view.Alarm);
    }

    [Fact]
    public void The_card_warning_stays_when_the_source_cannot_be_checked_after_a_stop_and_after_verify()
    {
        JobState job = Job(JobKind.Sort, "completed", TransferMethod.Copy, Item(@"CLIP\C0001.MP4", ItemStage.Done));
        ResultView unreachable = RunOutcome.Describe(Result(job, RunStatus.Completed), job,
            SourceCheck.ForSort(job, null, "the source folder can't be reached", memoryCard: Card));
        Assert.Equal(ResultTone.Attention, unreachable.Tone);
        Assert.StartsWith("F: SONY_A7IV looks like a memory card", unreachable.Alarm);

        JobState stopped = Job(JobKind.Sort, null, TransferMethod.Copy, Item(@"CLIP\C0001.MP4", ItemStage.Done), Item(@"CLIP\C0002.MP4", ItemStage.Pending));
        Assert.StartsWith("F: SONY_A7IV looks like a memory card",
            RunOutcome.Describe(Result(stopped, RunStatus.Stopped), stopped, SourceCheck.ForJob(stopped, memoryCard: Card)).Alarm);

        ResultView card = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, NothingLeft, memoryCard: Card));
        ResultView verified = VerifyOutcome.Show(new VerifyResult(1, 1, []), card);
        Assert.Equal(ResultTone.Attention, verified.Tone);
        Assert.Equal(card.Alarm, verified.Alarm);

        // An undo puts files back; its result has no card warning.
        JobState undo = Job(JobKind.Undo, "completed", Item(@"CLIP\C0001.MP4", ItemStage.Done));
        ResultView undone = RunOutcome.Describe(Result(undo, RunStatus.Completed), undo, SourceCheck.ForJob(undo, memoryCard: Card));
        Assert.Equal(ResultTone.Good, undone.Tone);
        Assert.Null(undone.Alarm);
    }

    [Fact]
    public void The_memory_card_warning_is_next_to_Confirm_so_it_can_never_be_scrolled_away()
    {
        string warning = SourceGuards.MemoryCardWarning(Card);
        List<PlanMessage> messages = [new(MessageLevel.Warning, warning), new(MessageLevel.Warning, "2 folders will be split")];
        MovePlan elsewhere = Plan([File(@"CLIP\C0001.MP4")], memoryCard: Card, messages: messages);

        Assert.Equal(warning, PlanFacts.CardLine(elsewhere));
        Assert.Equal(["2 folders will be split"], PlanFacts.ShownMessages(elsewhere).Select(m => m.Text)); // not twice
        Assert.False(PlanFacts.DestinationOnCard(elsewhere));
        Assert.Null(PlanFacts.DestinationWarning(elsewhere));

        MovePlan onCard = Plan([File(@"CLIP\C0001.MP4")], memoryCard: Card, messages: messages, method: TransferMethod.Rename);
        Assert.True(PlanFacts.DestinationOnCard(onCard));
        Assert.Equal(warning + " The target is on it too, so nothing leaves the card.", PlanFacts.CardLine(onCard));
        Assert.Equal("The target is on the memory card or camera drive itself: nothing leaves it.", PlanFacts.DestinationWarning(onCard));

        MovePlan plain = Plan([File(@"CLIP\C0001.MP4")], messages: messages);
        Assert.Null(PlanFacts.CardLine(plain));
        Assert.Equal(2, PlanFacts.ShownMessages(plain).Count);
    }

    [Fact]
    public void The_camera_folders_of_a_card_are_not_offered_for_removal()
    {
        Assert.Equal([@"Stills\A", "Export"], PlanFacts.WithoutCardFolders(
            [@"DCIM\100MSDCF", "DCIM", @"PRIVATE\M4ROOT\CLIP", @"PRIVATE\M4ROOT", "PRIVATE", @"Stills\A", "Export", @"AVCHD\BDMV", @"Backup\M4ROOT\CLIP"], @"F:\"));

        // RED and ARRI cards: their clip and reel folders are the camera's too.
        Assert.Equal(["Export"], PlanFacts.WithoutCardFolders(
            [@"A001_0101XY.RDM\A001_C001_0101AB.RDC", "A001_0101XY.RDM", "A016R1K4", @"A016R1K4\A016C001_120126_R1K4", "Export"], @"F:\"));

        // A source inside a card folder (F:\DCIM is flagged too): its folders are judged by their path on the card.
        Assert.Empty(PlanFacts.WithoutCardFolders(["100MSDCF", "101MSDCF", @"100MSDCF\Sub"], @"F:\DCIM"));
        Assert.Empty(PlanFacts.WithoutCardFolders(["100MSDCF"], @"F:\Shoot\..\DCIM\"));
        Assert.Empty(PlanFacts.WithoutCardFolders(["CLIP", "THMBNL"], @"F:\PRIVATE\M4ROOT"));
        Assert.Equal(["CLIP"], PlanFacts.WithoutCardFolders(["CLIP"], @"F:\Shoot")); // a plain folder of that name elsewhere

        // A camera drive sorted from a subfolder that holds the card's layout: judged from the source too.
        Assert.Equal(["Export"], PlanFacts.WithoutCardFolders(["PRIVATE", @"DCIM\100MSDCF", "Export"], @"G:\Cards\A7IV"));
    }

    // ---- What an ended job left of a clip ------------------------------------------------------------------------------

    /// <summary>A CinemaDNG clip (12 frames and its sound) and one MP4 clip.</summary>
    private static void AddClips(TestTree t)
    {
        for (int n = 0; n < 12; n++) t.Add($@"Video\A001_C003\A001_C003_{n:D6}.dng", 300);
        t.Add(@"Video\A001_C003\A001_C003.wav", 300);
        t.Add(@"Video\B\C0001.MP4", 300);
    }

    /// <summary>Sorts the clips and ends the job after 9 of the 12 frames moved ("End job without moving the rest").</summary>
    private static JobState EndedMidClip(TestTree t)
    {
        MovePlan plan = t.Plan();
        Assert.Equal("Move 2 videos (14 files)", PlanFacts.ConfirmText(plan));
        Assert.Throws<SimulatedCrash>(() => t.Run(plan, new CrashAt("after-rename", 9)));
        using (JobRunner runner = JobRunner.Open(t.Journal)) Assert.Equal(RunStatus.Closed, runner.Close().Status);
        JobState job = JournalReader.Read(t.Journal);
        Assert.Equal(9, job.DoneCount);
        Assert.Equal(5, job.StillInSourceCount);
        return job;
    }

    [Fact]
    public void After_a_job_ended_mid_clip_Move_the_remaining_moves_the_whole_rest_of_the_clip()
    {
        using var t = new TestTree();
        AddClips(t);
        JobState job = EndedMidClip(t);

        // On their own the last 3 frames look like photos, but the job planned them as part of the clip.
        (SourceCheck check, MovePlan? plan) = SourceCheck.Rescan(job, null, default);
        Assert.Equal(5, check.Left.Count);
        Assert.Equal("Ended - 2 videos (5 files) were not moved and are still in the source",
            RunOutcome.Describe(Result(job, RunStatus.Closed), job, check).Title);
        Assert.NotNull(plan);
        Assert.Equal(5, check.MovableNow);
        Assert.Equal("Move the remaining 2 videos (5 files)", PlanFacts.ConfirmText(plan, remaining: true));

        RunResult second = t.Run(plan);
        JobState next = JournalReader.Read(second.JournalPath);
        Assert.Equal(5, next.DoneCount);
        Assert.Empty(JobTestKit.Files(Path.Join(t.Source, "Video")));
        (SourceCheck after, _) = SourceCheck.Rescan(next, null, default);
        Assert.Equal(ResultTone.Good, RunOutcome.Describe(second, next, after).Tone);
    }

    [Fact]
    public void Files_an_ended_job_left_keep_the_next_result_amber_even_when_they_now_look_like_photos()
    {
        using var t = new TestTree();
        AddClips(t);
        EndedMidClip(t);

        // A job made from a plain preview of the folder (the command line, for example) moves only the sound and the MP4.
        MovePlan fresh = t.Plan();
        Assert.Equal("Move 1 video (2 files)", PlanFacts.ConfirmText(fresh));
        RunResult r = t.Run(fresh);
        JobState second = JournalReader.Read(r.JournalPath);
        Assert.True(r.NothingLeftBehind);

        (SourceCheck check, MovePlan? plan) = SourceCheck.Rescan(second, null, default);
        ResultView view = RunOutcome.Describe(r, second, check);
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Finished - 1 video (3 files) is still in the source", view.Title);
        Assert.Equal(3, check.Left.Count);
        Assert.All(check.Left, f => Assert.Equal(SourceCheck.EarlierJobReason + " - it can move now", f.Reason));
        Assert.Equal("Move the remaining 1 video (3 files)", PlanFacts.ConfirmText(plan!, remaining: true));
    }

    [Fact]
    public void A_new_preview_of_the_folder_includes_what_an_ended_job_left_of_a_clip()
    {
        using var t = new TestTree();
        AddClips(t);
        JobState first = EndedMidClip(t);

        MovePlan raw = t.Plan();
        List<JobState> earlier = Leftovers.EarlierSorts(raw.SourceRoot, raw.TargetRoot, raw.Mode);
        Assert.Equal(first.Header.Id, Assert.Single(earlier).Header.Id);
        MovePlan plan = Leftovers.Include(raw, earlier, rel => System.IO.File.Exists(Path.Join(raw.TargetRoot, rel)));

        Assert.Equal("Move 2 videos (5 files)", PlanFacts.ConfirmText(plan));
        SourceFile frame = plan.ToMove.Single(f => f.Name == "A001_C003_000011.dng");
        Assert.Equal(MediaSide.Video, frame.Side);
        Assert.Equal(@"Video\A001_C003", frame.GroupKey);
        Assert.Contains("earlier sort", frame.Reason);
        Assert.DoesNotContain(plan.Staying, f => f.Name == "A001_C003_000011.dng");
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Info && m.Text.Contains("earlier sort"));

        // The "By type" and "By folder" tabs agree with Confirm: the frames move as video, they are not photos that stay.
        Assert.Contains(raw.ByType, x => x.Extension == ".dng" && x.Classification == "Photo" && !x.Moves && x.Files == 3); // on their own they would stay
        Assert.DoesNotContain(plan.ByType, x => x.Extension == ".dng" && !x.Moves);
        TypeSummary frames = plan.ByType.Single(x => x.Extension == ".dng" && x.Moves);
        Assert.Equal(("Video", 3), (frames.Classification, frames.Files));
        Assert.Equal(plan.ToMove.Count, plan.ByType.Where(x => x.Moves).Sum(x => x.Files));
        Assert.Equal(plan.Staying.Count, plan.ByType.Where(x => !x.Moves).Sum(x => x.Files));
        FolderSummary clip = plan.ByFolder.Single(f => f.Folder == @"Video\A001_C003");
        Assert.Equal((4, 0), (clip.MovingFiles, clip.StayingFiles));

        // Sorts in the other mode are not asked; once the files have moved, nothing is added any more.
        Assert.Empty(Leftovers.EarlierSorts(raw.SourceRoot, raw.TargetRoot, MoveMode.Photos));
        RunResult r = t.Run(plan);
        MovePlan again = t.Plan();
        Assert.Same(again, Leftovers.Include(again, Leftovers.EarlierSorts(raw.SourceRoot, raw.TargetRoot, raw.Mode, except: r.JournalPath), _ => false));
    }

    [Fact]
    public void Leftovers_that_move_now_update_the_folder_totals_and_the_split_folder_warning()
    {
        JobState job = Job("closed",
            Item(@"Clip\F1.dng", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Card\C0001.XML", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\F0.dng", ItemStage.Done),
            Item(@"Card\C0001.MP4", ItemStage.Done));

        // Clip: nothing moved on its own, so it was not "split"; with the frame moving now, it is.
        // Card: its clip's sidecar stayed next to the next clip (split); moving now, the folder is whole again.
        SourceFile frame = File(@"Clip\F1.dng", MediaSide.Photo);
        SourceFile readme = File(@"Clip\readme.txt", MediaSide.Neutral, FileRole.Other);
        SourceFile sidecar = File(@"Card\C0001.XML", MediaSide.Neutral, FileRole.Sidecar);
        SourceFile next = File(@"Card\C0002.MP4");
        var oldSplit = new PlanMessage(MessageLevel.Warning, "1 folder(s) will be split - files next to your videos stay behind (e.g. Card: .xml x1). "
            + "They may belong to the files that move - check them before deleting or formatting the source.");
        var other = new PlanMessage(MessageLevel.Warning, "Something else to check.");
        MovePlan plan = Plan([next], staying: [frame, readme, sidecar], messages: [oldSplit, other]);

        MovePlan with = Leftovers.Include(plan, [job], _ => false);
        Assert.Equal([@"Card\C0002.MP4", @"Clip\F1.dng", @"Card\C0001.XML"], with.ToMove.Select(f => f.RelativePath));
        Assert.Equal([
            new PlanMessage(MessageLevel.Warning, "1 folder(s) will be split - files next to your videos stay behind (e.g. Clip: .txt x1). "
                + "They may belong to the files that move - check them before deleting or formatting the source."),
            other],
            with.Messages.Where(m => m.Level == MessageLevel.Warning));
        Assert.Equal([new FolderSummary("Card", 2, 2000, 0, 0, false), new FolderSummary("Clip", 1, 1000, 1, 1000, true)], with.ByFolder);
        Assert.Equal([new TypeSummary(".mp4", "Video", true, 1, 1000), new TypeSummary(".dng", "Video", true, 1, 1000),
            new TypeSummary(".xml", "Video companion", true, 1, 1000), new TypeSummary(".txt", "Not photo/video", false, 1, 1000)], with.ByType);
    }

    [Fact]
    public void A_recording_that_moves_now_is_no_longer_counted_among_those_that_stay()
    {
        SourceFile memo = File(@"Stills\memo.wav", MediaSide.Video, FileRole.Other, FileNote.UnmatchedAudio); // video sound in Photos mode
        SourceFile voice = File(@"Stills\voice.wav", MediaSide.Neutral, FileRole.Other, FileNote.UnmatchedAudio);
        var moving = new SourceFile
        {
            RelativePath = voice.RelativePath, Size = voice.Size, CreationTime = 0, LastWriteTime = 0, Attributes = voice.Attributes,
            Side = MediaSide.Photo, Role = FileRole.Sidecar,
        };
        var before = new PlanMessage(MessageLevel.Info, "2 audio recording(s) (2 KB) with no matching photo count as video sound and stay: Stills.");

        Leftovers.Summary s = Leftovers.Summarize([memo, moving], new HashSet<SourceFile> { moving }, MoveMode.Photos, [before]);
        PlanMessage after = Assert.Single(s.Messages);
        Assert.StartsWith("1 audio recording(s) (", after.Text);
        Assert.EndsWith(") with no matching photo count as video sound and stay: Stills.", after.Text);

        Assert.Empty(Leftovers.Summarize([moving], new HashSet<SourceFile> { moving }, MoveMode.Photos, [before]).Messages);
    }

    /// <summary>
    /// The totals and messages Leftovers works out again are those the Planner writes: on a plan with nothing added
    /// they come out the same, in both modes (so a change to the Planner's wording can't drift apart unnoticed).
    /// </summary>
    [Theory]
    [InlineData(MoveMode.Videos)]
    [InlineData(MoveMode.Photos)]
    public void The_leftover_totals_and_messages_match_the_planners(MoveMode mode)
    {
        using var t = new TestTree();
        AddClips(t);
        t.Add(@"Video\B\notes.txt", 10);
        t.Add(@"Video\B\C0001.XML", 20);
        t.Add(@"Photos\DSC0001.JPG", 30);
        t.Add(@"Photos\DSC0001.ARW", 40);
        t.Add(@"Photos\memo.wav", 50);
        t.Add(@"Photos\readme.txt", 5);
        t.Add(@"Other\thing.xyz", 7);
        t.Add(@"Other\Thumbs.db", 1);
        MovePlan plan = t.Plan(mode);
        Assert.Contains(plan.Messages, m => m.Text.Contains(" will be split - "));
        Assert.Contains(plan.Messages, m => m.Text.Contains(" with no matching photo "));

        Leftovers.Summary s = Leftovers.Summarize(plan.Scan.Files, new HashSet<SourceFile>(plan.ToMove), plan.Mode, plan.Messages);
        Assert.Equal(plan.ByType, s.ByType);
        Assert.Equal(plan.ByFolder, s.ByFolder);
        Assert.Equal(plan.Messages, s.Messages);
    }

    [Fact]
    public void An_undone_sort_is_not_asked_for_what_it_left()
    {
        using var t = new TestTree();
        AddClips(t);
        JobState first = EndedMidClip(t);
        Assert.Single(Leftovers.EarlierSorts(t.Source, t.Target, MoveMode.Videos));

        using (JobRunner undo = UndoFactory.Start(first.JournalPath)) Assert.Equal(RunStatus.Completed, undo.Run().Status);
        Assert.Empty(Leftovers.EarlierSorts(t.Source, t.Target, MoveMode.Videos));
    }

    [Fact]
    public void Only_the_same_file_is_added_back_and_never_one_that_must_stay()
    {
        JobState job = Job("closed",
            Item(@"Clip\F1.dng", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\F2.dng", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\F3.dng", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\F4.dng", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\C.wav", ItemStage.Skipped, SkipReasons.Closed),
            Item(@"Clip\F0.dng", ItemStage.Done));
        SourceFile same = File(@"Clip\F1.dng", MediaSide.Photo);
        var changed = new SourceFile { RelativePath = @"Clip\F2.dng", Size = 5, CreationTime = 0, LastWriteTime = 0, Attributes = FileAttributes.Archive, Side = MediaSide.Photo };
        SourceFile online = File(@"Clip\F3.dng", MediaSide.Neutral, FileRole.Other, FileNote.OnlineOnly);
        SourceFile inTarget = File(@"Clip\F4.dng", MediaSide.Photo);
        SourceFile wav = File(@"Clip\C.wav", role: FileRole.Other, note: FileNote.UnmatchedAudio);
        MovePlan plan = Plan([wav], staying: [same, changed, online, inTarget]);

        MovePlan with = Leftovers.Include(plan, [job], rel => rel == @"Clip\F4.dng");
        Assert.Equal([@"Clip\C.wav", @"Clip\F1.dng"], with.ToMove.Select(f => f.RelativePath));
        Assert.Equal([@"Clip\F2.dng", @"Clip\F3.dng", @"Clip\F4.dng"], with.Staying.Select(f => f.RelativePath));
        Assert.Equal(MediaSide.Photo, same.Side); // the scan's own file is not changed

        MovePlan refused = Plan([wav], staying: [same], messages: [new PlanMessage(MessageLevel.Error, "The target is read-only")]);
        Assert.Same(refused, Leftovers.Include(refused, [job], _ => false));
        Assert.Same(plan, Leftovers.Include(plan, [], _ => false));
    }

    // ---- Online-only files -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_online_only_video_left_in_the_source_keeps_the_result_amber_and_says_how_to_fix_it()
    {
        JobState job = Job("completed", Item(@"Card1\C0000.MP4", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [File(@"Card1\C0003.MP4", MediaSide.Neutral, FileRole.Other, FileNote.OnlineOnly, natural: MediaSide.Video),
            File(@"Card1\P0001.JPG", MediaSide.Neutral, FileRole.Other, FileNote.OnlineOnly, natural: MediaSide.Photo)]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, check);

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Finished - 1 video is still in the source", view.Title);
        LeftFile left = Assert.Single(check.Left);
        Assert.Equal(@"Card1\C0003.MP4", left.Rel);
        Assert.Equal(SourceCheck.OnlineOnlyReason, left.Reason);
        Assert.Equal("stays", left.Status);
        Assert.Contains($"1 file: {SourceCheck.OnlineOnlyReason}", view.Reasons);
        Assert.Contains("Always keep on this device", view.Detail);
        Assert.Equal(1, check.OtherLooks); // the online-only photo stays and needs a look, nothing more
    }

    [Fact]
    public void A_real_online_only_video_is_listed_as_still_in_the_source()
    {
        using var t = new TestTree();
        t.Add(@"Card1\C0001.MP4");
        t.Add(@"Card1\C0002.MP4", attributes: FileAttributes.Offline);
        RunResult r = t.Run(t.Plan());
        JobState job = JournalReader.Read(r.JournalPath);
        (SourceCheck check, _) = SourceCheck.Rescan(job, null, default);

        Assert.True(r.NothingLeftBehind); // the job moved all it planned ...
        Assert.Equal(@"Card1\C0002.MP4", Assert.Single(check.Left).Rel); // ... but a video that was never downloaded is still there
        Assert.Equal(ResultTone.Attention, RunOutcome.Describe(r, job, check).Tone);
    }

    [Fact]
    public void Links_named_like_videos_are_mentioned_but_do_not_keep_the_result_amber()
    {
        // A link is not the media itself (the preview says links are not followed): it is worth a look, not "left behind".
        JobState job = Job("completed", Item(@"Card1\C0000.MP4", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [File(@"Card1\C0009.MP4", MediaSide.Neutral, FileRole.Other, FileNote.Link, natural: MediaSide.Video),
            File(@"Card1\C0009.SRT", MediaSide.Neutral, FileRole.Other, FileNote.Link),
            File(@"Card1\P0001.JPG", MediaSide.Neutral, FileRole.Other, FileNote.Link, natural: MediaSide.Photo)]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, check);

        Assert.Empty(check.Left);
        Assert.Equal(2, check.OtherLooks); // the video's and its companion's links, not the photo link (photos stay in Videos mode)
        Assert.NotEqual(ResultTone.Attention, view.Tone);
    }

    // ---- A job whose folder was renamed or moved ---------------------------------------------------------------------------

    [Fact]
    public void An_unfinished_job_whose_folder_was_renamed_is_not_called_not_connected()
    {
        var job = new RecentJob { JournalPath = @"Q:\x\_IVAROffload\j.journal.jsonl", JobId = "j", Source = @"F:\Shoot", Target = @"Q:\x", TargetLabel = "SONY_SSD" };
        (string title, string paths, string text) = JobTexts.Banner(job, more: 0, MissingLog.FolderMoved);

        Assert.Equal(@"Unfinished sort: its folder Q:\x was renamed or moved (SONY_SSD is connected)", title);
        Assert.StartsWith(@"F:\Shoot  →  Q:\x  ·  videos, started ", paths);
        Assert.Contains("Put the folder back where it was (or give it its old name), then press “Check again”", text);
        Assert.Contains("“Forget this job”", text);
        Assert.Equal("Unfinished sort on SONY_SSD (not connected) - connect the drive to continue", JobTexts.Banner(job, 0, MissingLog.DriveAway).Title);
    }

    [Fact]
    public void A_missing_log_on_a_drive_without_a_label_names_both_causes()
    {
        // No label was recorded (many cards and exFAT drives have none): the drive now at Q: may be another one.
        var job = new RecentJob { JournalPath = @"Q:\Sorted\Card1-Video\_IVAROffload\j.journal.jsonl", JobId = "j", Source = @"F:\Shoot", Target = @"Q:\Sorted\Card1-Video" };
        (string title, _, string text) = JobTexts.Banner(job, 0, MissingLog.Either);

        Assert.Equal(@"Unfinished sort: its log is not in Q:\Sorted\Card1-Video - Q: is not connected, or the folder was renamed or moved", title);
        Assert.StartsWith("Nothing was lost. Connect the drive, or put the folder back where it was", text);
        Assert.DoesNotContain("is connected)", title);
    }

    [Fact]
    public void The_drive_of_a_missing_log_is_here_only_when_its_letter_answers_with_the_recorded_label()
    {
        const string log = @"Q:\x\_IVAROffload\j.journal.jsonl";
        bool Exists(string p) => p == @"Q:\";
        Assert.Equal(MissingLog.FolderMoved, JobTexts.WhyLogMissing(log, "SONY_SSD", Exists, _ => "SONY_SSD"));
        Assert.Equal(MissingLog.FolderMoved, JobTexts.WhyLogMissing(log, "SONY_SSD", Exists, _ => "sony_ssd"));
        Assert.Equal(MissingLog.DriveAway, JobTexts.WhyLogMissing(log, "SONY_SSD", Exists, _ => "OTHER_CARD")); // another drive got the letter
        Assert.Equal(MissingLog.DriveAway, JobTexts.WhyLogMissing(@"R:\x\_IVAROffload\j.journal.jsonl", "SONY_SSD", Exists, _ => "SONY_SSD"));
        Assert.Equal(MissingLog.DriveAway, JobTexts.WhyLogMissing("", "SONY_SSD", Exists, _ => "SONY_SSD"));
        // Nothing proves it is the same drive: no label recorded, or none that can be read now.
        Assert.Equal(MissingLog.Either, JobTexts.WhyLogMissing(log, "", Exists, _ => "SOME_OTHER_STICK"));
        Assert.Equal(MissingLog.Either, JobTexts.WhyLogMissing(log, "SONY_SSD", Exists, _ => null));
        Assert.Equal(MissingLog.Either, JobTexts.WhyLogMissing(log, "SONY_SSD", Exists, _ => ""));
    }

    [Fact]
    public void The_undo_list_never_calls_a_connected_drive_not_connected()
    {
        var away = new JobListing { JournalPath = @"D:\Sorted\Card1-Video\_IVAROffload\j.journal.jsonl", Id = "j", Source = Source, Target = Target, TargetLabel = "T7" };
        Assert.Equal("folder renamed or moved", JobTexts.ListingStatus(away, MissingLog.FolderMoved));
        string why = JobTexts.WhyNotUndo(away, MissingLog.FolderMoved)!;
        Assert.Equal(@"This sort's log is no longer in D:\Sorted\Card1-Video (the folder was renamed or moved). "
            + "Choose the sorted folder where it is now (“Choose a folder...”), or open its log (“Open a log file...”).", why);
        Assert.Equal("not connected", JobTexts.ListingStatus(away));
        Assert.Contains("(T7) is not connected", JobTexts.WhyNotUndo(away));

        Assert.Equal("log not found", JobTexts.ListingStatus(away, MissingLog.Either));
        Assert.Equal(@"This sort's log is not in D:\Sorted\Card1-Video: its drive (T7) is not connected, or the folder was renamed or moved. "
            + "Connect the drive, or choose the sorted folder where it is now (“Choose a folder...”), or open its log (“Open a log file...”).",
            JobTexts.WhyNotUndo(away, MissingLog.Either));
    }

    [Fact]
    public void A_receipt_whose_sorted_folder_was_renamed_says_where_its_log_was_and_whether_the_drive_is_here()
    {
        using var t = new TestTree();
        t.Add(@"Card1\C0001.MP4");
        RunResult r = t.Run(t.Plan());
        string receipt = Directory.GetFiles(Path.Join(t.Source, JobPaths.LogFolderName), "*" + JobPaths.ReceiptTextSuffix).Single();
        List<string> lines = System.IO.File.ReadLines(receipt).ToList();
        string recorded = JobTexts.ReceiptLog(lines)!;
        Assert.True(JobPaths.SamePath(r.JournalPath, recorded));
        JobState job = JournalReader.Read(r.JournalPath);
        Assert.Equal(job.Header.Id, JobTexts.ReceiptJobId(lines));

        Directory.Move(t.Target, t.Target + "-Smith"); // the usual next step: the sorted folder gets a better name
        Assert.False(System.IO.File.Exists(recorded));

        // The receipt has no label: the recent-jobs list has the one the job recorded, by job id or by log path.
        List<RecentJob> recent = [new RecentJob { JobId = "other", JournalPath = @"Q:\a\_IVAROffload\other.journal.jsonl", TargetLabel = "NOPE" },
            new RecentJob { JobId = job.Header.Id, JournalPath = recorded, TargetLabel = "SORTED_SSD" }];
        Assert.Equal("SORTED_SSD", JobTexts.RecordedLabel(recent, job.Header.Id, recorded));
        Assert.Equal("SORTED_SSD", JobTexts.RecordedLabel(recent, null, recorded));
        Assert.Equal("", JobTexts.RecordedLabel(recent, "unknown", @"Q:\z\_IVAROffload\unknown.journal.jsonl"));
        Assert.Equal(MissingLog.FolderMoved, JobTexts.WhyLogMissing(recorded, "SORTED_SSD", Directory.Exists, _ => "SORTED_SSD"));
        Assert.Equal(MissingLog.Either, JobTexts.WhyLogMissing(recorded, "", Directory.Exists, _ => null)); // not remembered: can't tell

        Assert.Equal($"The job log this receipt names is no longer in {t.Target}: its drive is connected, so the sorted folder was renamed or moved. "
            + $"Choose the sorted folder where it is now (“Choose a folder...”), or open the log in its {JobPaths.LogFolderName} folder (“Open a log file...”).",
            JobTexts.ReceiptLogNotFound(recorded, MissingLog.FolderMoved));
        Assert.Equal($"The job log this receipt names is not in {t.Target}: its drive is not connected, or the sorted folder was renamed or moved. "
            + "Connect the drive and open the receipt again, or choose the sorted folder where it is now (“Choose a folder...”).",
            JobTexts.ReceiptLogNotFound(recorded, MissingLog.Either));
        Assert.StartsWith($"The job log this receipt names is in {t.Target}, and that drive is not connected.", JobTexts.ReceiptLogNotFound(recorded, MissingLog.DriveAway));
        Assert.Null(JobTexts.ReceiptLog(["Job: 123", "Job log: ", "To undo: ..."]));
    }

    // ---- Undo questions --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_undo_question_does_not_count_files_whose_original_place_is_taken()
    {
        JobState sort = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Done), Item(@"DCIM\C.MOV", ItemStage.Done));
        var preview = new UndoPreview { Original = sort, From = Target, To = Source, Method = TransferMethod.Rename };
        preview.GoingBack.Add(sort.Items[0]);
        preview.PlaceTaken.AddRange(sort.Items.Skip(1));
        (string question, string details) = JobTexts.UndoQuestion(preview);

        Assert.StartsWith(@"Move 1 video (", question);
        Assert.EndsWith(@") back to E:\Card1?", question);
        Assert.Contains("2 files have a file with the same name in their original place, so they stay in the sorted folder: "
            + "an identical file there is left as it is, and a different one is never overwritten.", details);
        Assert.True(JobTexts.UndoCanMove(preview));

        preview.GoingBack.Clear();
        (question, details) = JobTexts.UndoQuestion(preview);
        Assert.Equal(@"Nothing can go back to E:\Card1.", question);
        Assert.DoesNotContain("renamed back", details);
        Assert.False(JobTexts.UndoCanMove(preview));
    }

    [Fact]
    public void Ending_an_undo_early_talks_about_the_sorted_folder_and_undoing_the_rest()
    {
        (string title, string text) = JobTexts.EndJobQuestion(undo: true);
        Assert.Equal("Stop here", title);
        Assert.Contains("The files not moved back yet stay in the sorted folder, and nothing is deleted", text);
        Assert.Contains("“Undo a sort”", text);
        Assert.DoesNotContain("sort them later", text);

        (title, text) = JobTexts.EndJobQuestion(undo: false);
        Assert.Equal("Stop here", title);
        Assert.Contains("The files not moved yet stay in the folder to sort, and nothing is deleted", text);
        Assert.Contains("You can sort them later.", text);

        Assert.Equal("Stop here...", JobTexts.EndJobButton(undo: true));
        Assert.Equal("Stop here...", JobTexts.EndJobButton(undo: false));
        JobState undo = Job(JobKind.Undo, null, Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Pending));
        Assert.Contains("“Stop here...” ends it and leaves the rest in the sorted folder - nothing is deleted.", JobTexts.Banner(undo, 0).Text);
    }
}
