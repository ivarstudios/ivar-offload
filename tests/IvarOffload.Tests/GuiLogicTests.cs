using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using IvarOffload.App;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// The GUI's decisions that must never be wrong: when the result is green, what its titles say, and what is still in
/// the source. The logic lives in the app (src/IvarOffload.App/Presentation) and is compiled into this project.
/// </summary>
public partial class GuiLogicTests
{
    private const string Source = @"E:\Card1", Target = @"D:\Sorted\Card1-Video";

    // ---- Builders -------------------------------------------------------------------------------------------------

    private static JobItem Item(string rel, ItemStage stage, string? note = null, bool failed = false, long size = 1000) => new()
    {
        Index = 0,
        Rel = rel,
        Size = size,
        CreationTime = 0,
        LastWriteTime = 0,
        Why = "",
        Stage = stage,
        Note = note,
        Failed = failed,
    };

    private static JobState Job(string end, params JobItem[] items) => Job(JobKind.Sort, end, items);

    private static JobState Job(JobKind kind, string? end, params JobItem[] items) => Job(kind, end, TransferMethod.Rename, items);

    private static JobState Job(JobKind kind, string? end, TransferMethod method, params JobItem[] items)
    {
        var state = new JobState
        {
            JournalPath = Path.Join(kind == JobKind.Undo ? Source : Target, JobPaths.LogFolderName, "job" + JobPaths.JournalSuffix),
            Header = new JobHeader
            {
                Id = "job",
                Source = kind == JobKind.Undo ? Target : Source,
                Target = kind == JobKind.Undo ? Source : Target,
                Mode = MoveMode.Videos,
                Method = method,
                Verify = true,
                CompareIds = true,
                Kind = kind,
            },
            PlanComplete = true,
            End = end is null ? null : new JournalRecord { Type = "end", What = end },
        };
        state.Items.AddRange(items);
        return state;
    }

    /// <summary>The result the engine returns for a job in this state.</summary>
    private static RunResult Result(JobState job, RunStatus status, HaltReason halt = HaltReason.None, string? message = null) =>
        new(status, job.DoneCount, job.SkippedCount, job.FailedCount, job.Items.Count(i => i.Stage == ItemStage.Pending && !i.Failed), message, job.JournalPath)
        {
            StillInSource = job.StillInSourceCount,
            StillInSourceBytes = job.StillInSourceBytes,
            HeldBack = job.HeldBack.Count,
            MissingFromSource = job.MissingCount,
            IdenticalInTarget = job.IdenticalInTargetCount,
            Halt = halt,
            Kind = job.Header.Kind,
        };

    /// <param name="natural">For a file that stays anyway (online-only, a link, in a project): the side it belongs to.</param>
    private static SourceFile File(string rel, MediaSide side = MediaSide.Video, FileRole role = FileRole.Primary, FileNote note = FileNote.None, string reason = "",
        MediaSide natural = MediaSide.Neutral) => new()
    {
        RelativePath = rel,
        Size = 1000,
        CreationTime = 0,
        LastWriteTime = 0,
        Attributes = FileAttributes.Archive,
        Side = side,
        Role = role,
        Note = note,
        Reason = reason,
        NaturalSide = natural,
    };

    /// <summary>A plan as the Planner hands it over: HeldBack files are part of Staying, identical copies part of ToMove.</summary>
    private static MovePlan Plan(IReadOnlyList<SourceFile> toMove, IReadOnlyList<SourceFile>? staying = null, IReadOnlyList<SourceFile>? identical = null,
        IReadOnlyList<SourceFile>? heldBack = null, MoveMode mode = MoveMode.Videos, List<PlanMessage>? messages = null, List<SkippedFolder>? skipped = null,
        List<string>? unreadable = null, string? memoryCard = null, TransferMethod method = TransferMethod.Copy)
    {
        staying ??= [];
        identical ??= [];
        heldBack ??= [];
        List<SourceFile> all = [.. toMove, .. staying, .. heldBack];
        return new MovePlan
        {
            Scan = new ScanResult { SourceRoot = Source, Files = all, Folders = [], SkippedFolders = skipped ?? [], Problems = unreadable ?? [] },
            SourceRoot = Source,
            TargetRoot = Target,
            Mode = mode,
            Method = method,
            MemoryCard = memoryCard,
            ToMove = toMove,
            Staying = [.. staying, .. heldBack],
            Conflicts = identical,
            IdenticalConflicts = identical,
            HeldBack = heldBack,
            // As the Planner lists them: files that stay anyway although they belong to the moving side.
            MovingSideStaying = [.. staying.Where(f => f.Side == MediaSide.Neutral && f.NaturalSide == MediaRules.SideFor(mode))],
            Messages = messages ?? [],
            ByType = [],
            ByFolder = [],
        };
    }

    private static readonly MovePlan NothingLeft = Plan([], staying: [File(@"DCIM\A.JPG", MediaSide.Photo)]);

    // ---- Result colour and titles -------------------------------------------------------------------------------

