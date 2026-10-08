using System.Text;
using IvarOffload.Core;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>
/// Planner decisions, mostly with injected drive facts so cards, FAT32 drives and read-only volumes can be tested
/// without the hardware. Paths on C: and D: are only looked at (never created) unless a test makes its own TestTree.
/// </summary>
public class PlannerTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;
    private const string Source = @"D:\offload-planner-test-does-not-exist\Card01";
    private const string Target = @"D:\offload-planner-test-does-not-exist\Card01-Video";

    private static readonly VolumeInfo Ssd = new(@"D:\", 1111, "NTFS", 500 * GB, 1000 * GB) { Label = "SSD", Kind = DriveKind.Fixed };

    private static readonly SystemFolders Folders = new(@"C:\", @"C:\Users\me", @"C:\Users\me\AppData", @"C:\Users\me\AppData\Local\Temp",
        [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"],
        [("Pictures", @"C:\Users\me\Pictures"), ("Downloads", @"C:\Users\me\Downloads")]);

    private static PlanEnvironment Env(Func<string, VolumeInfo>? volumes = null, Func<string, IReadOnlyCollection<string>>? subfolders = null,
        Func<string, bool?>? canDelete = null, Func<string, IEnumerable<string>>? journals = null, Func<string, bool>? fileExists = null,
        Func<string, IReadOnlyCollection<string>>? files = null, string? testCard = null) => new()
    {
        VolumeOf = volumes ?? (_ => Ssd),
        ClusterSizeOf = _ => 4096,
        CanDelete = canDelete ?? (_ => true),
        SubfolderNames = subfolders ?? (_ => []),
        FileNames = files ?? (_ => []),
        FileExists = fileExists ?? (_ => false),
        SyncOf = _ => null,
        JournalsIn = journals ?? (_ => []),
        RealPathOf = path => path, // the made-up drives are never looked at
        Folders = Folders,
        TestCard = testCard,
    };

    /// <summary>Drive facts from a table: folder -> subfolder names (and file names, for the second table).</summary>
    private static Func<string, IReadOnlyCollection<string>> Table(params (string Folder, string[] Names)[] rows) =>
        folder => rows.FirstOrDefault(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)).Names ?? [];

    private static VolumeInfo Card(DriveKind kind, string fileSystem = "exFAT") =>
        new(@"F:\", 42, fileSystem, 10 * GB, 64 * GB) { Label = "SONY_A", Kind = kind };

    private static ScanResult Scan(string source, params (string Path, long Size)[] entries) => Scan(source, null, entries);

    private static ScanResult Scan(string source, Func<SourceFile, bool>? isMovieFrame, params (string Path, long Size)[] entries) =>
        Scan(source, isMovieFrame, entries.Select(e => (e.Path, e.Size, FileAttributes.Archive)).ToArray());

    /// <summary>Clips named IMG_... stand for tagged Live Photo clips (see <see cref="LivePhotoClip"/>); nothing is read.</summary>
    private static ScanResult Scan(string source, Func<SourceFile, bool>? isMovieFrame, params (string Path, long Size, FileAttributes Attributes)[] entries)
    {
        var files = entries.Select(e => new SourceFile
        {
            RelativePath = e.Path,
            Size = e.Size,
            CreationTime = 0,
            LastWriteTime = 0,
            Attributes = e.Attributes,
        }).ToList();
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, null, Path.GetFileName(source), isMovieFrame,
            clip => clip.Name.StartsWith("IMG_", StringComparison.OrdinalIgnoreCase));
        return new ScanResult { SourceRoot = source, Files = files, Folders = [], SkippedFolders = [], Problems = [], Units = units };
    }

    private static ScanResult ScanPaths(params string[] paths) => Scan(Source, paths.Select(p => (p, 1000L)).ToArray());

    private static MovePlan Plan(ScanResult scan, PlanEnvironment env, MoveMode mode = MoveMode.Videos, string target = Target) =>
        Planner.Build(scan, target, mode, verifyChecksums: true, env);

    private static IEnumerable<string> Errors(MovePlan plan) => plan.Messages.Where(m => m.Level == MessageLevel.Error).Select(m => m.Text);

    // ---- Memory cards and too-broad sources -----------------------------------------------------------------------------

    private static IEnumerable<string> Warnings(MovePlan plan) => plan.Messages.Where(m => m.Level == MessageLevel.Warning).Select(m => m.Text);

    [Fact]
    public void A_memory_card_is_flagged_with_the_backup_question_but_can_still_be_sorted()
    {
        // Owner decision: folders are arbitrary and cameras (e.g. Blackmagic Pyxis) record straight to SSDs, so a
        // card-like source is flagged and the GUI asks; it is not refused.
        VolumeInfo card = Card(DriveKind.Removable);
        MovePlan plan = Plan(Scan(@"F:\", (@"DCIM\100MSDCF\C0001.MP4", 1000)),
            Env(volumes: p => p.StartsWith("F:", StringComparison.OrdinalIgnoreCase) ? card : Ssd,
                subfolders: Table((@"F:\", ["DCIM", "PRIVATE"]), (@"F:\DCIM", ["100MSDCF"]))),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Empty(Errors(plan));
        Assert.True(plan.CanRun);
        Assert.Contains("This looks like a memory card or camera drive (F: SONY_A). A sort moves files to different folders on it. "
            + "First, use the Backup tab to back up the card to a different drive. Then sort the backup.", Warnings(plan));
        Assert.Equal("F: SONY_A", plan.MemoryCard);
    }

    [Fact]
    public void The_test_hook_flags_any_source_as_a_card_and_changes_nothing_else()
    {
        // IVAROFFLOAD_TEST_CARD lets tools/Test-Gui.ps1 answer the card question without a real card.
        ScanResult scan = ScanPaths(@"a\C0001.MP4", @"a\DSC0001.JPG");
        MovePlan normal = Plan(scan, Env());
        MovePlan flagged = Plan(scan, Env(testCard: "F: TEST_CARD"));
        Assert.Null(normal.MemoryCard);
        Assert.Equal("F: TEST_CARD", flagged.MemoryCard);
        Assert.Contains(SourceGuards.MemoryCardWarning("F: TEST_CARD"), Warnings(flagged));
        Assert.Equal(normal.CanRun, flagged.CanRun);
        Assert.Equal(normal.ToMove.Select(f => f.RelativePath), flagged.ToMove.Select(f => f.RelativePath));
        Assert.Equal(normal.Messages.Count + 1, flagged.Messages.Count);
    }

    [Fact]
    public void A_write_protected_card_is_still_refused()
    {
        VolumeInfo locked = Card(DriveKind.Removable) with { FileSystemFlags = 0x00080000 }; // FILE_READ_ONLY_VOLUME
        MovePlan plan = Plan(Scan(@"F:\", (@"DCIM\100MSDCF\C0001.MP4", 1000)),
            Env(volumes: p => p.StartsWith("F:", StringComparison.OrdinalIgnoreCase) ? locked : Ssd,
                subfolders: Table((@"F:\", ["DCIM"]), (@"F:\DCIM", ["100MSDCF"]))),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.False(plan.CanRun);
        Assert.Contains(SourceGuards.CannotRemoveError, Errors(plan));
    }

    [Fact]
    public void A_breakout_folder_inside_the_source_is_allowed_and_left_out_of_the_scan()
    {
        using var t = new TestTree();
        t.Add(@"A001_09261234_C001.braw", 1000);
        t.Add(@"STILLS\A001_0001.JPG", 100);
        string breakout = Path.Join(t.Source, "Video");
        t.Add(@"Video\A000_09251111_C009.braw", 1000); // moved there by an earlier sort: must not be sorted again

        ScanResult scan = Scanner.Scan(t.Source, target: breakout);
        Assert.DoesNotContain(scan.Files, f => f.RelativePath.StartsWith(@"Video\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scan.SkippedFolders, s => s.RelativePath == "Video" && s.Reason == "the target folder");
        MovePlan plan = Planner.Build(scan, breakout, MoveMode.Videos, verifyChecksums: true);
        Assert.True(plan.CanRun, string.Join(" | ", Errors(plan)));
        Assert.Equal(TransferMethod.Rename, plan.Method); // same drive: moved by renaming, never copied
        Assert.Equal("A001_09261234_C001.braw", Assert.Single(plan.ToMove).RelativePath);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Info && m.Text.StartsWith("The target folder is inside the source folder (Video)."));

        // A scan that did include the target folder is refused rather than sorting its files a second time.
        MovePlan stale = Planner.Build(Scanner.Scan(t.Source), breakout, MoveMode.Videos, verifyChecksums: true);
        Assert.False(stale.CanRun);
        Assert.Contains("The target folder is inside the source folder, but the scan included it in the source folder. Scan again.", Errors(stale));

        // The other way round stays refused.
        Assert.Equal("The source folder cannot be inside the target folder.", Planner.ValidateTarget(breakout, t.Source, out _));
    }

    [Fact]
    public void A_target_named_through_another_path_into_the_source_is_still_the_folder_inside_it()
    {
        // A junction here; a subst or mapped drive letter (Z:\Card\Video for \\NAS\share\Card\Video) or a short name alike.
        using var t = new TestTree();
        t.Add(@"A\C0001.MP4", 1000);
        t.Add(@"A\C0002.MP4", 1000);
        t.Add(@"A\DSC00001.JPG", 100);
        string alias = Path.Join(t.Root, "SSD");
        JobTestKit.Junction(alias, t.Source);
        string target = Path.Join(alias, "Video");

        MovePlan first = Planner.Build(Scanner.Scan(t.Source, target: target), target, MoveMode.Videos, verifyChecksums: true);
        Assert.Contains(first.Messages, m => m.Level == MessageLevel.Info && m.Text.StartsWith("The target folder is inside the source folder (Video)."));
        Assert.Equal(2, first.FilesToTransfer);
        Assert.Equal(RunStatus.Completed, t.Run(first).Status);

        // Scanned again, as the app does after every sort: the sorted files are left out, not offered to move again.
        ScanResult again = Scanner.Scan(t.Source, target: target);
        Assert.DoesNotContain(again.Files, f => f.RelativePath.StartsWith(@"Video\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(again.SkippedFolders, s => s.RelativePath.Equals("Video", StringComparison.OrdinalIgnoreCase) && s.Reason == "the target folder");
        MovePlan rescan = Planner.Build(again, target, MoveMode.Videos, verifyChecksums: true);
        Assert.Empty(rescan.ToMove);
        Assert.DoesNotContain(Errors(rescan), e => e.Contains("the scan included it in the source folder"));

        // The source itself, or a folder around it, under another name is refused like the plain path.
        Assert.Equal("The target folder must be different from the source folder.", Planner.ValidateTarget(t.Source, alias, out _));
        Assert.Equal("The source folder cannot be inside the target folder.", Planner.ValidateTarget(Path.Join(t.Source, "A"), alias, out _));
        Assert.True(JobPaths.SamePath(SafeFile.RealPath(t.Source), SafeFile.RealPath(alias)));
        Assert.True(JobPaths.SamePath(Path.Join(SafeFile.RealPath(t.Source), @"New\Folder"), SafeFile.RealPath(Path.Join(alias, @"New\Folder"))));
    }

    /// <summary>A Sony card whose reader reports it as a fixed drive (common with CFexpress): every card folder counts.</summary>
    private static readonly Func<string, IReadOnlyCollection<string>> SonyCardRoot = Table(
        (@"F:\", ["AVCHD", "Backups", "DCIM", "PRIVATE"]), (@"F:\DCIM", ["100MSDCF"]), (@"F:\PRIVATE", ["M4ROOT", "SONY"]), (@"F:\AVCHD", ["BDMV"]));

    [Theory]
    [InlineData(DriveKind.Removable, @"F:\Backups\Card1", true)]  // removable volume with card folders at its root
    [InlineData(DriveKind.Fixed, @"F:\", true)]                    // the root of a drive that looks like a card
    [InlineData(DriveKind.Fixed, @"F:\DCIM\100MSDCF", true)]       // inside the card's own folders ...
    [InlineData(DriveKind.Fixed, @"F:\PRIVATE", true)]             // ... every one of them, not just the first by name
    [InlineData(DriveKind.Fixed, @"F:\PRIVATE\M4ROOT\CLIP", true)]
    [InlineData(DriveKind.Fixed, @"F:\AVCHD\BDMV", true)]
    [InlineData(DriveKind.Fixed, @"F:\Backups\Card1", false)]      // a card dump inside a folder on an SSD is fine
    public void Card_rule_uses_removable_root_and_card_folder_facts(DriveKind kind, string source, bool flagged)
    {
        IReadOnlyList<string> signs = SourceGuards.CardSignsAtRoot(@"F:\", SonyCardRoot, _ => []);
        Assert.Equal(new[] { "AVCHD", "DCIM", "PRIVATE" }, signs);
        Assert.Equal(flagged, SourceGuards.CheckMemoryCard(source, Card(kind), signs) is { Level: MessageLevel.Warning });
        Assert.Null(SourceGuards.CheckMemoryCard(source, Card(kind), []));
        Assert.Null(SourceGuards.CheckMemoryCard(source, Card(kind, "NTFS"), signs)); // cameras never write NTFS
    }

    [Fact]
    public void Folders_that_only_share_a_card_folder_name_are_not_cards()
    {
        // A personal "Private" folder, an empty DCIM and an AVCHD folder without BDMV on an exFAT backup drive.
        var root = Table((@"D:\", ["AVCHD", "DCIM", "Private", "Work"]), (@"D:\Private", ["Shoots", "Taxes"]), (@"D:\AVCHD", ["old"]));
        Assert.Empty(SourceGuards.CardSignsAtRoot(@"D:\", root, _ => []));
        var backup = new VolumeInfo(@"D:\", 1, "exFAT", 500 * GB, 1000 * GB) { Label = "BACKUP", Kind = DriveKind.Fixed };
        MovePlan plan = Plan(Scan(@"D:\Private\Shoots\2025-09-20 Smith", (@"C0001.MP4", 1000)), Env(volumes: _ => backup, subfolders: root),
            target: @"D:\Private\Shoots\Smith-Video");
        Assert.Null(plan.MemoryCard);
        Assert.Empty(Errors(plan));
    }

    [Fact]
    public void P2_red_and_arri_card_roots_count_as_card_folders_but_a_plain_contents_folder_does_not()
    {
        Assert.Equal(new[] { "CONTENTS" }, SourceGuards.CardSignsAtRoot(@"G:\", Table((@"G:\", ["CONTENTS"]), (@"G:\CONTENTS", ["CLIP", "VIDEO"])), _ => []));
        Assert.Equal(new[] { "A001_0101XY.RDM" }, SourceGuards.CardSignsAtRoot(@"G:\", Table((@"G:\", ["A001_0101XY.RDM"])), _ => []));
        Assert.Empty(SourceGuards.CardSignsAtRoot(@"G:\", Table((@"G:\", ["CONTENTS"]), (@"G:\CONTENTS", ["Invoices"])), _ => []));
        // ARRI writes a reel folder at the card root that holds the reel's clips.
        Assert.Equal(new[] { "A016R1K4" }, SourceGuards.CardSignsAtRoot(@"G:\", Table((@"G:\", ["A016R1K4"])),
            Table((@"G:\A016R1K4", ["A016C001_120126_R1K4.mxf", "A016R1K4.ale"]))));
        Assert.Empty(SourceGuards.CardSignsAtRoot(@"G:\", Table((@"G:\", ["B123R4XY"])), Table((@"G:\B123R4XY", ["notes.txt"]))));
    }

    [Theory]
    [InlineData(DriveKind.Removable, "A001_09201234_C001.braw", @"F:\", true)]  // a Blackmagic card: clips at the root
    [InlineData(DriveKind.Removable, "A001_09201234_C001.braw", @"F:\Proxy", true)]
    [InlineData(DriveKind.Fixed, "A001_09201234_C001.braw", @"F:\", true)]      // a USB SSD a Blackmagic camera recorded to
    [InlineData(DriveKind.Fixed, "A001_09201234_C001.braw", @"F:\Graded", false)]
    [InlineData(DriveKind.Removable, "ATOMOS_0001.MOV", @"F:\", true)]          // clips at the root of removable media
    [InlineData(DriveKind.Removable, "ATOMOS_0001.MOV", @"F:\Dumps\Card1", false)]
    [InlineData(DriveKind.Fixed, "clip.MOV", @"F:\", false)]                    // a backup SSD with a loose clip: only the drive-root warning
    public void Clips_recorded_straight_to_the_card_root_show_a_card(DriveKind kind, string rootFile, string source, bool refused)
    {
        IReadOnlyList<string> signs = SourceGuards.CardSignsAtRoot(@"F:\", Table((@"F:\", ["Dumps", "Graded", "Proxy"])), Table((@"F:\", [rootFile, "notes.txt"])));
        Assert.Equal(new[] { rootFile }, signs);
        Assert.Equal(refused, SourceGuards.CheckMemoryCard(source, Card(kind), signs) is not null);
    }

    [Theory]
    [InlineData(@"C:\", MessageLevel.Error)]
    [InlineData(@"C:\Windows\System32", MessageLevel.Error)]
    [InlineData(@"C:\Program Files\App", MessageLevel.Error)]
    [InlineData(@"C:\Users\me", MessageLevel.Error)]
    [InlineData(@"C:\Users", MessageLevel.Error)]
    [InlineData(@"C:\Users\me\AppData\Roaming\x", MessageLevel.Error)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\unzipped", null)]
    [InlineData(@"C:\Users\me\Pictures", MessageLevel.Warning)]
    [InlineData(@"C:\Users\me\Downloads", MessageLevel.Warning)]
    [InlineData(@"C:\Users\me\Pictures\Card dumps", null)]
    [InlineData(@"D:\", MessageLevel.Warning)]
    [InlineData(@"D:\Dumps", null)]
    public void Too_broad_sources_are_refused_or_warned_about(string source, MessageLevel? expected)
    {
        var volume = new VolumeInfo(Path.GetPathRoot(source)!, 1, "NTFS", GB, GB);
        PlanMessage? message = SourceGuards.CheckBroad(source, volume, Folders, MoveMode.Videos);
        Assert.Equal(expected, message?.Level);
        if (expected == MessageLevel.Warning) Assert.Contains("Choose the folder with the card backups", message!.Text);
    }

    [Fact]
    public void A_source_inside_a_card_structure_is_refused_and_the_card_folder_is_suggested()
    {
        const string card = @"D:\offload-planner-test-does-not-exist\Dumps\SonyA";
        string source = card + @"\PRIVATE\M4ROOT";
        MovePlan plan = Plan(Scan(source, (@"CLIP\C0001.MP4", 1000), (@"THMBNL\C0001T01.JPG", 10)), Env(),
            target: @"D:\offload-planner-test-does-not-exist\Out");
        Assert.False(plan.CanRun);
        Assert.Equal(card, plan.SuggestedSource);
        Assert.Contains($@"You selected a folder inside a video card structure (...\M4ROOT). Choose the folder that holds the whole card instead: {card}.", Errors(plan));

        // P2: the source is the CONTENTS folder itself; the card is the folder above it.
        var p2 = CardStructures.FindEnclosing(@"X:\Dumps\CardA\CONTENTS",
            p => p.EndsWith("CONTENTS") ? ["CLIP", "VIDEO", "AUDIO"] : [], _ => false);
        Assert.Equal((@"X:\Dumps\CardA\CONTENTS", "Panasonic P2 card structure", @"X:\Dumps\CardA"), p2);
        // AVCHD: BDMV inside AVCHD inside PRIVATE; the outermost structure decides.
        var avchd = CardStructures.FindEnclosing(@"X:\Dumps\Lumix\PRIVATE\AVCHD\BDMV\STREAM", _ => [], _ => false);
        Assert.Equal(@"X:\Dumps\Lumix", avchd?.SuggestedFolder);
        // A folder inside a Sony card copied without its wrapper: the card folder itself is the whole card.
        var sony = CardStructures.FindEnclosing(@"X:\Dumps\FX3_A\CLIP", p => p.EndsWith("FX3_A") ? ["CLIP", "SUB", "THMBNL"] : [],
            f => f.EndsWith(@"FX3_A\MEDIAPRO.XML"));
        Assert.Equal(@"X:\Dumps\FX3_A", sony?.SuggestedFolder);
        // That card folder picked as the source is not inside a structure: it is the card.
        Assert.Null(CardStructures.FindEnclosing(@"X:\Dumps\FX3_A", p => p.EndsWith("FX3_A") ? ["CLIP", "SUB", "THMBNL"] : [],
            f => f.EndsWith(@"FX3_A\MEDIAPRO.XML")));
        Assert.Null(CardStructures.FindEnclosing(@"X:\Dumps\CardA", _ => ["DCIM"], _ => false));
        // A DCIM folder next to the unwrapped Sony folders is not part of that card structure.
        Assert.Null(CardStructures.FindEnclosing(@"X:\Dumps\FX3_A\DCIM\100MSDCF", p => p.EndsWith("FX3_A") ? ["CLIP", "DCIM", "SUB", "THMBNL"] : [],
            f => f.EndsWith(@"FX3_A\MEDIAPRO.XML")));
    }

    [Fact]
    public void Inside_a_card_structure_on_a_memory_card_the_card_is_flagged_and_the_whole_card_is_suggested()
    {
        VolumeInfo card = Card(DriveKind.Removable);
        MovePlan plan = Plan(Scan(@"F:\PRIVATE\M4ROOT", (@"CLIP\C0001.MP4", 1000)),
            Env(volumes: p => p.StartsWith("F:", StringComparison.OrdinalIgnoreCase) ? card : Ssd,
                subfolders: Table((@"F:\", ["DCIM", "PRIVATE"]), (@"F:\DCIM", ["100MSDCF"]), (@"F:\PRIVATE", ["M4ROOT"])),
                canDelete: _ => throw new InvalidOperationException("a refused source is never probed")),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Contains(SourceGuards.MemoryCardWarning("F: SONY_A"), Warnings(plan));
        Assert.Equal(@"You selected a folder inside a video card structure (...\M4ROOT). Choose the folder that holds the whole card instead: F:\.",
            Assert.Single(Errors(plan)));
        Assert.Equal(@"F:\", plan.SuggestedSource);
    }

    [Fact]
    public void A_card_folder_that_would_be_refused_itself_is_not_suggested()
    {
        // A card structure straight in the root of the Windows drive: the drive itself is no better.
        MovePlan plan = Plan(Scan(@"C:\M4ROOT", (@"CLIP\C0001.MP4", 1000)), Env(), target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Null(plan.SuggestedSource);
        Assert.Contains(@"You selected a folder inside a video card structure (...\M4ROOT). Copy the card into a folder of its own. Then choose that folder as the source folder.",
            Errors(plan));

        // On a fixed exFAT drive with a camera DCIM at its root, the drive root is only flagged as card-like, not
        // refused, so it is still the folder to suggest.
        var drive = new VolumeInfo(@"F:\", 42, "exFAT", 10 * GB, 64 * GB) { Kind = DriveKind.Fixed };
        MovePlan onCardLikeDrive = Plan(Scan(@"F:\M4ROOT", (@"CLIP\C0001.MP4", 1000)),
            Env(volumes: p => p.StartsWith("F:", StringComparison.OrdinalIgnoreCase) ? drive : Ssd,
                subfolders: Table((@"F:\", ["DCIM", "M4ROOT"]), (@"F:\DCIM", ["100MSDCF"]))),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Null(onCardLikeDrive.MemoryCard);
        Assert.Equal(@"F:\", onCardLikeDrive.SuggestedSource);
    }

    [Fact]
    public void A_source_inside_an_application_library_is_refused_and_inside_a_project_is_warned_about()
    {
        MovePlan library = Plan(Scan(@"D:\Pictures\Photos Library.photoslibrary\originals", (@"4\4F1C.mov", 1000)), Env(),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Contains(Errors(library), e => e.StartsWith(@"This folder is inside an application library: Apple Photos library (D:\Pictures\Photos Library.photoslibrary)."));

        // A Luminar catalog folder is known by its catalog file.
        MovePlan luminar = Plan(Scan(@"D:\Pictures\Luminar Neo Catalog", (@"Cache Documents\a.jpg", 1000)),
            Env(files: Table((@"D:\Pictures\Luminar Neo Catalog", ["Luminar Neo Catalog.luminarneo"]))), MoveMode.Photos,
            target: @"D:\offload-planner-test-does-not-exist\Photos");
        Assert.Contains(Errors(luminar), e => e.StartsWith(@"This folder is inside an application library: Luminar catalog (D:\Pictures\Luminar Neo Catalog)."));
        // A catalog saved in the photo folder itself (not in a folder of its own) does not make that folder a library.
        MovePlan photos = Plan(Scan(@"D:\Photos\2025-09 Wedding", (@"C0001.MP4", 1000)),
            Env(files: Table((@"D:\Photos", ["Main.luminarneo"]))), target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.True(photos.CanRun, string.Join(" | ", Errors(photos)));

        MovePlan project = Plan(Scan(@"D:\Edits\Promo\Footage", (@"A001_C003.MP4", 1000)),
            Env(files: Table((@"D:\Edits\Promo", ["Promo.prproj", "notes.txt"]))), target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.True(project.CanRun);
        Assert.Contains(project.Messages, m => m.Level == MessageLevel.Warning
            && m.Text == @"This folder is inside an editing/processing project: Premiere Pro project (Promo.prproj in D:\Edits\Promo). If the project uses these files, it will show them as missing after the move.");

        // Processing folders without a project file (OpenDroneMap, DJI Terra) count too.
        MovePlan odm = Plan(Scan(@"D:\Proc\ODM_Crop\images", (@"DJI_0001.JPG", 1000)),
            Env(subfolders: Table((@"D:\Proc\ODM_Crop", ["images", "odm_dem", "odm_orthophoto"]))), MoveMode.Photos,
            target: @"D:\offload-planner-test-does-not-exist\Photos");
        Assert.Contains(odm.Messages, m => m.Level == MessageLevel.Warning
            && m.Text.StartsWith(@"This folder is inside an editing/processing project: OpenDroneMap project (odm_dem folder in D:\Proc\ODM_Crop)."));
        MovePlan terra = Plan(Scan(@"D:\Proc\Terra_Quarry\map", (@"result.tif", 1000)),
            Env(subfolders: Table((@"D:\Proc\Terra_Quarry", ["map"])), files: Table((@"D:\Proc\Terra_Quarry\map", ["result.tif", "dsm.tif"]))),
            MoveMode.Photos, target: @"D:\offload-planner-test-does-not-exist\Photos");
        Assert.Contains(terra.Messages, m => m.Text.StartsWith(@"This folder is inside an editing/processing project: DJI Terra project (map\result.tif in D:\Proc\Terra_Quarry)."));
        MovePlan terraLidar = Plan(Scan(@"D:\Proc\Terra_L2\images", (@"DJI_0001.JPG", 1000)),
            Env(subfolders: Table((@"D:\Proc\Terra_L2", ["images", "lidars"]), (@"D:\Proc\Terra_L2\lidars", ["terra_las", "terra_pnts"]))),
            MoveMode.Photos, target: @"D:\offload-planner-test-does-not-exist\Photos");
        Assert.Contains(terraLidar.Messages, m => m.Text.StartsWith(@"This folder is inside an editing/processing project: DJI Terra project (lidars\terra_las folder in D:\Proc\Terra_L2)."));
        // Folders merely called Map and Models (location scouting, casting photos) are no DJI Terra project.
        MovePlan shoot = Plan(Scan(@"D:\Shoots\Ad\Map", (@"scout.JPG", 1000)),
            Env(subfolders: Table((@"D:\Shoots\Ad", ["Map", "Models", "Lidars"]), (@"D:\Shoots\Ad\Models", ["pc"]))),
            MoveMode.Photos, target: @"D:\offload-planner-test-does-not-exist\Photos");
        Assert.DoesNotContain(shoot.Messages, m => m.Text.Contains("editing/processing project"));

        // A Final Cut Pro XML export (e.g. an Atomos recorder's) above the source is not a project.
        MovePlan fcpxml = Plan(Scan(@"D:\Ninja\Clips", (@"ATOMOS_NINJAV_S001_S001_T001.MOV", 1000)),
            Env(files: Table((@"D:\Ninja", ["ATOMOS_NINJAV_S001.fcpxml"]))), target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.DoesNotContain(fcpxml.Messages, m => m.Text.Contains("editing/processing project"));
    }

    [Fact]
    public void A_source_that_cannot_be_removed_from_is_refused_before_anything_is_copied()
    {
        MovePlan denied = Plan(ScanPaths(@"a\clip.MOV"), Env(canDelete: _ => false));
        Assert.Contains(SourceGuards.CannotRemoveError, Errors(denied));
        Assert.False(denied.CanRun);

        var locked = new VolumeInfo(@"D:\", 1111, "exFAT", GB, GB) { FileSystemFlags = 0x00080000 };
        MovePlan readOnly = Plan(ScanPaths(@"a\clip.MOV"), Env(volumes: _ => locked, canDelete: _ => throw new InvalidOperationException("not probed")));
        Assert.Contains(SourceGuards.CannotRemoveError, Errors(readOnly));

        MovePlan unknown = Plan(ScanPaths(@"a\clip.MOV"), Env(canDelete: _ => null)); // e.g. open in another program: decided at run time
        Assert.True(unknown.CanRun);
    }

    [Fact]
    public void The_delete_probe_opens_without_deleting_and_accepts_read_only_files()
    {
        using var t = new TestTree();
        string normal = t.Add(@"a\clip.MOV");
        string readOnly = t.Add(@"a\ro.MOV", attributes: FileAttributes.ReadOnly);
        var before = TestTree.Fingerprint(normal);
        Assert.True(DriveFacts.ProbeDelete(normal));
        Assert.True(DriveFacts.ProbeDelete(readOnly)); // the engine deletes read-only originals too, ignoring the attribute
        Assert.Equal(before, TestTree.Fingerprint(normal));
        Assert.True(File.Exists(readOnly));
        Assert.Null(DriveFacts.ProbeDelete(Path.Join(t.Source, "missing.MOV")));
    }

    // ---- Target checks ------------------------------------------------------------------------------------------------

    private static SourceFile File1(string rel, long size) =>
        new() { RelativePath = rel, Size = size, CreationTime = 0, LastWriteTime = 0, Attributes = FileAttributes.Archive };

    [Fact]
    public void Fat32_target_refuses_files_of_4_gb_or_more_and_names_them()
    {
        var fat32 = new VolumeInfo(@"G:\", 7, "FAT32", 500 * GB, 1000 * GB) { Label = "STICK" };
        List<SourceFile> files = [File1(@"a\C0003.MP4", 5 * GB), File1(@"a\C0004.MP4", 4 * GB), File1(@"a\small.MP4", 10 * MB)];
        string error = Assert.Single(Planner.CheckTarget(fat32, 32768, files, copying: true)).Text;
        Assert.Contains("FAT32", error);
        Assert.Contains("2 file(s)", error);
        Assert.Contains("C0003.MP4", error);

        var exfat = fat32 with { FileSystem = "exFAT" };
        Assert.Empty(Planner.CheckTarget(exfat, 32768, files, copying: true));
        Assert.Empty(Planner.CheckTarget(fat32, 32768, [File1(@"a\ok.MP4", uint.MaxValue)], copying: true));
    }

    [Fact]
    public void Free_space_includes_a_cluster_allowance_per_file_and_a_read_only_target_is_refused()
    {
        var target = new VolumeInfo(@"G:\", 7, "exFAT", 300 * MB, 1000 * GB);
        var sidecars = Enumerable.Range(0, 1000).Select(i => File1($@"a\{i}.xmp", 1)).ToList();
        Assert.Empty(Planner.CheckTarget(target, 0, sidecars, copying: true));
        // 1,000 tiny files on 128 KB clusters take 125 MB, which with the margin no longer fits in 300 MB.
        Assert.Contains(Planner.CheckTarget(target, 128 * 1024, sidecars, copying: true), m => m.Text.StartsWith("Not enough free space"));

        var readOnly = target with { FileSystemFlags = 0x00080000 };
        Assert.Contains(Planner.CheckTarget(readOnly, 0, sidecars, copying: false), m => m.Level == MessageLevel.Error && m.Text.Contains("read-only"));
    }

    [Fact]
    public void A_fat32_target_on_another_drive_blocks_the_plan()
    {
        var fat32 = new VolumeInfo(@"C:\", 7, "FAT32", 500 * GB, 1000 * GB);
        MovePlan plan = Plan(Scan(Source, (@"a\C0003.MP4", 5 * GB)), Env(volumes: p => p.StartsWith("C:") ? fat32 : Ssd),
            target: @"C:\offload-planner-test-does-not-exist\Video");
        Assert.Equal(TransferMethod.Copy, plan.Method);
        Assert.Contains(Errors(plan), e => e.Contains("cannot store files of 4 GB or more"));
    }

    // ---- Conflicts and groups -----------------------------------------------------------------------------------------

    [Fact]
    public void Identical_copies_are_skipped_but_a_different_file_holds_back_its_whole_clip()
    {
        using var t = new TestTree();
        t.Add(@"d\DJI_0001.MP4");
        t.Add(@"d\DJI_0001.LRF");
        t.Add(@"d\DJI_0001.SRT");
        string same = t.Add(@"d\DJI_0002.MP4");
        t.Add(@"d\DJI_0002.SRT");
        t.Add(@"d\DJI_0003.MP4");
        Directory.CreateDirectory(Path.Join(t.Target, "d"));
        File.WriteAllText(Path.Join(t.Target, @"d\DJI_0001.MP4"), "card A's clip, a different file");
        File.Copy(same, Path.Join(t.Target, @"d\DJI_0002.MP4"));

        MovePlan plan = t.Plan();
        Assert.Equal(new[] { @"d\DJI_0002.MP4", @"d\DJI_0002.SRT", @"d\DJI_0003.MP4" }, plan.ToMove.Select(f => f.RelativePath));
        Assert.Equal(@"d\DJI_0002.MP4", Assert.Single(plan.IdenticalConflicts).RelativePath);
        SourceFile clash = Assert.Single(plan.DifferentConflicts);
        Assert.Equal(@"d\DJI_0001.MP4", clash.RelativePath);
        Assert.Equal(FileNote.DifferentInTarget, clash.Note);
        Assert.Equal("stays: a DIFFERENT file with the same name is already in the target folder", clash.Reason);
        Assert.Equal(3, plan.HeldBack.Count);
        SourceFile proxy = plan.HeldBack.Single(f => f.Name == "DJI_0001.LRF");
        Assert.Equal(FileNote.HeldWithGroup, proxy.Note);
        Assert.StartsWith("stays with DJI_0001.MP4: ", proxy.Reason);
        Assert.True(proxy.NeedsAttention);
        // Conflicts keeps its old meaning (planned files the job skips), so ToMove minus Conflicts is what really moves.
        Assert.Equal(plan.IdenticalConflicts, plan.Conflicts);
        Assert.Equal(2, plan.FilesToTransfer);
        Assert.Equal(plan.FilesToTransfer, plan.ToMove.Count - plan.Conflicts.Count);
        Assert.True(plan.CanRun);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Info && m.Text == "1 file is already in the target folder (same name, size and date). The sort will skip it.");
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("1 file(s) have the same name as DIFFERENT files")
            && m.Text.Contains("Sort each card into its own folder"));

        // Planning the same scan again (after the clash is gone) starts from the classifier's words, not the old plan's.
        File.Delete(Path.Join(t.Target, @"d\DJI_0001.MP4"));
        MovePlan again = Planner.Build(plan.Scan, t.Target, MoveMode.Videos, verifyChecksums: true);
        Assert.Empty(again.HeldBack);
        Assert.Equal(FileNote.None, again.ToMove.Single(f => f.Name == "DJI_0001.LRF").Note);
        Assert.Equal("video (.LRF)", again.ToMove.Single(f => f.Name == "DJI_0001.LRF").Reason);
    }

    [Fact]
    public void A_clash_inside_a_card_structure_keeps_the_whole_structure_in_the_source()
    {
        using var t = new TestTree();
        t.Add(@"SonyB\PRIVATE\M4ROOT\CLIP\C0001.MP4");
        t.Add(@"SonyB\PRIVATE\M4ROOT\CLIP\C0002.MP4");
        t.Add(@"SonyB\PRIVATE\M4ROOT\MEDIAPRO.XML");
        Directory.CreateDirectory(Path.Join(t.Target, @"SonyB\PRIVATE\M4ROOT"));
        File.WriteAllText(Path.Join(t.Target, @"SonyB\PRIVATE\M4ROOT\MEDIAPRO.XML"), "card A's index");
        MovePlan plan = t.Plan();
        Assert.Empty(plan.ToMove);
        Assert.Equal(3, plan.HeldBack.Count);
        Assert.False(plan.CanRun);
        Assert.DoesNotContain(plan.Messages, m => m.Text.Contains("nothing to move"));
    }

    [Theory]
    [InlineData("exFAT", 1, true)]   // FAT and exFAT keep times in 2-second steps
    [InlineData("exFAT", 3, false)]
    [InlineData("NTFS", 1, false)]
    [InlineData("NTFS", 0, true)]
    public void Same_name_size_and_date_is_already_there_within_the_fat_time_step(string fileSystem, int secondsLater, bool alreadyThere)
    {
        using var t = new TestTree();
        string clip = t.Add(@"a\clip.MOV");
        string copy = Path.Join(t.Target, @"a\clip.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(clip, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(clip).AddSeconds(secondsLater));
        var volume = new VolumeInfo(Path.GetPathRoot(t.Root)!, 5, fileSystem, 500 * GB, 1000 * GB);
        MovePlan plan = Planner.Build(Scanner.Scan(t.Source), t.Target, MoveMode.Videos, verifyChecksums: true, Env(volumes: _ => volume));
        Assert.Equal(alreadyThere ? 1 : 0, plan.IdenticalConflicts.Count);
        Assert.Equal(alreadyThere ? 0 : 1, plan.DifferentConflicts.Count);
    }

    [Fact]
    public void A_plan_with_only_identical_copies_cannot_run()
    {
        using var t = new TestTree();
        string only = t.Add(@"a\only.MOV");
        Directory.CreateDirectory(Path.Join(t.Target, "a"));
        File.Copy(only, Path.Join(t.Target, @"a\only.MOV"));
        MovePlan plan = t.Plan();
        Assert.Single(plan.ToMove);
        Assert.Single(plan.IdenticalConflicts);
        Assert.Equal(0, plan.FilesToTransfer);
        Assert.False(plan.CanRun);
    }

    // ---- Explanations -------------------------------------------------------------------------------------------------

    [Fact]
    public void Split_folders_are_warned_about_and_listed_by_folder()
    {
        MovePlan plan = Plan(ScanPaths(@"a\clip.MOV", @"a\notes.bin", @"a\photo.JPG", @"a\Thumbs.db", @"a\._clip.MOV", @"p\x.JPG", @"q\y.MOV",
            @"CardA\CONTENTS\CLIP\0001AB.XML", @"CardA\CONTENTS\CLIP\0001AB.MXF"), Env());
        PlanMessage split = Assert.Single(plan.Messages, m => m.Text.Contains("The sort will split"));
        Assert.Equal(MessageLevel.Warning, split.Level);
        Assert.Contains(@"a: .bin x1", split.Text);
        Assert.DoesNotContain(".jpg", split.Text);
        Assert.DoesNotContain(".db", split.Text);
        Assert.StartsWith("The sort will split 1 folder(s): files next to your videos stay in the source folder", split.Text);

        FolderSummary a = plan.ByFolder.Single(f => f.Folder == "a");
        Assert.True(a.Split);
        Assert.Equal(2, a.MovingFiles); // the clip and its macOS "._" file
        FolderSummary photosOnly = plan.ByFolder.Single(f => f.Folder == "p");
        Assert.False(photosOnly.Split);
        Assert.Equal(0, photosOnly.MovingFiles);
        Assert.False(plan.ByFolder.Single(f => f.Folder == "q").Split);
    }

    [Fact]
    public void Unknown_types_are_listed_by_size_and_large_ones_get_their_own_warning()
    {
        var entries = new List<(string, long)> { (@"z\DSC_1001.NEWRAW", 4 * GB) };
        for (int i = 0; i < 300; i++) entries.Add(($@"z\cache{i}.qqq", 2000));
        foreach (string ext in new[] { ".aa", ".bb", ".cc", ".dd", ".ee", ".ff", ".gg", ".hh" }) entries.Add(($@"z\x{ext}", 10));
        MovePlan plan = Plan(Scan(Source, [.. entries, (@"z\DSC_1002.MP4", 1000)]), Env());

        string unknown = Assert.Single(plan.Messages, m => m.Text.Contains("of unrecognized types stay in place")).Text;
        Assert.StartsWith("309 file(s) of unrecognized types stay in place", unknown);
        Assert.Contains($": .newraw x1 ({Format.Bytes(4 * GB)}), .qqq x300 ", unknown);
        Assert.Contains("and 2 more types", unknown);
        string large = Assert.Single(plan.Messages, m => m.Text.Contains("of 100 MB or more")).Text;
        Assert.Contains($"DSC_1001.NEWRAW ({Format.Bytes(4 * GB)})", large);
        Assert.Contains("Check them before you delete or format anything", large);
    }

    [Fact]
    public void Recorder_audio_live_photos_units_and_asc_mhl_are_explained()
    {
        var entries = new List<(string, long)>
        {
            (@"Audio\ZOOM_F6\260920_001.WAV", 10 * MB), (@"Audio\ZOOM_F6\260920_002.WAV", 10 * MB), (@"Phone\IMG_1.HEIC", 3 * MB),
            (@"Phone\IMG_1.MOV", 3 * MB), (@"Phone\IMG_2.HEIC", 3 * MB), (@"Phone\IMG_2.MOV", 900 * MB),
            (@"Sony\PRIVATE\M4ROOT\CLIP\C0001.MP4", 50 * MB), (@"Sony\Hedge.mhl", 1000),
            (@"Survey\DJI_0001.JPG", 5 * MB), (@"Survey\DJI_Timestamp.MRK", 1000),
        };
        for (int i = 0; i < 10; i++) entries.Add(($@"X7_0012\X7_0012_{i:D6}.DNG", 30 * MB));
        // X7 frames carry the CinemaDNG movie tags (there is no sound file with a drone camera).
        Func<SourceFile, bool> movieFrames = f => f.Directory == "X7_0012";
        MovePlan videos = Plan(Scan(Source, movieFrames, entries.ToArray()), Env());
        Assert.Contains(videos.Messages, m => m.Text == $"2 audio recording(s) ({Format.Bytes(20 * MB)}) have no photo of the same name, so they go with the videos: Audio\\ZOOM_F6.");
        Assert.Contains(videos.Messages, m => m.Text == $"1 iPhone Live Photo clip(s) ({Format.Bytes(3 * MB)}) belong to their photos and stay with them: Phone\\IMG_1.MOV.");
        Assert.Contains(videos.Messages, m => m.Text.Contains(@"too large for Live Photo clips") && m.Text.Contains(@"Phone\IMG_2.MOV"));
        Assert.Contains(videos.Messages, m => m.Text.StartsWith("Video card structures move as a whole: Sony\\PRIVATE\\M4ROOT (Sony XAVC card structure, 1 file)"));
        Assert.Contains(videos.Messages, m => m.Text.Contains("CinemaDNG clip X7_0012 (10 frames)") && m.Text.EndsWith("They go with the videos."));
        Assert.Contains(videos.Messages, m => m.Text.StartsWith("1 mapping/LiDAR mission(s) stay whole (they stay with the photos): Survey"));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("The source folder has checksum manifests from your backup tool (ASC MHL)."));

        MovePlan photos = Plan(Scan(Source, movieFrames, entries.ToArray()), Env(), MoveMode.Photos);
        Assert.Contains(photos.Messages, m => m.Text.Contains("have no photo of the same name, so they stay with the videos as video sound"));
        Assert.Equal(new[] { @"Phone\IMG_1.HEIC", @"Phone\IMG_1.MOV", @"Phone\IMG_2.HEIC", @"Survey\DJI_0001.JPG", @"Survey\DJI_Timestamp.MRK" },
            photos.ToMove.Select(f => f.RelativePath));
    }

    /// <summary>A real clip with a photo's name (another camera counting IMG_0001, ...) is a video and needs a look.</summary>
    [Fact]
    public void A_clip_named_like_a_photo_without_the_live_photo_tag_moves_with_the_videos_and_is_pointed_out()
    {
        ScanResult scan = Scan(Source, null, (@"2026-09-20\IMG_0412.CR3", 30 * MB), (@"2026-09-20\IMG_0413.CR3", 30 * MB), (@"2026-09-20\IMG_0413.JPG", 8 * MB),
            (@"2026-09-20\IMG_0413.MOV", 14 * MB), (@"Phone\IMG_0900.HEIC", 3 * MB), (@"Phone\IMG_0900.MOV", 3 * MB));
        // Only the phone's clip carries the tag here (the scan helper would tag every IMG_ clip).
        List<SourceFile> files = scan.Files;
        Classifier.Classify(files, null, "Card01", null, clip => clip.Directory == "Phone");
        MovePlan videos = Plan(scan, Env());
        Assert.Equal(new[] { @"2026-09-20\IMG_0413.MOV" }, videos.ToMove.Select(f => f.RelativePath));
        Assert.DoesNotContain(videos.Messages, m => m.Text.Contains("nothing to move"));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning && m.Text == "1 video(s) have the same name as a photo next to them, "
            + $"but they are not Live Photo clips (no Apple Live Photo tag). IVAR Offload treats them as videos, so they move: 2026-09-20\\IMG_0413.MOV ({Format.Bytes(14 * MB)}). "
            + "Probably another camera used the same file numbers. Check that they are videos.");
        Assert.Contains(videos.Messages, m => m.Text.StartsWith("1 iPhone Live Photo clip(s)") && m.Text.EndsWith(@"stay with them: Phone\IMG_0900.MOV."));
        Assert.True(videos.ToMove.Single().NeedsAttention);
    }

    /// <summary>Media that stays although it is of the moving side is exposed on the plan and explained.</summary>
    [Fact]
    public void Online_only_media_links_and_project_media_of_the_moving_side_are_listed_as_staying()
    {
        ScanResult scan = Scan(Source, null, (@"Card1\C0000.MP4", 10 * MB, FileAttributes.Archive), (@"Card1\C0003.MP4", 10 * MB, FileAttributes.Offline),
            (@"Card1\DSC_1.JPG", MB, FileAttributes.Offline), (@"Card1\notes.txt", 1, FileAttributes.Offline),
            (@"Edits\Promo\Promo.prproj", 1, FileAttributes.Archive), (@"Edits\Promo\Footage\A.MP4", MB, FileAttributes.Archive),
            (@"Edits\Promo\logo.png", 1, FileAttributes.Archive), (@"Card1\shortcut.MOV", 1, FileAttributes.ReparsePoint));
        MovePlan videos = Plan(scan, Env());
        Assert.Equal(new[] { @"Card1\C0000.MP4" }, videos.ToMove.Select(f => f.RelativePath));
        Assert.Equal(new[] { @"Card1\C0003.MP4", @"Card1\shortcut.MOV", @"Edits\Promo\Footage\A.MP4" },
            videos.MovingSideStaying.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(videos.MovingSideStaying, f => Assert.Contains(f, videos.Staying));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("3 online-only cloud file(s) are not downloaded and stay in place")
            && m.Text.EndsWith(" When these files are on this device, 1 video of them will move. To sort them, make the folder available offline "
                + "(right-click > Always keep on this device). When the download is complete, check again."));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("1 editing/processing project folder(s) are in the source folder")
            && m.Text.EndsWith(" 1 video in them stays in the source folder."));

        MovePlan photos = Plan(scan, Env(), MoveMode.Photos);
        Assert.Equal(new[] { @"Card1\DSC_1.JPG", @"Edits\Promo\logo.png" }, photos.MovingSideStaying.Select(f => f.RelativePath).Order(StringComparer.Ordinal));

        // An online-only clip named like a Live Photo still may be either: it is counted on neither side, but pointed out.
        ScanResult phone = Scan(Source, null, (@"Phone\IMG_0501.HEIC", 2 * MB, FileAttributes.Offline), (@"Phone\IMG_0501.MOV", 3 * MB, FileAttributes.Offline),
            (@"Card1\C0003.MP4", 10 * MB, FileAttributes.Offline), (@"Card1\C0004.MP4", 10 * MB, FileAttributes.Archive));
        MovePlan phoneVideos = Plan(phone, Env());
        Assert.Equal(new[] { @"Card1\C0003.MP4" }, phoneVideos.MovingSideStaying.Select(f => f.RelativePath));
        Assert.Contains(phoneVideos.Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("3 online-only cloud file(s) are not downloaded")
            && m.Text.EndsWith(" When these files are on this device, 1 video of them will move. 1 short clip(s) have the same name as a photo next to them. "
                + "IVAR Offload can identify them as Live Photo clips or videos only after you download them. To sort them, make the folder available offline "
                + "(right-click > Always keep on this device). When the download is complete, check again."));
        MovePlan phonePhotos = Plan(phone, Env(), MoveMode.Photos);
        Assert.Equal(new[] { @"Phone\IMG_0501.HEIC" }, phonePhotos.MovingSideStaying.Select(f => f.RelativePath));
        Assert.Contains(phonePhotos.Messages, m => m.Text.Contains(" 1 photo of them will move. 1 short clip(s) have the same name as a photo"));

        // Nothing movable: the preview says which media stays and why instead of "The source folder has no videos".
        MovePlan stuck = Plan(Scan(Source, null, (@"Card1\C0003.MP4", 10 * MB, FileAttributes.Offline), (@"Edits\Promo\Promo.prproj", 1, FileAttributes.Archive),
            (@"Edits\Promo\Footage\A.MP4", MB, FileAttributes.Archive), (@"Edits\Promo\Footage\B.MP4", MB, FileAttributes.Archive)), Env());
        Assert.False(stuck.CanRun);
        Assert.DoesNotContain(stuck.Messages, m => m.Text.StartsWith("The source folder has no videos"));
        Assert.Contains(stuck.Messages, m => m.Level == MessageLevel.Warning && m.Text == "None of the videos can move, so there is nothing to move. "
            + "The source folder still holds 3 videos (2 inside an editing/processing project, 1 online-only (not downloaded)).");
        Assert.Contains(Plan(ScanPaths(@"a\notes.txt"), Env()).Messages, m => m.Text == "The source folder has no videos, so there is nothing to move.");
    }

    /// <summary>An Atomos recorder writes its tag export (.fcpxml) next to its clips; the clips are still videos.</summary>
    [Fact]
    public void A_recorders_fcpxml_export_does_not_keep_its_clips_in_the_source()
    {
        MovePlan plan = Plan(ScanPaths(@"230901-Ninja-SSD\ATOMOS_NINJAV_S001_S001_T001.MOV", @"230901-Ninja-SSD\ATOMOS_NINJAV_S001_S001_T002.MOV",
            @"230901-Ninja-SSD\ATOMOS_NINJAV_S001.fcpxml", @"Card\C0001.MP4"), Env());
        Assert.Equal(3, plan.ToMove.Count);
        Assert.Empty(plan.Units);
        Assert.DoesNotContain(plan.Messages, m => m.Text.Contains("project"));
        Assert.DoesNotContain(plan.Messages, m => m.Text.Contains("unrecognized"));
        SourceFile export = plan.Staying.Single();
        Assert.Equal("not a photo or video (.FCPXML) - stays", export.Reason);
        Assert.Contains(plan.Messages, m => m.Text.StartsWith("The sort will split 1 folder(s)") && m.Text.Contains(".fcpxml x1"));
    }

    /// <summary>Frames are never split from their sound silently; missing frames are pointed out.</summary>
    [Fact]
    public void Cinemadng_clips_with_missing_frames_and_frame_runs_that_are_no_clip_are_warned_about()
    {
        var entries = new List<(string, long)>();
        foreach (int i in Enumerable.Range(0, 20).Where(i => i != 5)) entries.Add(($@"BMPCC\C0002\C0002_{i:D6}.dng", MB));
        entries.Add((@"BMPCC\C0002\C0002.wav", MB));
        // Untagged (e.g. a converter stripped the tags) and too short for the name rule: the frames stay photos, the sound is recorder sound.
        for (int i = 0; i < 8; i++) entries.Add(($@"BMPCC\C0005\C0005_{i:D6}.dng", MB));
        entries.Add((@"BMPCC\C0005\C0005.wav", MB));
        Func<SourceFile, bool> tagged = f => f.Directory.EndsWith("C0002");

        MovePlan videos = Plan(Scan(Source, tagged, entries.ToArray()), Env());
        Assert.Equal(21, videos.ToMove.Count); // C0002's 19 frames and sound, and C0005.wav
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning
            && m.Text == @"1 CinemaDNG clip(s) have missing frames, but each one still stays together as one clip: BMPCC\C0002 (1 missing). Check the card or its backup for the missing frames.");
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning
            && m.Text.StartsWith("1 sound file(s) have numbered frames with their name next to them, but IVAR Offload does not treat these frames as a clip")
            && m.Text.Contains(@"BMPCC\C0005\C0005.wav (8 files)"));
        Assert.Contains(videos.Messages, m => m.Text.StartsWith("The sort will split 1 folder(s)") && m.Text.Contains(@"BMPCC\C0005: .dng x8"));
        Assert.True(videos.ByFolder.Single(f => f.Folder == @"BMPCC\C0005").Split);

        MovePlan photos = Plan(Scan(Source, tagged, entries.ToArray()), Env(), MoveMode.Photos);
        Assert.Equal(8, photos.ToMove.Count);
        Assert.Contains(photos.Messages, m => m.Text.StartsWith("The sort will split 1 folder(s)") && m.Text.Contains(@"BMPCC\C0005: .wav x1"));
    }

    /// <summary>
    /// Tagged frame grabs next to raw photos do not make the folder a clip, and a tagged clip dumped next
    /// to raw photos does not take their edits along: a raw and its .xmp move together, and nothing moves silently apart.
    /// </summary>
    [Fact]
    public void Tagged_frames_next_to_raw_photos_never_separate_the_photos_from_their_edits()
    {
        ScanResult Stills() => Scan(Source, f => f.Name.StartsWith("A001_") || f.Name.StartsWith("C0007"),
            (@"Stills\2026-09-21\IMG_0001.CR3", MB), (@"Stills\2026-09-21\IMG_0001.xmp", 1), (@"Stills\2026-09-21\IMG_0002.CR3", MB),
            (@"Stills\2026-09-21\IMG_0002.xmp", 1), (@"Stills\2026-09-21\notes.pdf", 1),
            (@"Stills\2026-09-21\A001_C003_0101AB_000123.dng", MB), (@"Stills\2026-09-21\A001_C003_0101AB_000456.dng", MB),
            (@"Mixed\C0007_000000.dng", MB), (@"Mixed\C0007_000001.dng", MB), (@"Mixed\C0007_000002.dng", MB), (@"Mixed\C0007.wav", MB),
            (@"Mixed\DSC_0100.NEF", MB), (@"Mixed\DSC_0100.xmp", 1));

        MovePlan photos = Plan(Stills(), Env(), MoveMode.Photos);
        var moving = photos.ToMove.Select(f => f.RelativePath).ToHashSet();
        foreach (string photo in new[] { @"Stills\2026-09-21\IMG_0001.CR3", @"Stills\2026-09-21\IMG_0001.xmp", @"Stills\2026-09-21\IMG_0002.xmp",
                     @"Stills\2026-09-21\A001_C003_0101AB_000123.dng", @"Mixed\DSC_0100.NEF", @"Mixed\DSC_0100.xmp" })
            Assert.Contains(photo, moving);
        Assert.DoesNotContain(moving, p => p.StartsWith(@"Mixed\C0007"));
        Assert.Contains(photos.Messages, m => m.Text.Contains("The sort will split") && m.Text.Contains(@"Stills\2026-09-21: .pdf x1"));

        MovePlan videos = Plan(Stills(), Env());
        Assert.Equal(new[] { @"Mixed\C0007.wav", @"Mixed\C0007_000000.dng", @"Mixed\C0007_000001.dng", @"Mixed\C0007_000002.dng" },
            videos.ToMove.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(videos.Messages, m => m.Text.Contains("missing frames"));
    }

    /// <summary>Base-station and ground control data outside the mission folder is named when the mission moves.</summary>
    [Fact]
    public void Survey_data_left_outside_a_moving_mission_is_warned_about_in_photo_mode()
    {
        ScanResult scan = ScanPaths(@"Roof\DCIM\M\DJI_0001.JPG", @"Roof\DCIM\M\DJI_Timestamp.MRK", @"Roof\DCIM\M\DJI_Rinex.obs",
            @"Roof\BASE\DRTK3_20240119.24O", @"Roof\BASE\DRTK3_20240119.24N", @"Roof\GCP\gcp_list.csv", @"Roof\DCIM\100MEDIA\DJI_0002.MP4");
        MovePlan photos = Plan(scan, Env(), MoveMode.Photos);
        Assert.Equal(3, photos.ToMove.Count);
        Assert.Contains(photos.Messages, m => m.Level == MessageLevel.Warning && m.Text == @"Survey data outside the mapping mission folders stays in the source folder: "
            + @"Roof\BASE (DRTK3_20240119.24N, DRTK3_20240119.24O), Roof\GCP (gcp_list.csv). If you process PPK or use ground control points, copy it next to the photos.");
        Assert.DoesNotContain(photos.Messages, m => m.Text.Contains("unrecognized"));
        Assert.DoesNotContain(Plan(scan, Env()).Messages, m => m.Text.StartsWith("Survey data"));
    }

    /// <summary>Hyperlapse source frames stay photos, are kept together, and are pointed out in both modes.</summary>
    [Fact]
    public void Hyperlapse_source_frames_stay_photos_are_kept_together_and_are_pointed_out()
    {
        using var t = new TestTree();
        for (int i = 1; i <= 12; i++) t.Add($@"240120-Mavic3Pro\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_{i:D4}.JPG", 100);
        t.Add(@"240120-Mavic3Pro\DCIM\DJI_001\DJI_20240120120000_0005_D.MP4");
        MovePlan videos = t.Plan();
        Assert.Equal(new[] { @"240120-Mavic3Pro\DCIM\DJI_001\DJI_20240120120000_0005_D.MP4" }, videos.ToMove.Select(f => f.RelativePath));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning
            && m.Text.StartsWith(@"1 DJI hyperlapse folder(s) hold the still frames of a hyperlapse: 240120-Mavic3Pro\DCIM\HYPERLAPSE\HYPERLAPSE_0005 (12 frames). They are photos and stay together with the photos"));

        // Photo mode: a name clash on one frame keeps the whole hyperlapse in the source, never half of it.
        string clash = Path.Join(t.Target, @"240120-Mavic3Pro\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0007.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(clash)!);
        File.WriteAllText(clash, "another hyperlapse");
        MovePlan photos = t.Plan(MoveMode.Photos);
        Assert.Empty(photos.ToMove);
        Assert.Equal(12, photos.HeldBack.Count);
        Assert.Contains(photos.Messages, m => m.Text.Contains("They are photos and move together with the photos"));
    }

    /// <summary>Two bodies counting the same numbers (a D850's NEF, a Z9's MOV): the edits follow the raw photo.</summary>
    [Fact]
    public void Raw_edits_matching_a_raw_and_an_mov_move_with_the_raw()
    {
        MovePlan photos = Plan(ScanPaths(@"Nikon\DSC_0001.NEF", @"Nikon\NKSC_PARAM\DSC_0001.NEF.nksc", @"Nikon\DSC_0001.xmp", @"Nikon\DSC_0001.MOV"),
            Env(), MoveMode.Photos);
        Assert.Equal(new[] { @"Nikon\DSC_0001.NEF", @"Nikon\DSC_0001.xmp", @"Nikon\NKSC_PARAM\DSC_0001.NEF.nksc" },
            photos.ToMove.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(photos.Messages, m => m.Text.Contains("match both a photo and a video"));
        Assert.DoesNotContain(photos.Messages, m => m.Text.Contains("The sort will split"));
    }

    [Fact]
    public void Earlier_jobs_in_the_target_from_another_folder_or_mode_are_warned_about()
    {
        using var t = new TestTree();
        string log = Path.Join(t.Root, "logs");
        Directory.CreateDirectory(log);
        string Journal(string name, string source, string mode, string at)
        {
            string path = Path.Join(log, name + JobPaths.JournalSuffix);
            string json = $$"""{"t":"job","v":1,"id":"{{name}}","source":{{System.Text.Json.JsonSerializer.Serialize(source)}},"target":"x","mode":"{{mode}}","method":"Rename","at":"{{at}}"}""";
            File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
            return path;
        }
        string smith = Journal("a", @"E:\Smith", "Videos", "2025-09-25T10:00:00.000+02:00");
        string same = Journal("b", Source, "Videos", "2025-09-26T10:00:00.000+02:00");
        string photos = Journal("c", Source, "Photos", "2025-09-27T10:00:00.000+02:00");

        MovePlan other = Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [smith, same]));
        Assert.Contains(other.Messages, m => m.Level == MessageLevel.Warning
            && m.Text.StartsWith(@"This target folder already holds files that an earlier sort moved from E:\Smith on 25 Sep"));
        Assert.Contains(other.Messages, m => m.Text.EndsWith("(videos)."));

        MovePlan continues = Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [same]));
        Assert.Contains(continues.Messages, m => m.Level == MessageLevel.Info && m.Text.StartsWith("This sort continues an earlier sort of this folder"));
        Assert.DoesNotContain(continues.Messages, m => m.Text.StartsWith("This target folder already holds"));

        MovePlan otherMode = Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [same, photos]));
        Assert.Contains(otherMode.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("(photos)"));

        MovePlan several = Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [smith, same, photos]));
        Assert.Contains(several.Messages, m => m.Text.EndsWith("on 27 Sep 2025 (photos), and files from 1 other earlier sort."));

        // Logs this version cannot read (a future Backup mode, a damaged header) never stop the preview.
        string backup = Journal("d", @"E:\Card", "Backup", "2025-09-28T10:00:00.000+02:00");
        string damaged = Path.Join(log, "e" + JobPaths.JournalSuffix);
        File.WriteAllText(damaged, "{\"t\":\"job\",\"v\":9,\"mode\":\"Videos\",\"method\":\"Teleport\",\"source\":\"x\",\"target\":\"y\"}\n");
        MovePlan unreadable = Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [backup, damaged]));
        Assert.True(unreadable.CanRun);
        Assert.DoesNotContain(unreadable.Messages, m => m.Text.Contains("earlier sort"));
        Assert.Contains(Plan(ScanPaths(@"a\clip.MOV"), Env(journals: _ => [backup, smith])).Messages, m => m.Text.Contains(@"an earlier sort moved from E:\Smith"));
    }

    [Fact]
    public void A_skipped_ascmhl_folder_alone_gives_the_manifest_warning()
    {
        ScanResult scan = ScanPaths(@"Card\clip.MOV");
        var withManifests = new ScanResult
        {
            SourceRoot = scan.SourceRoot, Files = scan.Files, Folders = [], Problems = [], Units = scan.Units,
            SkippedFolders = [new SkippedFolder(@"Card\ascmhl", "ASC MHL checksum manifests")],
        };
        Assert.Contains(Plan(withManifests, Env()).Messages, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("The source folder has checksum manifests"));
        Assert.DoesNotContain(Plan(scan, Env()).Messages, m => m.Text.StartsWith("The source folder has checksum manifests"));
    }

    [Fact]
    public void A_refused_source_is_never_probed_for_delete_access()
    {
        MovePlan plan = Plan(Scan(@"C:\", (@"a\clip.MOV", 1000)), Env(canDelete: _ => throw new InvalidOperationException("probed")),
            target: @"D:\offload-planner-test-does-not-exist\Video");
        Assert.Contains(Errors(plan), e => e.StartsWith("This folder is too broad to sort"));
    }

    [Fact]
    public void Type_and_folder_summaries_show_what_is_held_back()
    {
        using var t = new TestTree();
        t.Add(@"d\C0001.MP4");
        t.Add(@"d\C0001M01.XML");
        Directory.CreateDirectory(Path.Join(t.Target, "d"));
        File.WriteAllText(Path.Join(t.Target, @"d\C0001.MP4"), "different");
        MovePlan plan = t.Plan();
        Assert.Contains(plan.ByType, x => x.Classification == "Name taken in target folder (different)" && !x.Moves);
        Assert.Contains(plan.ByType, x => x.Classification == "Kept with its clip" && !x.Moves);
        FolderSummary d = Assert.Single(plan.ByFolder);
        Assert.Equal((0, 2), (d.MovingFiles, d.StayingFiles));
    }

    // ---- Sync detection -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("C:\\bad\0name")]
    [InlineData(@"Q:\no\such\drive")]
    public void Sync_detection_never_throws(string folder) => SyncDetector.Describe(folder);

    [Fact]
    public void Google_drive_mirror_folders_are_detected()
    {
        using var t = new TestTree();
        t.Add(@"a\clip.MOV");
        Assert.Null(SyncDetector.Describe(t.Source));
        Directory.CreateDirectory(Path.Join(t.Root, ".tmp.drivedownload"));
        Assert.StartsWith("Google Drive", SyncDetector.Describe(Path.Join(t.Source, "a")));
    }

    [Fact]
    public void Google_drive_icon_in_desktop_ini_is_detected_without_touching_the_file()
    {
        using var t = new TestTree();
        string ini = t.Add("desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=C:\\Program Files\\Google\\Drive File Stream\\100.0\\GoogleDriveFS.exe,23\r\n", Encoding.Unicode);
        var accessed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(ini, accessed);
        Assert.StartsWith("Google Drive", SyncDetector.Describe(t.Source));
        Assert.Equal(accessed, File.GetLastAccessTimeUtc(ini));
    }
}
