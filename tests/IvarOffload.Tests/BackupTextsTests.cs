using IvarOffload.App;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;

namespace IvarOffload.Tests;

/// <summary>
/// What the Backup tab's result card says: green only when every file of the card has a verified copy on every
/// destination, never "safe to format", and the card is named as the only copy whenever something is missing.
/// </summary>
public class BackupTextsTests
{
    private static BackupDestinationResult Dest(string folder, int verified, bool ended = true, bool mhl = true, int failed = 0, string? problem = null,
        HaltReason halt = HaltReason.None) =>
        new(folder, "E:", folder + @"\_IVAROffload\x.backup.jsonl", verified, failed, 412 - verified - failed, ended, mhl, problem, halt);

    private static BackupResult Result(RunStatus status, params BackupDestinationResult[] destinations) =>
        new(status, 412, 1_000_000_000, destinations, null, HaltReason.None, destinations[0].JournalPath);

    [Fact]
    public void Every_file_verified_everywhere_is_green_with_the_plain_title()
    {
        BackupResult r = Result(RunStatus.Completed, Dest(@"E:\Cards\A", 412), Dest(@"F:\Cards\A", 412));
        Assert.True(r.AllVerified);
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal(ResultTone.Good, v.Tone);
        // Green only says what happened: never advice about the card.
        Assert.Equal("Copied to 2 drives", v.Title);
        Assert.Equal("All 412 files were copied and checked. The card was not changed.", v.LeftLine);
        Assert.Null(v.Alarm);
        Assert.Equal("Copied to 1 drive", BackupTexts.CopiedTitle(1));
        Assert.Equal("The file was copied and checked. The card was not changed.", BackupTexts.AllCheckedLine(1));
        // How the copies were checked, and the checksum files, are one click away.
        Assert.Contains(v.Technical, line => line.Contains("SHA-256"));
        Assert.Contains(v.Technical, line => line == @"E:\Cards\A: ASC MHL checksum files written.");
    }

    [Fact]
    public void Verified_copies_that_share_a_drive_are_never_green()
    {
        BackupResult sameDrive = Result(RunStatus.Completed, Dest(@"E:\Cards\A", 412) with { Serial = 7 }, Dest(@"E:\Backup\A", 412) with { Serial = 7 });
        Assert.True(sameDrive.AllVerified);
        Assert.Equal(1, sameDrive.SeparateDrives);
        ResultView v = BackupTexts.Describe(sameDrive);
        Assert.Equal(ResultTone.Attention, v.Tone);
        Assert.Equal("Copied, but not to separate drives", v.Title);
        Assert.Equal("Don't format the card yet: only 1 of 2 copies is on a drive of its own.", v.Alarm);

        BackupResult onSource = Result(RunStatus.Completed, Dest(@"C:\Backup\A", 412) with { Serial = 3 }) with { SourceSerial = 3 };
        Assert.Equal(0, onSource.SeparateDrives);
        Assert.Equal("Don't format the card yet: every copy is on the drive being backed up.", BackupTexts.Describe(onSource).Alarm);

        BackupResult separate = Result(RunStatus.Completed, Dest(@"E:\A", 412) with { Serial = 7 }, Dest(@"F:\A", 412) with { Serial = 8 }) with { SourceSerial = 3 };
        Assert.Equal(ResultTone.Good, BackupTexts.Describe(separate).Tone);
    }

    public static TheoryData<string, BackupResult> NotComplete() => new()
    {
        { "a destination dropped out", Result(RunStatus.CompletedWithFailures, Dest(@"E:\A", 412), Dest(@"F:\A", 100, ended: false, mhl: false, problem: "The destination F: is full.", halt: HaltReason.TargetFull)) },
        { "files failed", Result(RunStatus.CompletedWithFailures, Dest(@"E:\A", 410, ended: false, mhl: false, failed: 2)) },
        { "ended early", Result(RunStatus.Closed, Dest(@"E:\A", 200, mhl: false)) },
        { "stopped", Result(RunStatus.Stopped, Dest(@"E:\A", 200, ended: false, mhl: false)) },
        { "halted", Result(RunStatus.Halted, Dest(@"E:\A", 200, ended: false, mhl: false)) with { Message = "The card (SONY_A was F:) is not connected. Reconnect it." } },
        { "left out", Result(RunStatus.Completed, Dest(@"E:\A", 412)) with { LeftOut = 1 } },
        { "added after the preview", Result(RunStatus.Completed, Dest(@"E:\A", 412)) with { NotInBackup = ["DCIM\\X.JPG (added after the preview)"] } },
        { "card not checked again", Result(RunStatus.Completed, Dest(@"E:\A", 412)) with { CardNotRescanned = true } },
        { "no manifest", Result(RunStatus.Completed, Dest(@"E:\A", 412, mhl: false)) },
    };

    [Theory]
    [MemberData(nameof(NotComplete))]
    public void Anything_less_than_every_file_verified_everywhere_is_amber_and_keeps_the_card(string why, BackupResult r)
    {
        Assert.False(r.AllVerified, why);
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal(ResultTone.Attention, v.Tone);
        Assert.Equal(BackupTexts.KeepTheCard, v.Alarm);
        Assert.DoesNotContain("Copied to", v.Title);
    }

