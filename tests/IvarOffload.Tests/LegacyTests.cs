using System.Text;
using System.Xml;
using System.Xml.Linq;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;
using static IvarOffload.Tests.JobTestKit;

namespace IvarOffload.Tests;

/// <summary>
/// The app's earlier names: new jobs log in _IVAROffload, and what IVAR Ingest (_IVARIngest) and Ingest Sorter
/// (_IngestSorter) left keeps working: sorts are found, listed, resumed, verified and undone, backups are resumed,
/// verified and added to, and settings are read until the first save. No log folder is ever scanned as part of a source.
/// </summary>
public class LegacyTests
{
    /// <summary>An earlier version's log folder, and the tool name it wrote into its job logs.</summary>
    public static TheoryData<string, string> EarlierVersions => new()
    {
        { JobPaths.IvarIngestLogFolderName, "IVAR Ingest 1.0.0-beta.7" },
        { JobPaths.IngestSorterLogFolderName, "Ingest Sorter 1.0.0" },
    };

    private static void Sample(TestTree t)
    {
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.MOV", 200_000);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0001.SRT", 900);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0002.MOV", 150_000);
        t.Add(@"Card1\DCIM\100MEDIA\DJI_0002.JPG", 4000);
        t.Add(@"Card2\PRIVATE\M4ROOT\CLIP\C0001.MP4", 120_000);
        t.Add(@"Card2\C0002.MP4", 90_000, FileAttributes.ReadOnly);
    }

    /// <summary>Turns the job's log (and receipt, if there is one yet) into what an earlier version left: its log folders.</summary>
    private static string MakeLegacy(TestTree t, string folderName, string tool)
    {
        string journal = t.Journal;
        string legacyFolder = Path.Join(t.Target, folderName);
        MoveFolder(JobPaths.LogFolder(t.Target), legacyFolder);
        if (Directory.Exists(JobPaths.LogFolder(t.Source))) MoveFolder(JobPaths.LogFolder(t.Source), Path.Join(t.Source, folderName));
        string legacy = Path.Join(legacyFolder, Path.GetFileName(journal));
        RewriteHeader(legacy, h => h["tool"] = tool);
        return legacy;
    }

    [Fact]
    public void New_jobs_log_in_IVAROffload_on_both_sides_under_the_new_name()
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        Assert.Equal(JobPaths.LogFolderName, Path.GetFileName(Path.GetDirectoryName(t.Journal)));
        JobState state = JournalReader.Read(t.Journal);
        Assert.StartsWith("IVAR Offload ", state.Header.Tool);
        Assert.True(File.Exists(JobPaths.ReceiptTextPath(t.Source, state.Header.Id)));
        Assert.StartsWith("IVAR Offload", File.ReadAllText(JobPaths.SummaryPath(t.Journal)).TrimStart('﻿'));
        foreach (string legacy in JobPaths.LegacyLogFolderNames)
        {
            Assert.False(Directory.Exists(Path.Join(t.Target, legacy)));
            Assert.False(Directory.Exists(Path.Join(t.Source, legacy)));
        }
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void A_job_an_earlier_version_logged_is_found_resumed_verified_and_undone(string folderName, string tool)
    {
        using var t = new TestTree();
        Sample(t);
        var before = Fingerprints(t.Source);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 3)));
        string legacy = MakeLegacy(t, folderName, tool);

        // Found where the app and the command line look for jobs.
        Assert.Equal(Path.GetFullPath(legacy), Path.GetFullPath(Assert.Single(JobPaths.FindJournals(t.Target))));
        Assert.Equal(legacy, Assert.Single(JobPaths.FindUnfinished(t.Target)).JournalPath);
        Assert.True(JobPaths.SamePath(t.Target, JobPaths.RootOfJournal(legacy)!));
        Assert.Equal(folderName, JobPaths.LogFolderNameOf(legacy));
        Assert.Equal("unfinished", Assert.Single(JobCatalog.ForFolder(t.Target)).Status);

        // Resumed where it stopped; its receipt goes next to its own kind of log folder, and no second log folder appears.
        RunResult resumed = TestTree.Resume(legacy);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.True(resumed.NothingLeftBehind);
        Assert.False(Directory.Exists(JobPaths.LogFolder(t.Target)), $"the resumed job keeps its {folderName} log folder");
        JobState state = JournalReader.Read(legacy);
        string receipt = JobPaths.ReceiptTextPath(t.Source, state.Header.Id, folderName);
        Assert.True(File.Exists(receipt));
        Assert.False(File.Exists(JobPaths.ReceiptTextPath(t.Source, state.Header.Id)));
        Assert.Equal(Path.GetFullPath(legacy), UndoFactory.ResolveJournal(receipt));

        // Verified, and listed from both folders.
        Assert.True(JobVerifier.Verify(legacy).AllGood);
        Assert.Equal(state.Header.Id, Assert.Single(JobCatalog.ForFolder(t.Target)).Id);
        JobListing fromSource = Assert.Single(JobCatalog.ForFolder(t.Source));
        Assert.True(fromSource.CanUndo);

        // Undone: every file back exactly; the undo is a new job, logged in _IVAROffload in the original folder.
        UndoPreview preview = UndoFactory.Preview(legacy);
        Assert.True(preview.CanRun, preview.Blocked);
        RunResult undo;
        using (JobRunner runner = UndoFactory.Start(legacy)) undo = runner.Run();
        Assert.Equal(RunStatus.Completed, undo.Status);
        Assert.True(undo.NothingLeftBehind);
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), JobTestKit.Files(t.Source).Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string rel, var fingerprint) in before)
            Assert.Equal(fingerprint, TestTree.Fingerprint(Path.Join(t.Source, rel)));
        Assert.Empty(JobTestKit.Files(t.Target));
        Assert.True(JobPaths.SamePath(JobPaths.LogFolder(t.Source), Path.GetDirectoryName(undo.JournalPath)!));
        Assert.Equal("completed", JournalReader.Read(legacy).UndoneBy?.Status);
        Assert.Contains("already undone", UndoFactory.Preview(legacy).Blocked);
        // The old receipt stays in the old log folder; it names the folder the undo's log really is in.
        string receiptText = File.ReadAllText(receipt);
        Assert.Contains($"its log is in {Path.GetDirectoryName(undo.JournalPath)})", receiptText);
        Assert.DoesNotContain("its log is next to this file", receiptText);
        Assert.Equal(2, JobCatalog.ForFolder(t.Source).Count); // the sort (from its receipt) and its undo
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void An_old_job_can_be_ended_without_moving_the_rest(string folderName, string tool)
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        string legacy = MakeLegacy(t, folderName, tool);

        RunResult closed;
        using (JobRunner runner = JobRunner.Open(legacy)) closed = runner.Close(CancellationToken.None);
        Assert.Equal(RunStatus.Closed, closed.Status);
        Assert.True(closed.StillInSource > 0);
        JobState state = JournalReader.Read(legacy);
        Assert.True(state.IsEnded);
        Assert.Empty(JobPaths.FindUnfinished(t.Target));
        Assert.True(File.Exists(JobPaths.ReceiptCsvPath(t.Source, state.Header.Id, folderName)));
        Assert.Equal(state.StillInSourceCount, state.StillInSource.Count(i => File.Exists(Path.Join(t.Source, i.Rel))));
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void A_target_renamed_under_an_old_job_still_halts_as_a_moved_log(string folderName, string tool)
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-rename", 2)));
        MakeLegacy(t, folderName, tool);
        string renamed = t.Target + " renamed";
        MoveFolder(t.Target, renamed);
        string journal = JobPaths.FindJournals(renamed).Single();

        RunResult halted = TestTree.Resume(journal);
        Assert.Equal(RunStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.LogMoved, halted.Halt);
        Assert.Contains($"its log is now in {renamed}.", halted.Message);
        Assert.False(Directory.Exists(t.Target), "nothing is recreated in the old place");

        MoveFolder(renamed, t.Target);
        Assert.True(TestTree.Resume(Path.Join(t.Target, folderName, Path.GetFileName(journal))).NothingLeftBehind);
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void The_preview_sees_earlier_sorts_logged_in_any_log_folder(string folderName, string tool)
    {
        using var t = new TestTree();
        Sample(t);
        Assert.Equal(RunStatus.Completed, t.Run(t.Plan()).Status);
        MakeLegacy(t, folderName, tool);

        string other = Path.Join(t.Root, "OTHER");
        Directory.CreateDirectory(Path.Join(other, "Card3"));
        File.WriteAllBytes(Path.Join(other, @"Card3\C0003.MP4"), new byte[5000]);
        MovePlan plan = Planner.Build(Scanner.Scan(other), t.Target, MoveMode.Videos, verifyChecksums: true);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith($"This target folder already holds files that an earlier sort moved from {t.Source}", StringComparison.Ordinal));
    }

    [Fact]
    public void No_log_folder_is_ever_scanned()
    {
        using var t = new TestTree();
        t.Add(@"_IVAROffload\stray.mov", 1000);
        t.Add(@"_IVARIngest\beta.mov", 1000);
        t.Add(@"_IngestSorter\old.mov", 1000);
        t.Add(@"Card1\_IngestSorter\nested.mov", 1000);
        t.Add(@"Card1\_ivaringest\lower-case.mov", 1000);
        t.Add(@"Card1\clip.mov", 1000);

        ScanResult scan = Scanner.Scan(t.Source);
        Assert.Equal(@"Card1\clip.mov", Assert.Single(scan.Files).RelativePath);
        Assert.Equal(5, scan.SkippedFolders.Count(s => s.Reason.StartsWith("IVAR Offload logs", StringComparison.Ordinal)));
        Assert.Equal(@"Card1\clip.mov", Assert.Single(Planner.Build(scan, t.Target, MoveMode.Videos, verifyChecksums: false).ToMove).RelativePath);
    }

    [Theory]
    [InlineData("~j-000001" + JobPaths.TempExtension)]
    [InlineData("~j-000001" + JobPaths.LegacyTempExtension)]
    public void Unfinished_copies_of_this_and_earlier_versions_stay_and_are_named(string name)
    {
        using var t = new TestTree();
        t.Add(@"Card1\clip.mov", 1000);
        t.Add($@"Card1\{name}", 500);
        MovePlan plan = Planner.Build(Scanner.Scan(t.Source), t.Target, MoveMode.Videos, verifyChecksums: false);
        Assert.Equal(@"Card1\clip.mov", Assert.Single(plan.ToMove).RelativePath);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("unfinished temporary file"));
    }

    [Fact]
    public void Settings_are_read_from_the_newest_earlier_folder_until_IVAR_Offload_has_its_own()
    {
        string root = Path.Join(Path.GetTempPath(), "offload-data-" + Guid.NewGuid().ToString("N")[..8]);
        string current = Path.Join(root, "IVAROffload"), ivarIngest = Path.Join(root, "IVARIngest"), ingestSorter = Path.Join(root, "IngestSorter");
        string[] legacy = [ivarIngest, ingestSorter];
        try
        {
            foreach (string folder in new[] { current, ivarIngest, ingestSorter }) Directory.CreateDirectory(folder);
            Assert.Equal(Path.Join(current, "settings.json"), RecentJobs.FileToRead("settings.json", current, legacy)); // none has one

            File.WriteAllText(Path.Join(ingestSorter, "settings.json"), "{}");
            Assert.Equal(Path.Join(ingestSorter, "settings.json"), RecentJobs.FileToRead("settings.json", current, legacy));
            File.WriteAllText(Path.Join(ivarIngest, "settings.json"), "{}");
            Assert.Equal(Path.Join(ivarIngest, "settings.json"), RecentJobs.FileToRead("settings.json", current, legacy)); // the newest
            Assert.Equal(Path.Join(current, "settings.json"), RecentJobs.FileToRead("settings.json", current, [])); // IVAROFFLOAD_DATA set

            File.WriteAllText(Path.Join(current, "settings.json"), "{}"); // the first save
            Assert.Equal(Path.Join(current, "settings.json"), RecentJobs.FileToRead("settings.json", current, legacy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        using var t = new TestTree(); // sets IVAROFFLOAD_DATA: then the real profile folders are never read
        Assert.Empty(RecentJobs.LegacyAppDataFolders);
        Assert.DoesNotContain("IngestSorter", RecentJobs.AppDataFileToRead("recent-jobs.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("IVARIngest", RecentJobs.AppDataFileToRead("recent-jobs.json"), StringComparison.Ordinal);
    }

    // ---- Backups IVAR Ingest made -----------------------------------------------------------------------------------

    /// <summary>
    /// Turns a backup into what IVAR Ingest left: its logs in _IVARIngest with IVAR Ingest's tool name, and its ASC MHL
    /// manifests with IVAR Ingest's tool name and ignore patterns (each manifest's new C4 id written into the chain).
    /// </summary>
    internal static void MakeIvarIngestBackup(BackupTree t, int destinations = 2)
    {
        for (int k = 0; k < destinations; k++)
        {
            string dest = t.Dest(k);
            MoveFolder(Path.Join(dest, JobPaths.LogFolderName), Path.Join(dest, JobPaths.IvarIngestLogFolderName));
            foreach (string journal in BackupPaths.FindJournals(dest)) RewriteHeader(journal, h => h["tool"] = "IVAR Ingest 1.0.0-beta.7");

            string ascmhl = Path.Join(dest, AscMhl.FolderName);
            if (!Directory.Exists(ascmhl)) continue;
            string chainPath = Path.Join(ascmhl, AscMhl.ChainFileName);
            XDocument chain = XDocument.Load(chainPath);
            foreach (string manifest in Directory.EnumerateFiles(ascmhl, "*.mhl"))
            {
                XDocument doc = XDocument.Load(manifest);
                doc.Descendants().Single(e => e.Name.LocalName == "tool").Value = "IVAR Ingest";
                doc.Descendants().Where(e => e.Name.LocalName == "pattern" && e.Value == JobPaths.LogFolderName + "/").Remove();
                byte[] bytes = Xml(doc);
                File.WriteAllBytes(manifest, bytes);
                XElement entry = chain.Descendants().Single(e => e.Name.LocalName == "hashlist"
                    && e.Elements().Any(c => c.Name.LocalName == "path" && c.Value == Path.GetFileName(manifest)));
                entry.Elements().Single(c => c.Name.LocalName == "c4").Value = AscMhl.C4(bytes);
            }
            File.WriteAllBytes(chainPath, Xml(chain));
        }
    }

    private static byte[] Xml(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            doc.Save(writer);
        return stream.ToArray();
    }

    private static BackupPlan TopUp(BackupTree t) =>
        BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "ignored", topUp: true);

    [Fact]
    public void An_unfinished_backup_IVAR_Ingest_made_is_found_and_resumed_in_its_own_log_folder()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        var card = t.CardFingerprints();
        Assert.Throws<SimulatedCrash>(() => t.Run(t.Plan(), new CrashAt("after-done", 3)));
        MakeIvarIngestBackup(t);
        string journal = t.Journal(0);
        Assert.Equal(JobPaths.IvarIngestLogFolderName, Path.GetFileName(Path.GetDirectoryName(journal)));
        string id = JournalReader.Read(journal).Header.Id;
        Assert.Equal(journal, BackupPaths.JournalPath(t.Dest(0), id));
        Assert.Equal(t.Dest(1), BackupPaths.FindDestination(t.Dest(1), 0, id));

        BackupResult resumed = BackupTree.Resume(journal);
        Assert.True(resumed.AllVerified, $"{resumed.Status} {resumed.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, card);
            Assert.False(Directory.Exists(Path.Join(t.Dest(k), JobPaths.LogFolderName)), "no second log folder appears");
            Assert.True(resumed.Destinations[k].MhlWritten);
        }
        Assert.True(BackupVerifier.Verify(journal).AllGood);
    }

    [Fact]
    public void A_backup_IVAR_Ingest_made_is_added_to_in_its_own_log_folder_and_its_history_continued()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        MakeIvarIngestBackup(t);
        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        var card = t.CardFingerprints();

        TopUpOffer offer = Assert.IsType<TopUpOffer>(BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "second").TopUp);
        Assert.Equal($@"{JobPaths.IvarIngestLogFolderName}\{BackupRunner.ReplacedFolderName}", offer.ReplacedFolder);
        BackupPlan plan = TopUp(t);
        Assert.True(plan.CanRun, string.Join(" | ", plan.Messages.Select(m => m.Text)));
        BackupResult result = t.Run(plan);
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            t.AssertExactCopy(k, card);
            Assert.Null(result.Destinations[k].MhlRestarted);
            Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(k)));
            Assert.All(BackupPaths.FindJournals(t.Dest(k)), j => Assert.Equal(JobPaths.IvarIngestLogFolderName, Path.GetFileName(Path.GetDirectoryName(j))));
            Assert.Equal(2, BackupPaths.FindJournals(t.Dest(k)).Count());
            Assert.False(Directory.Exists(Path.Join(t.Dest(k), JobPaths.LogFolderName)), "no second log folder appears");
        }
    }

    [Fact]
    public void A_top_up_of_a_backup_IVAR_Ingest_made_moves_old_versions_into_its_log_folder()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        MakeIvarIngestBackup(t);
        string mediaPro = Path.Join(t.Card, @"PRIVATE\M4ROOT\MEDIAPRO.XML");
        File.SetAttributes(mediaPro, FileAttributes.Normal);
        File.WriteAllText(mediaPro, "<rewritten by the camera/>");
        File.SetLastWriteTimeUtc(mediaPro, new DateTime(2024, 5, 7, 9, 0, 0, DateTimeKind.Utc));

        BackupResult result = t.Run(TopUp(t));
        Assert.True(result.AllVerified, $"{result.Status} {result.Message}");
        for (int k = 0; k < 2; k++)
        {
            string replaced = Path.Join(t.Dest(k), JobPaths.IvarIngestLogFolderName, BackupRunner.ReplacedFolderName);
            Assert.Single(Directory.EnumerateFiles(replaced, "MEDIAPRO.XML", SearchOption.AllDirectories));
            Assert.StartsWith(JobPaths.IvarIngestLogFolderName + "\\", result.Destinations[k].MhlRestarted);
            Assert.False(Directory.Exists(Path.Join(t.Dest(k), JobPaths.LogFolderName)), "no second log folder appears");
        }
    }
}

/// <summary>Backups IVAR Ingest made, and added to by IVAR Offload, pass the ASC's own reference tool (pip install ascmhl).</summary>
[Trait("Category", "ascmhl")]
public class LegacyMhlTests
{
    [Fact]
    public void A_backup_IVAR_Ingest_made_verifies_before_and_after_a_top_up()
    {
        using var t = new BackupTree();
        t.TypicalCard();
        Assert.True(t.Run(t.Plan()).AllVerified);
        LegacyTests.MakeIvarIngestBackup(t);
        for (int k = 0; k < 2; k++)
        {
            (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", "-dh", t.Dest(k));
            Assert.True(exit == 0, output);
        }

        t.Add(@"DCIM\100MSDCF\DSC00002.JPG", 25_000);
        Assert.True(t.Run(BackupPlanner.Build(BackupScanner.Scan(t.Card), t.Parents.Take(2).ToList(), "ignored", topUp: true)).AllVerified);
        for (int k = 0; k < 2; k++)
        {
            Assert.Equal(2, AscMhl.GenerationsIn(t.Dest(k)));
            (int exit, string output) = AscMhlTool.Run("ascmhl-debug.exe", "verify", t.Dest(k));
            Assert.True(exit == 0, output);
        }
    }
}
