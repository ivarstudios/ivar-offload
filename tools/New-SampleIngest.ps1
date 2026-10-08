<#
.SYNOPSIS
  Creates a synthetic ingest folder that mirrors typical camera card layouts (DJI, Fuji, Nikon, Sony, GoPro, Skydio, Olympus,
  Panasonic P2, Canon XF, XDCAM EX, RED, Blackmagic, Atomos, field recorders, phones, mapping missions, hyperlapses,
  photogrammetry processing folders), plus edge cases, and writes a manifest with the expected outcome and fingerprint
  of every file.

.DESCRIPTION
  The manifest (<Path>.manifest.csv) is the independent test oracle used by Test-SampleIngest.ps1:
  Expect = video | photo | stay says which side each file belongs to, written by hand from the rules,
  not computed by the tool. File contents are random; timestamps (including last-access) are set to
  distinctive old values so that any change made by the tool is detected.
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [int] $LargeFileMB = 256,
    [int] $Seed = 20260926,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath $root) {
    if (-not $Force) { throw "$root already exists (use -Force to replace it)" }
    cmd /c "attrib -r -h -s `"$root\*`" /s /d" | Out-Null
    # With the \\?\ prefix: Windows PowerShell's Remove-Item fails on the sample's paths of more than 260 characters.
    [IO.Directory]::Delete("\\?\$root", $true)
}

$rng = New-Object Random $Seed
$block = New-Object byte[] (4MB)
$rng.NextBytes($block)
$files = New-Object System.Collections.Generic.List[object]
$KB = 1KB; $MB = 1MB

# Non-ASCII built from code points so this script works regardless of its own file encoding.
$o = [char]0xF6; $a = [char]0xE4; $aa = [char]0xE5; $e = [char]0xE9; $u = [char]0xFC; $i = [char]0xEF
$camera = [char]::ConvertFromUtf32(0x1F3A5); $clapper = [char]::ConvertFromUtf32(0x1F3AC)

function Ext([string] $rel) { '\\?\' + [IO.Path]::Combine($root, $rel) }

function Add-File([string] $Rel, [string] $Expect, [long] $Size, [string] $Attributes = '') {
    $full = Ext $Rel
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    $stream = New-Object IO.FileStream($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $id = [BitConverter]::GetBytes([long]($files.Count + 1))
        $remaining = $Size; $chunk = 0
        while ($remaining -gt 0) {
            [Array]::Copy($id, 0, $block, 0, 8)
            [Array]::Copy([BitConverter]::GetBytes([long]$chunk), 0, $block, 8, 8)
            $n = [int][Math]::Min($remaining, $block.Length)
            $stream.Write($block, 0, $n)
            $remaining -= $n; $chunk++
        }
    } finally { $stream.Dispose() }
    $files.Add([pscustomobject]@{ Rel = $Rel; Expect = $Expect; Size = $Size; Attributes = $Attributes })
}

# A DNG whose first bytes are a real TIFF header with an IFD0 holding the given tags (0xC764 = CinemaDNG FrameRate).
function Add-Dng([string] $Rel, [string] $Expect, [long] $Size, [int[]] $Tags) {
    Add-File $Rel $Expect $Size
    $ifd = New-Object Collections.Generic.List[byte]
    $ifd.AddRange([byte[]](0x49, 0x49, 42, 0, 64, 0, 0, 0))   # "II", 42, IFD0 at offset 64
    $ifd.AddRange([BitConverter]::GetBytes([uint16]$Tags.Count))
    foreach ($t in $Tags) {
        $ifd.AddRange([BitConverter]::GetBytes([uint16]$t)); $ifd.AddRange([BitConverter]::GetBytes([uint16]3))
        $ifd.AddRange([BitConverter]::GetBytes([uint32]1)); $ifd.AddRange([BitConverter]::GetBytes([uint32]0))
    }
    $bytes = $ifd.ToArray()
    $stream = New-Object IO.FileStream((Ext $Rel), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, 8)
        $stream.Position = 64; $stream.Write($bytes, 8, $bytes.Length - 8)
        $stream.Position = 256; $stream.Write([BitConverter]::GetBytes([long]$files.Count), 0, 8)   # keep every file unique
    } finally { $stream.Dispose() }
}

# A QuickTime clip: ftyp and the media data first, then moov with meta\keys holding the given metadata keys (like an
# iPhone clip). An Apple Live Photo clip carries "com.apple.quicktime.content.identifier"; an ordinary video does not.
function Add-QuickTime([string] $Rel, [string] $Expect, [long] $Size, [string[]] $Keys) {
    Add-File $Rel $Expect $Size
    function BE32([long] $v) { $b = [BitConverter]::GetBytes([uint32]$v); [Array]::Reverse($b); , $b }
    function Box([string] $type, [byte[]] $body) {
        $box = New-Object Collections.Generic.List[byte]
        $box.AddRange((BE32 (8 + $body.Length))); $box.AddRange([Text.Encoding]::ASCII.GetBytes($type)); $box.AddRange($body)
        , $box.ToArray()
    }
    $entries = New-Object Collections.Generic.List[byte]
    $entries.AddRange([byte[]](0, 0, 0, 0)); $entries.AddRange((BE32 $Keys.Count))
    foreach ($k in $Keys) {
        $name = [Text.Encoding]::ASCII.GetBytes($k)
        $entries.AddRange((BE32 (8 + $name.Length))); $entries.AddRange([Text.Encoding]::ASCII.GetBytes('mdta')); $entries.AddRange($name)
    }
    $meta = New-Object Collections.Generic.List[byte]
    $meta.AddRange([byte[]](0, 0, 0, 0)); $meta.AddRange((Box 'keys' $entries.ToArray()))
    $moov = Box 'moov' ((Box 'mvhd' (New-Object byte[] 100)) + (Box 'meta' $meta.ToArray()))
    $ftyp = Box 'ftyp' ([Text.Encoding]::ASCII.GetBytes('qt  ') + [byte[]](0, 0, 0, 0) + [Text.Encoding]::ASCII.GetBytes('qt  '))
    $stream = New-Object IO.FileStream((Ext $Rel), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($ftyp, 0, $ftyp.Length)
        [byte[]]$mdat = (BE32 ($Size - $ftyp.Length - $moov.Length)) + [Text.Encoding]::ASCII.GetBytes('mdat')
        $stream.Write($mdat, 0, 8)
        $stream.Position = $Size - $moov.Length; $stream.Write($moov, 0, $moov.Length)
    } finally { $stream.Dispose() }
}
$livePhotoKeys = @('com.apple.quicktime.content.identifier', 'com.apple.quicktime.make', 'com.apple.quicktime.model')

# --- Sync tool and offload tool artifacts (the tool must never enter .sync or @eaDir) --------------------------
Add-File '.sync\ID' 'stay' 40
# Application libraries are never entered: a Luminar catalog (known by its catalog file) and a Photo Booth library.
Add-File 'Luminar Neo Catalog\Luminar Neo Catalog.luminarneo' 'stay' (200 * $KB)
Add-File 'Luminar Neo Catalog\Cache Documents\0001.jpg' 'stay' (40 * $KB)
Add-File 'Photo Booth Library\Pictures\Photo on 20-09-2025 at 12.00.jpg' 'stay' (300 * $KB)
Add-File '.sync\IgnoreList' 'stay' 826
Add-File '.sync\Archive\DJI_0001.MOV' 'stay' (2 * $MB)
Add-File '@eaDir\DJI_0003.MOV\SYNOPHOTO_THUMB_XL.jpg' 'stay' (40 * $KB)
Add-File 'HedgeEnabled\HedgeEnabled.json' 'stay' 62
Add-File 'Transfer Logs\Hedge Log - 2024-01-17 at 12.00.00 - 240117-X100V - SAMPLE.txt' 'stay' (85 * $KB)

# --- Loose files in the root ------------------------------------------------------------------------------------
Add-File 'DJI_0002.MOV' 'video' (24 * $MB)
Add-File 'DJI_0007.DNG' 'photo' (2 * $MB)
Add-File 'Poster.psd' 'photo' (1 * $MB)
Add-File 'New Text Document.txt' 'stay' 0
Add-File 'conference-2021.mp4' 'video' ($LargeFileMB * $MB)

# --- DJI Mavic 2: thumbnails/screennails live in MISC\THM, apart from the clips ----------------------------------
$d = 'Stills\240101-Mavic2-Coast-2'
Add-File "$d\Hedge Media Hash List - 2024-01-01 at 12.00.00 - 240101-Mavic2-Coast-2 - Stills.mhl" 'stay' (6 * $KB)
Add-File "$d\DCIM\100MEDIA\DJI_0001.DNG" 'photo' (2 * $MB)
Add-File "$d\DCIM\100MEDIA\DJI_0001.JPG" 'photo' (900 * $KB)
Add-File "$d\DCIM\100MEDIA\DJI_0002.DNG" 'photo' (2 * $MB)
Add-File "$d\DCIM\100MEDIA\DJI_0003.MOV" 'video' (20 * $MB)
Add-File "$d\DCIM\100MEDIA\DJI_0004.MOV" 'video' (12 * $MB)
Add-File "$d\DCIM\PANORAMA\100_0033\PANO0001.DNG" 'photo' (1 * $MB)
Add-File "$d\DCIM\PANORAMA\100_0033\PANO0002.DNG" 'photo' (1 * $MB)
Add-File "$d\MISC\GIS\dji.gis" 'stay' (300 * $KB)
Add-File "$d\MISC\THM\100\DJI_0003.SCR" 'video' (220 * $KB)
Add-File "$d\MISC\THM\100\DJI_0003.THM" 'video' (12 * $KB)
Add-File "$d\MISC\THM\100\DJI_0004.SCR" 'video' (220 * $KB)
Add-File "$d\MISC\THM\100\DJI_0004.THM" 'video' (12 * $KB)

# --- DJI Mavic 3: the MP4 was renamed on ingest, its LRF/SRT proxies were not -------------------------------------
$d = 'Stills\240102-Mavic3-River'
Add-File "$d\DCIM\101MEDIA\DJI_0240.JPG" 'photo' (800 * $KB)
Add-File "$d\DCIM\101MEDIA\DJI_0240.DNG" 'photo' (2 * $MB)
Add-File "$d\DCIM\101MEDIA\Photographer-XX-MAVIC3-240102-1200-0241.MP4" 'video' (18 * $MB)
Add-File "$d\DCIM\101MEDIA\DJI_0241.LRF" 'video' (3 * $MB)
Add-File "$d\DCIM\101MEDIA\DJI_0241.SRT" 'video' (40 * $KB)
Add-File "$d\MISC\L2D-20c.db" 'stay' (76 * $KB)
Add-File "$d\MISC\THM\101\DJI_0241.SCR" 'video' (220 * $KB)
Add-File "$d\MISC\THM\101\DJI_0241.THM" 'video' (12 * $KB)

$d = 'Stills\240103-Mavic3-Flyover'
Add-File "$d\DJI_0036.DNG" 'photo' (2 * $MB)
Add-File "$d\DJI_0036.JPG" 'photo' (800 * $KB)
Add-File "$d\DJI_0047.MOV" 'video' (16 * $MB)
Add-File "$d\DJI_0047.LRF" 'video' (2 * $MB)
Add-File "$d\DJI_0047.SRT" 'video' (30 * $KB)

# --- Fujifilm X100V with Kyno metadata in .LP_Store ----------------------------------------------------------------
$d = 'Stills\240104-X100V-Street'
Add-File "$d\Hedge Media Hash List - 2024-01-04 at 12.00.00 - 240104-X100V-Street - Stills.mhl" 'stay' (4 * $KB)
Add-File "$d\122_FUJI\DSCF3666.MOV" 'video' (14 * $MB)
Add-File "$d\122_FUJI\DSCF3666-H264-1080p-1.mp4" 'video' (6 * $MB)
Add-File "$d\122_FUJI\Photographer-XX-X100V-240104-1200-3319.JPG" 'photo' (700 * $KB)
Add-File "$d\122_FUJI\Photographer-XX-X100V-240104-1200-3319.RAF" 'photo' (3 * $MB)
Add-File "$d\122_FUJI\Photographer-XX-X100V-240104-1200-3320.JPG" 'photo' (700 * $KB)
Add-File "$d\122_FUJI\Photographer-XX-X100V-240104-1200-3320.RAF" 'photo' (3 * $MB)
Add-File "$d\122_FUJI\Photographer-XX-X100V-240104-1200-3320.xmp" 'photo' (8 * $KB)
Add-File "$d\122_FUJI\.LP_Store\DSCF3666.MOV.lpmd" 'video' 400
Add-File "$d\122_FUJI\.LP_Store\DSCF3666-H264-1080p-1.mp4.lpmd" 'video' 400

# --- Nikon Z7 (non-ASCII folder name), XMP sidecars, card files -----------------------------------------------------
$d = "Stills\240105-Vinterm$($o)rker"
Add-File "$d\NIKON001.DSC" 'stay' (1 * $MB)
Add-File "$d\DCIM\111NCZ_7\Photographer-XX-Z7-240105-1200-6632.JPG" 'photo' (900 * $KB)
Add-File "$d\DCIM\111NCZ_7\Photographer-XX-Z7-240105-1200-6634.NEF" 'photo' (4 * $MB)
Add-File "$d\DCIM\111NCZ_7\Photographer-XX-Z7-240105-1200-6634.xmp" 'photo' (6 * $KB)
Add-File "$d\DCIM\111NCZ_7\NC_FLLST.DAT" 'stay' (80 * $KB)
Add-File "$d\NIKON\Z_7\PCSENDL1.BIN" 'stay' (16 * $KB)

# --- Skydio 2: MP4 with LRV proxy, an orphan proxy, flight logs ---------------------------------------------------
$d = "Stills\240106-Skydio2-Sj$($o)n"
Add-File "$d\.skydio_media.pbuf" 'stay' (75 * $KB)
Add-File "$d\flight_history.csv" 'stay' (2 * $KB)
Add-File "$d\DCIM\100SKYDO\Photographer-XX-Skydio2-240106-1200-1006811.JPG" 'photo' (900 * $KB)
Add-File "$d\DCIM\100SKYDO\Photographer-XX-Skydio2-240106-1200-1006812.DNG" 'photo' (2 * $MB)
Add-File "$d\DCIM\100SKYDO\Photographer-XX-Skydio2-240106-1300-1006900.MP4" 'video' (15 * $MB)
Add-File "$d\DCIM\100SKYDO\Photographer-XX-Skydio2-240106-1300-1006900.LRV" 'video' (2 * $MB)
Add-File "$d\DCIM\100SKYDO\Photographer-XX-Skydio2-240106-1300-1006901.LRV" 'video' (2 * $MB)

# --- Fuji voice memo belongs to its photo; Olympus; old AVI camera ----------------------------------------------------
$d = 'Stills\240107-X100V-Mountains\DCIM\100_FUJI'
Add-File "$d\DSCF0001.JPG" 'photo' (700 * $KB)
Add-File "$d\DSCF0001.RAF" 'photo' (3 * $MB)
Add-File "$d\DSCF0001.WAV" 'photo' (400 * $KB)
Add-File "$d\DSCF0002.MOV" 'video' (9 * $MB)
$d = "Stills\240108-TG6-Fj$($aa)rd\DCIM\100OLYMP"
Add-File "$d\P6200001.JPG" 'photo' (600 * $KB)
Add-File "$d\P6200001.ORF" 'photo' (2 * $MB)
Add-File "$d\P6200003.MOV" 'video' (7 * $MB)
Add-File 'Stills\240109-OldCamera\DCIM\MOVI0105.avi' 'video' (5 * $MB)
Add-File 'Stills\240109-OldCamera\DCIM\PICT0000.jpg' 'photo' (300 * $KB)

# --- Sony: the whole PRIVATE\M4ROOT structure is video (including its JPG thumbnails) --------------------------------
$d = 'Stills\240110-A7RIII-Portrait'
Add-File "$d\DCIM\100MSDCF\DSC00001.ARW" 'photo' (5 * $MB)
Add-File "$d\DCIM\100MSDCF\DSC00001.JPG" 'photo' (900 * $KB)
Add-File "$d\PRIVATE\M4ROOT\CLIP\C0001.MP4" 'video' (11 * $MB)
Add-File "$d\PRIVATE\M4ROOT\CLIP\C0001M01.XML" 'video' (3 * $KB)
Add-File "$d\PRIVATE\M4ROOT\THMBNL\C0001T01.JPG" 'video' (20 * $KB)
Add-File "$d\PRIVATE\M4ROOT\MEDIAPRO.XML" 'video' (2 * $KB)
Add-File "$d\PRIVATE\M4ROOT\STATUS.BIN" 'video' 512
Add-File 'Stills\240111-FX3-Flat\C0002.MP4' 'video' (8 * $MB)
Add-File 'Stills\240111-FX3-Flat\C0002M01.XML' 'video' (3 * $KB)
# A Sony card copied without PRIVATE\M4ROOT, with the card's DCIM folder copied next to it: only the Sony folders are video.
$d = 'Stills\240112-A7IV-Unwrapped'
Add-File "$d\MEDIAPRO.XML" 'video' (2 * $KB)
Add-File "$d\CLIP\C0003.MP4" 'video' (6 * $MB)
Add-File "$d\CLIP\C0003M01.XML" 'video' (3 * $KB)
Add-File "$d\THMBNL\C0003T01.JPG" 'video' (20 * $KB)
Add-File "$d\DCIM\100MSDCF\DSC00003.ARW" 'photo' (5 * $MB)
Add-File "$d\DCIM\100MSDCF\DSC00003.JPG" 'photo' (900 * $KB)

# --- LRTimelapse ---------------------------------------------------------------------------------------------------
$d = 'Stills\240113 Night Timelapse'
Add-File "$d\DSC_0001.NEF" 'photo' (4 * $MB)
Add-File "$d\DSC_0001.xmp" 'photo' (5 * $KB)
Add-File "$d\DSC_0001.lrtpreview" 'photo' (60 * $KB)
Add-File "$d\LRTimelapse.properties" 'stay' 500

# --- A video folder with photos in it (GoPro Fusion) and one that only holds video ---------------------------------
$d = 'Video.Conflict1\240114 Climbing\Fusion\2'
Add-File "$d\Get_started_with_GoPro.url" 'stay' 120
Add-File "$d\DCIM\leinfo.sav" 'stay' 0
Add-File "$d\DCIM\139GBACK\GB019381.JPG" 'photo' (1 * $MB)
Add-File "$d\DCIM\139GBACK\GB019381.GPR" 'photo' (4 * $MB)
Add-File "$d\DCIM\139GBACK\GB019382.MP4" 'video' (13 * $MB)
Add-File "$d\DCIM\139GBACK\GB019382.LRV" 'video' (2 * $MB)
Add-File "$d\DCIM\139GBACK\GB019382.THM" 'video' (9 * $KB)
Add-File "$d\DCIM\139GBACK\GB019382.WAV" 'video' (1 * $MB)
Add-File "$d\MISC\card" 'stay' 64
Add-File "$d\MISC\sd_info.txt" 'stay' 300
Add-File 'Video.Conflict1\240115 Ski Trip -VIDEO\DJI_0004.MOV' 'video' (10 * $MB)
Add-File 'Video.Conflict1\240115 Ski Trip -VIDEO\2\DJI_0035.MOV' 'video' (4 * $MB)
$d = 'VIDEO\JOB-000123-Interview-Video'
Add-File "$d\JOB-000123_240118-00001.mov" 'video' (10 * $MB)
Add-File "$d\JOB-000123_240118-00002.mp4" 'video' (6 * $MB)
Add-File "$d\JOB-000123-Interview Video log.xlsx" 'stay' (480 * $KB)
Add-File "$d\.LP_Store\JOB-000123_240118-00001.mov.lpmd" 'video' 400
Add-File 'temp ingest\DCIM\121_FUJI\.LP_Store\Photographer-XX-X100V-20240116-1200-DSCF0183.MOV.lpmd' 'video' 400
Add-File 'temp ingest\UNTITLED\FFDB\FFXFER.DAT' 'stay' (4 * $KB)

# --- Nikon Z9 N-RAW: the .NEV master and its MP4 proxy are both video; NX Studio edits follow their NEF ---------------
$d = 'Stills\250901-Z9-Wedding'
Add-File "$d\DCIM\100NCZ_9\DSC_0003.NEV" 'video' (6 * $MB)
Add-File "$d\DCIM\100NCZ_9\DSC_0003.MP4" 'video' (2 * $MB)
Add-File "$d\DCIM\100NCZ_9\DSC_0004.NEF" 'photo' (3 * $MB)
Add-File "$d\DCIM\100NCZ_9\DSC_0004.JPG" 'photo' (800 * $KB)
Add-File "$d\DCIM\100NCZ_9\NKSC_PARAM\DSC_0004.NEF.nksc" 'photo' (2 * $KB)

# --- DJI Osmo 360 master with its LRF proxy; Blackmagic RAW with its settings sidecar ---------------------------------
Add-File 'Video\250801-Osmo360\DCIM\DJI_001\CAM_20250801120000_0001_D.OSV' 'video' (5 * $MB)
Add-File 'Video\250801-Osmo360\DCIM\DJI_001\CAM_20250801120000_0001_D.LRF' 'video' (1 * $MB)
Add-File 'Video\250920-BMPCC6K\A001_09201234_C001.braw' 'video' (6 * $MB)
Add-File 'Video\250920-BMPCC6K\A001_09201234_C001.sidecar' 'video' (2 * $KB)

# --- Field recorders: sound with no matching clip goes with the videos --------------------------------------------------
Add-File 'Audio\ZOOM_F6\260920\260920_001.WAV' 'video' (2 * $MB)
Add-File 'Audio\ZOOM_F6\260920\260920_001_Tr1.WAV' 'video' (2 * $MB)
Add-File 'Audio\MixPre\take1.BWF' 'video' (1 * $MB)
Add-File 'Audio\H5\ZOOM0001.flac' 'video' (1 * $MB)
# A recorder's take file (Zoom .hprj / .ZDT) holds no sound: it follows the recordings next to it.
Add-File 'Audio\H6\FOLDER01\ZOOM0001\ZOOM0001.hprj' 'video' (1 * $KB)
Add-File 'Audio\H6\FOLDER01\ZOOM0001\ZOOM0001_LR.WAV' 'video' (2 * $MB)
Add-File 'Audio\H6\FOLDER01\ZOOM0001\ZOOM0001_Tr1.WAV' 'video' (2 * $MB)
Add-File 'Audio\ZOOM_F6\260920\260920_001.ZDT' 'video' (1 * $KB)

# --- Professional card structures move as a whole ---------------------------------------------------------------------
$d = 'Video\250910-P2-CardA'
Add-File "$d\LASTCLIP.TXT" 'video' 64
Add-File "$d\CONTENTS\VIDEO\0001AB.MXF" 'video' (4 * $MB)
Add-File "$d\CONTENTS\AUDIO\0001AB00.MXF" 'video' (1 * $MB)
Add-File "$d\CONTENTS\AUDIO\0001AB01.MXF" 'video' (1 * $MB)
Add-File "$d\CONTENTS\CLIP\0001AB.XML" 'video' (6 * $KB)
Add-File "$d\CONTENTS\ICON\0001AB.BMP" 'video' (20 * $KB)
Add-File "$d\CONTENTS\PROXY\0001AB.MP4" 'video' (1 * $MB)
Add-File "$d\CONTENTS\PROXY\0001AB.BIN" 'video' (4 * $KB)
Add-File "$d\CONTENTS\VOICE\0001AB.WAV" 'video' (200 * $KB)
Add-File "$d\ascmhl\0001_250910-P2-CardA_2025-09-10_120000.mhl" 'stay' (3 * $KB)   # backup tool manifests: never entered
Add-File "$d\ascmhl\ascmhl_chain.xml" 'stay' 800
$d = 'Video\250911-C300-CardB\CONTENTS\CLIPS001'
Add-File "$d\INDEX.MIF" 'video' (2 * $KB)
Add-File "$d\AA0001\AA000101.MXF" 'video' (4 * $MB)
Add-File "$d\AA0001\AA0001.CIF" 'video' (1 * $KB)
Add-File "$d\AA0001\AA0001M01.XML" 'video' (3 * $KB)
Add-File "$d\AA0001\AA000101.PPN" 'video' (1 * $KB)
Add-File "$d\AA0001\AA0001.THM" 'video' (9 * $KB)
$d = 'Video\250912-EX3\BPAV'
Add-File "$d\MEDIAPRO.XML" 'video' (2 * $KB)
Add-File "$d\CLPR\501_0001_01\501_0001_01.MP4" 'video' (3 * $MB)
Add-File "$d\CLPR\501_0001_01\501_0001_01M01.XML" 'video' (3 * $KB)
Add-File "$d\CLPR\501_0001_01\501_0001_01.SMI" 'video' (1 * $KB)
Add-File "$d\CLPR\501_0001_01\501_0001_01I01.PPN" 'video' (1 * $KB)
Add-File "$d\CLPR\501_0001_01\501_0001_01R01.BIM" 'video' (1 * $KB)
$d = 'Video\250913-RED\A001_0101XY.RDM\A001_C001_0101AB.RDC'
Add-File "$d\A001_C001_0101AB_001.R3D" 'video' (4 * $MB)
Add-File "$d\A001_C001_0101AB_002.R3D" 'video' (2 * $MB)
Add-File "$d\A001_C001_0101AB.RMD" 'video' (8 * $KB)
# ARRIRAW: a folder of .ari frames is one clip, and the XML named after it is its metadata.
$d = 'Video\250913-ARRI\A016R1K4\A016C001_120126_R1K4'
foreach ($n in 1..3) { Add-File ("$d\A016C001_120126_R1K4.{0:D7}.ari" -f $n) 'video' (1 * $MB) }
Add-File "$d\A016C001_120126_R1K4.xml" 'video' (4 * $KB)
Add-File 'Video\250914-ZCAM-E2\A001C0001_20250914.ZRAW' 'video' (3 * $MB)
# A client's folder called CONTENTS with a VIDEO folder is no P2 card (no CLIP folder): its photo stays a photo.
Add-File 'Stills\250901-Delivery\CONTENTS\VIDEO\teaser.MP4' 'video' (2 * $MB)
Add-File 'Stills\250901-Delivery\CONTENTS\VIDEO\poster.JPG' 'photo' (300 * $KB)

# --- A CinemaDNG clip is one video; a mapping flight's numbered DNG stills stay photos ----------------------------------
$d = 'Video\250914-BMPCC4K-DNG\A001_C003_0921XY'
foreach ($n in 0..11) { Add-File ("$d\A001_C003_0921XY_{0:D6}.dng" -f $n) 'video' (300 * $KB) }
Add-File "$d\A001_C003_0921XY.wav" 'video' (500 * $KB)
Add-File "$d\behind the scenes.JPG" 'photo' (400 * $KB)   # a photo in the clip folder is not a frame
# A drone camera records no sound: the CinemaDNG tags in the frames give the clip away.
$d = 'Video\250915-Inspire2-X7\X7_0012'
foreach ($n in 0..11) { Add-Dng ("$d\X7_0012_{0:D6}.DNG" -f $n) 'video' (300 * $KB) @(0x100, 0x101, 0xC612, 0xC764) }
# Photos renamed "Custom Name - Sequence # (00001)" into a folder of the same name stay photos (still DNGs, no movie tags).
$d = 'Stills\250920-Smith-Wedding\Smith_Wedding'
foreach ($n in 1..12) { Add-Dng ("$d\Smith_Wedding_{0:D5}.dng" -f $n) 'photo' (300 * $KB) @(0x100, 0x101, 0x10F, 0xC612) }
$d = 'Stills\250815-Mavic3E-Mapping'
foreach ($n in 101..112) { Add-File ("$d\DCIM\100MEDIA\DJI_{0:D4}.DNG" -f $n) 'photo' (200 * $KB) }
Add-File "$d\Ortho\ortho.tif" 'photo' (2 * $MB)
Add-File "$d\Ortho\ortho.tfw" 'photo' 120   # GIS world file follows its image
Add-File "$d\Ortho\ortho.prj" 'photo' 400   # and so do its projection, overviews and GDAL metadata
Add-File "$d\Ortho\ortho.tif.ovr" 'photo' (200 * $KB)
Add-File "$d\Ortho\ortho.tif.aux.xml" 'photo' (2 * $KB)
Add-File "$d\Ortho\ortho_mesh.obj" 'stay' (300 * $KB)   # a mesh is not a photo

# --- Mapping missions stay whole: timestamps, PPK, RINEX and LiDAR files go where the photos go -------------------------
$d = 'Stills\250815-P4RTK-Survey\DCIM'
Add-File "$d\SURVEY\100_0001\100_0001_0001.JPG" 'photo' (1 * $MB)
Add-File "$d\SURVEY\100_0001\100_0001_0002.JPG" 'photo' (1 * $MB)
Add-File "$d\SURVEY\100_0001\100_0001_Timestamp.MRK" 'photo' (2 * $KB)
Add-File "$d\SURVEY\100_0001\100_0001_PPKRAW.bin" 'photo' (300 * $KB)
Add-File "$d\SURVEY\100_0001\100_0001_PPKNAV.bin" 'photo' (40 * $KB)
Add-File "$d\SURVEY\100_0001\100_0001_Rinex.obs" 'photo' (200 * $KB)
Add-File "$d\100MEDIA\DJI_0001.MOV" 'video' (3 * $MB)
# Base-station observations and ground control points kept outside the mission folder stay (the preview names them).
Add-File 'Stills\250815-P4RTK-Survey\BASE\DRTK3_20250815.25O' 'stay' (300 * $KB)
Add-File 'Stills\250815-P4RTK-Survey\BASE\DRTK3_20250815.25N' 'stay' (20 * $KB)
Add-File 'Stills\250815-P4RTK-Survey\GCP\gcp_list.csv' 'stay' (2 * $KB)
$d = 'Stills\250816-M350-L2\DJI_202508161200_001_Quarry'
Add-File "$d\DJI_20250816120000_0001.JPG" 'photo' (1 * $MB)
Add-File "$d\DJI_202508161200_001_Quarry_Timestamp.MRK" 'photo' (1 * $KB)
Add-File "$d\DJI_202508161200_001_Quarry_0.LDR" 'photo' (2 * $MB)
Add-File "$d\DJI_202508161200_001_Quarry_0.LDRT" 'photo' (40 * $KB)
Add-File "$d\DJI_202508161200_001_Quarry_0.IMU" 'photo' (200 * $KB)
Add-File "$d\DJI_202508161200_001_Quarry_0.RTK" 'photo' (20 * $KB)
Add-File "$d\DJI_202508161200_001_Quarry_0.SIG" 'photo' 256

# --- Phones: short Live Photo clips (with Apple's Live Photo tag) stay with their photo; a large video or a clip without
#     the tag (a Canon body counting IMG_0001 too) is a video --------------------------------------------------------
$d = 'Phones\iPhoneA'
Add-File "$d\IMG_5000.JPG" 'photo' (1 * $MB)
Add-QuickTime "$d\IMG_5000.MOV" 'photo' (3 * $MB) $livePhotoKeys
Add-File "$d\IMG_5000.AAE" 'photo' (1 * $KB)
Add-File "$d\IMG_5001.HEIC" 'photo' (1 * $MB)
Add-QuickTime "$d\IMG_5001.MP4" 'photo' (2 * $MB) $livePhotoKeys
Add-File 'Phones\Merged\IMG_4410.HEIC' 'photo' (1 * $MB)
Add-File 'Phones\Merged\IMG_4410.MOV' 'video' (41 * $MB)
Add-File 'Phones\Merged\IMG_0413.CR3' 'photo' (3 * $MB)
Add-File 'Phones\Merged\IMG_0413.JPG' 'photo' (1 * $MB)
Add-QuickTime 'Phones\Merged\IMG_0413.MOV' 'video' (5 * $MB) @('com.apple.quicktime.make', 'com.apple.quicktime.model')

# --- CinemaDNG tags decide: a lost frame, a short clip or a renamed folder without sound is still one clip -----------
$cdng = @(0x100, 0x101, 0xC612, 0xC764)
$d = 'Video\250916-BMPCC-CDNG\C0002'
foreach ($n in 0..11) { if ($n -ne 5) { Add-Dng ("$d\C0002_{0:D6}.dng" -f $n) 'video' (200 * $KB) $cdng } }
Add-File "$d\C0002.wav" 'video' (300 * $KB)
$d = 'Video\250916-BMPCC-CDNG\C0003'
foreach ($n in 0..7) { Add-Dng ("$d\C0003_{0:D6}.dng" -f $n) 'video' (200 * $KB) $cdng }
Add-File "$d\C0003.wav" 'video' (300 * $KB)
$d = 'Video\250917-Inspire2-X7\Aerials\Shot05_Hero'
foreach ($n in 0..11) { Add-Dng ("$d\X7_0020_{0:D6}.DNG" -f $n) 'video' (200 * $KB) $cdng }

# --- An Atomos recorder writes its tag export (.fcpxml) next to its clips: not a project, the clips are videos ----------
$d = 'Video\230901-Ninja-SSD'
Add-File "$d\ATOMOS_NINJAV_S001_S001_T001.MOV" 'video' (3 * $MB)
Add-File "$d\ATOMOS_NINJAV_S001_S001_T002.MOV" 'video' (3 * $MB)
Add-File "$d\ATOMOS_NINJAV_S001.fcpxml" 'stay' (20 * $KB)

# --- DJI hyperlapse: the source frames are photos, kept together; the finished hyperlapse is a video --------------------
$d = 'Stills\240121-Mavic3Pro\DCIM'
foreach ($n in 1..12) { Add-File ("$d\HYPERLAPSE\HYPERLAPSE_0005\HYPERLAPSE_{0:D4}.JPG" -f $n) 'photo' (100 * $KB) }
Add-File "$d\DJI_001\DJI_20240121120000_0005_D.MP4" 'video' (3 * $MB)

# --- Two bodies counting the same numbers (D850 NEF, Z9 MOV): the raw's edit settings (.xmp) follow the raw -------------
$d = 'Stills\250921-D850-Z9'
Add-File "$d\DSC_0001.NEF" 'photo' (4 * $MB)
Add-File "$d\DSC_0001.xmp" 'photo' (6 * $KB)
Add-File "$d\DSC_0001.MOV" 'video' (3 * $MB)

# --- CinemaDNG frame grabs next to stills: two tagged grabs far apart are stills; the raws keep their edits --------------
$d = 'Stills\250921-R5-Grabs'
Add-File "$d\IMG_0001.CR3" 'photo' (3 * $MB)
Add-File "$d\IMG_0001.xmp" 'photo' (6 * $KB)
Add-File "$d\IMG_0002.CR3" 'photo' (3 * $MB)
Add-File "$d\IMG_0002.xmp" 'photo' (6 * $KB)
Add-File "$d\notes.pdf" 'stay' (20 * $KB)
Add-Dng "$d\A001_C003_0101AB_000123.dng" 'photo' (200 * $KB) $cdng
Add-Dng "$d\A001_C003_0101AB_000456.dng" 'photo' (200 * $KB) $cdng
# A short tagged clip dumped next to a raw photo: the clip takes its sound, not the raw's .xmp.
$d = 'Stills\250922-Mixed'
foreach ($n in 0..3) { Add-Dng ("$d\C0007_{0:D6}.dng" -f $n) 'video' (200 * $KB) $cdng }
Add-File "$d\C0007.wav" 'video' (300 * $KB)
Add-File "$d\DSC_0100.NEF" 'photo' (4 * $MB)
Add-File "$d\DSC_0100.xmp" 'photo' (6 * $KB)

# --- Folders merely called Map and Models (scouting, casting) are no DJI Terra project ----------------------------------
Add-File 'Stills\250923-Casting\Map\scout_01.JPG' 'photo' (400 * $KB)
Add-File 'Stills\250923-Casting\Models\casting_01.JPG' 'photo' (400 * $KB)

# --- Frame grabs named like their clip: telemetry, thumbnails and clip XML still follow the video ----------------------
$d = 'Video\Selects'
Add-File "$d\C0001.MP4" 'video' (2 * $MB)
Add-File "$d\C0001.png" 'photo' (1 * $MB)
Add-File "$d\C0001M01.XML" 'video' (3 * $KB)
Add-File "$d\GX010042.MP4" 'video' (2 * $MB)
Add-File "$d\GX010042.JPG" 'photo' (600 * $KB)
Add-File "$d\GX010042.THM" 'video' (9 * $KB)

# --- Application libraries are never entered; editing and processing projects stay whole -------------------------------
Add-File 'Libraries\Photos Library.photoslibrary\originals\4\4F1C8E9F_3.mov' 'stay' (2 * $MB)
Add-File 'Libraries\Photos Library.photoslibrary\originals\4\4F1C8E9F.heic' 'stay' (1 * $MB)
Add-File 'Libraries\Client Films.fcpbundle\2025-09-20\Original Media\C0001.MP4' 'stay' (2 * $MB)
Add-File 'Libraries\Lightroom\Wedding.lrcat' 'stay' (500 * $KB)
Add-File 'Libraries\Lightroom\Wedding Smart Previews.lrdata\0\0A1F\0A1F3B2C.dng' 'stay' (300 * $KB)
Add-File 'Edits\Promo\Promo.prproj' 'stay' (100 * $KB)
Add-File 'Edits\Promo\Footage\A001_C003.MP4' 'stay' (2 * $MB)
Add-File 'Edits\Promo\Graphics\logo.png' 'stay' (50 * $KB)
Add-File 'Processing\Bridge\Bridge.p4d' 'stay' (20 * $KB)
Add-File 'Processing\Bridge\3_dsm_ortho\Bridge_ortho.tif' 'stay' (1 * $MB)
Add-File 'Processing\Solar_Pix4Dmatic\Solar.p4m' 'stay' (20 * $KB)
Add-File 'Processing\Solar_Pix4Dmatic\exports\Solar_orthomosaic.tif' 'stay' (1 * $MB)
Add-File 'Processing\Solar_Pix4Dmatic\exports\Solar_dsm.tif' 'stay' (1 * $MB)
# Processing folders without a project file, recognized by their layout: OpenDroneMap and DJI Terra.
Add-File 'Processing\ODM_Crop\images\DJI_0001.JPG' 'stay' (300 * $KB)
Add-File 'Processing\ODM_Crop\odm_orthophoto\odm_orthophoto.tif' 'stay' (1 * $MB)
Add-File 'Processing\ODM_Crop\odm_dem\dsm.tif' 'stay' (1 * $MB)
Add-File 'Processing\Terra_Quarry\map\result.tif' 'stay' (1 * $MB)
Add-File 'Processing\Terra_Quarry\map\result.tfw' 'stay' 120
Add-File 'Processing\Terra_Quarry\lidars\terra_las\cloud.las' 'stay' (1 * $MB)
Add-File 'Processing\Terra_Bridge3D\models\pc\0\terra_las\cloud_merged.las' 'stay' (1 * $MB)
Add-File 'Processing\Terra_Bridge3D\models\mesh\0\terra_obj\texture_0.jpg' 'stay' (300 * $KB)

# --- Edge cases ----------------------------------------------------------------------------------------------------
$d = 'Edge cases'
Add-File "$d\$($u)n$($i)c$($o)d$($e) & $($e)moji $camera\clip $clapper r$($a)ksm$($o)rg$($aa)s.mov" 'video' (3 * $MB)
Add-File "$d\readonly.MOV" 'video' (3 * $MB) 'ReadOnly'
Add-File "$d\hidden.MP4" 'video' (2 * $MB) 'Hidden'
Add-File "$d\empty.mov" 'video' 0
Add-File "$d\DJI_9999.MOV" 'video' (2 * $MB)
Add-File "$d\._DJI_9999.MOV" 'video' (4 * $KB)
Add-File "$d\DJI_9999.MOV.xmp" 'video' (4 * $KB)
Add-File "$d\._orphan.jpg" 'stay' (4 * $KB)
Add-File "$d\Thumbs.db" 'stay' (20 * $KB) 'Hidden,System'
Add-File "$d\desktop.ini" 'stay' 100 'Hidden,System'
Add-File "$d\IMG_1234.HEIC" 'photo' (2 * $MB)
Add-QuickTime "$d\IMG_1234.MOV" 'photo' (3 * $MB) $livePhotoKeys
Add-File "$d\AMBI0001.JPG" 'photo' (500 * $KB)
Add-File "$d\AMBI0001.MP4" 'video' (2 * $MB)
Add-File "$d\AMBI0001.xmp" 'stay' (4 * $KB)
Add-File "$d\unpaired.wav" 'video' (1 * $MB)   # recorder / dual-system sound goes with the videos
Add-File "$d\NEWCAM_0001.CAMRAW" 'video' (2 * $MB)   # unknown type with a video's name follows it
Add-File "$d\NEWCAM_0001.MP4" 'video' (1 * $MB)
Add-File "$d\orphan.thm" 'video' (8 * $KB)
Add-File "$d\notes.xyz" 'stay' 1000
Add-File "$d\a.b.c.mp4" 'video' (1 * $MB)
Add-File "$d\UPPER.JPG" 'photo' (300 * $KB)
Add-File "$d\Mixed.Mp4" 'video' (1 * $MB)
Add-File "$d\chunk-exact.mov" 'video' (8 * $MB)
Add-File "$d\chunk-plus-one.mov" 'video' (8 * $MB + 1)
Add-File "$d\chunk-minus-one.mov" 'video' (8 * $MB - 1)
Add-File "$d\two-chunks.mov" 'video' (16 * $MB)
$long = "$d\long\" + ((1..5 | ForEach-Object { "folder-with-a-rather-long-name-number-$_-for-path-length-testing" }) -join '\')
Add-File "$long\A_VERY_LONG_VIDEO_FILE_NAME_TO_PUSH_THE_FULL_PATH_WELL_BEYOND_THE_CLASSIC_260_CHARACTER_LIMIT.MOV" 'video' (2 * $MB)
Add-File "$long\A_VERY_LONG_PHOTO_FILE_NAME_TO_PUSH_THE_FULL_PATH_WELL_BEYOND_THE_CLASSIC_260_CHARACTER_LIMIT.JPG" 'photo' (300 * $KB)

# --- Fingerprint every file, then give it distinctive old timestamps and attributes --------------------------------
$sha = [Security.Cryptography.SHA256]::Create()
$epoch = [datetime]::SpecifyKind([datetime]'2024-01-01 10:00:00', 'Utc')
$accessEpoch = [datetime]::SpecifyKind([datetime]'2024-06-01 00:00:00', 'Utc')
$k = 0
foreach ($f in $files) {
    $full = Ext $f.Rel
    $stream = [IO.File]::Open($full, 'Open', 'Read', 'Read')
    try { $f | Add-Member Sha256 ([BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()) } finally { $stream.Dispose() }
    $created = $epoch.AddMinutes(7 * $k).AddTicks(1234567)
    [IO.File]::SetCreationTimeUtc($full, $created)
    [IO.File]::SetLastWriteTimeUtc($full, $created.AddSeconds(13).AddTicks(7654321))
    [IO.File]::SetLastAccessTimeUtc($full, $accessEpoch.AddSeconds($k).AddTicks(1111))
    if ($f.Attributes) { [IO.File]::SetAttributes($full, [IO.FileAttributes]$f.Attributes) }
    $k++
}
foreach ($f in $files) {
    $info = New-Object IO.FileInfo (Ext $f.Rel)
    $f | Add-Member CreationTicks $info.CreationTimeUtc.Ticks
    $f | Add-Member WriteTicks $info.LastWriteTimeUtc.Ticks
    $f | Add-Member AccessTicks $info.LastAccessTimeUtc.Ticks
}

$manifest = "$root.manifest.csv"
$files | Export-Csv -LiteralPath $manifest -NoTypeInformation -Encoding UTF8
$total = ($files | Measure-Object Size -Sum).Sum
"Created {0} files ({1:N0} MB) in {2}" -f $files.Count, ($total / 1MB), $root
"  video: {0}  photo: {1}  stay: {2}" -f @($files | Where-Object Expect -eq 'video').Count, @($files | Where-Object Expect -eq 'photo').Count, @($files | Where-Object Expect -eq 'stay').Count
"Manifest: $manifest"
