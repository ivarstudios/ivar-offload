using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

public class ClassifierTests
{
    private const long MB = 1024 * 1024;

    private static Dictionary<string, SourceFile> Classify(params string[] paths) => Classify(paths.Select(p => (p, 1L)).ToArray());

    private static Dictionary<string, SourceFile> Classify(params (string Path, long Size)[] entries) => ClassifyIn(null, entries);

    private static Dictionary<string, SourceFile> ClassifyIn(string? sourceName, params (string Path, long Size)[] entries)
    {
        List<SourceFile> files = Files(entries);
        Classifier.Classify(files, null, sourceName, isLivePhotoClip: TaggedLivePhoto);
        return files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stands in for reading the clip: test clips whose name contains "IMG_" carry Apple's Live Photo tag, others do not.</summary>
    private static bool TaggedLivePhoto(SourceFile clip) => clip.Name.Contains("IMG_", StringComparison.OrdinalIgnoreCase);

    private static List<SourceFile> Files(params (string Path, long Size)[] entries) => entries.Select(e => new SourceFile
    {
        RelativePath = e.Path,
        Size = e.Size,
        CreationTime = 0,
        LastWriteTime = 0,
        Attributes = FileAttributes.Archive,
    }).ToList();

    private static MediaSide Side(string expected) => expected switch
    {
        "video" => MediaSide.Video,
        "photo" => MediaSide.Photo,
        "stay" => MediaSide.Neutral,
        _ => throw new ArgumentException(expected),
    };

    [Theory]
    [InlineData("a\\clip.MOV", MediaSide.Video)]
    [InlineData("a\\clip.mp4", MediaSide.Video)]
    [InlineData("a\\clip.avi", MediaSide.Video)]
    [InlineData("a\\DJI_0001.LRF", MediaSide.Video)]
    [InlineData("a\\GL010001.LRV", MediaSide.Video)]
    [InlineData("a\\DSC_0003.NEV", MediaSide.Video)]
    [InlineData("a\\CAM_20250801120000_0001_D.OSV", MediaSide.Video)]
    [InlineData("a\\goggles_0001.ts", MediaSide.Video)]
    [InlineData("a\\M12-1234.MLV", MediaSide.Video)]
    [InlineData("a\\clip.mcraw", MediaSide.Video)]
    [InlineData("a\\A001C0001_20190801.ZRAW", MediaSide.Video)]
    [InlineData("a\\photo.JPG", MediaSide.Photo)]
    [InlineData("a\\photo.RAF", MediaSide.Photo)]
    [InlineData("a\\photo.NEF", MediaSide.Photo)]
    [InlineData("a\\photo.CR3", MediaSide.Photo)]
    [InlineData("a\\photo.HEIC", MediaSide.Photo)]
    [InlineData("a\\photo.GPR", MediaSide.Photo)]
    [InlineData("a\\DSC00001.ARQ", MediaSide.Photo)]
    [InlineData("a\\DSCF0001.MPO", MediaSide.Photo)]
    [InlineData("a\\Hedge Media Hash List.mhl", MediaSide.Neutral)]
    [InlineData("a\\NIKON001.DSC", MediaSide.Neutral)]
    [InlineData("a\\notes.xyz", MediaSide.Neutral)]
    public void Primary_types(string path, MediaSide expected) => Assert.Equal(expected, Classify(path)[path].Side);

    /// <summary>One row per device family: "relative path=expected side[:size in MB]".</summary>
    public static TheoryData<string, string[]> Families => new()
    {
        { "Nikon Z9 N-RAW master with its MP4 proxy, NX Studio edits in NKSC_PARAM", [
            @"z\DCIM\100NCZ_9\DSC_0003.NEV=video", @"z\DCIM\100NCZ_9\DSC_0003.MP4=video",
            @"z\DCIM\100NCZ_9\DSC_0004.NEF=photo", @"z\DCIM\100NCZ_9\DSC_0004.JPG=photo",
            @"z\DCIM\100NCZ_9\NKSC_PARAM\DSC_0004.NEF.nksc=photo", @"z\DCIM\100NCZ_9\NKSC_PARAM\DSC_0003.MP4.nksc=video"] },
        { "DJI Osmo 360 master with its LRF proxy", [
            @"o\DCIM\DJI_001\CAM_20250801120000_0001_D.OSV=video", @"o\DCIM\DJI_001\CAM_20250801120000_0001_D.LRF=video",
            @"o\DCIM\DJI_001\CAM_20250801120500_0002_D.JPG=photo"] },
        { "Blackmagic RAW with sidecar settings", [
            @"b\A001_09201234_C001.braw=video", @"b\A001_09201234_C001.sidecar=video", @"b\A001_09201234_C002.sidecar=video"] },
        { "Capture One settings, mask and cache follow the photo two folders up", [
            @"c\IMG_5001.CR3=photo", @"c\CaptureOne\Settings153\IMG_5001.CR3.cos=photo", @"c\CaptureOne\Settings153\IMG_5001.CR3.comask=photo",
            @"c\CaptureOne\Cache\Proxies\IMG_5001.CR3.cop=photo", @"c\MVI_5002.MP4=video", @"c\CaptureOne\Settings153\MVI_5002.MP4.cos=video"] },
        { "Canon DPP recipe and GIS world files follow their photo by name", [
            @"d\IMG_0001.CR2=photo", @"d\IMG_0001.dr4=photo", @"g\ortho.tif=photo", @"g\ortho.tfw=photo", @"g\map.jpg=photo", @"g\map.jgw=photo",
            @"g\lonely.wld=stay"] },
        { "Panasonic P2 card: the whole CONTENTS folder and LASTCLIP.TXT move together", [
            @"p\CONTENTS\VIDEO\0001AB.MXF=video", @"p\CONTENTS\AUDIO\0001AB00.MXF=video", @"p\CONTENTS\CLIP\0001AB.XML=video",
            @"p\CONTENTS\ICON\0001AB.BMP=video", @"p\CONTENTS\PROXY\0001AB.MP4=video", @"p\CONTENTS\PROXY\0001AB.BIN=video",
            @"p\CONTENTS\VOICE\0001AB.WAV=video", @"p\LASTCLIP.TXT=video"] },
        { "A user folder called CONTENTS is not a card", [
            @"u\CONTENTS\notes\x.JPG=photo", @"u\CONTENTS\readme.txt=stay", @"u\LASTCLIP.TXT=stay"] },
        { "Canon XF card", [
            @"x\CONTENTS\CLIPS001\AA0001\AA000101.MXF=video", @"x\CONTENTS\CLIPS001\AA0001\AA0001.CIF=video",
            @"x\CONTENTS\CLIPS001\AA0001\AA0001M01.XML=video", @"x\CONTENTS\CLIPS001\AA0001\AA000101.PPN=video",
            @"x\CONTENTS\CLIPS001\AA0001\AA0001.THM=video", @"x\CONTENTS\CLIPS001\INDEX.MIF=video"] },
        { "Sony XDCAM EX BPAV card", [
            @"e\BPAV\CLPR\501_0001_01\501_0001_01.MP4=video", @"e\BPAV\CLPR\501_0001_01\501_0001_01M01.XML=video",
            @"e\BPAV\CLPR\501_0001_01\501_0001_01.SMI=video", @"e\BPAV\CLPR\501_0001_01\501_0001_01I01.PPN=video",
            @"e\BPAV\CLPR\501_0001_01\501_0001_01R01.BIM=video", @"e\BPAV\MEDIAPRO.XML=video", @"e\BPAV\TAKR\x.BIN=video"] },
        { "RED magazine: .RDM and .RDC folders", [
            @"r\A001_0101XY.RDM\A001_C001_0101AB.RDC\A001_C001_0101AB_001.R3D=video",
            @"r\A001_0101XY.RDM\A001_C001_0101AB.RDC\A001_C001_0101AB_002.R3D=video",
            @"r\A001_0101XY.RDM\A001_C001_0101AB.RDC\A001_C001_0101AB.RMD=video", @"r\A001_0101XY.RDM\A001_C001_0101AB.RDC\x.RDX=video"] },
        { "Sony card copied without PRIVATE\\M4ROOT", [
            @"s\FX3_A\CLIP\C0001.MP4=video", @"s\FX3_A\CLIP\C0001M01.XML=video", @"s\FX3_A\THMBNL\C0001T01.JPG=video",
            @"s\FX3_A\MEDIAPRO.XML=video", @"s\FX3_A\STATUS.BIN=video"] },
        { "Panasonic MOV card (PANA_GRP)", [
            @"v\PRIVATE\PANA_GRP\001RAQAM\A001C001250101AB.MOV=video", @"v\PRIVATE\PANA_GRP\001RAQAM\INDEX.BIN=video", @"v\DCIM\100_PANA\P1000001.JPG=photo"] },
        { "DJI P4 RTK mapping mission: timestamps, PPK and RINEX stay with the photos", [
            @"m\DCIM\SURVEY\100_0001\100_0001_0001.JPG=photo", @"m\DCIM\SURVEY\100_0001\100_0001_0002.JPG=photo",
            @"m\DCIM\SURVEY\100_0001\100_0001_Timestamp.MRK=photo", @"m\DCIM\SURVEY\100_0001\100_0001_PPKRAW.bin=photo",
            @"m\DCIM\SURVEY\100_0001\100_0001_PPKNAV.bin=photo", @"m\DCIM\SURVEY\100_0001\100_0001_Rinex.obs=photo",
            @"m\DCIM\SURVEY\100_0001\100_0001_Rinex.25O=photo", @"m\DCIM\100MEDIA\DJI_0001.MOV=video"] },
        { "DJI L2 LiDAR mission", [
            @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.LDR=photo", @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.LDRT=photo",
            @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.IMU=photo", @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.RTK=photo",
            @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.CLC=photo", @"l\DJI_202401011200_001_Quarry\DJI_202401011200_001_Quarry_0.SIG=photo"] },
        { "A video inside a mission folder still moves with its telemetry", [
            @"n\DJI_0001.JPG=photo", @"n\DJI_20240101_Timestamp.MRK=photo", @"n\DJI_0005.MP4=video", @"n\DJI_0005.SRT=video", @"n\notes.txt=photo"] },
        { "A DPOF print order (AUTPRINT.MRK) is not a mission", [
            @"q\MISC\AUTPRINT.MRK=stay", @"q\MISC\IMG_0001.JPG=photo"] },
        { "Live Photos: HEIC or IMG_ JPG with a short tagged MOV/MP4; large, untagged clips and other cameras' names are videos", [
            @"i\IMG_5000.JPG=photo", @"i\IMG_5000.MOV=photo:3", @"i\IMG_3000.HEIC=photo", @"i\IMG_3000.MP4=photo:2",
            @"i\IMG_4410.HEIC=photo", @"i\IMG_4410.MOV=video:1500", @"i\IMG_4411.HEIC=photo", @"i\IMG_4411.MOV=video:28",
            @"i\DSC_0001.JPG=photo", @"i\DSC_0001.MOV=video:2", @"i\IMG_5000.AAE=photo",
            @"i\20240101_120500.heic=photo", @"i\20240101_120500.mp4=video:3"] },
        { "Frame grab named like its clip: video-only companions follow the video", [
            @"g\C0001.MP4=video", @"g\C0001.png=photo", @"g\C0001M01.XML=video", @"g\C0001.xmp=stay",
            @"g\GX010042.MP4=video", @"g\GX010042.JPG=photo", @"g\GX010042.THM=video",
            @"d\DJI_0001.JPG=photo", @"d\DJI_0001.MP4=video", @"d\DJI_0001.SRT=video", @"d\DJI_0001.SCR=video",
            @"a\X.JPG=photo", @"a\X.MOV=video:100", @"a\X.AAE=photo", @"a\X.WAV=stay"] },
        { "Audio: name-matched follows its file, recorder sound goes with the videos", [
            @"f\DSCF0001.JPG=photo", @"f\DSCF0001.RAF=photo", @"f\DSCF0001.WAV=photo", @"g\GB01.MP4=video", @"g\GB01.WAV=video",
            @"Audio\ZOOM_F6\260920_001.WAV=video", @"Audio\ZOOM_F6\260920_001_Tr1.WAV=video", @"Audio\MixPre\take1.BWF=video",
            @"Audio\h5\ZOOM0001.flac=video", @"Audio\misc\voice.m4a=video", @"Audio\misc\long.rf64=video"] },
        { "Editing and processing projects stay whole", [
            @"Edits\Promo\Promo.prproj=stay", @"Edits\Promo\Footage\A001_C003.MP4=stay", @"Edits\Promo\Exports\Promo_v3.mp4=stay",
            @"Edits\Promo\Graphics\logo.png=stay", @"Edits\Other\clip.MP4=video",
            @"Processing\Bridge\Bridge.p4d=stay", @"Processing\Bridge\3_dsm_ortho\Bridge_ortho.tif=stay",
            @"Processing\Solar\Solar.p4m=stay", @"Processing\Solar\exports\Solar_orthomosaic.tif=stay", @"Processing\Solar\exports\Solar_dsm.tif=stay",
            @"Processing\ODM_Crop\images\DJI_0001.JPG=stay", @"Processing\ODM_Crop\odm_orthophoto\odm_orthophoto.tif=stay",
            @"Processing\ODM_Crop\odm_dem\dsm.tif=stay",
            @"Processing\Terra_Quarry\map\result.tif=stay", @"Processing\Terra_Quarry\map\dsm.tif=stay",
            @"Processing\Terra_Quarry\lidars\terra_las\cloud.las=stay"] },
        { "An FCPXML export next to recorder clips is not a project (changed deliberately: .fcpxml used to be a project marker)", [
            @"Ninja\ATOMOS_NINJAV_S001_S001_T001.MOV=video", @"Ninja\ATOMOS_NINJAV_S001_S001_T002.MOV=video", @"Ninja\ATOMOS_NINJAV_S001.fcpxml=stay",
            @"FCP\Cut.fcpxmld\Info.fcpxml=stay", @"FCP\Media\B001.MOV=video"] },
        { "Map rasters keep their projection, overviews and GDAL metadata; meshes and point clouds stay", [
            @"Deliv\Farm_ortho.tif=photo", @"Deliv\Farm_ortho.tfw=photo", @"Deliv\Farm_ortho.prj=photo", @"Deliv\Farm_ortho.tif.aux.xml=photo",
            @"Deliv\Farm_ortho.tif.ovr=photo", @"Deliv\Farm_ortho_small.jpg=photo", @"Deliv\Farm_ortho_small.jgw=photo",
            @"Deliv\Farm_ortho_small.prj=photo", @"Deliv\Farm_mesh.obj=stay", @"Deliv\Farm.laz=stay", @"Deliv\lonely.prj=stay"] },
        { "Base-station and ground control data outside a mission stays", [
            @"Roof\DCIM\M\DJI_0001.JPG=photo", @"Roof\DCIM\M\DJI_Timestamp.MRK=photo", @"Roof\DCIM\M\DJI_Rinex.obs=photo",
            @"Roof\BASE\DRTK3_20240119.24O=stay", @"Roof\BASE\DRTK3_20240119.24N=stay", @"Roof\BASE\base.ubx=stay", @"Roof\GCP\gcp_list.csv=stay"] },
        { "DJI hyperlapse source frames are photos; the finished hyperlapse is a video", [
            @"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0001.JPG=photo", @"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0002.JPG=photo",
            @"M3\DCIM\DJI_001\DJI_20240120120000_0005_D.MP4=video"] },
        { "An .xmp next to a raw photo and an MOV/MP4 of the same name holds the raw's edits", [
            @"x\DSC_0001.NEF=photo", @"x\DSC_0001.MOV=video", @"x\DSC_0001.xmp=photo",
            @"y\DSC_0002.NEF=photo", @"y\DSC_0002.MTS=video", @"y\DSC_0002.xmp=stay",
            @"z\DSC_0003.JPG=photo", @"z\DSC_0003.MP4=video", @"z\DSC_0003.xmp=stay"] },
        { "An unrecognized file with a video's name follows it; a photo's name or a known type does not", [
            @"u\DSC_0003.xyz=video", @"u\DSC_0003.MP4=video", @"u\DSC_0003.txt=stay", @"u\PIC_1.xyz=stay", @"u\PIC_1.JPG=photo",
            @"u\BOTH.xyz=stay", @"u\BOTH.MP4=video", @"u\BOTH.JPG=photo"] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Device_families(string family, string[] rows)
    {
        var expected = rows.Select(r =>
        {
            string[] parts = r.Split('=');
            string[] sideAndSize = parts[1].Split(':');
            return (Path: parts[0], Side: Side(sideAndSize[0]), Size: sideAndSize.Length > 1 ? long.Parse(sideAndSize[1]) * MB : 1);
        }).ToList();
        var r = Classify(expected.Select(e => (e.Path, e.Size)).ToArray());
        Assert.All(expected, e => Assert.True(e.Side == r[e.Path].Side, $"{family}: {e.Path} should be {e.Side}, is {r[e.Path]}"));
        Assert.All(r.Values, f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
    }

    [Fact]
    public void Dji_thumbnails_in_misc_folder_go_with_videos_even_without_a_name_match()
    {
        var r = Classify(@"s\DCIM\100MEDIA\DJI_0003.MOV", @"s\MISC\THM\100\DJI_0003.THM", @"s\MISC\THM\100\DJI_0003.SCR", @"s\MISC\GIS\dji.gis",
            @"s\MISC\THM\100\DJI_0009.THM");
        Assert.Equal(MediaSide.Video, r[@"s\MISC\THM\100\DJI_0003.THM"].Side);
        Assert.Equal(MediaSide.Video, r[@"s\MISC\THM\100\DJI_0003.SCR"].Side);
        Assert.Equal(MediaSide.Neutral, r[@"s\MISC\GIS\dji.gis"].Side);
        // They belong to the clip in DCIM\100MEDIA, so they stay together with it.
        Assert.Equal(r[@"s\DCIM\100MEDIA\DJI_0003.MOV"].GroupKey, r[@"s\MISC\THM\100\DJI_0003.THM"].GroupKey);
        Assert.Contains("DJI_0003.MOV", r[@"s\MISC\THM\100\DJI_0003.SCR"].Reason);
        Assert.Equal(MediaSide.Video, r[@"s\MISC\THM\100\DJI_0009.THM"].Side);
    }

    [Fact]
    public void Srt_and_proxies_of_a_renamed_clip_still_go_with_videos()
    {
        var r = Classify(@"d\Renamed-0241.MP4", @"d\DJI_0241.LRF", @"d\DJI_0241.SRT", @"d\DJI_0240.JPG");
        Assert.Equal(MediaSide.Video, r[@"d\DJI_0241.LRF"].Side);
        Assert.Equal(MediaSide.Video, r[@"d\DJI_0241.SRT"].Side);
        Assert.Equal(MediaSide.Photo, r[@"d\DJI_0240.JPG"].Side);
    }

    [Fact]
    public void Xmp_follows_the_file_with_the_same_name()
    {
        var r = Classify(@"d\A.NEF", @"d\A.xmp", @"d\B.MP4", @"d\B.xmp", @"d\C.RAF.xmp", @"d\C.RAF");
        Assert.Equal(MediaSide.Photo, r[@"d\A.xmp"].Side);
        Assert.Equal(MediaSide.Video, r[@"d\B.xmp"].Side);
        Assert.Equal(MediaSide.Photo, r[@"d\C.RAF.xmp"].Side);
    }

    [Fact]
    public void Xmp_matching_both_a_photo_and_a_video_stays_and_is_flagged()
    {
        var r = Classify(@"d\X.JPG", @"d\X.MP4", @"d\X.xmp");
        Assert.Equal(MediaSide.Neutral, r[@"d\X.xmp"].Side);
        Assert.Equal(FileNote.Ambiguous, r[@"d\X.xmp"].Note);
    }

    [Fact]
    public void Video_only_companions_matching_a_photo_and_a_video_follow_the_video_and_say_why()
    {
        var r = Classify(@"d\DJI_0001.JPG", @"d\DJI_0001.MP4", @"d\DJI_0001.SRT", @"d\C0001.png", @"d\C0001.MP4", @"d\C0001M01.XML",
            @"d\IMG_1.JPG", @"d\IMG_1.MP4", @"d\IMG_1.AAE", @"d\IMG_1.WAV");
        Assert.Equal(MediaSide.Video, r[@"d\DJI_0001.SRT"].Side);
        Assert.Contains("not with the photo DJI_0001.JPG", r[@"d\DJI_0001.SRT"].Reason);
        Assert.Equal(MediaSide.Video, r[@"d\C0001M01.XML"].Side);
        Assert.Equal(MediaSide.Photo, r[@"d\IMG_1.AAE"].Side); // Apple edit data follows the photo (IMG_1.MP4 is a Live Photo clip here anyway)
        Assert.Equal(FileNote.None, r[@"d\DJI_0001.SRT"].Note);
    }

    [Fact]
    public void Kyno_metadata_in_lp_store_follows_its_clip_and_uses_the_inner_extension_when_orphaned()
    {
        var r = Classify(@"f\DSCF3666.MOV", @"f\.LP_Store\DSCF3666.MOV.lpmd", @"g\.LP_Store\GONE.MOV.lpmd", @"g\.LP_Store\PIC.JPG.lpmd");
        Assert.Equal(MediaSide.Video, r[@"f\.LP_Store\DSCF3666.MOV.lpmd"].Side);
        Assert.Contains("DSCF3666.MOV", r[@"f\.LP_Store\DSCF3666.MOV.lpmd"].Reason);
        Assert.Equal(MediaSide.Video, r[@"g\.LP_Store\GONE.MOV.lpmd"].Side);
        Assert.Equal(MediaSide.Photo, r[@"g\.LP_Store\PIC.JPG.lpmd"].Side);
    }

    /// <summary>Changed deliberately: unmatched audio used to stay; it is recorder / dual-system sound and goes with the videos.</summary>
    [Fact]
    public void Audio_follows_its_photo_or_video_and_unpaired_audio_goes_with_the_videos()
    {
        var r = Classify(@"f\DSCF0001.JPG", @"f\DSCF0001.RAF", @"f\DSCF0001.WAV", @"g\GB01.MP4", @"g\GB01.WAV", @"g\lonely.wav",
            @"h\X.JPG", @"h\X.MP4", @"h\X.WAV");
        Assert.Equal(MediaSide.Photo, r[@"f\DSCF0001.WAV"].Side);
        Assert.Equal(MediaSide.Video, r[@"g\GB01.WAV"].Side);
        Assert.Equal(MediaSide.Video, r[@"g\lonely.wav"].Side);
        Assert.Equal(FileNote.UnmatchedAudio, r[@"g\lonely.wav"].Note);
        Assert.Contains("recorder / dual-system sound", r[@"g\lonely.wav"].Reason);
        Assert.Equal(FileNote.None, r[@"g\GB01.WAV"].Note);
        Assert.Equal(MediaSide.Neutral, r[@"h\X.WAV"].Side); // matches both a photo and a video: never guessed
        Assert.Equal(FileNote.Ambiguous, r[@"h\X.WAV"].Note);
    }

    [Fact]
    public void Sony_clip_xml_follows_its_clip()
    {
        var r = Classify(@"s\C0002.MP4", @"s\C0002M01.XML", @"s\other.xml");
        Assert.Equal(MediaSide.Video, r[@"s\C0002M01.XML"].Side);
        Assert.Equal(MediaSide.Neutral, r[@"s\other.xml"].Side);
    }

    [Fact]
    public void Everything_inside_a_video_card_structure_is_video_including_jpg_thumbnails()
    {
        var r = Classify(@"a\PRIVATE\M4ROOT\CLIP\C0001.MP4", @"a\PRIVATE\M4ROOT\THMBNL\C0001T01.JPG", @"a\PRIVATE\M4ROOT\MEDIAPRO.XML",
            @"b\PRIVATE\AVCHD\BDMV\STREAM\00000.MTS", @"b\PRIVATE\AVCHD\BDMV\CLIPINF\00000.CPI", @"a\DCIM\100MSDCF\DSC00001.JPG");
        Assert.All(r.Values.Where(f => !f.RelativePath.Contains("DCIM")), f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.Equal(MediaSide.Photo, r[@"a\DCIM\100MSDCF\DSC00001.JPG"].Side);
        Assert.Equal(@"a\PRIVATE\M4ROOT", r[@"a\PRIVATE\M4ROOT\THMBNL\C0001T01.JPG"].GroupKey);
        Assert.Equal(@"b\PRIVATE\AVCHD", r[@"b\PRIVATE\AVCHD\BDMV\STREAM\00000.MTS"].GroupKey);
    }

    /// <summary>
    /// Changed deliberately: a Live Photo clip must be short (at most 15 MB, was 40 MB) and carry Apple's Live Photo tag;
    /// JPEG pairs count too (named like an iPhone photo).
    /// </summary>
    [Fact]
    public void Live_photo_clip_stays_with_its_photo_but_a_large_clip_is_a_video()
    {
        var r = Classify((@"p\IMG_1234.HEIC", 1), (@"p\IMG_1234.MOV", 3 * MB), (@"p\IMG_9999.MOV", 3 * MB),
            (@"p\IMG_2000.JPG", 1), (@"p\IMG_2000.MOV", 2 * MB), (@"p\IMG_4410.HEIC", 1), (@"p\IMG_4410.MOV", 16 * MB));
        Assert.Equal(MediaSide.Photo, r[@"p\IMG_1234.MOV"].Side);
        Assert.Equal(FileNote.LivePhoto, r[@"p\IMG_1234.MOV"].Note);
        Assert.Equal(MediaSide.Video, r[@"p\IMG_9999.MOV"].Side);
        Assert.Equal(MediaSide.Photo, r[@"p\IMG_2000.MOV"].Side);
        Assert.Equal(MediaSide.Video, r[@"p\IMG_4410.MOV"].Side);
        Assert.Equal(FileNote.LivePhotoTooLarge, r[@"p\IMG_4410.MOV"].Note);
        Assert.Contains("too large for a Live Photo clip", r[@"p\IMG_4410.MOV"].Reason);
        Assert.False(r[@"p\IMG_4410.MOV"].NeedsAttention);
    }

    /// <summary>
    /// Canon bodies and iPhones both count IMG_0001, ... A real clip with a Canon photo's name (or any clip without
    /// Apple's Live Photo tag) is a video and is pointed out; only a tagged clip stays with its photo. Big clips are never read.
    /// </summary>
    [Fact]
    public void Only_a_clip_with_apples_live_photo_tag_is_paired_with_a_photo_of_the_same_name()
    {
        List<SourceFile> files = Files((@"t3\IMG_0413.CR3", 30 * MB), (@"t3\IMG_0413.JPG", 8 * MB), (@"t3\IMG_0413.MOV", 14 * MB),
            (@"phone\IMG_0500.HEIC", 2 * MB), (@"phone\IMG_0500.MOV", 3 * MB), (@"phone\IMG_0501.HEIC", 2 * MB), (@"phone\IMG_0501.MOV", 28 * MB),
            (@"galaxy\20240101_120500.heic", 3 * MB), (@"galaxy\20240101_120500.mp4", 4 * MB));
        var read = new List<string>();
        Classifier.Classify(files, null, "Card", _ => false, clip =>
        {
            read.Add(clip.RelativePath);
            return clip.Directory == "phone";
        });
        var r = files.ToDictionary(f => f.RelativePath);

        SourceFile canonClip = r[@"t3\IMG_0413.MOV"];
        Assert.Equal(MediaSide.Video, canonClip.Side);
        Assert.Equal(FileNote.NotLivePhoto, canonClip.Note);
        Assert.True(canonClip.NeedsAttention);
        Assert.Equal("video (.MOV) - same name as the photo IMG_0413.JPG, but not a Live Photo clip (no Apple Live Photo tag in it)", canonClip.Reason);
        Assert.Equal(@"t3\IMG_0413", canonClip.GroupKey);

        Assert.Equal((MediaSide.Photo, FileNote.LivePhoto), (r[@"phone\IMG_0500.MOV"].Side, r[@"phone\IMG_0500.MOV"].Note));
        Assert.Equal(r[@"phone\IMG_0500.HEIC"].GroupKey, r[@"phone\IMG_0500.MOV"].GroupKey);
        Assert.Equal((MediaSide.Video, FileNote.LivePhotoTooLarge), (r[@"phone\IMG_0501.MOV"].Side, r[@"phone\IMG_0501.MOV"].Note));
        Assert.Equal((MediaSide.Video, FileNote.NotLivePhoto), (r[@"galaxy\20240101_120500.mp4"].Side, r[@"galaxy\20240101_120500.mp4"].Note));
        Assert.Equal(new[] { @"galaxy\20240101_120500.mp4", @"phone\IMG_0500.MOV", @"t3\IMG_0413.MOV" }, read.Order(StringComparer.Ordinal));

        // Without anything to read the clips with, nothing is paired.
        List<SourceFile> unread = Files((@"phone\IMG_0500.HEIC", 2 * MB), (@"phone\IMG_0500.MOV", 3 * MB));
        Classifier.Classify(unread, null, "Card");
        Assert.Equal(MediaSide.Video, unread[1].Side);
    }

    [Fact]
    public void Apples_live_photo_tag_is_found_in_the_clips_metadata_without_touching_the_clip()
    {
        using var t = new TestTree();
        string live = QuickTime(t, "live.MOV", movieFirst: false, "com.apple.quicktime.content.identifier", "com.apple.quicktime.still-image-time");
        string liveMovieFirst = QuickTime(t, "live-faststart.MOV", movieFirst: true, "com.apple.quicktime.content.identifier");
        string largeMdat = QuickTime(t, "live-64bit.MOV", movieFirst: false, largeSize: true, "com.apple.quicktime.content.identifier");
        string video = QuickTime(t, "video.MOV", movieFirst: false, "com.apple.quicktime.make", "com.apple.quicktime.model");
        string keyInMediaData = t.Add("fake.MOV", 0);
        File.WriteAllBytes(keyInMediaData, [.. Box("ftyp", "qt  \0\0\0\0qt  "u8.ToArray()), .. Box("mdat", "com.apple.quicktime.content.identifier"u8.ToArray()),
            .. Box("moov", Box("mvhd", new byte[100]))]);
        string random = t.Add("random.MOV", 5000);
        var accessed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(live, accessed);

        Assert.True(LivePhotoClip.IsLivePhotoClip(live));
        Assert.True(LivePhotoClip.IsLivePhotoClip(liveMovieFirst));
        Assert.True(LivePhotoClip.IsLivePhotoClip(largeMdat));
        Assert.False(LivePhotoClip.IsLivePhotoClip(video));
        Assert.False(LivePhotoClip.IsLivePhotoClip(keyInMediaData)); // only the metadata box counts
        Assert.False(LivePhotoClip.IsLivePhotoClip(random));
        Assert.False(LivePhotoClip.IsLivePhotoClip(Path.Join(t.Source, "missing.MOV")));
        Assert.Equal(accessed, File.GetLastAccessTimeUtc(live));
    }

    /// <summary>A QuickTime box: 32-bit big-endian size, type, body.</summary>
    private static byte[] Box(string type, byte[] body)
    {
        byte[] box = new byte[8 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box, 8);
        return box;
    }

    /// <summary>Writes a minimal QuickTime file: ftyp, media data, and moov\meta\keys with the given metadata keys.</summary>
    private static string QuickTime(TestTree t, string name, bool movieFirst, params string[] keys) => QuickTime(t, name, movieFirst, false, keys);

    private static string QuickTime(TestTree t, string name, bool movieFirst, bool largeSize, params string[] keys)
    {
        string path = t.Add(name, 0);
        var entries = new List<byte>();
        foreach (string key in keys)
        {
            byte[] k = System.Text.Encoding.ASCII.GetBytes(key);
            byte[] entry = new byte[8 + k.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)entry.Length);
            "mdta"u8.CopyTo(entry.AsSpan(4));
            k.CopyTo(entry, 8);
            entries.AddRange(entry);
        }
        byte[] keysBody = [0, 0, 0, 0, 0, 0, 0, (byte)keys.Length, .. entries];
        byte[] moov = Box("moov", [.. Box("mvhd", new byte[100]), .. Box("meta", [0, 0, 0, 0, .. Box("hdlr", new byte[25]), .. Box("keys", keysBody)])]);
        byte[] media = new byte[70_000];
        new Random(7).NextBytes(media);
        byte[] mdat;
        if (largeSize)
        {
            mdat = new byte[16 + media.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
            "mdat"u8.CopyTo(mdat.AsSpan(4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
            media.CopyTo(mdat, 16);
        }
        else
            mdat = Box("mdat", media);
        byte[] ftyp = Box("ftyp", "qt  \0\0\0\0qt  "u8.ToArray());
        File.WriteAllBytes(path, movieFirst ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. Box("wide", []), .. mdat, .. moov]);
        return path;
    }

    [Fact]
    public void Cinemadng_clip_folder_is_one_video_but_a_photogrammetry_run_stays_photos()
    {
        var entries = new List<(string, long)>();
        for (int i = 0; i < 12; i++) entries.Add(($@"clips\A001_C003_0921XY\A001_C003_0921XY_{i:D6}.dng", 1));
        entries.Add((@"clips\A001_C003_0921XY\A001_C003_0921XY.wav", 1));
        entries.Add((@"clips\A001_C003_0921XY\A001_C003_0921XY_000000.xmp", 1));
        // Folder named differently from the clip: the WAV named after the clip gives it away.
        for (int i = 0; i < 12; i++) entries.Add(($@"clips\Clip 1\X7_0012_{i:D6}.DNG", 1));
        entries.Add((@"clips\Clip 1\X7_0012.WAV", 1));
        // A mapping flight: 4-digit DNG stills in 100MEDIA, and even in a folder named like their prefix.
        for (int i = 1; i <= 30; i++) entries.Add(($@"flight\DCIM\100MEDIA\DJI_{i:D4}.DNG", 1));
        for (int i = 1; i <= 30; i++) entries.Add(($@"flight\DJI\DJI_{i:D4}.DNG", 1));
        // Too few frames to be a clip.
        for (int i = 0; i < 5; i++) entries.Add(($@"few\few_{i:D6}.dng", 1));

        List<SourceFile> files = Files(entries.ToArray());
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files);
        var r = files.ToDictionary(f => f.RelativePath);

        Assert.All(files.Where(f => f.Directory == @"clips\A001_C003_0921XY"), f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.Contains("CinemaDNG clip A001_C003_0921XY (12 frames)", r[@"clips\A001_C003_0921XY\A001_C003_0921XY.wav"].Reason);
        Assert.All(files.Where(f => f.Directory == @"clips\Clip 1"), f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.All(files.Where(f => f.RelativePath.StartsWith(@"flight\")), f => Assert.Equal(MediaSide.Photo, f.Side));
        Assert.All(files.Where(f => f.Directory == "few"), f => Assert.Equal(MediaSide.Photo, f.Side));
        Assert.Equal(@"clips\A001_C003_0921XY", r[@"clips\A001_C003_0921XY\A001_C003_0921XY_000003.dng"].GroupKey);
        Assert.Equal(2, units.Count(u => u.Kind == UnitKind.ImageSequence));
        Assert.Equal(14, units.Single(u => u.Folder == @"clips\A001_C003_0921XY").Files);
    }

    [Fact]
    public void A_clip_folder_picked_as_the_source_itself_is_still_one_clip()
    {
        var entries = Enumerable.Range(0, 10).Select(i => ($"SDIM0002_{i:D6}.DNG", 1L)).Append(("SDIM0002.WAV", 1L)).ToArray();
        var r = ClassifyIn("SDIM0002", entries);
        Assert.All(r.Values, f => Assert.Equal(MediaSide.Video, f.Side));
    }

    [Fact]
    public void Dng_photos_named_like_their_folder_stay_photos()
    {
        var entries = new List<(string, long)>();
        // Lightroom "Custom Name - Sequence # (00001)" renamed into a folder of the same name.
        for (int i = 1; i <= 40; i++) entries.Add(($@"Weddings\Smith_Wedding\Smith_Wedding_{i:D5}.dng", 30 * MB));
        entries.Add((@"Weddings\Smith_Wedding\Smith_Wedding_00001.dng.xmp", 1));
        // Phone RAW + JPG pairs (yyyyMMdd_HHmmss) in a date folder named yyyyMMdd.
        for (int i = 0; i < 12; i++)
        {
            entries.Add(($@"Phone\20230615\20230615_1430{i:D2}.dng", 20 * MB));
            entries.Add(($@"Phone\20230615\20230615_1430{i:D2}.jpg", 4 * MB));
        }
        List<SourceFile> files = Files(entries.ToArray());
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, null, "Card", _ => false);
        Assert.All(files, f => Assert.Equal(MediaSide.Photo, f.Side));
        Assert.Empty(units);
    }

    [Fact]
    public void A_dng_clip_needs_its_sound_file_or_cinemadng_tags_and_frames_without_gaps()
    {
        var entries = new List<(string, long)>();
        for (int i = 0; i < 12; i++) entries.Add(($@"X7_0012\X7_0012_{i:D6}.DNG", 1));   // drone camera: no sound file
        for (int i = 0; i < 12; i++) entries.Add(($@"Gappy\Gappy_{i * 2:D6}.dng", 1));    // every other number: not one recording
        entries.Add((@"Gappy\Gappy.wav", 1));
        for (int i = 0; i < 12; i++) entries.Add(($@"A001_C004\A001_C004_{i:D6}.dng", 1));
        entries.Add((@"A001_C004\A001_C004.wav", 1));
        entries.Add((@"A001_C004\behind the scenes.JPG", 1)); // a photo in the clip folder is not a frame
        entries.Add((@"A001_C004\A001_C004_000000.xmp", 1));

        List<SourceFile> withoutTags = Files(entries.ToArray());
        Classifier.Classify(withoutTags, null, "Card", _ => false);
        var r = withoutTags.ToDictionary(f => f.RelativePath);
        Assert.Equal(MediaSide.Photo, r[@"X7_0012\X7_0012_000000.DNG"].Side);
        Assert.Equal(MediaSide.Photo, r[@"Gappy\Gappy_000000.dng"].Side);
        Assert.Equal(MediaSide.Video, r[@"A001_C004\A001_C004_000011.dng"].Side);
        Assert.Equal(MediaSide.Video, r[@"A001_C004\A001_C004_000000.xmp"].Side);
        Assert.Equal(MediaSide.Photo, r[@"A001_C004\behind the scenes.JPG"].Side);

        var probed = new List<string>();
        List<SourceFile> withTags = Files(entries.ToArray());
        Classifier.Classify(withTags, null, "Card", f => { probed.Add(f.RelativePath); return true; });
        Assert.All(withTags.Where(f => f.Directory == "X7_0012"), f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.Contains("CinemaDNG clip X7_0012 (12 frames)", withTags.First(f => f.Directory == "X7_0012").Reason);
        // One frame read per run, and only where no sound file decides. Changed deliberately: tagged frames with gaps
        // are still one clip (with its sound), and the preview warns about the missing frames.
        Assert.Equal(new[] { @"X7_0012\X7_0012_000000.DNG", @"Gappy\Gappy_000000.dng" }, probed);
        Assert.All(withTags.Where(f => f.Directory == "Gappy"), f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.Contains("CinemaDNG clip Gappy (12 frames, 11 missing)", withTags.First(f => f.Directory == "Gappy").Reason);
    }

    /// <summary>
    /// CinemaDNG movie tags decide on their own. A lost frame, a clip of fewer than 10 frames or a renamed folder
    /// without sound (a drone camera) keeps the frames together as one clip, with its sound. Untagged runs are unchanged.
    /// </summary>
    [Fact]
    public void Frames_with_cinemadng_tags_stay_one_clip_despite_gaps_few_frames_or_a_renamed_folder()
    {
        var entries = new List<(string, long)>();
        foreach (int i in Enumerable.Range(0, 20).Where(i => i != 5)) entries.Add(($@"E_BMPCC_CDNG\C0002\C0002_{i:D6}.dng", 1));
        entries.Add((@"E_BMPCC_CDNG\C0002\C0002.wav", 1));
        for (int i = 0; i < 8; i++) entries.Add(($@"E_BMPCC_CDNG\C0003\C0003_{i:D6}.dng", 1));
        entries.Add((@"E_BMPCC_CDNG\C0003\C0003.wav", 1));
        for (int i = 0; i < 48; i++) entries.Add(($@"Aerials\Shot05_Hero\X7_0012_{i:D6}.DNG", 1));
        // Two clips dumped into one folder: both are clips, and the sound stays with them.
        for (int i = 0; i < 3; i++) entries.Add(($@"Flat\A001_C001_{i:D6}.dng", 1));
        for (int i = 0; i < 4; i++) entries.Add(($@"Flat\A001_C002_{i:D6}.dng", 1));
        entries.Add((@"Flat\scratch.wav", 1));
        // Still DNGs named like frames (no tags) stay photos, and so does a single tagged frame.
        for (int i = 1; i <= 12; i++) entries.Add(($@"Wedding\Smith_{i:D5}.dng", 1));
        entries.Add((@"Single\B001_000001.dng", 1));

        List<SourceFile> files = Files(entries.ToArray());
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, null, "Card", f => !f.Directory.StartsWith("Wedding"));

        foreach (string folder in new[] { @"E_BMPCC_CDNG\C0002", @"E_BMPCC_CDNG\C0003", @"Aerials\Shot05_Hero", "Flat" })
            Assert.All(files.Where(f => f.Directory == folder), f => Assert.True(f.Side == MediaSide.Video, $"{f}"));
        Assert.All(files.Where(f => f.Directory is "Wedding" or "Single"), f => Assert.Equal(MediaSide.Photo, f.Side));

        MediaUnit gap = units.Single(u => u.Folder == @"E_BMPCC_CDNG\C0002");
        Assert.Equal((UnitKind.ImageSequence, 1, 20), (gap.Kind, gap.MissingFrames, gap.Files));
        Assert.Equal("CinemaDNG clip C0002 (19 frames, 1 missing)", gap.Description);
        Assert.Equal(0, units.Single(u => u.Folder == @"E_BMPCC_CDNG\C0003").MissingFrames);
        Assert.Equal("CinemaDNG clip X7_0012 (48 frames)", units.Single(u => u.Folder == @"Aerials\Shot05_Hero").Description);
        Assert.Equal("CinemaDNG clips A001_C002, A001_C001 (7 frames)", units.Single(u => u.Folder == "Flat").Description);
        Assert.All(files.Where(f => f.Directory == @"E_BMPCC_CDNG\C0002"), f => Assert.Equal(@"E_BMPCC_CDNG\C0002", f.GroupKey));
    }

    /// <summary>
    /// A clip found by its CinemaDNG tags alone may share its folder with stills: it takes in only its own frames, its
    /// sound and companions named after it. A still's edits stay with the still, and two frame grabs far apart are stills.
    /// </summary>
    [Fact]
    public void A_clip_found_by_its_cinemadng_tags_alone_does_not_take_in_other_photos_companions()
    {
        List<SourceFile> files = Files(
            // Frame grabs exported from one clip, next to stills and their edits.
            (@"Stills\2026-09-21\IMG_0001.CR3", 1), (@"Stills\2026-09-21\IMG_0001.xmp", 1), (@"Stills\2026-09-21\IMG_0002.CR3", 1),
            (@"Stills\2026-09-21\IMG_0002.xmp", 1), (@"Stills\2026-09-21\notes.pdf", 1),
            (@"Stills\2026-09-21\A001_C003_0101AB_000123.dng", 1), (@"Stills\2026-09-21\A001_C003_0101AB_000456.dng", 1),
            // A real (short) clip dumped next to a still with its edits, a voice memo and other files.
            (@"Mixed\C0007_000000.dng", 1), (@"Mixed\C0007_000001.dng", 1), (@"Mixed\C0007_000003.dng", 1), (@"Mixed\C0007.wav", 1),
            (@"Mixed\C0007.xmp", 1), (@"Mixed\C0007_000001.xmp", 1), (@"Mixed\DSC_0100.NEF", 1), (@"Mixed\DSC_0100.xmp", 1),
            (@"Mixed\DSC_0100.WAV", 1), (@"Mixed\notes.pdf", 1), (@"Mixed\Thumbs.db", 1), (@"Mixed\B-roll.MOV", 1));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, null, "Card", _ => true);
        var r = files.ToDictionary(f => f.RelativePath);

        Assert.All(files.Where(f => f.Directory == @"Stills\2026-09-21" && f.Extension != ".pdf"), f => Assert.Equal(MediaSide.Photo, f.Side));
        Assert.Equal(r[@"Stills\2026-09-21\IMG_0001.CR3"].GroupKey, r[@"Stills\2026-09-21\IMG_0001.xmp"].GroupKey);
        Assert.Equal(MediaSide.Neutral, r[@"Stills\2026-09-21\notes.pdf"].Side);
        Assert.DoesNotContain(units, u => u.Folder == @"Stills\2026-09-21");

        MediaUnit clip = units.Single(u => u.Folder == "Mixed");
        Assert.Equal("CinemaDNG clip C0007 (3 frames, 1 missing)", clip.Description);
        foreach (string video in new[] { "C0007_000003.dng", "C0007.wav", "C0007.xmp", "C0007_000001.xmp", "B-roll.MOV" })
            Assert.Equal((MediaSide.Video, "Mixed"), (r[$@"Mixed\{video}"].Side, r[$@"Mixed\{video}"].GroupKey));
        foreach (string photo in new[] { "DSC_0100.NEF", "DSC_0100.xmp", "DSC_0100.WAV" })
            Assert.Equal((MediaSide.Photo, @"Mixed\DSC_0100"), (r[$@"Mixed\{photo}"].Side, r[$@"Mixed\{photo}"].GroupKey));
        Assert.Equal(MediaSide.Neutral, r[@"Mixed\notes.pdf"].Side);
        Assert.Equal(FileNote.SystemFile, r[@"Mixed\Thumbs.db"].Note);
        Assert.Equal(6, clip.Files); // 3 frames, the sound, the clip's .xmp and the video; the frame's .xmp follows its frame
    }

    [Fact]
    public void Processing_folders_without_a_project_file_are_recognized_by_their_layout()
    {
        List<SourceFile> files = Files((@"Proc\ODM_Crop\images\DJI_0001.JPG", 1), (@"Proc\ODM_Crop\odm_orthophoto\odm_orthophoto.tif", 1),
            (@"Proc\ODM_Crop\odm_dem\dsm.tif", 1), (@"Proc\Terra\map\result.tif", 1), (@"Proc\Terra\map\result.tfw", 1),
            (@"Proc\Solar\Solar.p4m", 1), (@"Proc\Solar\exports\Solar_dsm.tif", 1),
            (@"Proc\Terra3D\models\pc\0\terra_las\cloud_merged.las", 1), (@"Proc\Terra3D\images\DJI_0002.JPG", 1),
            (@"Proc\TerraL2\lidars\terra_las\cloud_merged.las", 1), (@"Proc\TerraL2\DJI_0003.JPG", 1),
            (@"Maps\map\city.jpg", 1), (@"Maps\images\x.jpg", 1), // a folder called "map" or "images" alone is no project
            // Nor are folders called map, models and lidars without DJI Terra's results in them (location scouting, casting).
            (@"Shoot\Map\scout.jpg", 1), (@"Shoot\Models\casting.jpg", 1), (@"Shoot\Lidars\scan.jpg", 1), (@"Shoot\A001.MP4", 1),
            (@"Shoot2\map\result.jpg", 1), (@"Shoot2\models\pc\front.jpg", 1));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files);
        var r = files.ToDictionary(f => f.RelativePath);
        Assert.Equal(new[]
            {
                "OpenDroneMap project (odm_dem folder)", "Pix4Dmatic project (Solar.p4m)", @"DJI Terra project (map\result.tif)",
                @"DJI Terra project (models\pc\0\terra_las folder)", @"DJI Terra project (lidars\terra_las folder)",
            },
            units.Where(u => u.Kind == UnitKind.Project).Select(u => u.Description));
        Assert.Equal("inside an editing/processing project (odm_dem folder) - stays so the project keeps its media", r[@"Proc\ODM_Crop\images\DJI_0001.JPG"].Reason);
        Assert.Equal(MediaSide.Photo, r[@"Proc\ODM_Crop\images\DJI_0001.JPG"].NaturalSide);
        Assert.Equal(FileNote.InProject, r[@"Proc\Terra3D\images\DJI_0002.JPG"].Note);
        Assert.Equal(FileNote.InProject, r[@"Proc\TerraL2\DJI_0003.JPG"].Note);
        Assert.Equal(MediaSide.Photo, r[@"Maps\map\city.jpg"].Side);
        Assert.Equal(MediaSide.Photo, r[@"Maps\images\x.jpg"].Side);
        Assert.All(files.Where(f => f.RelativePath.StartsWith("Shoot")), f => Assert.NotEqual(FileNote.InProject, f.Note));
        Assert.Equal(MediaSide.Video, r[@"Shoot\A001.MP4"].Side);
    }

    [Fact]
    public void Survey_data_and_hyperlapse_frames_are_named_for_what_they_are()
    {
        var entries = new List<(string, long)> { (@"Roof\BASE\DRTK3_20240119.24O", 1), (@"Roof\GCP\GCPs.txt", 1), (@"Roof\GCP\notes.txt", 1) };
        for (int i = 1; i <= 30; i++) entries.Add(($@"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_{i:D4}.JPG", 1));
        entries.Add((@"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0001.xmp", 1));
        entries.Add((@"M3\DCIM\100MEDIA\DJI_0001.JPG", 1));
        entries.Add((@"M3\DCIM\100MEDIA\HYPERLAPSE_0001.JPG", 1)); // one photo with that name is just a photo
        entries.Add((@"Air\DCIM\100MEDIA\HYPERLAPSE_0001.JPG", 1));
        entries.Add((@"Air\DCIM\100MEDIA\HYPERLAPSE_0002.JPG", 1));
        entries.Add((@"Air\DCIM\100MEDIA\DJI_0003.JPG", 1));
        var r = Classify(entries.ToArray());

        Assert.Equal((MediaSide.Neutral, FileNote.SurveyData), (r[@"Roof\BASE\DRTK3_20240119.24O"].Side, r[@"Roof\BASE\DRTK3_20240119.24O"].Note));
        Assert.Equal(FileNote.SurveyData, r[@"Roof\GCP\GCPs.txt"].Note);
        Assert.Equal(FileNote.None, r[@"Roof\GCP\notes.txt"].Note);
        Assert.False(r[@"Roof\BASE\DRTK3_20240119.24O"].NeedsAttention);

        SourceFile frame = r[@"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0017.JPG"];
        Assert.Equal((MediaSide.Photo, FileNote.HyperlapseFrame), (frame.Side, frame.Note));
        Assert.Equal(@"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005", frame.GroupKey); // the frames of one hyperlapse stay together
        Assert.Equal("photo (.JPG), a still frame of the DJI hyperlapse HYPERLAPSE_0005 (30 frames) - stays together with its folder", frame.Reason);
        Assert.Equal(frame.GroupKey, r[@"M3\DCIM\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_0001.xmp"].GroupKey);
        Assert.Equal(FileNote.None, r[@"M3\DCIM\100MEDIA\HYPERLAPSE_0001.JPG"].Note);
        Assert.Equal(@"M3\DCIM\100MEDIA\DJI_0001", r[@"M3\DCIM\100MEDIA\DJI_0001.JPG"].GroupKey);
        // Frames kept in a folder with other photos: only the frames are kept together.
        Assert.Equal(@"Air\DCIM\100MEDIA", r[@"Air\DCIM\100MEDIA\HYPERLAPSE_0002.JPG"].GroupKey);
        Assert.Equal((FileNote.None, @"Air\DCIM\100MEDIA\DJI_0003"), (r[@"Air\DCIM\100MEDIA\DJI_0003.JPG"].Note, r[@"Air\DCIM\100MEDIA\DJI_0003.JPG"].GroupKey));
    }

    [Fact]
    public void An_xmp_matching_a_raw_photo_and_an_mov_follows_the_raw_and_says_why()
    {
        var r = Classify(@"Nikon\DSC_0001.NEF", @"Nikon\DSC_0001.JPG", @"Nikon\DSC_0001.MOV", @"Nikon\DSC_0001.xmp");
        Assert.Equal(MediaSide.Photo, r[@"Nikon\DSC_0001.xmp"].Side);
        Assert.Equal(FileNote.None, r[@"Nikon\DSC_0001.xmp"].Note);
        Assert.Contains("(not with the video DSC_0001.MOV)", r[@"Nikon\DSC_0001.xmp"].Reason);
        Assert.Equal(r[@"Nikon\DSC_0001.NEF"].GroupKey, r[@"Nikon\DSC_0001.xmp"].GroupKey);
    }

    [Fact]
    public void Online_only_files_and_links_remember_the_side_they_belong_to()
    {
        List<SourceFile> files = Files((@"Card1\C0000.MP4", 1), (@"Card1\C0003.MP4", 1), (@"Card1\DSC_1.JPG", 1), (@"Card1\notes.txt", 1),
            (@"Card1\PRIVATE\M4ROOT\CLIP\C0009M01.XML", 1), (@"Card1\link.MOV", 1));
        files[1] = Offline(files[1]);
        files[2] = Offline(files[2]);
        files[3] = Offline(files[3]);
        files[4] = Offline(files[4]);
        files[5] = new SourceFile { RelativePath = files[5].RelativePath, Size = 1, CreationTime = 0, LastWriteTime = 0, Attributes = FileAttributes.ReparsePoint };
        Classifier.Classify(files);
        var r = files.ToDictionary(f => f.RelativePath);
        Assert.Equal((MediaSide.Video, MediaSide.Neutral), (r[@"Card1\C0000.MP4"].Side, r[@"Card1\C0000.MP4"].NaturalSide));
        SourceFile offline = r[@"Card1\C0003.MP4"];
        Assert.Equal((MediaSide.Neutral, FileNote.OnlineOnly, MediaSide.Video), (offline.Side, offline.Note, offline.NaturalSide));
        Assert.Equal("online-only cloud placeholder (video, not downloaded) - stays. To move it, make it available offline.", offline.Reason);
        Assert.Equal(MediaSide.Photo, r[@"Card1\DSC_1.JPG"].NaturalSide);
        Assert.Equal(MediaSide.Neutral, r[@"Card1\notes.txt"].NaturalSide);
        Assert.Equal(MediaSide.Video, r[@"Card1\PRIVATE\M4ROOT\CLIP\C0009M01.XML"].NaturalSide);
        Assert.Equal((FileNote.Link, MediaSide.Video), (r[@"Card1\link.MOV"].Note, r[@"Card1\link.MOV"].NaturalSide));
    }

    /// <summary>
    /// An online-only clip named like a Live Photo still cannot be read without downloading it, so it may be the still's
    /// Live Photo clip or a video: it is counted on neither side. A clip too large for a Live Photo is a video.
    /// </summary>
    [Fact]
    public void An_online_only_clip_that_may_be_a_live_photo_clip_belongs_to_no_side()
    {
        List<SourceFile> files = Files((@"Phone\IMG_0501.HEIC", 2 * MB), (@"Phone\IMG_0501.MOV", 3 * MB), (@"Phone\IMG_0502.HEIC", 2 * MB),
            (@"Phone\IMG_0502.MOV", 30 * MB), (@"Phone\IMG_0503.JPG", 2 * MB), (@"Phone\IMG_0503.MP4", 3 * MB), (@"Phone\DSC_0504.JPG", 2 * MB),
            (@"Phone\DSC_0504.MOV", 3 * MB));
        for (int i = 0; i < files.Count; i++)
            if (files[i].Name != "IMG_0503.JPG") files[i] = Offline(files[i]);
        Classifier.Classify(files, null, null, isLivePhotoClip: _ => throw new InvalidOperationException("online-only files are never read"));
        var r = files.ToDictionary(f => f.RelativePath);

        SourceFile maybe = r[@"Phone\IMG_0501.MOV"];
        Assert.Equal((MediaSide.Neutral, FileNote.OnlineOnly, MediaSide.Neutral), (maybe.Side, maybe.Note, maybe.NaturalSide));
        Assert.Equal("online-only cloud placeholder (Live Photo clip or video: unknown until you download it) - stays. To sort it, make it available offline.",
            maybe.Reason);
        Assert.Equal(MediaSide.Neutral, r[@"Phone\IMG_0503.MP4"].NaturalSide); // its still is on the disk
        Assert.Equal(MediaSide.Photo, r[@"Phone\IMG_0501.HEIC"].NaturalSide);
        Assert.Equal(MediaSide.Video, r[@"Phone\IMG_0502.MOV"].NaturalSide);  // too large for a Live Photo clip
        Assert.Equal(MediaSide.Video, r[@"Phone\DSC_0504.MOV"].NaturalSide);  // a JPEG not named like an iPhone photo
    }

    private static SourceFile Offline(SourceFile f) => new()
    {
        RelativePath = f.RelativePath, Size = f.Size, CreationTime = 0, LastWriteTime = 0, Attributes = FileAttributes.Archive | FileAttributes.Offline,
    };

    [Fact]
    public void Cinemadng_movie_frames_are_told_from_still_dngs_by_their_tags_without_touching_them()
    {
        using var t = new TestTree();
        string movie = Tiff(t, "movie.dng", littleEndian: true, 0x0100, 0xC612, 0xC764);
        string movieBigEndian = Tiff(t, "movie-mm.dng", littleEndian: false, 0x0100, 0xC612, 0xC763);
        string still = Tiff(t, "still.dng", littleEndian: true, 0x0100, 0x010F, 0xC612);
        string random = t.Add("random.dng", 5000);
        var accessed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(movie, accessed);

        Assert.True(CinemaDng.IsMovieFrame(movie));
        Assert.True(CinemaDng.IsMovieFrame(movieBigEndian));
        Assert.False(CinemaDng.IsMovieFrame(still));
        Assert.False(CinemaDng.IsMovieFrame(random));
        Assert.False(CinemaDng.IsMovieFrame(Path.Join(t.Source, "missing.dng")));
        Assert.Equal(accessed, File.GetLastAccessTimeUtc(movie));
    }

    /// <summary>Writes a minimal TIFF: header, then IFD0 with the given tags (SHORT values).</summary>
    private static string Tiff(TestTree t, string name, bool littleEndian, params ushort[] tags)
    {
        string path = t.Add(name, 0);
        var bytes = new List<byte>();
        void U16(int v) => bytes.AddRange(littleEndian ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v]);
        void U32(long v) => bytes.AddRange(littleEndian
            ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)] : [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        bytes.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(8);
        U16(tags.Length);
        foreach (ushort tag in tags)
        {
            U16(tag);
            U16(3); // SHORT
            U32(1);
            U32(0);
        }
        U32(0);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    [Fact]
    public void A_dcim_folder_next_to_an_unwrapped_sony_card_keeps_its_photos()
    {
        var r = Classify(@"CardA\MEDIAPRO.XML", @"CardA\CLIP\C0001.MP4", @"CardA\CLIP\C0001M01.XML", @"CardA\THMBNL\C0001T01.JPG",
            @"CardA\DCIM\100MSDCF\DSC00001.ARW", @"CardA\DCIM\100MSDCF\DSC00001.JPG", @"CardA\cover.JPG");
        Assert.Equal(MediaSide.Video, r[@"CardA\THMBNL\C0001T01.JPG"].Side);
        Assert.Equal(MediaSide.Video, r[@"CardA\MEDIAPRO.XML"].Side);
        Assert.Equal(@"CardA", r[@"CardA\CLIP\C0001.MP4"].GroupKey);
        Assert.Equal(MediaSide.Photo, r[@"CardA\DCIM\100MSDCF\DSC00001.ARW"].Side);
        Assert.Equal(MediaSide.Photo, r[@"CardA\DCIM\100MSDCF\DSC00001.JPG"].Side);
        Assert.Equal(MediaSide.Photo, r[@"CardA\cover.JPG"].Side);
    }

    [Fact]
    public void Group_keys_keep_a_clip_its_proxies_and_companions_together()
    {
        var r = Classify(@"d\DJI_0001.MP4", @"d\DJI_0001.LRF", @"d\DJI_0001.SRT", @"d\._DJI_0001.MP4", @"d\DJI_0002.JPG", @"d\notes.txt",
            @"d\DJI_0001.MP4.xmp", @"m\x.JPG", @"m\x_Timestamp.MRK", @"m\PPKRAW.bin");
        Assert.Equal(@"d\DJI_0001", r[@"d\DJI_0001.MP4"].GroupKey);
        Assert.Equal(@"d\DJI_0001", r[@"d\DJI_0001.LRF"].GroupKey);
        Assert.Equal(@"d\DJI_0001", r[@"d\DJI_0001.SRT"].GroupKey);
        Assert.Equal(@"d\DJI_0001", r[@"d\._DJI_0001.MP4"].GroupKey);
        Assert.Equal(@"d\DJI_0001", r[@"d\DJI_0001.MP4.xmp"].GroupKey);
        Assert.Equal(@"d\DJI_0002", r[@"d\DJI_0002.JPG"].GroupKey);
        Assert.Equal(@"d\notes.txt", r[@"d\notes.txt"].GroupKey);
        Assert.Equal("m", r[@"m\x.JPG"].GroupKey);
        Assert.Equal("m", r[@"m\PPKRAW.bin"].GroupKey);
        Assert.Contains("(the folder has x_Timestamp.MRK)", r[@"m\PPKRAW.bin"].Reason);
    }

    [Fact]
    public void Projects_are_reported_and_explained()
    {
        List<SourceFile> files = Files((@"Edits\Promo\Promo.prproj", 1), (@"Edits\Promo\Footage\A.MP4", 5), (@"Edits\clip.MP4", 1));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files);
        SourceFile footage = files[1];
        Assert.Equal(FileNote.InProject, footage.Note);
        Assert.Equal(@"inside an editing/processing project (Promo.prproj) - stays so the project keeps its media", footage.Reason);
        MediaUnit project = Assert.Single(units, u => u.Kind == UnitKind.Project);
        Assert.Equal(@"Edits\Promo", project.Folder);
        Assert.Equal("Premiere Pro project (Promo.prproj)", project.Description);
        Assert.Equal(2, project.Files);
        Assert.Equal(MediaSide.Video, files[2].Side);
    }

    [Fact]
    public void A_project_file_in_the_source_folder_itself_keeps_everything()
    {
        var r = Classify(@"Promo.prproj", @"Footage\A.MP4", @"Stills\B.JPG");
        Assert.All(r.Values, f => Assert.Equal(FileNote.InProject, f.Note));
    }

    [Fact]
    public void Card_structures_are_units_and_the_scanned_folder_set_decides_the_signature()
    {
        // With the scanner's folder list, an empty VIDEO folder is enough to recognize P2.
        List<SourceFile> files = Files((@"card\CONTENTS\CLIP\0001AB.XML", 1), (@"card\CONTENTS\ICON\0001AB.BMP", 1));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, [@"card", @"card\CONTENTS", @"card\CONTENTS\CLIP", @"card\CONTENTS\ICON", @"card\CONTENTS\VIDEO"], "src");
        Assert.All(files, f => Assert.Equal(MediaSide.Video, f.Side));
        Assert.Equal("Panasonic P2 card structure", Assert.Single(units).Description);

        List<SourceFile> plain = Files((@"card\CONTENTS\CLIP\0001AB.XML", 1), (@"card\CONTENTS\ICON\0001AB.BMP", 1));
        Classifier.Classify(plain);
        Assert.Equal(MediaSide.Photo, plain[1].Side); // no VIDEO or AUDIO folder: just a folder called CONTENTS

        // A user folder CONTENTS with a VIDEO or AUDIO folder but no CLIP folder is no P2 card either.
        var user = Classify(@"u\CONTENTS\VIDEO\holiday.MP4", @"u\CONTENTS\VIDEO\poster.JPG", @"u\CONTENTS\AUDIO\notes.txt");
        Assert.Equal(MediaSide.Photo, user[@"u\CONTENTS\VIDEO\poster.JPG"].Side);
        Assert.Equal(MediaSide.Neutral, user[@"u\CONTENTS\AUDIO\notes.txt"].Side);
    }

    [Fact]
    public void An_unwrapped_sony_card_picked_as_the_source_is_one_card_structure()
    {
        List<SourceFile> files = Files((@"MEDIAPRO.XML", 1), (@"CLIP\C0001.MP4", 1), (@"CLIP\C0001M01.XML", 1), (@"THMBNL\C0001T01.JPG", 1),
            (@"DCIM\100MSDCF\DSC00001.JPG", 1), (@"cover.JPG", 1));
        IReadOnlyList<MediaUnit> units = Classifier.Classify(files, null, "FX3_A");
        var r = files.ToDictionary(f => f.RelativePath);
        Assert.Equal(MediaSide.Video, r[@"THMBNL\C0001T01.JPG"].Side);
        Assert.Equal(MediaSide.Video, r["MEDIAPRO.XML"].Side);
        Assert.Equal("part of the Sony card structure (FX3_A)", r[@"CLIP\C0001.MP4"].Reason);
        // One group for the whole card, although its folder is the source folder itself (an empty key would mean no group).
        Assert.Equal("*", r[@"CLIP\C0001.MP4"].GroupKey);
        Assert.Equal("*", r[@"THMBNL\C0001T01.JPG"].GroupKey);
        Assert.Equal(MediaSide.Photo, r[@"DCIM\100MSDCF\DSC00001.JPG"].Side);
        Assert.Equal(MediaSide.Photo, r["cover.JPG"].Side);
        MediaUnit card = Assert.Single(units);
        Assert.Equal("", card.Folder);
        Assert.Equal(4, card.Files);

        // Loose files next to the card's folders keep their own rules: a raw's .xmp stays with it, and a clip from
        // another camera is a video of its own (a name clash on it does not hold back the whole card).
        List<SourceFile> loose = Files((@"MEDIAPRO.XML", 1), (@"STATUS.BIN", 1), (@"CLIP\C0001.MP4", 1), (@"THMBNL\C0001T01.JPG", 1),
            (@"IMG_0001.CR3", 1), (@"IMG_0001.xmp", 1), (@"GX010001.MP4", 1), (@"notes.txt", 1));
        Classifier.Classify(loose, null, "FX3_A");
        var l = loose.ToDictionary(f => f.RelativePath);
        Assert.Equal("*", l["STATUS.BIN"].GroupKey);
        Assert.Equal(MediaSide.Photo, l["IMG_0001.xmp"].Side);
        Assert.Equal(MediaSide.Video, l["GX010001.MP4"].Side);
        Assert.Equal("video (.MP4)", l["GX010001.MP4"].Reason);
        Assert.NotEqual("*", l["GX010001.MP4"].GroupKey);
        Assert.Equal(MediaSide.Neutral, l["notes.txt"].Side);

        // Sorting it runs: the card's folders move as one, and the photos next to them stay.
        using (var t = new TestTree())
        {
            foreach (string rel in new[] { "MEDIAPRO.XML", @"CLIP\C0001.MP4", @"CLIP\C0001M01.XML", @"THMBNL\C0001T01.JPG", @"DCIM\100MSDCF\DSC00001.JPG" })
                t.Add(rel);
            MovePlan plan = t.Plan();
            Assert.True(plan.CanRun, string.Join(" | ", plan.Messages.Select(m => m.Text)));
            Assert.Null(plan.SuggestedSource);
            Assert.Equal(4, plan.ToMove.Count);
            Assert.Contains(plan.Messages, m => m.Text.Contains("(source folder) (Sony card structure, 4 files)"));
            var result = t.Run(plan);
            Assert.True(File.Exists(Path.Join(t.Target, @"THMBNL\C0001T01.JPG")), result.ToString());
            Assert.True(File.Exists(Path.Join(t.Target, "MEDIAPRO.XML")));
            Assert.True(File.Exists(Path.Join(t.Source, @"DCIM\100MSDCF\DSC00001.JPG")));
            Assert.False(File.Exists(Path.Join(t.Source, @"CLIP\C0001.MP4")));
        }

        // A source merely holding a CLIP folder is not a card.
        var plain = ClassifyIn("Shoot", (@"CLIP\C0001.MP4", 1), (@"THMBNL\C0001T01.JPG", 1));
        Assert.Equal(MediaSide.Photo, plain[@"THMBNL\C0001T01.JPG"].Side);
    }

    [Fact]
    public void Field_recorder_take_files_follow_the_recordings_next_to_them()
    {
        var r = Classify(@"H6\FOLDER01\ZOOM0001\ZOOM0001.hprj", @"H6\FOLDER01\ZOOM0001\ZOOM0001_LR.WAV", @"H6\FOLDER01\ZOOM0001\ZOOM0001_Tr1.WAV",
            @"F6\260920\260920_001.ZDT", @"F6\260920\260920_001_Tr1.WAV",
            @"alone\ZOOM0002.hprj", @"alone\readme.txt",
            @"mixed\DSCF0001.JPG", @"mixed\DSCF0001.WAV", @"mixed\take.WAV", @"mixed\take.ZDT");
        Assert.Equal(MediaSide.Video, r[@"H6\FOLDER01\ZOOM0001\ZOOM0001.hprj"].Side);
        Assert.Equal("field recorder take file for the recordings next to it (ZOOM0001_LR.WAV, ...) - goes with the videos",
            r[@"H6\FOLDER01\ZOOM0001\ZOOM0001.hprj"].Reason);
        Assert.Equal(MediaSide.Video, r[@"F6\260920\260920_001.ZDT"].Side);
        Assert.Equal(MediaSide.Neutral, r[@"alone\ZOOM0002.hprj"].Side);
        Assert.Equal(FileNote.None, r[@"alone\ZOOM0002.hprj"].Note);
        // A camera's voice memo goes with its photo, the other recording with the videos: the take file is not guessed.
        Assert.Equal(MediaSide.Neutral, r[@"mixed\take.ZDT"].Side);
        Assert.Equal(FileNote.Ambiguous, r[@"mixed\take.ZDT"].Note);
        Assert.Equal(MediaSide.Photo, r[@"mixed\DSCF0001.WAV"].Side);
        Assert.Equal(MediaSide.Video, r[@"mixed\take.WAV"].Side);

        // A card copied through a Mac has "._" files next to the recordings: they do not count as recordings.
        var mac = Classify(@"H6\ZOOM0001\ZOOM0001.hprj", @"H6\ZOOM0001\ZOOM0001_LR.WAV", @"H6\ZOOM0001\._ZOOM0001_LR.WAV", @"H6\ZOOM0001\._ZOOM0001.hprj");
        Assert.Equal(MediaSide.Video, mac[@"H6\ZOOM0001\ZOOM0001.hprj"].Side);
        Assert.Equal(MediaSide.Video, mac[@"H6\ZOOM0001\._ZOOM0001.hprj"].Side);
    }

    [Fact]
    public void Xml_named_after_a_clip_folder_follows_the_clip()
    {
        const string arri = @"A016R1K4\A016C001_120126_R1K4";
        var entries = Enumerable.Range(1, 12).Select(i => $@"{arri}\A016C001_120126_R1K4.{i:D7}.ari")
            .Append($@"{arri}\A016C001_120126_R1K4.xml").ToArray();
        var r = Classify(entries.Concat([@"Shoot\clip.MP4", @"Shoot\Shoot.xml", @"Mixed\clip.MP4", @"Mixed\still.JPG", @"Mixed\Mixed.xml",
            @"Other\clip.MP4", @"Other\settings.xml"]).ToArray());
        SourceFile xml = r[$@"{arri}\A016C001_120126_R1K4.xml"];
        Assert.Equal(MediaSide.Video, xml.Side);
        Assert.Equal("XML metadata for the clip A016C001_120126_R1K4 - goes with the videos", xml.Reason);
        Assert.Equal(r[$@"{arri}\A016C001_120126_R1K4.0000001.ari"].GroupKey, xml.GroupKey);
        Assert.Equal(MediaSide.Video, r[@"Shoot\Shoot.xml"].Side);
        Assert.Equal(MediaSide.Neutral, r[@"Mixed\Mixed.xml"].Side);    // a photo in the folder: not just one clip's folder
        Assert.Equal(MediaSide.Neutral, r[@"Other\settings.xml"].Side); // named after neither the folder nor the clip
    }

    [Fact]
    public void Unknown_file_with_a_videos_name_is_flagged_for_a_look()
    {
        var r = Classify(@"z\DSC_0003.NEWRAW", @"z\DSC_0003.MP4");
        Assert.Equal(MediaSide.Video, r[@"z\DSC_0003.NEWRAW"].Side);
        Assert.Equal(FileNote.FollowsByName, r[@"z\DSC_0003.NEWRAW"].Note);
        Assert.True(r[@"z\DSC_0003.NEWRAW"].NeedsAttention);
        Assert.Equal("unrecognized type with the same name as video DSC_0003.MP4 - goes with the videos", r[@"z\DSC_0003.NEWRAW"].Reason);
        Assert.Equal(r[@"z\DSC_0003.MP4"].GroupKey, r[@"z\DSC_0003.NEWRAW"].GroupKey);
    }

    [Fact]
    public void Mac_resource_files_follow_their_file()
    {
        var r = Classify(@"d\DJI_1.MOV", @"d\._DJI_1.MOV", @"d\._missing.jpg");
        Assert.Equal(MediaSide.Video, r[@"d\._DJI_1.MOV"].Side);
        Assert.Equal(MediaSide.Neutral, r[@"d\._missing.jpg"].Side);
    }

    [Fact]
    public void Unmatched_thumbnail_goes_with_videos_system_files_stay()
    {
        var r = Classify(@"d\orphan.THM", @"d\Thumbs.db", @"d\desktop.ini", @"d\card");
        Assert.Equal(MediaSide.Video, r[@"d\orphan.THM"].Side);
        Assert.Equal(FileNote.SystemFile, r[@"d\Thumbs.db"].Note);
        Assert.Equal(FileNote.SystemFile, r[@"d\desktop.ini"].Note);
        Assert.Equal(FileNote.UnknownType, r[@"d\card"].Note);
    }

    [Fact]
    public void Canon_crw_thumbnail_with_a_single_photo_owner_still_follows_the_photo()
    {
        var r = Classify(@"d\CRW_0001.CRW", @"d\CRW_0001.THM");
        Assert.Equal(MediaSide.Photo, r[@"d\CRW_0001.THM"].Side);
    }

    [Fact]
    public void Every_file_gets_a_reason()
    {
        var r = Classify(@"a\x.MOV", @"a\x.xmp", @"a\y.JPG", @"a\z.bin", @"a\q.qqq", @"a\._x.MOV", @"a\rec.WAV");
        Assert.All(r.Values, f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
    }
}

public class ScannerAndPlannerTests
{
    [Fact]
    public void Sync_and_nas_folders_are_never_scanned()
    {
        using var t = new TestTree();
        t.Add(@".sync\Archive\old.MOV");
        t.Add(@"@eaDir\x.MOV\SYNOPHOTO_THUMB_XL.jpg");
        t.Add(@"card\ascmhl\0001_card_2025-09-25_120000.mhl");
        t.Add(@"real\clip.MOV");
        ScanResult scan = Scanner.Scan(t.Source);
        Assert.Equal(new[] { @"real\clip.MOV" }, scan.Files.Select(f => f.RelativePath));
        Assert.Contains(scan.SkippedFolders, s => s.RelativePath == ".sync");
        Assert.Contains(scan.SkippedFolders, s => s.RelativePath == @"card\ascmhl");
    }

    [Fact]
    public void Application_libraries_are_never_entered()
    {
        using var t = new TestTree();
        t.Add(@"Photos Library.photoslibrary\originals\4\4F1C_3.mov");
        t.Add(@"Photos Library.photoslibrary\originals\A\A0B1.heic");
        t.Add(@"Client Films.fcpbundle\2025-09-20\Original Media\C0001.MP4");
        t.Add(@"Lightroom\Wedding.lrcat");
        t.Add(@"Lightroom\Wedding Smart Previews.lrdata\0\0A1F\0A1F3B2C.dng");
        t.Add(@"Avid MediaFiles\MXF\1\clip.mxf");
        t.Add(@"Photo Booth Library\Pictures\Photo on 20-09-2025 at 12.00.jpg");
        t.Add(@"Luminar Neo Catalog\Luminar Neo Catalog.luminarneo");
        t.Add(@"Luminar Neo Catalog\Cache Documents\preview.jpg");
        t.Add(@"Luminar Neo Catalog\Cache Documents\clip.mov");
        t.Add(@"real\clip.MOV");
        MovePlan plan = t.Plan();
        Assert.Equal(new[] { @"real\clip.MOV" }, plan.ToMove.Select(f => f.RelativePath));
        Assert.Contains(plan.Scan.SkippedFolders, s => s.RelativePath == "Photos Library.photoslibrary" && s.LibraryKind == "Apple Photos library");
        Assert.Contains(plan.Scan.SkippedFolders, s => s.RelativePath == @"Lightroom\Wedding Smart Previews.lrdata" && s.Reason == "application library - not changed");
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Info && m.Text.Contains("does not scan or change") && m.Text.Contains("Final Cut Pro library"));
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("Wedding.lrcat") && m.Text.Contains("Relink"));
        Assert.Contains(plan.Scan.SkippedFolders, s => s.RelativePath == "Photo Booth Library" && s.LibraryKind == "Photo Booth library");
        // A Luminar catalog is known by the catalog file in its folder; nothing in or below that folder is in the scan.
        Assert.Contains(plan.Scan.SkippedFolders, s => s.RelativePath == "Luminar Neo Catalog" && s.LibraryKind == "Luminar catalog");
        Assert.DoesNotContain(plan.Scan.Files, f => f.RelativePath.StartsWith("Luminar", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Scan.Folders.Keys, k => k.StartsWith("Luminar", StringComparison.Ordinal));
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("Luminar Neo Catalog") && m.Text.Contains("Relink"));
    }

    [Fact]
    public void A_luminar_catalog_saved_among_photos_is_only_warned_about()
    {
        using var t = new TestTree();
        t.Add(@"Photos\Main.luminarneo");
        t.Add(@"Photos\2025-09 Wedding\C0001.MP4");
        t.Add(@"Photos\2025-09 Wedding\DSC00001.ARW");
        MovePlan plan = t.Plan();
        Assert.Equal(new[] { @"Photos\2025-09 Wedding\C0001.MP4" }, plan.ToMove.Select(f => f.RelativePath));
        Assert.DoesNotContain(plan.Scan.SkippedFolders, s => s.LibraryKind is not null);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("Main.luminarneo") && m.Text.Contains("Relink"));
    }

    [Fact]
    public void Projects_stay_whole_with_a_warning()
    {
        using var t = new TestTree();
        t.Add(@"Edits\Promo\Promo.prproj");
        t.Add(@"Edits\Promo\Footage\A001_C003.MP4");
        t.Add(@"Edits\Promo\Graphics\logo.png");
        t.Add(@"card\DCIM\100MEDIA\DJI_0001.MP4");
        MovePlan videos = t.Plan();
        Assert.Equal(new[] { @"card\DCIM\100MEDIA\DJI_0001.MP4" }, videos.ToMove.Select(f => f.RelativePath));
        Assert.Contains(videos.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains(@"Premiere Pro project (Promo.prproj) in Edits\Promo"));
        MovePlan photos = t.Plan(MoveMode.Photos);
        Assert.Empty(photos.ToMove);
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var t = new TestTree();
        t.Add(@"real\clip.MOV");
        string outside = Path.Join(t.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "other.MOV"), "x");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Join(t.Source, "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        ScanResult scan = Scanner.Scan(t.Source);
        Assert.DoesNotContain(scan.Files, f => f.Name == "other.MOV");
        Assert.Contains(scan.SkippedFolders, s => s.RelativePath == "link");
    }

    [Fact]
    public void Resilio_share_is_detected_and_warned_about()
    {
        using var t = new TestTree();
        t.Add(@".sync\ID");
        t.Add(@"a\clip.MOV");
        MovePlan plan = t.Plan();
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("Resilio Sync"));
    }

    /// <summary>A target inside the source is allowed (see PlannerTests.A_breakout_folder_inside_the_source_...).</summary>
    [Theory]
    [InlineData("same")]
    [InlineData("parent")]
    public void Identical_folders_or_a_source_inside_the_target_are_refused(string kind)
    {
        using var t = new TestTree();
        t.Add(@"a\clip.MOV");
        string target = kind == "same" ? t.Source : t.Root;
        MovePlan plan = t.Plan(target: target);
        Assert.False(plan.CanRun);
        Assert.Contains(plan.Messages, m => m.Level == MessageLevel.Error);
    }

    /// <summary>
    /// Changed deliberately: a file whose name is taken in the target by a DIFFERENT file is no longer planned (it used to
    /// be planned and skipped at run time); it stays in the source with a clear note.
    /// </summary>
    [Fact]
    public void Preview_changes_nothing_and_reports_conflicts()
    {
        using var t = new TestTree();
        t.Add(@"a\clip.MOV");
        t.Add(@"a\other.MOV");
        Directory.CreateDirectory(Path.Join(t.Target, "a"));
        File.WriteAllText(Path.Join(t.Target, @"a\clip.MOV"), "already here");
        MovePlan plan = t.Plan();
        Assert.Equal(new[] { @"a\other.MOV" }, plan.ToMove.Select(f => f.RelativePath));
        SourceFile clash = Assert.Single(plan.DifferentConflicts);
        Assert.Equal(FileNote.DifferentInTarget, clash.Note);
        Assert.Contains(clash, plan.Staying);
        Assert.Empty(plan.Conflicts); // nothing planned is skipped: the clash is not planned at all
        Assert.False(Directory.Exists(Path.Join(t.Target, JobPaths_LogFolderName)));
        Assert.True(File.Exists(Path.Join(t.Source, @"a\clip.MOV")));
    }

    private const string JobPaths_LogFolderName = Core.Jobs.JobPaths.LogFolderName;
}
