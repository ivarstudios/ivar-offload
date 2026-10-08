using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace IvarOffload.Core.Sorting;

/// <summary>What a file belongs to. Neutral files never move.</summary>
public enum MediaSide { Neutral, Photo, Video }

/// <summary>What the user wants to move out of the source folder.</summary>
public enum MoveMode { Videos, Photos }

/// <summary>
/// The classification tables. Everything here is deliberately explicit so the preview can explain,
/// for every file, why it moves or stays.
/// </summary>
public static partial class MediaRules
{
    public static readonly FrozenSet<string> VideoExtensions = Set(
        ".mov", ".mp4", ".m4v", ".avi", ".mts", ".m2ts", ".m2t", ".mxf", ".mkv", ".wmv", ".mpg", ".mpeg", ".mpe",
        ".m2v", ".3gp", ".3g2", ".webm", ".flv", ".vob", ".mod", ".tod", ".dv", ".braw", ".r3d", ".crm", ".ari",
        ".insv", ".360", ".hevc", ".h264", ".h265", ".qt", ".f4v", ".asf", ".ogv",
        ".nev",   // Nikon N-RAW (Z6III, Z8, Z9), always with an MP4 proxy of the same name
        ".osv",   // DJI Osmo 360
        ".ts", ".trp", // MPEG transport stream (FPV goggles, HDV, dashcams)
        ".mlv",   // Magic Lantern raw video
        ".mcraw", // MotionCam raw video
        ".zraw",  // Z CAM raw video
        ".cine",  // Phantom high-speed cameras
        ".arx",   // ARRIRAW HDE
        ".lrv",   // GoPro / Skydio low-resolution proxy video
        ".lrf");  // DJI low-resolution proxy video

    public static readonly FrozenSet<string> PhotoExtensions = Set(
        ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif", ".hif", ".avif", ".webp", ".jxl", ".png", ".gif", ".bmp",
        ".tif", ".tiff", ".psd", ".psb",
        ".mpo", ".jps", // multi-picture / stereo JPEG
        ".dng", ".nef", ".nrw", ".raf", ".arw", ".arq", ".srf", ".sr2", ".cr2", ".cr3", ".crw", ".orf", ".ori", ".rw2", ".raw",
        ".rwl", ".pef", ".ptx", ".srw", ".x3f", ".3fr", ".fff", ".iiq", ".cap", ".eip", ".erf", ".mef", ".mos", ".mrw",
        ".kdc", ".dcr", ".k25", ".drf", ".bay", ".mdc", ".rwz",
        ".gpr", ".insp");

    /// <summary>Camera raw photos. A same-named .xmp next to a raw photo and an MOV/MP4 is the raw's edit settings.</summary>
    public static readonly FrozenSet<string> RawPhotoExtensions = Set(
        ".dng", ".nef", ".nrw", ".raf", ".arw", ".arq", ".srf", ".sr2", ".cr2", ".cr3", ".crw", ".orf", ".ori", ".rw2", ".raw",
        ".rwl", ".pef", ".ptx", ".srw", ".x3f", ".3fr", ".fff", ".iiq", ".cap", ".eip", ".erf", ".mef", ".mos", ".mrw",
        ".kdc", ".dcr", ".k25", ".drf", ".bay", ".mdc", ".rwz", ".gpr");

    /// <summary>Video types that carry their XMP metadata inside the file, so a separate .xmp is never theirs.</summary>
    public static readonly FrozenSet<string> EmbeddedXmpVideoExtensions = Set(".mov", ".mp4", ".m4v");

    /// <summary>
    /// Sound recordings. Name-matched ones follow their photo or video (camera voice memos); the rest are recorder or
    /// dual-system sound and go with the videos.
    /// </summary>
    public static readonly FrozenSet<string> AudioExtensions = Set(
        ".wav", ".bwf", ".rf64", ".w64", ".mp3", ".m4a", ".aac", ".aif", ".aiff", ".flac");

    private const string UnmatchedAudioReason = "audio recording with no photo or video of the same name (recorder / dual-system sound)";

    /// <summary>
    /// Companion files. They follow the photo or video they belong to (matched by name). When no match is found,
    /// <see cref="SidecarRule.Unmatched"/> decides: some types only ever exist for video clips.
    /// When a companion matches both a photo and a video, <see cref="SidecarRule.Prefer"/> decides.
    /// </summary>
    public static readonly FrozenDictionary<string, SidecarRule> Sidecars = BuildSidecars();