    [Fact]
    public void Green_only_when_the_job_left_nothing_and_the_fresh_scan_finds_nothing_to_move()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\A.SRT", ItemStage.Done));
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, NothingLeft));

        Assert.Equal(ResultTone.Good, view.Tone);
        Assert.Equal("Done: 1 video (2 files) moved", view.Title);
        Assert.Equal("Left in the source folder: nothing that needs to move", view.LeftLine);
        Assert.Empty(view.Reasons);
        Assert.Null(view.Alarm);
        Assert.Equal("Finished.", view.Status);
        // Where the log and the receipt are is one click away, under "Details".
        Assert.Contains(view.Technical, line => line.StartsWith(@"The log and a list of every file are in D:\Sorted\Card1-Video\_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_video_the_fresh_scan_finds_keeps_the_result_amber_and_can_move_as_a_new_job()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        MovePlan rescan = Plan([File(@"DCIM\B.MOV")]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, check);

        Assert.True(Result(job, RunStatus.Completed).NothingLeftBehind); // the job alone would have said "all good"
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Finished: 1 video is still in the source folder", view.Title);
        Assert.StartsWith("Left in the source folder: 1 video (", view.LeftLine);
        Assert.DoesNotContain(") (", view.LeftLine);
        Assert.EndsWith("). Check this file before you delete or format anything.", view.LeftLine);
        Assert.Equal($"1 file: {SourceCheck.NotInThisSort}", Assert.Single(view.Reasons));
        Assert.Equal(1, check.MovableNow);
        Assert.Contains("moves the files that can move now", view.Detail);
    }

    [Fact]
    public void Every_file_the_job_did_not_move_is_counted_once_with_its_real_reason()
    {
        const string locked = """The open operation failed for "E:\Card1\DCIM\E.MOV": The process cannot access the file because it is being used by another process. (error 32)""";
        JobState job = Job(null!,
            Item(@"DCIM\A.MOV", ItemStage.Done),
            Item(@"DCIM\B.MOV", ItemStage.Skipped, SkipReasons.DifferentInTarget),
            Item(@"DCIM\C.MOV", ItemStage.Skipped, SkipReasons.IdenticalInTarget),
            Item(@"DCIM\D.MOV", ItemStage.Placed, FailReasons.OriginalNotRemovable, failed: true),
            Item(@"DCIM\E.MOV", ItemStage.Pending, locked, failed: true));
        // The fresh scan sees B held back by the name clash, D as an identical copy (its verified copy is in the target) and E.
        SourceFile placed = File(@"DCIM\D.MOV");
        MovePlan rescan = Plan([placed, File(@"DCIM\E.MOV")], identical: [placed],
            heldBack: [File(@"DCIM\B.MOV", note: FileNote.DifferentInTarget, reason: "stays: a DIFFERENT file with the same name is already in the target folder")]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.CompletedWithFailures), job, check);

        Assert.Equal([@"DCIM\B.MOV", @"DCIM\D.MOV", @"DCIM\E.MOV"], check.Left.Select(f => f.Rel));
        Assert.Equal(1, check.MovableNow); // only E: D is an identical copy now, and B clashes
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Not finished: 3 videos are still in the source folder", view.Title);
        Assert.Contains("1 file: an identical copy was already in the target folder (skipped, nothing is missing)", view.Reasons);
        Assert.Contains("1 file: a DIFFERENT file with the same name is already in the target folder", view.Reasons);
        Assert.Contains("1 file: a checked copy is in the target folder, and the original is still in the source folder", view.Reasons);
        Assert.Contains("1 file: could not move: The process cannot access the file because it is being used by another process.", view.Reasons);
        Assert.DoesNotContain(view.Reasons, r => r.Contains("for example", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("click Resume", view.Detail);
        Assert.Equal("copied, original kept", check.Left.Single(f => f.Rel == @"DCIM\D.MOV").Status);
        Assert.Contains("could not delete the original", check.Left.Single(f => f.Rel == @"DCIM\D.MOV").Detail);
    }

    [Fact]
    public void An_ended_job_says_what_was_not_moved()
    {
        JobState job = Job("closed", Item(@"DCIM\A.MOV", ItemStage.Done),
            Item(@"DCIM\B.MOV", ItemStage.Skipped, SkipReasons.Closed), Item(@"DCIM\B.SRT", ItemStage.Skipped, SkipReasons.Closed));
        MovePlan rescan = Plan([File(@"DCIM\B.MOV"), File(@"DCIM\B.SRT", role: FileRole.Sidecar)]);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Closed), job, SourceCheck.ForSort(job, rescan));

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Ended: 1 video (2 files) did not move and is still in the source folder", view.Title);
        Assert.Equal("2 files: not moved because you ended the job early", Assert.Single(view.Reasons));
        Assert.Equal("The job ended. The files that did not move are still in the source folder.", view.Status);
    }

    [Fact]
    public void A_halted_job_shows_the_engine_message_and_a_red_line_when_the_source_reads_differently()
    {
        const string gone = @"The source drive (SONY_A was F:) is not connected, or the source folder F:\Shoot has a new name. Connect the drive again. Then click Resume. Nothing was lost.";
        JobState job = Job(null!, Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Pending));
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Halted, HaltReason.SourceNotConnected, gone), job, SourceCheck.ForJob(job));

        Assert.Equal(gone, view.Title);
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Null(view.Alarm);
        Assert.Contains("The job did not move this file yet. Resume continues from the point where the job stopped.", view.LeftLine);

        ResultView inconsistent = RunOutcome.Describe(Result(job, RunStatus.Halted, HaltReason.SourceInconsistent, "The source drive gives inconsistent data"), job, SourceCheck.ForJob(job));
        Assert.StartsWith("Do NOT format or delete the source folder:", inconsistent.Alarm);

        ResultView readOnly = RunOutcome.Describe(Result(job, RunStatus.Halted, HaltReason.OriginalsNotRemovable, "The job cannot remove the originals from the source folder"), job, SourceCheck.ForJob(job));
        Assert.Contains("end the job and keep the remaining files where they are", readOnly.Detail);
    }

    [Fact]
    public void A_stopped_job_says_it_is_safe_to_unplug_the_drives()
    {
        JobState job = Job(null!, Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Pending));
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Stopped), job, SourceCheck.ForJob(job));

        Assert.Equal("Stopped: 1 video moved so far", view.Title);
        Assert.Equal("It is now safe to disconnect the drives. Resume continues the job from the point where it stopped.", view.Detail);
        Assert.Equal("Stopped. It is now safe to disconnect the drives. Resume continues the job from the point where it stopped.", view.Status);
        Assert.Equal(ResultTone.Attention, view.Tone);
    }

    [Fact]
    public void While_the_source_is_checked_again_the_result_is_neither_green_nor_amber()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForJob(job), checking: true);

        Assert.Equal(ResultTone.Checking, view.Tone);
        Assert.Equal("Done: 1 video moved", view.Title);
        Assert.Equal("The app now checks what is left in the source folder...", view.LeftLine);

        // Something already left behind is amber at once; there is nothing to wait for.
        JobState partial = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Skipped, SkipReasons.ChangedAfterPreview));
        Assert.Equal(ResultTone.Attention, RunOutcome.Describe(Result(partial, RunStatus.Completed), partial, SourceCheck.ForJob(partial), checking: true).Tone);
    }

    [Fact]
    public void A_source_that_could_not_be_checked_again_is_never_green()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, null, "the source folder is not available"));

        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Done: 1 video moved", view.Title);
        Assert.Contains("could not check the source folder again (the source folder is not available)", view.LeftLine);
        Assert.Equal("Finished. The app could not check the source folder again. For details, see above.", view.Status);

        ResultView stopped = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, null, "the check stopped before the end"));
        Assert.Equal(ResultTone.Attention, stopped.Tone);
        Assert.Equal("Finished. The app could not check the source folder again. For details, see above.", stopped.Status);
        Assert.DoesNotContain("some files are still in", stopped.Status);
    }

    [Fact]
    public void Folders_the_fresh_scan_could_not_read_keep_the_result_amber()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        MovePlan rescan = Plan([], staying: [File(@"DCIM\A.JPG", MediaSide.Photo)],
            unreadable: [@"DCIM\101MEDIA: Access to the path is denied.", "Private: Access to the path is denied."]);
        SourceCheck check = SourceCheck.ForSort(job, rescan);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, check);

        Assert.False(check.IsChecked);
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Done: 1 video moved", view.Title); // never "Nothing that should move is left"
        Assert.Equal(@"Left in the source folder: nothing that needs to move in the folders that the app could read. But the scan could not read 2 folders (DCIM\101MEDIA, Private). "
            + "Check them before you delete or format anything.", view.LeftLine);
        Assert.Equal("Finished. The app could not read some folders in the source folder again. For details, see above.", view.Status);

        // With files left as well, the unread folders are named in the details.
        MovePlan more = Plan([File(@"DCIM\B.MOV")], unreadable: ["Private: Access to the path is denied."]);
        ResultView both = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, more));
        Assert.Equal("Finished: 1 video is still in the source folder", both.Title);
        Assert.Contains("In the source folder, the scan could not read 1 folder (Private). The app did not check the files in this folder.", both.Detail);
    }

    [Fact]
    public void Identical_copies_and_unrecognized_files_are_reported_without_turning_the_result_amber()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\C.MOV", ItemStage.Skipped, SkipReasons.IdenticalInTarget));
        SourceFile copy = File(@"DCIM\C.MOV");
        MovePlan rescan = Plan([copy], identical: [copy], staying: [File(@"DCIM\X.ABC", MediaSide.Neutral, FileRole.Other, FileNote.UnknownType)]);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, rescan));

        Assert.Equal(ResultTone.Good, view.Tone);
        Assert.Equal("Left in the source folder: nothing that needs to move. 1 unrecognized file stays there. Check it before you delete or format anything.", view.LeftLine);
        Assert.Equal("1 file: an identical copy was already in the target folder (skipped, nothing is missing)", Assert.Single(view.Reasons));
    }

    [Fact]
    public void Undo_results_talk_about_the_sorted_folder()
    {
        JobState done = Job(JobKind.Undo, "completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Done));
        ResultView view = RunOutcome.Describe(Result(done, RunStatus.Completed), done, SourceCheck.ForJob(done));
        Assert.Equal(ResultTone.Good, view.Tone);
        Assert.Equal(@"Undone: 2 videos are back in E:\Card1", view.Title);
        Assert.Equal("Left in the target folder: nothing", view.LeftLine);

        JobState partly = Job(JobKind.Undo, "completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Skipped, SkipReasons.ChangedSinceSorted));
        ResultView changed = RunOutcome.Describe(Result(partly, RunStatus.Completed), partly, SourceCheck.ForJob(partly));
        Assert.Equal(ResultTone.Attention, changed.Tone);
        Assert.Equal("Finished: 1 video did not return", changed.Title);
        Assert.EndsWith(". This file did not return.", changed.LeftLine);
        Assert.Equal("1 file: changed after the sort (the undo does not move a changed file)", Assert.Single(changed.Reasons));

        // In an undo the engine reads the sorted folder twice: that is the folder not to format.
        ResultView inconsistent = RunOutcome.Describe(Result(partly, RunStatus.Halted, HaltReason.SourceInconsistent, "The source drive gives inconsistent data"),
            partly, SourceCheck.ForJob(partly));
        Assert.StartsWith("Do NOT format or delete the target folder:", inconsistent.Alarm);
        Assert.Equal("Show files still in the target folder", RunOutcome.ShowLeftText(undo: true));
        Assert.Equal("Show files still in the source folder", RunOutcome.ShowLeftText(undo: false));
    }

    [Fact]
    public void Many_different_reasons_are_summed_up_after_a_few_lines()
    {
        var items = new List<JobItem> { Item(@"DCIM\A.MOV", ItemStage.Done) };
        for (int i = 0; i < 9; i++) items.Add(Item($@"DCIM\F{i}.MOV", ItemStage.Pending, $"problem number {i}", failed: true));
        JobState job = Job(null!, [.. items]);
        ResultView view = RunOutcome.Describe(Result(job, RunStatus.CompletedWithFailures), job, SourceCheck.ForJob(job));

        Assert.Equal(RunOutcome.MaxReasons, view.Reasons.Count);
        Assert.StartsWith("4 files: other reasons", view.Reasons[^1]);
    }

    [Theory]
    [InlineData("""The move failed for "E:\a\b.mov": Access is denied. (error 5)""", "Access is denied.")]
    [InlineData("The process cannot access the file 'E:\\a\\b.mov' because it is being used by another process.",
        "The process cannot access the file because it is being used by another process.")]
    [InlineData(FailReasons.NotMovable, FailReasons.NotMovable)]
    public void Failure_messages_lose_their_paths_so_the_same_problem_is_counted_once(string message, string plain) =>
        Assert.Equal(plain, LeftReasons.Plain(message));

    [Fact]
    public void Verifying_again_keeps_what_the_result_said_about_the_source()
    {
        JobState job = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done));
        ResultView amber = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, Plan([File(@"DCIM\B.MOV")])));
        var intact = new VerifyResult(1, 1, []);

        ResultView verified = VerifyOutcome.Show(intact, amber);
        Assert.Equal("Checked again: the moved file is the same as before", verified.Title);
        Assert.Equal(ResultTone.Attention, verified.Tone); // the moved file is fine, but a video is still in the source
        Assert.Equal(amber.LeftLine, verified.LeftLine);
        Assert.Equal(amber.Reasons, verified.Reasons);
        Assert.Contains("see above", verified.Status);

        ResultView green = RunOutcome.Describe(Result(job, RunStatus.Completed), job, SourceCheck.ForSort(job, NothingLeft));
        Assert.Equal(ResultTone.Good, VerifyOutcome.Show(intact, green).Tone);
        Assert.Equal("Left in the source folder: nothing that needs to move", VerifyOutcome.Show(intact, green).LeftLine);
        Assert.Equal(ResultTone.Attention, VerifyOutcome.Show(new VerifyResult(1, 0, ["changed: x"]), green).Tone);
        Assert.Equal(ResultTone.Attention, VerifyOutcome.Show(intact, null).Tone); // nothing known about the source

        // Files that did not move are on the card already: the details do not list their paths again.
        var notMoved = new VerifyResult(1, 1, [@"not moved (still in the source folder): E:\Card1\DCIM\B.MOV - skipped"]) { NotMoved = 1 };
        ResultView partly = VerifyOutcome.Show(notMoved, amber);
        Assert.Equal("The moved files did not change, but 1 planned file is still in the source folder", partly.Title);
        Assert.DoesNotContain(@"E:\Card1", partly.Detail);
        Assert.Equal("The moved files did not change, but 1 planned file is still in the target folder", VerifyOutcome.Show(notMoved, amber, undo: true).Title);
        Assert.Contains(@"E:\Card1", VerifyOutcome.Show(notMoved, null).Detail); // with nothing else on the card, the list is the detail
    }

    [Fact]
    public void Verification_that_finds_unmoved_files_says_the_moved_ones_are_intact()
    {
        (bool good, string title, _) = VerifyOutcome.Describe(new VerifyResult(3, 3, [@"not moved (still in the source folder): E:\x.mov - skipped"]) { NotMoved = 1 });
        Assert.False(good);
        Assert.Equal("The moved files did not change, but 1 planned file is still in the source folder", title);
        Assert.Equal("Checked again: all 3 moved files are the same as before", VerifyOutcome.Describe(new VerifyResult(3, 3, [])).Title);
    }

    // ---- Preview texts --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\Shoot\Card1", MoveMode.Videos, @"D:\Shoot\Card1-Video")]
    [InlineData(@"D:\Shoot\Card1\", MoveMode.Photos, @"D:\Shoot\Card1-Photos")]
    [InlineData(@" ""D:\Shoot\Card1"" ", MoveMode.Videos, @"D:\Shoot\Card1-Video")]
    [InlineData(@"E:\", MoveMode.Videos, @"E:\Video")]
    [InlineData("E:", MoveMode.Photos, @"E:\Photos")]
    [InlineData(@"\\nas\media", MoveMode.Videos, @"\\nas\media\Video")]
    [InlineData(@"\\nas\media\Card1", MoveMode.Videos, @"\\nas\media\Card1-Video")]
    [InlineData("", MoveMode.Videos, null)]
    public void The_target_suggestion_is_a_sibling_folder_or_a_folder_inside_a_drive_root(string source, MoveMode mode, string? expected) =>
        Assert.Equal(expected, PlanFacts.SuggestTarget(source, mode));

    [Fact]
    public void Confirm_names_what_moves_and_counts_only_files_that_really_move()
    {
        SourceFile a = File(@"DCIM\A.MOV"), srt = File(@"DCIM\A.SRT", role: FileRole.Sidecar), b = File(@"DCIM\B.MOV");
        MovePlan plan = Plan([a, srt, b], identical: [b]);
        Assert.Equal("Move 1 video (2 files)", PlanFacts.ConfirmText(plan));
        Assert.Equal("Move the remaining 1 video (2 files)", PlanFacts.ConfirmText(plan, remaining: true));
        Assert.Equal("Move 2 videos", PlanFacts.ConfirmText(Plan([a, b])));
        Assert.Equal("Move 3 photos", PlanFacts.ConfirmText(Plan([.. Enumerable.Range(1, 3).Select(i => File($"P{i}.JPG", MediaSide.Photo))], mode: MoveMode.Photos)));
        Assert.Equal(@"The videos will move to: D:\Sorted\Card1-Video", PlanFacts.Destination(plan));
    }

    [Theory]
    [InlineData(@"DCIM\100MEDIA\DJI_0001.MOV", MoveMode.Videos, @"DCIM\100MEDIA\DJI_0001.MOV")]
    [InlineData(@"DCIM\100MEDIA\._DJI_0001.MOV", MoveMode.Videos, null)]      // macOS resource file
    [InlineData(@"DCIM\100MEDIA\DJI_0001.SRT", MoveMode.Videos, null)]
    [InlineData(@"A001_C003\A001_C003_000001.dng", MoveMode.Videos, @"A001_C003\*")] // every frame of a clip: one video
    [InlineData(@"A001_C003\A001_C003_000002.DNG", MoveMode.Videos, @"A001_C003\*")]
    [InlineData(@"A001_C001.RDC\A001_C001_001.R3D", MoveMode.Videos, @"A001_C001.RDC\*")] // the parts of a RED clip
    [InlineData(@"Phone\IMG_0001.HEIC", MoveMode.Photos, @"Phone\IMG_0001.HEIC")]
    [InlineData(@"Phone\IMG_0001.MOV", MoveMode.Photos, null)]                 // a Live Photo clip goes with its photo
    [InlineData(@"DCIM\100MEDIA\DJI_0001.DNG", MoveMode.Photos, @"DCIM\100MEDIA\DJI_0001.DNG")]
    public void One_rule_counts_videos_and_photos(string rel, MoveMode mode, string? key) => Assert.Equal(key, MediaRules.CountKey(rel, mode));

    [Fact]
    public void The_preview_the_result_and_the_undo_question_count_the_same_videos()
    {
        using var t = new TestTree();
        for (int n = 0; n < 12; n++) t.Add($@"BMPCC\A001_C003_0921XY\A001_C003_0921XY_{n:D6}.dng", 300);
        t.Add(@"BMPCC\A001_C003_0921XY\A001_C003_0921XY.wav", 300);
        t.Add(@"RED\A001_C001_0101AB.RDC\A001_C001_0101AB_001.R3D", 400);
        t.Add(@"RED\A001_C001_0101AB.RDC\A001_C001_0101AB_002.R3D", 400);
        t.Add(@"DJI\DJI_0001.MOV", 500);
        t.Add(@"DJI\._DJI_0001.MOV", 50);
        t.Add(@"DJI\DJI_0001.SRT", 50);
        MovePlan plan = t.Plan();
        Assert.Equal(18, plan.FilesToTransfer);
        Assert.Equal("Move 3 videos (18 files)", PlanFacts.ConfirmText(plan));

        RunResult r = t.Run(plan);
        JobState job = JournalReader.Read(r.JournalPath);
        Assert.Equal("Done: 3 videos (18 files) moved",
            RunOutcome.Describe(r, job, SourceCheck.ForSort(job, Rescan(job))).Title);
        Assert.StartsWith("Return 3 videos (18 files, ", JobTexts.UndoQuestion(UndoFactory.Preview(r.JournalPath)).Question);
    }

    [Fact]
    public void Needs_a_look_counts_every_kind_of_attention_note()
    {
        SourceFile identical = File(@"DCIM\C.MOV");
        MovePlan plan = Plan(
            [File(@"DCIM\A.MOV"), identical, File(@"DCIM\A.XYZ", role: FileRole.Other, note: FileNote.FollowsByName), File(@"Phone\IMG_1.MOV", note: FileNote.LivePhotoTooLarge)],
            staying: [File("x.abc", MediaSide.Neutral, FileRole.Other, FileNote.UnknownType), File("a.xmp", MediaSide.Neutral, FileRole.Sidecar, FileNote.Ambiguous),
                File(@"Edit\clip.mov", MediaSide.Neutral, FileRole.Other, FileNote.InProject), File("p.jpg", MediaSide.Photo)],
            identical: [identical],
            heldBack: [File(@"DCIM\D.MOV", note: FileNote.DifferentInTarget), File(@"DCIM\D.SRT", role: FileRole.Sidecar, note: FileNote.HeldWithGroup)],
            skipped: [new SkippedFolder("Photos Library.photoslibrary", "application library - not changed") { LibraryKind = "Apple Photos library" }]);
        Attention a = PlanFacts.Attention(plan);

        Assert.Equal(8, a.Files);
        Assert.Equal("8 files", a.CountText);
        Assert.Equal("1 already in the target folder (skipped), 1 with a DIFFERENT file of the same name in the target folder, 1 that stays with its clip, "
            + "1 of an unknown type (it stays), 1 of an unknown type named like a video (it moves with that video), 1 that matches both a photo and a video, "
            + "1 inside an editing project, 1 too large for a Live Photo clip (it counts as a video), 1 application library not scanned", a.Detail);
        Assert.Equal("Nothing", PlanFacts.Attention(Plan([File("A.MOV")])).CountText);
    }

    [Fact]
    public void The_destination_line_warns_when_the_Planner_found_other_sorts_in_the_target()
    {
        using var t = new TestTree();
        string log = Path.Join(t.Root, "logs");
        Directory.CreateDirectory(log);
        string journal = Path.Join(log, "a" + JobPaths.JournalSuffix);
        System.IO.File.WriteAllText(journal,
            """{"t":"job","v":1,"id":"a","source":"E:\\Smith","target":"x","mode":"Videos","method":"Rename","at":"2025-09-25T10:00:00.000+02:00"}""" + "\n",
            new UTF8Encoding(false));
        var env = new PlanEnvironment
        {
            VolumeOf = _ => new VolumeInfo(@"D:\", 1, "NTFS", 1L << 40, 1L << 41) { Kind = DriveKind.Fixed },
            ClusterSizeOf = _ => 4096,
            CanDelete = _ => true,
            SubfolderNames = _ => [],
            FileNames = _ => [],
            FileExists = _ => false,
            SyncOf = _ => null,
            JournalsIn = _ => [journal],
            Folders = new SystemFolders(@"C:\", @"C:\Users\me", @"C:\Users\me\AppData", @"C:\Users\me\AppData\Local\Temp", [@"C:\Windows"], []),
        };
        var scan = new ScanResult
        {
            SourceRoot = @"D:\Shoot\Card1",
            Files = [File(@"DCIM\A.MOV")],
            Folders = [],
            SkippedFolders = [],
            Problems = [],
        };
        MovePlan plan = Planner.Build(scan, @"D:\offload-gui-test-does-not-exist\Out", MoveMode.Videos, verifyChecksums: true, env);

        Assert.True(PlanFacts.TargetHoldsOtherJobs(plan));
        Assert.False(PlanFacts.TargetHoldsOtherJobs(Plan([File("A.MOV")], messages: [new PlanMessage(MessageLevel.Info, "This sort continues an earlier sort of this folder.")])));
    }

    // ---- Banner, undo and the memory-card question ------------------------------------------------------------------

    [Fact]
    public void The_memory_card_question_says_to_back_the_card_up_first_and_offers_the_Backup_tab()
    {
        string question = JobTexts.MemoryCardQuestion("F: SONY_A");
        Assert.Equal("This looks like a memory card or camera drive (F: SONY_A). A sort moves files to different folders on it.\n\n"
            + "Back up the card to a different drive first. Then sort the backup. "
            + "Click “Sort anyway” only if you want to sort the card itself (for example, if your camera records to an SSD).", question);
        Assert.DoesNotContain("coming soon", question);
        Assert.Equal("Back up this card first", JobTexts.MemoryCardDontSort);
        Assert.Contains("The Backup tab is open, and the card is selected", JobTexts.MemoryCardNotSorted);

        string warning = SourceGuards.MemoryCardWarning("F: SONY_A");
        Assert.DoesNotContain("coming soon", warning);
        Assert.EndsWith("First, use the Backup tab to back up the card to a different drive. Then sort the backup.", warning);
    }

    [Fact]
    public void A_job_on_a_drive_that_is_not_connected_says_which_drive_to_connect()
    {
        var job = new RecentJob { JournalPath = @"Q:\x\_IngestSorter\j.journal.jsonl", JobId = "j", Source = @"F:\Shoot", Target = @"Q:\x", TargetLabel = "SONY_SSD" };
        (string title, string paths, string text) = JobTexts.Banner(job, more: 2);
        Assert.Equal("Unfinished sort on SONY_SSD (not connected). Connect the drive to continue the job.", title);
        Assert.StartsWith(@"F:\Shoot  →  Q:\x  ·  videos, started ", paths);
        Assert.Contains("(+2 more unfinished jobs", text);
        Assert.Equal("Unfinished undo on Q: (not connected). Connect the drive to continue the job.",
            JobTexts.Banner(new RecentJob { JournalPath = job.JournalPath, JobId = "u", Kind = JobKind.Undo, Target = @"Q:\x" }, 0).Title);
    }

    [Fact]
    public void A_reachable_unfinished_job_can_be_resumed_or_ended()
    {
        JobState job = Job(null!, Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Pending));
        (string title, string paths, string text) = JobTexts.Banner(job, more: 0);
        Assert.StartsWith("Unfinished sort: 1 of 2 files moved (videos), started ", title);
        Assert.Equal(@"E:\Card1  →  D:\Sorted\Card1-Video", paths);
        Assert.Contains("“Stop here...” ends the job and keeps the remaining files in the source folder", text);
        Assert.DoesNotContain("more unfinished", text);
    }

    [Fact]
    public void The_undo_question_names_the_files_the_folder_and_what_stays()
    {
        JobState sort = Job("completed", Item(@"DCIM\A.MOV", ItemStage.Done), Item(@"DCIM\A.SRT", ItemStage.Done), Item(@"DCIM\B.MOV", ItemStage.Done),
            Item(@"DCIM\C.MOV", ItemStage.Done), Item(@"DCIM\D.MOV", ItemStage.Done));
        var preview = new UndoPreview { Original = sort, From = Target, To = @"G:\Card1", CreatesFolder = true, Method = TransferMethod.Copy };
        preview.GoingBack.AddRange(sort.Items.Take(3));
        preview.CannotGoBack.Add(new UndoIssue(sort.Items[3], SkipReasons.ChangedSinceSorted));
        preview.CannotGoBack.Add(new UndoIssue(sort.Items[4], SkipReasons.ChangedSinceSorted));
        (string question, string details) = JobTexts.UndoQuestion(preview);

        Assert.StartsWith(@"Return 2 videos (3 files, ", question);
        Assert.EndsWith(@") to G:\Card1?", question);
        Assert.Contains(@"has a different letter now. The files return to G:\Card1, not to E:\Card1.", details);
        Assert.Contains(@"G:\Card1 does not exist now. The app will make this folder again on the drive that the files came from.", details);
        Assert.Contains("2 files changed after the sort and will stay where they are.", details);
        Assert.Contains("the app copies each file and checks the copy with SHA-256", details);

        preview.Blocked = "This sort is already undone (undo job X, 26 Sep 2026 15:42).";
        Assert.Equal(("You cannot undo this sort now.", preview.Blocked), JobTexts.UndoQuestion(preview));
    }

    [Fact]
    public void The_undo_list_says_which_sorts_can_be_undone()
    {
        JobListing Listing(JobState? state, JobKind kind = JobKind.Sort) =>
            new() { JournalPath = @"D:\x\_IngestSorter\j.journal.jsonl", Id = "j", Kind = kind, Source = Source, Target = Target, State = state, TargetLabel = "T7" };

        JobListing done = Listing(Job("completed", Item("A.MOV", ItemStage.Done)));
        Assert.Equal("can undo", JobTexts.ListingStatus(done));
        Assert.True(JobTexts.CanTryUndo(done));
        Assert.Null(JobTexts.WhyNotUndo(done));

        JobListing away = Listing(null);
        Assert.Equal("not connected", JobTexts.ListingStatus(away));
        Assert.False(JobTexts.CanTryUndo(away));
        Assert.Contains("(T7) is not connected", JobTexts.WhyNotUndo(away));

        Assert.Equal("unfinished", JobTexts.ListingStatus(Listing(Job(null!, Item("A.MOV", ItemStage.Done)))));
        Assert.Equal("nothing moved", JobTexts.ListingStatus(Listing(Job("closed", Item("A.MOV", ItemStage.Skipped, SkipReasons.Closed)))));

        JobState undone = Job("completed", Item("A.MOV", ItemStage.Done));
        undone.UndoneBy = new UndoMark("u", "u.journal.jsonl", null, "completed");
        Assert.Equal("undone", JobTexts.ListingStatus(Listing(undone)));
        undone.UndoneBy = new UndoMark("u", "u.journal.jsonl", null, "closed");
        Assert.Equal("partly undone (you can undo the rest)", JobTexts.ListingStatus(Listing(undone)));
    }

    // ---- With the real engine and a real rescan ----------------------------------------------------------------------

    private static MovePlan Rescan(JobState job) =>
        Planner.Build(Scanner.Scan(job.Header.Source, null, default, job.Header.Target), job.Header.Target, job.Header.Mode, job.Header.Verify);

    [Fact]
    public void A_real_sort_is_green_until_a_new_clip_appears_in_the_source()
    {
        using var t = new TestTree();
        t.Add(@"card\DCIM\100MEDIA\DJI_0001.MOV");
        t.Add(@"card\DCIM\100MEDIA\DJI_0001.SRT");
        t.Add(@"card\DCIM\100MEDIA\DJI_0002.JPG");
        RunResult r = t.Run(t.Plan());
        JobState job = JournalReader.Read(r.JournalPath);

        ResultView view = RunOutcome.Describe(r, job, SourceCheck.ForSort(job, Rescan(job)));
        Assert.Equal(ResultTone.Good, view.Tone);
        Assert.Equal("Done: 1 video (2 files) moved", view.Title);

        t.Add(@"card\DCIM\100MEDIA\DJI_0003.MOV");
        SourceCheck later = SourceCheck.ForSort(job, Rescan(job));
        ResultView after = RunOutcome.Describe(r, job, later);
        Assert.Equal(ResultTone.Attention, after.Tone);
        Assert.Equal(@"card\DCIM\100MEDIA\DJI_0003.MOV", Assert.Single(later.Left).Rel);
        Assert.Equal(1, later.MovableNow);
    }

    [Fact]
    public void A_real_sort_into_a_folder_inside_the_source_is_green()
    {
        using var t = new TestTree();
        t.Add(@"DCIM\100MEDIA\C0001.MP4");
        t.Add(@"DCIM\100MEDIA\C0002.JPG");
        string target = Path.Join(t.Source, "Video");
        RunResult r = t.Run(Planner.Build(Scanner.Scan(t.Source, null, default, target), target, MoveMode.Videos, true));
        JobState job = JournalReader.Read(r.JournalPath);

        Assert.Equal(1, job.DoneCount);
        Assert.Equal(ResultTone.Good, RunOutcome.Describe(r, job, SourceCheck.ForSort(job, Rescan(job))).Tone);
    }

    [Fact]
    public void A_real_sort_is_not_green_while_a_folder_of_the_source_cannot_be_read()
    {
        using var t = new TestTree();
        t.Add(@"A\C0001.MP4");
        string closed = Path.GetDirectoryName(t.Add(@"B\C0002.MP4"))!;
        var info = new DirectoryInfo(closed);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            RunResult r = t.Run(t.Plan());
            JobState job = JournalReader.Read(r.JournalPath);
            SourceCheck check = SourceCheck.ForSort(job, Rescan(job));
            ResultView view = RunOutcome.Describe(r, job, check);

            Assert.True(r.NothingLeftBehind); // the job moved all it knew about ...
            Assert.Equal("B", Assert.Single(check.Unreadable).Split(':')[0]);
            Assert.Equal(ResultTone.Attention, view.Tone); // ... but a folder of the source was never looked at
            Assert.Contains("the scan could not read 1 folder (B)", view.LeftLine);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_real_sort_with_a_locked_clip_is_amber_and_lists_it()
    {
        using var t = new TestTree();
        t.Add(@"DCIM\A.MOV");
        string locked = t.Add(@"DCIM\B.MOV");
        RunResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using JobRunner runner = JobRunner.Start(t.Plan(), JobTestKit.Quick());
            r = runner.Run();
        }
        JobState job = JournalReader.Read(r.JournalPath);
        SourceCheck check = SourceCheck.ForSort(job, Rescan(job));
        ResultView view = RunOutcome.Describe(r, job, check);

        Assert.Equal(RunStatus.CompletedWithFailures, r.Status);
        Assert.Equal(ResultTone.Attention, view.Tone);
        Assert.Equal("Not finished: 1 video is still in the source folder", view.Title);
        Assert.Equal(@"DCIM\B.MOV", Assert.Single(check.Left).Rel);
        Assert.Equal("failed", check.Left[0].Status);
    }
}
