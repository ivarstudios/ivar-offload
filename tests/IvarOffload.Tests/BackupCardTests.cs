using IvarOffload.Core.Backup;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// The card side of a backup: a card that was already backed up to the chosen drive is recognized, the camera make is
/// read from the card's folders, and the folder name follows the user's pattern.
/// </summary>
public class BackupCardTests
{
    private static BackupPlan PlanAs(BackupTree t, string name, int destinations = 1) =>
        BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(destinations).ToList(), name);

    // ---- Already backed up ------------------------------------------------------------------------------------------

    [Fact]
    public void A_card_already_backed_up_to_the_destination_under_another_name_is_offered_for_checking_against_it()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan(2)).AllVerified);

        BackupPlan again = PlanAs(t, "second", 2);
        TopUpOffer offer = Assert.IsType<TopUpOffer>(again.TopUp);
        string text = offer.Describe(adding: false, again.Files.Count);
        Assert.Contains(t.Dest(0), text);
        Assert.Contains(t.Dest(1), text); // one offer for the earlier backup, naming both of its copies
        Assert.Contains("It contains all files of the card as they are now", text);
        Assert.DoesNotContain(again.Messages, m => m.Text.Contains("backup of this card from") || m.Text.Contains("already a backup") || m.Text.Contains("already in a backup"));
        Assert.True(again.CanRun); // a second copy is the user's decision
    }

    [Fact]
    public void A_card_with_new_files_since_an_earlier_backup_says_how_many_are_new()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Run(t.Plan(1));
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 30_000); // shot after the backup, card not formatted

        TopUpOffer offer = Assert.IsType<TopUpOffer>(PlanAs(t, "second").TopUp);
        Assert.Equal((1, 7, 0), (offer.New, offer.Unchanged, offer.Changed));
        Assert.Contains("the card now has 1 new file", offer.Describe(adding: false, 8), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_different_card_or_a_changed_file_is_not_called_already_backed_up()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Run(t.Plan(1));

        // Another card with the same folder layout, sizes and names (a camera that numbers every card alike): other dates.
        using var other = new BackupTree();
        other.TypicalCard();
        foreach (string f in Directory.EnumerateFiles(other.Card, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddDays(3));
        BackupPlan wrong = BackupPlanner.Build(BackupScanner.Scan(other.Card), [t.Parents[0]], "other");
        Assert.DoesNotContain(wrong.Messages, m => m.Text.Contains("already a backup") || m.Text.Contains("already in a backup") || m.Text.Contains("backup of this card from"));

        // The same card with one file edited since: not the same card any more.
        string clip = Path.Join(t.Card, @"PRIVATE\M4ROOT\CLIP\C0001M01.XML");
        File.SetLastWriteTimeUtc(clip, File.GetLastWriteTimeUtc(clip).AddMinutes(1));
        Assert.DoesNotContain(PlanAs(t, "second").Messages, m => m.Text.Contains("already a backup") || m.Text.Contains("already in a backup"));
    }

    [Fact]
    public void An_earlier_backup_whose_files_were_moved_out_is_never_said_to_hold_the_card()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Run(t.Plan(1));
        // "Sort this backup" moved the clips out; the log stays behind.
        Directory.Move(Path.Join(t.Dest(0), @"PRIVATE\M4ROOT\CLIP"), Path.Join(t.Root, "sorted-out"));

        TopUpOffer offer = Assert.IsType<TopUpOffer>(PlanAs(t, "second").TopUp);
        Assert.Equal(2, offer.Missing);
        string text = offer.Describe(adding: false, 7);
        Assert.Contains("2 files of that backup are no longer in its folder (because of a sort or a deletion)", text);
        Assert.DoesNotContain("It contains all files", text);
    }

    [Fact]
    public void A_fat_card_read_an_hour_off_after_a_daylight_saving_change_is_still_recognized()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        t.Run(t.Plan(1));
        foreach (string f in Directory.EnumerateFiles(t.Card, "*", SearchOption.AllDirectories))
        {
            FileAttributes a = File.GetAttributes(f);
            File.SetAttributes(f, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddHours(1));
            File.SetAttributes(f, a);
        }
        TopUpOffer offer = Assert.IsType<TopUpOffer>(PlanAs(t, "second").TopUp);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, offer.Shift);
        Assert.Equal((0, 7, 0), (offer.New, offer.Unchanged, offer.Changed));

        // One file shifted by another amount: it counts as changed (copied again, its old version moved aside).
        string one = Path.Join(t.Card, "empty.bin");
        File.SetLastWriteTimeUtc(one, File.GetLastWriteTimeUtc(one).AddHours(1));
        offer = Assert.IsType<TopUpOffer>(PlanAs(t, "third").TopUp);
        Assert.Equal((0, 6, 1), (offer.New, offer.Unchanged, offer.Changed));
    }

    [Fact]
    public void A_card_backed_up_day_after_day_gets_one_note_for_the_newest_earlier_backup()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        foreach (string name in new[] { "day1", "day2", "day3" })
        {
            t.Add($@"DCIM\100MSDCF\{name}.JPG", 5000);
            t.Run(PlanAs(t, name));
        }
        t.Add(@"DCIM\100MSDCF\day4.JPG", 5000);

        BackupPlan plan = PlanAs(t, "day4");
        TopUpOffer offer = Assert.IsType<TopUpOffer>(plan.TopUp);
        Assert.Equal(Path.Join(t.Parents[0], "day3"), Assert.Single(offer.Folders).Folder); // the newest
        Assert.Equal((1, 10), (offer.New, offer.Unchanged));
        Assert.DoesNotContain(plan.Messages, m => m.Text.Contains("backup of this card from") || m.Text.Contains("already a backup") || m.Text.Contains("already in a backup")); // nor the older ones
    }

    [Fact]
    public void A_backup_that_was_ended_early_is_not_called_verified()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(1), new CrashAt("after-done", 2)));
        BackupTree.Close(t.Journal(0));

        // Adding to it copies what it did not get to: never said to hold the card, nor that files are new.
        TopUpOffer offer = Assert.IsType<TopUpOffer>(PlanAs(t, "second").TopUp);
        Assert.True(offer.EndedEarly);
        string text = offer.Describe(adding: false, 7);
        Assert.Contains("that backup ended before it copied all files", text);
        Assert.Contains("of the card are not in that backup", text);
        Assert.DoesNotContain("verified", text);
        Assert.DoesNotContain("checked", text);
        Assert.DoesNotContain("new file", text);
    }

    [Fact]
    public void An_unfinished_backup_of_the_card_under_another_name_is_offered_for_resume()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(1), new CrashAt("after-done", 2)));

        BackupPlan again = PlanAs(t, "second");
        Assert.Contains(again.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("unfinished backup in") && m.Text.Contains(t.Dest(0)));
        Assert.Equal(t.Journal(0), Assert.Single(again.UnfinishedJournals), StringComparer.OrdinalIgnoreCase);
        Assert.True(again.CanRun);
    }

    // ---- The camera make --------------------------------------------------------------------------------------------

    private static Func<string, IReadOnlyCollection<string>> Table(params (string Folder, string[] Names)[] rows) =>
        folder => rows.FirstOrDefault(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)).Names ?? [];

    [Fact]
    public void The_camera_make_is_read_from_the_card_folders()
    {
        Assert.Equal("Sony", CardCamera.Of(@"F:\", Table((@"F:\", ["DCIM", "PRIVATE"]), (@"F:\DCIM", ["100MSDCF"]), (@"F:\PRIVATE", ["M4ROOT"])), _ => []));
        Assert.Equal("Canon", CardCamera.Of(@"F:\", Table((@"F:\", ["DCIM"]), (@"F:\DCIM", ["100CANON"])), _ => []));
        Assert.Equal("Canon", CardCamera.Of(@"F:\", Table((@"F:\", ["CONTENTS"]), (@"F:\CONTENTS", ["CLIPS001"])), _ => []));
        Assert.Equal("Panasonic", CardCamera.Of(@"F:\", Table((@"F:\", ["CONTENTS"]), (@"F:\CONTENTS", ["CLIP", "VIDEO", "AUDIO"])), _ => []));
        Assert.Equal("Panasonic", CardCamera.Of(@"F:\", Table((@"F:\", ["DCIM", "PRIVATE"]), (@"F:\DCIM", ["100_PANA"]), (@"F:\PRIVATE", ["AVCHD"])), _ => []));
        Assert.Equal("RED", CardCamera.Of(@"F:\", Table((@"F:\", ["A001_0101XY.RDM"])), _ => []));
        Assert.Equal("ARRI", CardCamera.Of(@"F:\", Table((@"F:\", ["A016R1K4"])), Table((@"F:\A016R1K4", ["A016C001_120126_R1K4.mxf"]))));
        Assert.Equal("Blackmagic", CardCamera.Of(@"F:\", _ => [], Table((@"F:\", ["A001_09201234_C001.braw"]))));
        Assert.Equal("DJI", CardCamera.Of(@"F:\", Table((@"F:\", ["DCIM"]), (@"F:\DCIM", ["DJI_001"])), _ => []));
        // The folders don't say which make: no guess.
        Assert.Null(CardCamera.Of(@"F:\", Table((@"F:\", ["DCIM"]), (@"F:\DCIM", ["100MEDIA"])), _ => []));
        Assert.Null(CardCamera.Of(@"F:\", Table((@"F:\", ["PRIVATE"]), (@"F:\PRIVATE", ["AVCHD"])), _ => []));
        Assert.Null(CardCamera.Of(@"D:\", Table((@"D:\", ["Work", "Photos"])), _ => []));
    }

    [Theory]
    [InlineData("100MSDCF", "Sony")]
    [InlineData("101CANON", "Canon")]
    [InlineData("100EOSR5", "Canon")]
    [InlineData("100NIKON", "Nikon")]
    [InlineData("100NCZ_8", "Nikon")]
    [InlineData("100_PANA", "Panasonic")]
    [InlineData("100_FUJI", "Fujifilm")]
    [InlineData("100GOPRO", "GoPro")]
    [InlineData("Camera01", "Insta360")]
    [InlineData("100MEDIA", null)]
    [InlineData("Holiday", null)]
    public void Dcim_camera_folders_name_their_make(string folder, string? make) => Assert.Equal(make, CardCamera.DcimMake(folder));

    // ---- The folder name pattern ------------------------------------------------------------------------------------

    [Fact]
    public void A_name_pattern_fills_in_the_date_the_card_and_the_camera()
    {
        var day = new DateTime(2026, 9, 28, 14, 30, 15);
        Assert.Equal("{YYMMDD}_{card}", BackupPlanner.DefaultTemplate);
        Assert.Equal("260928_SONY_A", BackupPlanner.ExpandTemplate(BackupPlanner.DefaultTemplate, day, "SONY_A", "Sony"));
        Assert.Equal("260928_Sony_SONY_A", BackupPlanner.ExpandTemplate("{YYMMDD}_{camera}_{card}", day, "SONY_A", "Sony"));
        Assert.Equal("Wedding 2026-09 SONY_A", BackupPlanner.ExpandTemplate("Wedding {YYYY}-{MM} {CARD}", day, "SONY_A", null));
        // No camera make: its part is left out with its separator, never "__".
        Assert.Equal("260928_SONY_A", BackupPlanner.ExpandTemplate("{YYMMDD}_{camera}_{card}", day, "SONY_A", null));
        Assert.Equal("SONY_A_28", BackupPlanner.ExpandTemplate("{camera}_{card}_{DD}", day, "SONY_A", null));
        Assert.Equal("260928", BackupPlanner.ExpandTemplate("{YYMMDD}-{camera}", day, "SONY_A", ""));
        // Plain text is never read as codes: only what is in braces.
        Assert.Equal("SUMMER ADDISON 260928", BackupPlanner.ExpandTemplate("SUMMER ADDISON {YYMMDD}", day, "SONY_A", null));
    }

    [Theory]
    [InlineData("{YYMMDD}", "260928")]
    [InlineData("{YYYYMMDD}", "20260928")]
    [InlineData("{YYYY-MM-DD}", "2026-09-28")]
    [InlineData("{DD.MM.YY}", "28.09.26")]
    [InlineData("{yymmdd}", "260928")]
    [InlineData("{HHMMSS}", "143015")]
    [InlineData("{HH-MM-SS}", "14-30-15")]
    [InlineData("{HHMM}", "1430")]            // MM right after HH: minutes
    [InlineData("{MMSS}", "3015")]            // MM right before SS: minutes
    [InlineData("{MM}", "09")]                // MM alone: month
    [InlineData("{YYMMDD_HHMMSS}", "260928_143015")]
    [InlineData("{YYMMDD HHMM}", "260928 1430")]
    [InlineData("{YYMMDD}_{HHMMSS}", "260928_143015")]
    public void Date_and_time_codes_combine_freely(string template, string expected) =>
        Assert.Equal(expected, BackupPlanner.ExpandTemplate(template, new DateTime(2026, 9, 28, 14, 30, 15), "SONY_A", null));

    [Fact]
    public void A_name_pattern_that_cannot_be_used_is_explained()
    {
        Assert.Null(BackupPlanner.ValidateTemplate(BackupPlanner.DefaultTemplate));
        Assert.Null(BackupPlanner.ValidateTemplate("Job 42 {YYYY-MM-DD}"));
        Assert.Null(BackupPlanner.ValidateTemplate("{HHMMSS}"));
        Assert.Contains("{label} is not a known part of a pattern", BackupPlanner.ValidateTemplate("{YYMMDD}_{label}"));
        Assert.Contains("{date} is not a known part of a pattern", BackupPlanner.ValidateTemplate("{date}_{card}")); // only through UpgradeTemplate
        Assert.NotNull(BackupPlanner.ValidateTemplate("{YYY}"));
        Assert.NotNull(BackupPlanner.ValidateTemplate("{MMM}"));
        Assert.NotNull(BackupPlanner.ValidateTemplate("{-}"));
        Assert.NotNull(BackupPlanner.ValidateTemplate("{HH:MM}")); // ":" can't be in a folder name
        Assert.NotNull(BackupPlanner.ValidateTemplate("{YYMMDD"));
        Assert.NotNull(BackupPlanner.ValidateTemplate("{YYMMDD}/{card}"));
        Assert.NotNull(BackupPlanner.ValidateTemplate(" "));
        Assert.NotNull(BackupPlanner.ValidateTemplate("{camera}")); // empty for most cards
    }

    [Fact]
    public void Patterns_saved_by_earlier_versions_are_upgraded()
    {
        Assert.Equal(BackupPlanner.DefaultTemplate, BackupPlanner.UpgradeTemplate("{date}_{card}", saved: true)); // the app's old default
        Assert.Equal("{YYYY-MM-DD}_{card}", BackupPlanner.UpgradeTemplate("{date}_{card}")); // asked for on the command line: same names
        Assert.Equal("{YYYY-MM-DD}_{camera}_{card}", BackupPlanner.UpgradeTemplate("{date}_{camera}_{card}"));
        Assert.Equal("Wedding {YYYY}-{MM}-{DD} {card}", BackupPlanner.UpgradeTemplate("Wedding {Year}-{month}-{DAY} {card}"));
        Assert.Equal("{YYMMDD}_{card}", BackupPlanner.UpgradeTemplate("{YYMMDD}_{card}"));
        Assert.Equal("2026-09-28_Sony_SONY_A",
            BackupPlanner.ExpandTemplate(BackupPlanner.UpgradeTemplate("{date}_{camera}_{card}"), new DateTime(2026, 9, 28), "SONY_A", "Sony"));
    }

    [Fact]
    public void The_default_name_follows_the_pattern_and_still_gets_a_number_when_taken()
    {
        using var t = new BackupTree();
        var day = new DateTime(2026, 9, 28);
        Assert.Equal("CARD 2026-09-28", BackupPlanner.DefaultName(t.Card, day, t.Parents, "{card} {YYYY-MM-DD}"));
        File.WriteAllText(Path.Join(Directory.CreateDirectory(Path.Join(t.Parents[0], "CARD 2026-09-28")).FullName, "x.txt"), "first card");
        Assert.Equal("CARD 2026-09-28_2", BackupPlanner.DefaultName(t.Card, day, t.Parents, "{card} {YYYY-MM-DD}"));
        // A pattern that is not valid is never used.
        Assert.Equal("260928_CARD", BackupPlanner.DefaultName(t.Card, day, t.Parents, "{nope}"));
    }
}