    /// <summary>File types that are neither photo nor video but are expected on cards and in ingests. They stay.</summary>
    public static readonly FrozenSet<string> KnownOtherExtensions = Set(
        ".mhl", ".txt", ".json", ".csv", ".html", ".htm", ".url", ".lnk", ".db", ".dat", ".bin", ".dsc", ".ctg", ".ind",
        ".gis", ".pbuf", ".sav", ".device", ".lpuuid", ".properties", ".ini", ".log", ".md", ".pdf", ".xlsx", ".xls",
        ".docx", ".doc", ".zip", ".7z", ".rar", ".lrcat", ".lrdata", ".cpi", ".mpl", ".bdm", ".cif", ".smi", ".bup",
        ".ifo", ".sig", ".md5", ".sha1", ".sha256", ".xxh", ".plist", ".cfg",
        ".cosessiondb", ".cocatalogdb", ".lrcat-wal", ".lrcat-shm", ".lock", ".luminar", ".luminarneo",
        ".fcpxml",  // Final Cut Pro XML: an interchange export (Atomos recorders write one next to their clips), not a project
        ".las", ".laz", ".obj", ".mtl", ".kmz", ".kml", ".shp", ".shx", ".dbf", ".geojson"); // point clouds, meshes, map layers

    /// <summary>Photo catalogs and sessions that store absolute paths: moved files show as missing there.</summary>
    public static readonly FrozenSet<string> CatalogExtensions = Set(".lrcat", ".cosessiondb", ".cocatalogdb", ".luminar", ".luminarneo");

    /// <summary>
    /// Field-recorder take and project files (Zoom H4n/H5/H6 .hprj, Zoom .ZDT). They hold no sound, only the list of a
    /// take's tracks, so they follow the recordings in their folder.
    /// </summary>
    public static readonly FrozenSet<string> RecorderProjectExtensions = Set(".hprj", ".zdt");

    /// <summary>
    /// Luminar creates its catalog (Luminar 4/AI ".luminar", Luminar Neo ".luminarneo") in a folder of its own, named like
    /// the catalog, next to its previews and edit history ("Luminar Neo Catalog\Luminar Neo Catalog.luminarneo"). Such a
    /// folder is an application library: <paramref name="folderName"/> directly holds a catalog named after it, or its
    /// name says Luminar. A catalog saved among photos only makes the preview warn, as a Lightroom catalog does.
    /// </summary>
    public static bool IsLuminarCatalogFolder(string folderName, IEnumerable<string> fileNames) =>
        fileNames.Any(n => Path.GetExtension(n).StartsWith(".luminar", StringComparison.OrdinalIgnoreCase)
            && (Path.GetFileNameWithoutExtension(n).Equals(folderName, StringComparison.OrdinalIgnoreCase)
                || folderName.Contains("Luminar", StringComparison.OrdinalIgnoreCase)));

    public const string LuminarCatalogKind = "Luminar catalog";