    [Fact]
    public void No_result_ever_says_the_card_is_safe_to_format()
    {
        foreach (BackupResult r in NotComplete().Select(row => (BackupResult)row[1]).Append(Result(RunStatus.Completed, Dest(@"E:\A", 412))))
        {
            ResultView v = BackupTexts.Describe(r);
            string all = string.Join(" ", new[] { v.Title, v.LeftLine, v.Detail, v.Alarm ?? "", v.Status }.Concat(v.Reasons).Concat(v.Technical));
            Assert.DoesNotContain("safe to format", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("can be formatted", all, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_destination_that_dropped_out_is_named_with_its_reason()
    {
        BackupResult r = Result(RunStatus.CompletedWithFailures, Dest(@"E:\A", 412), Dest(@"F:\A", 100, ended: false, mhl: false, problem: "The destination F: is full.", halt: HaltReason.TargetFull));
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal("Finished on 1 of 2 drives - the others need Resume", v.Title);
        Assert.Contains(v.Reasons, line => line.StartsWith(@"F:\A: 100 of 412 checked") && line.EndsWith("The destination F: is full."));
        Assert.Equal("Complete copies: 1 of 2.", v.LeftLine);
    }

    [Fact]
    public void Verify_again_is_green_only_when_every_copy_matches()
    {
        var good = new BackupVerifyResult(3, [new BackupVerifyDestination(@"E:\A", "E:", 3, 3, [], null)]);
        Assert.Equal(ResultTone.Good, BackupTexts.DescribeVerify(good).Tone);
        var damaged = new BackupVerifyResult(3, [new BackupVerifyDestination(@"E:\A", "E:", 3, 2, ["x: CHECKSUM MISMATCH"], null)]);
        Assert.Equal(ResultTone.Attention, BackupTexts.DescribeVerify(damaged).Tone);
        var away = new BackupVerifyResult(3, [new BackupVerifyDestination(@"E:\A", "E:", 3, 3, [], null), new BackupVerifyDestination(@"F:\A", "F:", 0, 0, [], "not connected")]);
        Assert.Equal(ResultTone.Attention, BackupTexts.DescribeVerify(away).Tone);
    }

    [Fact]
    public void The_mode_descriptions_say_what_each_mode_does()
    {
        Assert.Contains("never changed", BackupTexts.BackupDescription);
        Assert.Contains("moves the videos (or the photos)", BackupTexts.SortDescription);
        Assert.Equal("Back up 412 files to 2 drives", BackupTexts.StartButton(412, 2));
        Assert.Equal("Add 38 files, check all 450", BackupTexts.TopUpStartButton(38, 450));
        Assert.Equal("Check all 450 files", BackupTexts.TopUpStartButton(0, 450));
    }

    // ---- Top-ups ----------------------------------------------------------------------------------------------

    private static BackupResult TopUp(params BackupDestinationResult[] destinations) =>
        Result(RunStatus.Completed, destinations) with { AddsTo = "backup-earlier" };

    [Fact]
    public void A_top_up_that_verified_every_file_is_green_and_says_what_was_added()
    {
        BackupResult r = TopUp(Dest(@"E:\A", 412) with { Serial = 7, Copied = 38, Rechecked = 374 }, Dest(@"F:\A", 412) with { Serial = 8, Copied = 38, Rechecked = 374 });
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal(ResultTone.Good, v.Tone);
        Assert.Equal("Copied to 2 drives", v.Title);
        Assert.Equal("38 new files copied, all 412 files checked. The card was not changed.", v.LeftLine);
        Assert.Equal("Nothing new to copy, all 1 file checked. The card was not changed.", BackupTexts.TopUpCheckedLine(0, 0, 1));
        Assert.Equal("2 new files copied, 1 copied again, all 9 files checked. The card was not changed.", BackupTexts.TopUpCheckedLine(2, 1, 9));
        Assert.Equal("1 file copied again, all 9 files checked. The card was not changed.", BackupTexts.TopUpCheckedLine(0, 1, 9));
    }

    [Fact]
    public void A_top_up_names_changed_restored_repaired_and_kept_files_and_a_restarted_history()
    {
        BackupResult r = TopUp(
            Dest(@"E:\A", 412) with { Serial = 7, Copied = 5, Replaced = 2, Restored = 3, Kept = 4, MhlRestarted = @"_IVAROffload\replaced\job\ascmhl" },
            Dest(@"F:\A", 412) with { Serial = 8, Copied = 3, Replaced = 2, Repaired = 1, Kept = 4, KeptGone = 1 });
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal(ResultTone.Good, v.Tone); // every file of the card is verified on both
        string all = string.Join("\n", v.Reasons);
        Assert.Contains(@"2 camera index files changed on the card since the earlier backup: copied again. The old versions are kept in _IVAROffload\replaced", all);
        Assert.Contains("3 files no longer in the backup folder (moved out by a sort, or deleted) were copied again from the card.", all);
        Assert.Contains(@"1 copy in F:\A no longer matched its checksum (damaged on E: since the earlier backup)", all);
        Assert.Contains("4 files in the backup are no longer on the card: they stay in the backup, verified.", all);
        Assert.Contains("1 file of the earlier backup is neither on the card nor in the backup folder", all);
        Assert.Contains("a new ASC MHL history was started", all);
    }

    [Fact]
    public void A_top_up_that_found_a_damaged_file_no_longer_on_the_card_is_never_green()
    {
        BackupResult r = TopUp(Dest(@"E:\A", 412) with { Serial = 7, KeptDamaged = [@"DCIM\100MSDCF\DSC00001.JPG"] });
        Assert.False(r.AllVerified);
        ResultView v = BackupTexts.Describe(r);
        Assert.Equal(ResultTone.Attention, v.Tone);
        Assert.Contains("damaged files that are no longer on the card", v.Title);
        Assert.Contains(v.Reasons, l => l.StartsWith("DAMAGED on E:") && l.Contains("DSC00001.JPG"));
        Assert.Null(v.Alarm); // the card itself is complete in the backup
    }
}