    /// <summary>Card folder structures that belong to a video format as a whole (clips, proxies, thumbnails, indexes).</summary>
    public static readonly FrozenDictionary<string, string> VideoStructureFolders = new Dictionary<string, string>
    {
        ["M4ROOT"] = "Sony XAVC card structure",
        ["AVCHD"] = "AVCHD card structure",
        ["BDMV"] = "AVCHD card structure",
        ["XDROOT"] = "Sony XDCAM card structure",
        ["BPAV"] = "Sony XDCAM EX card structure",
        ["PANA_GRP"] = "Panasonic MOV card structure",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders that are never entered: sync-tool state, NAS thumbnails, OS folders, and this tool's own logs.</summary>
    public static readonly FrozenDictionary<string, string> ExcludedFolders = new Dictionary<string, string>
    {
        [".sync"] = "Resilio Sync data",
        [".stfolder"] = "Syncthing marker",
        [".stversions"] = "Syncthing versions",
        [".dropbox.cache"] = "Dropbox cache",
        ["$RECYCLE.BIN"] = "Recycle Bin",
        ["System Volume Information"] = "Windows system folder",
        ["@eaDir"] = "Synology thumbnails",
        [".@__thumb"] = "QNAP thumbnails",
        ["@Recycle"] = "NAS recycle bin",
        ["#recycle"] = "NAS recycle bin",
        [".Trashes"] = "macOS trash",
        [".Spotlight-V100"] = "macOS index",
        [".fseventsd"] = "macOS events",
        [".TemporaryItems"] = "macOS temporary files",
        ["ascmhl"] = "ASC MHL checksum manifests",
        [Jobs.JobPaths.LogFolderName] = "IVAR Offload logs",
        [Jobs.JobPaths.IvarIngestLogFolderName] = "IVAR Offload logs (from IVAR Ingest)",
        [Jobs.JobPaths.IngestSorterLogFolderName] = "IVAR Offload logs (from Ingest Sorter)",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders that belong to an application (a library or catalog package). They are never entered: moving media out
    /// of them breaks the application's own store.
    /// </summary>
    public static readonly FrozenDictionary<string, string> ApplicationLibrarySuffixes = new Dictionary<string, string>
    {
        [".photoslibrary"] = "Apple Photos library",
        [".migratedphotolibrary"] = "Apple Photos library",
        [".photolibrary"] = "iPhoto library",
        [".aplibrary"] = "Aperture library",
        [".fcpbundle"] = "Final Cut Pro library",
        [".fcpcache"] = "Final Cut Pro cache",
        [".imovielibrary"] = "iMovie library",
        [".lrdata"] = "Lightroom previews",
        [".lrcat-data"] = "Lightroom catalog data",
        [".lrlibrary"] = "Lightroom library",
        [".cocatalog"] = "Capture One catalog",
        [".dra"] = "DaVinci Resolve project archive",
        [".logicx"] = "Logic Pro project",
        [".band"] = "GarageBand project",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Application folders recognized by their exact name.</summary>
    public static readonly FrozenDictionary<string, string> ApplicationLibraryNames = new Dictionary<string, string>
    {
        ["Avid MediaFiles"] = "Avid media folder",
        ["Photo Booth Library"] = "Photo Booth library",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Project files of editing and processing applications. The folder that holds one (and everything below it) is a
    /// project, and its media stays so the project keeps working. Interchange exports (.fcpxml, .edl, .xml) are not
    /// projects: recorders such as Atomos write them next to their clips. Processing folders without a project file
    /// (OpenDroneMap, DJI Terra) are recognized by their layout (<see cref="ProcessingProjects"/>).
    /// </summary>
    public static readonly FrozenDictionary<string, string> ProjectMarkers = new Dictionary<string, string>
    {
        [".prproj"] = "Premiere Pro project",
        [".prel"] = "Premiere Elements project",
        [".drp"] = "DaVinci Resolve project",
        [".aep"] = "After Effects project",
        [".aepx"] = "After Effects project",
        [".veg"] = "VEGAS project",
        [".kdenlive"] = "Kdenlive project",
        [".avp"] = "Avid Media Composer project",
        [".tscproj"] = "Camtasia project",
        [".psx"] = "Metashape project",
        [".psz"] = "Metashape project",
        [".p4d"] = "Pix4D project",
        [".p4m"] = "Pix4Dmatic project",
        [".rcproj"] = "RealityCapture project",
        [".rpp"] = "REAPER project",
        [".sesx"] = "Audition session",
        [".als"] = "Ableton Live set",
        [".cpr"] = "Cubase project",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Metadata folders that sit next to media and hold per-file sidecars named "&lt;media file name&gt;.&lt;ext&gt;".</summary>
    public static readonly FrozenSet<string> SidecarStoreFolders = Set(".LP_Store", "NKSC_PARAM");

    public static readonly FrozenSet<string> SystemFileNames = Set("thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db");

    /// <summary>Photo types that Apple pairs with a .MOV/.MP4 "Live Photo" clip of the same name.</summary>
    public static readonly FrozenSet<string> LivePhotoExtensions = Set(".heic", ".heif", ".jpg", ".jpeg");

    /// <summary>JPEG Live Photos are only paired when named like an iPhone photo (other cameras reuse numbers across bodies).</summary>
    public const string LivePhotoJpegPrefix = "IMG_";

    public static readonly FrozenSet<string> LivePhotoClipExtensions = Set(".mov", ".mp4");

    /// <summary>
    /// A Live Photo clip lasts about 3 seconds (2-8 MB). A same-named clip larger than this is a real video; a smaller one
    /// also needs Apple's Live Photo tag in the clip (<see cref="LivePhotoClip"/>).
    /// </summary>
    public const long LivePhotoMaxClipBytes = 15L * 1024 * 1024;

    /// <summary>Frames of a CinemaDNG or other image-sequence clip.</summary>
    public static readonly FrozenSet<string> SequenceFrameExtensions = Set(".dng", ".dpx", ".exr");

    /// <summary>A folder needs at least this many numbered frames to count as an image-sequence clip.</summary>
    public const int SequenceMinimumFrames = 10;

    /// <summary>
    /// DNG frames that carry CinemaDNG movie tags are a clip from this many frames on, even with missing frames or a name
    /// that does not match the folder (a renamed clip folder, a drone camera without sound). Fewer frames must be missing
    /// than are there: two frame grabs exported from one clip (…_000123.dng, …_000456.dng) are stills, not a clip.
    /// </summary>
    public const int TaggedSequenceMinimumFrames = 2;

    /// <summary>DJI LiDAR raw data (L1, L2): with .LDR/.LDRT, a folder is a LiDAR mission.</summary>
    public static readonly FrozenSet<string> LidarExtensions = Set(".ldr", ".ldrt");

    public static readonly FrozenSet<string> LidarCompanionExtensions = Set(
        ".imu", ".rtk", ".rtb", ".rtl", ".rts", ".clc", ".cli", ".cmi", ".rpos", ".rpt");

    /// <summary>Unrecognized files of at least this size get their own warning: they are probably media.</summary>
    public const long LargeUnknownBytes = 100L * 1024 * 1024;

    public static MediaSide PrimarySide(string extension) =>
        VideoExtensions.Contains(extension) ? MediaSide.Video
        : PhotoExtensions.Contains(extension) ? MediaSide.Photo
        : MediaSide.Neutral;

    public static MediaSide SideFor(MoveMode mode) => mode == MoveMode.Videos ? MediaSide.Video : MediaSide.Photo;

    /// <summary>
    /// The one rule for "412 videos" / "1,203 photos". The preview, the result, the job's summary, the list of what is
    /// left and the undo question all count with it, so the same files always give the same number. It needs only the
    /// file's path, which the job's log keeps: a file counts by its extension (companion files never do, nor macOS "._"
    /// resource files), and the frames of an image-sequence clip (CinemaDNG, DPX, EXR, ARRIRAW) or the parts of a RED
    /// clip, which lie together in the clip's folder, count as one video.
    /// </summary>
    /// <returns>The key the file counts under (files with the same key are one video or photo), or null.</returns>
    public static string? CountKey(string rel, MoveMode mode)
    {
        string name = Path.GetFileName(rel);
        if (name.StartsWith("._", StringComparison.Ordinal)) return null;
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string folder = Path.GetDirectoryName(rel) ?? "";
        if (mode == MoveMode.Videos && (SequenceFrameExtensions.Contains(ext) || ext is ".ari" or ".r3d"))
            return folder + "\\*"; // "*" never appears in a file name, so this cannot be another file's key
        return PrimarySide(ext) == SideFor(mode) ? rel : null;
    }

    /// <summary>How many videos (or photos) these files are, by <see cref="CountKey"/>.</summary>
    public static int CountPrimaries(IEnumerable<string> rels, MoveMode mode) =>
        rels.Select(rel => CountKey(rel, mode)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// A file of a kind that moves in this mode when nothing holds it back: a video (or photo), or a companion that only
    /// exists for them (.SRT, .THM, recorder sound for videos). Used for files the plan could not look at (online-only).
    /// </summary>
    public static bool IsMovingType(string rel, MoveMode mode) =>
        CountKey(rel, mode) is not null
        || !Path.GetFileName(rel).StartsWith("._", StringComparison.Ordinal)
           && Sidecars.TryGetValue(Path.GetExtension(rel), out SidecarRule? rule) && rule.Unmatched == SideFor(mode);

    public static bool IsAudio(string extension) => AudioExtensions.Contains(extension);

    /// <summary>GDAL's "X.tif.aux.xml": map metadata for X.tif.</summary>
    public static readonly SidecarRule GisAuxXml = new("GIS metadata", MediaSide.Neutral);

    /// <summary>
    /// GNSS survey data: RINEX observation and navigation files (.obs .nav .rnx .24O .24N ...), receiver logs (u-blox .ubx,
    /// Septentrio .sbf, Trimble .T02/.T04) and ground control point lists ("gcp_list.csv", "GCPs.txt").
    /// </summary>
    public static bool IsSurveyData(string name)
    {
        string ext = Path.GetExtension(name);
        if (SurveyDataExtensions.Contains(ext) || RinexShortName().IsMatch(ext)) return true;
        return (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            && GroundControlName().IsMatch(Path.GetFileNameWithoutExtension(name));
    }

    private static readonly FrozenSet<string> SurveyDataExtensions = Set(".obs", ".nav", ".rnx", ".crx", ".ubx", ".sbf", ".t01", ".t02", ".t04");

    /// <summary>RINEX 2 short names: ".yyT" with a two-digit year and a type letter (O observations, N/G/L/P/Q/H/C navigation, M meteo).</summary>
    [GeneratedRegex(@"^\.\d\d[onglpqhcm]$", RegexOptions.IgnoreCase)]
    private static partial Regex RinexShortName();

    [GeneratedRegex(@"(^|[^a-z])(gcps?|ground[ _-]?control|control[ _-]?points?)([^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex GroundControlName();

    /// <summary>The kind of application library a folder is ("Apple Photos library"), or null.</summary>
    public static string? ApplicationLibraryKind(string folderName)
    {
        if (ApplicationLibraryNames.TryGetValue(folderName, out string? named)) return named;
        string ext = Path.GetExtension(folderName);
        return ext.Length > 0 && ApplicationLibrarySuffixes.TryGetValue(ext, out string? kind) ? kind : null;
    }

    private static FrozenDictionary<string, SidecarRule> BuildSidecars()
    {
        var rules = new Dictionary<string, SidecarRule>
        {
            [".xmp"] = new("XMP metadata", MediaSide.Neutral),
            [".thm"] = new("thumbnail", MediaSide.Video, "video thumbnail (.THM files only exist for video clips)", MediaSide.Video),
            [".scr"] = new("DJI screennail", MediaSide.Video, "DJI video preview image", MediaSide.Video),
            [".srt"] = new("subtitle/telemetry", MediaSide.Video, "video subtitle / drone flight telemetry", MediaSide.Video),
            [".sidecar"] = new("Blackmagic RAW settings", MediaSide.Video, "Blackmagic RAW settings (.sidecar files only exist for .braw clips)", MediaSide.Video),
            [".rmd"] = new("RED look metadata", MediaSide.Video, "RED look metadata (.RMD files only exist for RED clips)", MediaSide.Video),
            [".xml"] = new("XML metadata", MediaSide.Neutral),
            [".aae"] = new("Apple edit data", MediaSide.Neutral, Prefer: MediaSide.Photo),
            [".lpmd"] = new("Kyno metadata", MediaSide.Neutral),
            [".pp3"] = new("RawTherapee settings", MediaSide.Neutral),
            [".dop"] = new("DxO settings", MediaSide.Neutral),
            [".cos"] = new("Capture One settings", MediaSide.Neutral),
            [".comask"] = new("Capture One mask", MediaSide.Neutral),
            [".cop"] = new("Capture One preview", MediaSide.Neutral),
            [".cot"] = new("Capture One thumbnail", MediaSide.Neutral),
            [".cof"] = new("Capture One focus data", MediaSide.Neutral),
            [".on1"] = new("ON1 settings", MediaSide.Neutral),
            [".acr"] = new("Camera Raw settings", MediaSide.Neutral),
            [".nksc"] = new("NX Studio edits", MediaSide.Neutral),
            [".dr4"] = new("Canon DPP recipe", MediaSide.Neutral),
            [".vrd"] = new("Canon DPP recipe", MediaSide.Neutral),
            [".drx"] = new("DaVinci Resolve grade", MediaSide.Neutral),
            [".lrtpreview"] = new("LRTimelapse preview", MediaSide.Neutral),
        };
        foreach (string ext in new[] { ".tfw", ".tifw", ".tfwx", ".jgw", ".jpgw", ".pgw", ".wld" })
            rules[ext] = new("GIS world file", MediaSide.Neutral);
        // A map raster's projection, overviews and statistics: X.prj / X.ovr, and GDAL's X.tif.ovr / X.tif.aux.xml.
        rules[".prj"] = new("GIS projection file", MediaSide.Neutral);
        rules[".ovr"] = new("GIS overview file", MediaSide.Neutral);
        rules[".aux"] = new("GIS metadata", MediaSide.Neutral);
        foreach (string ext in AudioExtensions)
            rules[ext] = new("audio", MediaSide.Video, UnmatchedAudioReason);
        return rules.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Kind">Short description shown in the preview.</param>
/// <param name="Unmatched">Side used when no photo/video with a matching name is found.</param>
/// <param name="UnmatchedReason">Explanation used when the unmatched side is not Neutral.</param>
/// <param name="Prefer">
/// Side to follow when the name matches both a photo and a video. Neutral: the file is ambiguous and stays.
/// </param>
public sealed record SidecarRule(string Kind, MediaSide Unmatched, string? UnmatchedReason = null, MediaSide Prefer = MediaSide.Neutral);
