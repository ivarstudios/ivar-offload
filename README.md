# IVAR Offload

Back up your memory cards safely, and sort the videos from the photos. For Windows 10 and 11.

- **Backup** copies a card to up to three drives and checks every copy. It only says a card is backed up when it
  really is.
- **Sort** moves the videos (or the photos) out of a folder of card backups into a folder of their own, with the same
  sub-folders.

Nothing is ever overwritten, and nothing is deleted before its copy has been checked. If a job stops halfway (a loose
cable, a crash, a power cut), **Resume** picks up where it left off.

## Download

Get the latest zip from [Releases](https://github.com/ivarstudios/ivar-offload/releases):

- `…-win-x64.zip` for most PCs
- `…-win-arm64.zip` for Windows on Arm (Snapdragon laptops and Arm Surfaces)

Unzip it and run **IVAR Offload.exe**. There is nothing to install, so it also runs from a USB stick. The first start
of a new version takes a few seconds longer.

This is a beta and not signed yet, so Windows may show *Windows protected your PC*: click **More info → Run anyway**,
or right-click the zip → **Properties → Unblock** before unzipping. With Smart App Control turned on, Windows blocks it
for now.

## Back up a card

1. Insert the card. In the **Backup** tab, click it under **What to back up**, or choose any folder, such as a camera
   SSD. The card is only read, never changed.
2. Under **Where to save the copies**, choose one to three backup drives. Use separate drives: two copies on one drive
   are not two backups.
3. The **New folder name** is today's date and the card's name, such as `260930_MAVIC`. Pick another order under
   **Made from**, and add a **Description** if you like: `260930_MAVIC_KebnekaiseFlight`.
4. Click **Check card**. Nothing is copied yet. You see what is on the card, how long it will take, and anything
   worth knowing.
5. Click **Back up 412 files to 2 drives**.

When it is done:

- **Green** means every file is on every drive, read back and checked, and each copy is on a drive of its own.
- **Amber** means the card isn't safely backed up yet, for example a file is missing or two copies are on one drive.
  It says why.

Don't format a card until you get a green result. **Sort this backup...** on the result opens the backup in the Sort
tab.

**Shot more on the same card?** Back it up again to the same drives. The app recognizes the card and offers to add
just the new files to the earlier backup, and then it checks the whole card again.

## Sort the videos from the photos

Sort takes a folder of card backups and moves one kind of file out, keeping the folders as they were:

```
E:\Footage\260930_MAVIC\DCIM\100MEDIA\DJI_0003.MOV
  -> E:\Footage-Video\260930_MAVIC\DCIM\100MEDIA\DJI_0003.MOV
```

1. In the **Sort** tab, choose the **Folder to sort**: the folder that holds your card backups.
2. Under **What to move**, choose **Videos** or **Photos**.
3. **Move them to** is filled in for you (`…-Video` or `…-Photos` next to the folder). Change it if you like.
4. Click **Check folder**. Nothing moves yet. You see what will move, what stays and why.
5. Click **Move 412 videos**.

Sidecars, proxies, drone telemetry and sound recordings go with the files they belong to, and camera card folders
move as a whole. Sort a backup, not the card itself: if you point Sort at a memory card, it asks you to back it up
first.

Before you delete anything from the original folder, open **More** and run **Check the moved files again**. **More**
also offers to remove the folders the sort left empty (it asks first).

**Undo** puts every file back: **Undo this sort** on the result, or **Undo a sort...** at the top of the window, also
days later or on another PC.

## If something goes wrong

- **A drive was unplugged, a cable came loose or a drive is full:** the app says what happened (a backup carries on
  to your other drives). Fix it and press **Resume**. Nothing is lost.
- **The app was closed, crashed or the power went:** the next time you start it, a banner offers **Resume**.
- **You need to stop:** **Stop** finishes or undoes the file in progress; then it is safe to unplug the drives.
  **Pause** is not enough: keep the card and drives plugged in while paused. Later, choose **Resume**, or **Stop
  here...** to end the job and keep what is done.
- **The folder is synced** (OneDrive, Dropbox, Google Drive, iCloud, Resilio, Syncthing, Synology Drive, ...): to
  the sync tool, moving files out of it looks like deleting them, so they also disappear from your other devices.
  Pausing sync doesn't prevent that. Sync the new folder too if you want the files there. The preview warns you.

The **How it works** button (top right) and the small (i) buttons explain each step in the app.

## Reporting a problem

Open an [issue][issues] with the version (lower right of the window), what you did and what the result said. The
job's `.summary.txt` from its `_IVAROffload` folder helps most. It lists your file and folder names, so remove
anything private before you post it, and never attach footage.

---

## Under the hood

For DITs, data wranglers and anyone who wants to know exactly what happens. Everything below is what the app does
by itself; none of it needs to be set up.

- [Backup](#backup): copying and checking, when it is green, ASC MHL, logs, folder names, adding to a backup
- [Sort](#sort): how a file moves, results, memory cards, what counts as video or photo, logs, undo
- [Command line](#command-line)
- [Known limits](#known-limits)
- [Development](#development): building, testing, publishing, what's next

### Backup

**What is copied.** Every file on the card, including hidden and system files, camera XML, `.BIN`, `.MHL` and empty
folders, with all three timestamps, attributes and folder dates. Only `System Volume Information`, `$RECYCLE.BIN`,
`.Trashes`, `.Spotlight-V100` and `.fseventsd` are left out. Links are never followed, and cloud placeholders that
are not downloaded are never read; both are listed and keep the backup from being complete. Named data streams (only
NTFS folders have them; cards don't) are copied and checked too, except `Zone.Identifier`; a file that has them fails
on a destination that can't store them, such as exFAT.

**How every file is checked.**

- The card is read once. Each chunk is written to every destination at the same time and hashed (SHA-256 and
  xxHash64) in the same pass.
- Each copy is written to a temporary name and flushed, then **read back from its own drive, bypassing the Windows
  cache**, while the card's file is read a second time (**Read the card twice**, on by default; turning it off is
  faster, and every copy is still read back). Only a copy whose SHA-256 matches the card's is renamed into place,
  never over an existing file.
- A copy that does not match is made once more. A destination that damages it twice drops out.
- The card is opened read-only and never written to, not even its last-access times. It works with the write-protect
  switch on.

**One drive failing never stops the others.** A destination that is full, unplugged or damaging files drops out with
the reason, the others finish, and **Resume** completes it later. A card that is pulled or stops answering halts the
whole backup (nothing is recorded as skipped), and **Resume** continues it. A file the card can't read, or that
changes or reads differently the second time, fails on its own and the rest goes on; Resume reads it again. After two
files that read differently, the backup halts: check the card, reader, cable or port.

**When it is green.** Only when every file of the card has a verified copy on every destination, each destination is
on a drive (volume) of its own, and each has its ASC MHL files (or says why the card's own history prevented them). Also:

- The card is scanned again afterwards. Files added or changed after the preview keep the result amber.
- Every copy of a file must be identical: a copy made later (on Resume) that differs from one already verified on
  another destination is refused.
- A destination that finished in an earlier run is looked at again; a file sorted out of it or deleted since makes it
  incomplete.
- Copies that share a drive, or are on the drive being backed up, are checked but never green (*Copied, but not to
  separate drives*). The preview says so next to the start button.
- Anything less is amber, with a line per drive (*F:\Cards\260928_SONY_A: 100 of 412 checked - The destination F: is
  full.*) and a red line not to format the card. The app never says a card may be formatted.

**Details** on the result says how the copies were compared and whether the ASC MHL files were written on each drive.
Under **More**, **Check the copies again** re-reads every copy on every drive, and counts files that **Sort this
backup...** moved out as moved, not damaged.

**Refused before anything is copied:** a destination folder that already exists and is not empty (a backup never mixes
two cards; an unfinished backup of the same card there is offered for resume), a destination on or inside the card,
the same destination twice, more than three, a read-only drive, too little free space (copies on one drive each need
room), and files of 4 GB or more for a FAT32 drive.

**ASC MHL.** Every destination gets an `ascmhl` folder with a generation manifest (xxHash64 of every file, plus
directory content and structure hashes) and `ascmhl_chain.xml`, so Hedge, Silverstack or `ascmhl-debug verify` (also
`-dh`) can check it independently.

A card that already has ASC MHL histories from an earlier offload (in its top folder or in subfolders) is copied as
it is, and each history gets the next generation (in xxHash64), recording each file as *verified* where the history
had an xxHash64 for it and *original* otherwise; the top-level one references the nested ones. That only happens when every history is intact (every manifest its chain
lists is there and unchanged), uses only the standard ignore patterns, and the card still matches it, compared in
the format the history used (xxh64, xxh128, xxh3, md5, sha1, c4). Otherwise that destination gets no manifest and the
result says why. A card that no longer matches its own earlier checksums is never green. The `_IVAROffload` log
folders are kept out of the manifests by an ignore pattern (a card's own `_IVAROffload` folder, from an earlier sort,
is still copied and verified).

**Logs.** Each destination keeps its own log, `<backup folder>\_IVAROffload\<job>.backup.jsonl`, with a `.summary.txt`
and a `.manifest.csv` (every file, its SHA-256 and xxHash64, and its status). All destinations share the job id, so
the backup can be resumed from any of them. Nothing is written to the card, and Sort never picks up a backup's log.

**Unfinished backups** (closed app, crash, power cut, an unplugged destination) show in a banner at the next start,
**Resume** needs the card. **Stop here...** keeps the checked copies, records the rest as not copied and deletes
nothing; a destination with every file verified is finished properly, ASC MHL included. When the unfinished copy is
on a drive that is not connected, the banner names it and offers **Check again** (once it is plugged in) or **Forget
this backup** instead. On another PC, **Check card** with that destination finds an unfinished backup of the card
there and offers it in the banner. A card
or destination that comes back under another drive letter (card readers often change letters) is found by its
drive's serial number. A destination whose log is damaged sits out; the others go on. A new backup of another card
can start while one is unfinished.

The destinations are remembered once a backup starts. A remembered drive that is not connected next time is named in
the preview instead of silently making one copy fewer.

**Folder names.** `{YYMMDD}_{card}` (the day of the backup) unless you choose otherwise: the card's label is its name in Windows, or the
folder's name when you picked a folder. **Made from** offers four ready-made names, each shown with an example; the
**Description** is added to the end, and **Saved as** shows the full folder on every backup drive as you type. When a
name is already taken (the second card of a camera that labels every card alike, the same day), `_2`, `_3`, ... is
added.

**Your own pattern...** combines `{card}`, `{camera}` and date and time codes in braces: `YYYY` or `YY`, `MM`, `DD`,
`HH`, `MM` (minutes, right after `HH` or right before `SS`) and `SS`, with `-`, `_`, `.` or a space between them. For
example `{YYYY-MM-DD}_{card}` or `Wedding {YYMMDD} {card}`. Text outside braces is kept as it is, and a camera the
card doesn't show is left out with its `_`. Patterns from earlier versions (`{date}`, `{year}`, `{month}`, `{day}`)
keep giving the same names. **Reset** goes back to `{YYMMDD}_{card}`.

The camera make comes from the card's folders: Sony's `M4ROOT` or `100MSDCF`, Canon's `100CANON`, a RED `.RDM`, an
ARRI reel, Blackmagic `.braw`, and so on.

**Adding to an earlier backup.** The preview looks through each destination folder (every backup in `E:\Cards`, say)
for the newest finished backup of the same card: the same drive serial number and folder, and at least half of the
files that backup verified still on the card with the same name, size and date. Deleted shots, new shots and the
index files cameras rewrite with every shot (Sony's `MEDIAPRO.XML`, AVCHD's `INDEX.BDM`, Canon's catalogs, ...) don't
change that; formatting the card does. When every destination holds that same backup, the preview says so (*This card
was backed up on 2026-09-28 14:02 to E:\Cards\260928_SONY_A. The card has 38 new files since then (12.4 GB) ...*) and
offers:

- **Add the new files to that backup** (the default). Only what the folder does not have as the card has it now is
  written: new files, files changed on the card, and files gone from the folder (moved out by **Sort this
  backup...**, or deleted), so the folder mirrors the card again. Then every file of the card is read again, from the
  card and from each copy, and compared with the SHA-256 the earlier backup recorded. The result means the same as a
  full backup: *38 new files copied, all 450 files checked. The card was not changed.*
- **Make a new full backup** into a new folder.

Nothing in the backup folder is overwritten or deleted. A file there that is not the card's file as the card has it
now is replaced only once the card's copy is made and verified:

- The earlier version of a file that changed on the card (usually a shot whose number the camera used again after a
  deletion) stays next to the new one as `IMG_0450 (earlier).JPG`, with a sidecar of the same name renamed the same
  way. It is read as it is renamed, listed in the ASC MHL manifest and kept by later top-ups. The preview warns about
  these first.
- Anything else, such as an older camera index file or a copy that was edited or damaged on the drive, is moved into
  `_IVAROffload\replaced\<job>\` with its sub-folders. The preview warns about edited copies; a damaged one is only
  found when the top-up reads it, and the result names the drive.
- A copy that can't be made (the file left the card, the card was pulled, the backup was ended) leaves the old file
  where it is.
- Files no longer on the card are never moved. They are read again: one edited there since (another size or date) is
  reported and left as it is; one with the same size and date that no longer matches its checksum is damaged, and the
  result is amber (without the line not to format the card, since the card can't replace it).

When the card returns different data for a file than the backup verified, with the same size and date, it is read
once more. If it reads the same, the file was changed in place and is copied again. If not, the reader is flaky: the
copy is kept and the file fails (Resume reads it again). A top-up writes a new log next to the earlier one, which is
never changed, so it resumes like any backup. The ASC MHL history gets a new generation; when files it lists changed
or are gone (which an ASC MHL history can't record), it is moved into `_IVAROffload\replaced` and a new history is
started, so `ascmhl verify` passes.

A top-up is not offered (the preview says why) when a destination has no earlier backup of the card, the destinations
hold different ones, the card's own ASC MHL history changed or appeared since (another offload tool wrote it), or an
unfinished backup of the card is waiting. Otherwise, earlier backups of the card whose files are all still on it
unchanged are named in the preview. None of this blocks a new full backup.

### Sort

**How a file moves.**

| Where | What happens |
|---|---|
| Same drive (e.g. `E:` to `E:`) | The file is **renamed** into place; its data is never read back or rewritten. Before the rename its SHA-256 (unless **Record checksums** is turned off) and NTFS file id are recorded. Afterwards the target must be the *same file record* with the same size and timestamps. |
| Between drives | The file is copied to a temporary `.offload-partial` while its SHA-256 is computed, and flushed. The copy is then **read back bypassing the Windows cache** while the **original is read a second time**. Both must match before the copy is renamed into place and the original deleted. Named data streams are copied and checked too. |
| Deleting an original | Only after a verified copy is in place, through a handle that first re-checks that the original is unchanged (size, dates, file id) and that nobody is writing to it. |

**When something is in the way.**

| Situation | What happens |
|---|---|
| A file with the same name is already in the target | Never overwritten. An identical copy (same name, size and date) counts as done. A **different** file (probably another card with restarted numbering) keeps that file **and the files that belong with it** in the source; sort each card into its own folder. |
| A file changed after the preview | Left alone. What you previewed is exactly what runs. |
| A file is in use (sync tool, antivirus, player) | Retried for a few seconds, then reported. **Resume** tries again. |
| The source reads differently the second time | The original is kept and reported (check the drive, cable, port or reader). After two such files the job halts: do not format or delete that source. |
| The target damages a copy | It is set aside as `<name>.damaged-copy` and the file is copied again. If that copy is damaged too, the job halts. |
| A drive is unplugged, stops responding or a folder is renamed | The job **halts**; nothing is recorded as skipped. The message says whether the drive or only the folder is missing. A drive back under another letter is recognized by its serial number, and the message says which letter to give it back. |
| The target is full | Checked before each copy. The job halts and resumes once there is room. |
| Originals can't be removed (read-only, no permission) | After two such files the job halts with the verified copies in place. Fix the permission and resume, or end the job. |
| An original disappears during a job (removed by something else) | Nothing that may be the last copy is ever deleted, also after a crash, on Resume or on ending the job. A verified copy is put in place (or kept as `<name>.verified-copy` when its name is taken); an unchecked one is kept as `<name>.unverified-copy`, a damaged one as `<name>.damaged-copy`. The file is reported as *missing from the source*, with the copy that was kept. |
| A job is ended while its source can't be reached | Nothing is deleted and no original is judged. Verified copies are put in place as *copied, original not checked*; other copies are kept under the names above and reported as not moved, never as missing. The moved files can be undone once the folder is back. |
| Crash, power loss, killed process | Every step is written to the journal and flushed *before* the next one. On resume each unfinished file is checked against what is actually on disk, not only the log. |
| The same job opened twice | A running, verifying or undoing job is locked; another window or command line is told it is open elsewhere. |

**Timestamps and attributes.** Created, modified *and* last-accessed times are kept: files are read for checksums
through a handle that tells NTFS not to update the last-access time. Attributes (read-only, hidden, archive) are
kept, also on a same-drive move and its undo. Target folders get the dates of their source folders.

**Speed** (NVMe): a same-drive move with checksums runs at about 1.4 GB/s, about 4–5 minutes for 360 GB of video, or
seconds without checksums. Between drives each file is written once, read back once and read twice from the source,
so expect a little less than the speed of the slower drive.

**Results.** When a sort finishes or is ended, the source is scanned again in the background and combined with the
job's own record. The command line does the same, and its exit code and the job's summary go by it.

- **Green** only when the job ended, nothing failed, nothing planned is still in the source, and the fresh scan finds
  nothing else that should move. When the scan couldn't run or couldn't read some folders, it stays amber.
- Otherwise **amber**, with a line that is always there (*Left in the source: 17 videos (38 GB), 2 unrecognized files -
  check before deleting or formatting anything.*) and a line per reason: a different file with the same name in the
  target, changed after the preview, in use, original could not be removed, job ended early, and so on.
- Files the preview **held back** count as left in the source, also when the source can't be scanned again.
- **Online-only** files (cloud placeholders that are not downloaded) are never read, so they stay behind and keep the
  result amber; the result says how to fix it. A short online-only clip next to a photo of its name may be a Live
  Photo or a video, so it is listed under *Needs a look*.
- Links named like a video (never followed) and media inside an editing or processing project (which stays by design)
  are mentioned, but don't keep the result amber.
- Files that **disappeared from the source** are their own group, *Missing from the source (removed by something
  else)*, and never counted as still in the source.
- **Show files still in the source** lists what is left. When some of it can move now (added after the preview, or
  left by an earlier sort of the same folder), the bottom button offers **Move the remaining 17 videos**.
- A CinemaDNG or other image-sequence clip, or a RED clip in several parts, counts as one video. Companion files and
  macOS `._` files never count.

**Check the moved files again** re-reads every moved file and compares its checksum, and lists every planned file that
did not move, every held-back file and every missing file. A green result only speaks for the side that moved: what
stayed is still only in the source.

**Memory cards and camera drives.** Sort is meant for card backups. When the source looks like a card, the move
button first asks: **Back up this card first** (the default; opens the Backup tab with the card) or **Sort anyway**
(cameras such as the Blackmagic Pyxis record straight to an SSD, so sorting one can be intended). After sorting a card
the result is never green: a red line says the card still holds the only copy of what stayed, and it stays after
checking again. The card's own camera folders (`DCIM\100MSDCF`, `PRIVATE\M4ROOT`, ...) are never offered when **More**
offers to remove the folders a sort left empty.

A source counts as a card when the root of its drive shows a camera structure (`DCIM` with a camera folder,
`PRIVATE\M4ROOT`, `AVCHD`, a P2 or Canon XF `CONTENTS`, `BPAV`, `XDROOT`, a RED `.RDM` folder, an ARRI reel folder, or
clips in the root as Blackmagic cameras write them) and the drive is removable, or the source is the drive root, or
it is inside one of those card folders. Cameras write FAT32, exFAT or UDF, so NTFS and ReFS drives are never taken for
cards. A source files can't be moved out of (write-protected, read-only, no permission) is refused.

**Other checks before a sort.**

- System folders are refused: the system drive root, Windows, Program Files, AppData and the user folder itself. Other
  drive roots, and Pictures, Videos, Desktop, Documents and Downloads, get a warning.
- A folder inside a video card structure (e.g. `...\PRIVATE\M4ROOT\CLIP`) is refused, with **Use this folder** for the
  folder that holds the whole card. A Sony card copied without its `PRIVATE\M4ROOT` wrapper is a whole card itself.
- A folder inside an application library (Apple Photos, Final Cut Pro, Lightroom, Capture One, Luminar, ...) is
  refused: moving files out would break the library. A folder inside an editing or processing project gets a warning.
- **A target inside the source is allowed** (`E:\` → `E:\Video` on a camera SSD): the target is left out of the scan.
  The target can't be the source itself, and the source can't be inside the target. This is judged where the paths
  really lead, so a junction, a `subst` or mapped drive letter or a short name can't hide it. A `subst` letter given
  another folder later halts the job instead of working in that folder.
- A FAT32 target can't hold files of 4 GB or more, and a read-only target is refused. The free-space check includes
  some room per file.
- When the target already holds files sorted from another folder, or in the other mode, the preview warns and the
  line above the button turns amber. Sorting the same folder into it again continues the earlier sort.

#### What counts as video, photo or neither

- **Video**: `.mov .mp4 .m4v .avi .mts .m2ts .mxf .mkv .braw .r3d .crm .ari .insv .360 .nev` (Nikon N-RAW) `.osv`
  (DJI Osmo 360) `.zraw` (Z CAM) `.ts .trp .mlv .mcraw .cine .arx ...`, and the proxies `.lrf` (DJI) and `.lrv` (GoPro,
  Skydio).
- **Photo**: `.jpg .jpeg .heic .hif .png .tif .psd .dng .nef .raf .arw .arq .cr2 .cr3 .orf .rw2 .gpr .insp .mpo .jps ...`
- **Sound** (`.wav .bwf .rf64 .w64 .mp3 .m4a .aac .aif .aiff .flac`): a recording named like a photo or video follows
  it (camera voice memos). One with no matching photo or video is recorder or dual-system sound and **goes with the
  videos**. One that matches both stays and is listed. A field recorder's take file (Zoom `.hprj`, `.ZDT`) follows the
  recordings in its folder.
- **Companion files follow their file**, matched by name in the same folder: `.xmp`, `.aae`, Sony `C0001M01.XML`,
  macOS `._name`, raw editor settings (Capture One, NX Studio `.nksc`, Canon DPP, DxO, ON1, RawTherapee, Resolve
  `.drx`), and GIS world files and rasters' `.prj`, `.ovr` and `.aux` files. With double names (`clip.MOV.xmp`, Kyno's
  `.LP_Store\clip.MOV.lpmd`, NX Studio's `NKSC_PARAM\X.NEF.nksc`) the inner extension decides. When a photo and a
  video share a name, an `.aae` follows the photo, and so does an `.xmp` when the photo is a raw and the videos are
  `.mov`/`.mp4`/`.m4v`; otherwise the `.xmp` stays and is listed.
- **Always video**, even without a name match:
  - `.thm` thumbnails, DJI `.scr` screennails (also in `MISC\THM\100\`), `.srt` drone telemetry, Blackmagic RAW
    `.sidecar` and RED `.rmd` files.
  - An `.xml` named after the clip folder it is in, when that folder holds videos and no photos (ARRIRAW).
  - An unrecognized file with the same name as a video next to it (listed under *Needs a look*).
  - Everything inside a **video card structure**, which moves as a whole: Sony `PRIVATE\M4ROOT`, `AVCHD`/`BDMV`, XDCAM
    `XDROOT` and `BPAV`, Panasonic `PANA_GRP` and P2 `CONTENTS`, Canon XF `CONTENTS`, RED `.RDM`/`.RDC`.
  - **CinemaDNG and image-sequence clips**: a folder with at least 10 gap-free numbered frames (`.dng .dpx .exr`) named
    after the folder or a sound file next to them (`A001_C003\A001_C003_000001.dng`) is one clip, and everything in
    the folder goes with the videos. DNG frames also need CinemaDNG tags. DNG frames with those tags are a clip even
    with gaps, fewer than 10 frames or a renamed folder, as long as fewer frames are missing than are there. A drone's
    mapping run (`100MEDIA\DJI_0001.DNG`, ...) stays with the photos.
- **iPhone Live Photos**: a `.MOV`/`.MP4` next to a `.HEIC` (or an `IMG_` `.JPG`) of the same name stays with the photo
  when it carries Apple's Live Photo identifier and is up to 15 MB (about 3 seconds). A clip without the tag goes with
  the videos and is listed under *Needs a look*.
- **DJI hyperlapse frames** (a `HYPERLAPSE_nnnn` folder, or at least two such photos in a folder) stay photos but are
  kept together; a name clash holds back the whole hyperlapse.
- **Mapping missions stay whole**: in a folder with photos and a DJI timestamp file (`.MRK`, not `AUTPRINT.MRK`) or DJI
  LiDAR data (`.LDR` with `.IMU`, `.RTK`, ...), the timestamp, LiDAR and RINEX/PPK files go where the photos go.
  Base-station data (`.obs .nav .rnx .24O ...`, `.ubx .sbf .T02 ...`) and ground control point lists outside a mission
  folder stay; in photo mode the preview names them.
- **Stays in both modes**:
  - Card and app files (`.mhl .dsc .dat .bin .db .gis .pbuf .txt .json .csv ...`), Final Cut Pro XML exports
    (`.fcpxml`, which recorders such as Atomos write next to their clips), point clouds, meshes and map layers (`.las
    .laz .obj .mtl .kmz .kml .shp .shx .dbf .geojson`).
  - Everything in an **editing or processing project** folder: one holding a `.prproj .drp .aep .veg .psx .p4d .p4m
    .rcproj ...` file, an OpenDroneMap/WebODM project, or a DJI Terra project. The preview names them.
  - Unrecognized types. They are listed; large ones get their own warning.
- **Never entered**: application libraries (`.photoslibrary .fcpbundle .imovielibrary .lrdata .lrlibrary .cocatalog
  ...`, the Photo Booth library, a Luminar catalog's folder), sync folders (`.sync`, `.stfolder`, `@eaDir`, `.@__thumb`),
  `ascmhl`, `$RECYCLE.BIN`, `System Volume Information`, and the app's own log folders (`_IVAROffload`, and
  `_IVARIngest` and `_IngestSorter` from its earlier names). Links and junctions are not followed.
- The preview warns about photo catalogs in the source (`.lrcat`, Capture One, Luminar), whose imported files will need
  relinking, and about ASC MHL manifests, which will report the moved files as missing.

#### Logs, receipt and undo

Each sort writes into **the target**, `<target>\_IVAROffload\`:

- `<job>.journal.jsonl`: the step-by-step journal (append-only JSON Lines): the plan, with every file's size and
  dates, and every step with its time, SHA-256 and file ids. Resume and undo work from it.
- `<job>.manifest.csv`: one row per file: status, source, destination, size, SHA-256, method, time. Opens in Excel.
- `<job>.summary.txt`: what happened in plain words, with a section for each kind of file that did not move, and why.

And, once files have left, a receipt into **the source**, `<source>\_IVAROffload\`:

- `<job>.moved-out.csv`: every file that left, where it went and its SHA-256, so every file can be traced without the
  app.
- `<job>.moved-out.txt`: what left, when and where to, what stayed, how to undo it, and where the job log is.

These are written while the job runs (at its start, after the first file, about every 15 seconds and at the end),
each through a temporary file, so a crash never leaves a cut-off list. After a crash they are brought up to date from
the journal. Nothing is written into the source before a file has left it. Any file's checksum can be checked
independently with `Get-FileHash -Algorithm SHA256 <file>`.

**Undo** lists the sorts this PC remembers, the sorts of any folder you choose, or the sort of a log or receipt you
open. It shows what will happen first (*Move 412 videos back to E:\Smith\Card1? 3 files changed since the sort and will
stay where they are.*) and then runs like any job, with the same progress, crash safety and honest result.

- Files only go back into the folder they came from, and that folder has to prove it: it holds the receipt, or still
  matches what the sort recorded. A new folder that merely has the old name is refused. Renamed and moved folders are
  followed. When the folder can't be confirmed, **Choose the folder the files came from...** lets you pick it, and it
  is checked and previewed again.
- A file that changed since the sort stays where it is. A file whose original place is taken is never overwritten.
  The rest of a clip goes back even when one of its files can't.
- Between drives each file is copied back and must match its recorded checksum before the sorted copy is removed.
- Undo works from the logs alone, so it survives restarts and works on another PC. A sort can be undone once; an undo
  that could not finish leaves the sort *partly undone*, and the rest can be undone later.

**Unfinished sorts.** The banner offers **Resume** or **Stop here...**, which finishes or rolls back the file in
progress, leaves everything else where it is and ends the job. When the drive with the job's log is not connected,
the banner says so; **Check again** once it is plugged in, or **Forget this job** (its log stays on the drive). A job
whose sorted folder was renamed or moved on its drive is found again by itself (nearby folders, other sorts on that
drive, the drive's root, within about 2 seconds); a copy of the folder is never taken for it.

**Jobs from earlier versions.** The app was called Ingest Sorter, then IVAR Ingest. Sorts they logged in
`_IngestSorter` and `_IVARIngest` folders are resumed, verified and undone like any other, and keep their log folder.
A backup IVAR Ingest made is resumed, checked again and added to in its `_IVARIngest` folder, and its ASC MHL history
is continued. Settings are in `%LOCALAPPDATA%\IVAROffload`; until it has saved its own, the app reads those IVAR
Ingest or Ingest Sorter left in `%LOCALAPPDATA%\IVARIngest` or `%LOCALAPPDATA%\IngestSorter`.

### Command line

`ivar-offload.exe`, in the same zip, uses the same engine, for scripting:

```
ivar-offload preview --source E:\Footage --target E:\Footage-Video [--mode videos|photos] [--no-checksums] [--list]
ivar-offload run     --source ... --target ... [--mode ...] [--no-checksums] --yes
ivar-offload resume  --target E:\Footage-Video                  (or --journal <job log>, for all of these)
ivar-offload close   --target ...      end the job: files in flight are finished or rolled back, the rest stays
ivar-offload verify  --target ...      re-read every moved file; also lists planned files that did not move
ivar-offload status  --target ...      the job's summary
ivar-offload jobs    --target <folder> the jobs sorted into or out of a folder, and their undos
ivar-offload undo    --target <folder> [--job <id>] [--to <folder>] [--yes] [--list]
ivar-offload undo    --journal <job log, manifest, summary or .moved-out receipt> [--to <folder>] [--yes] [--list]

ivar-offload backup  --source F:\ --target E:\Cards [--target N:\Cards ...] [--name <folder name> | --name-template "{YYMMDD}_{camera}_{card}"] [--no-source-reread] [--top-up] [--list] [--yes]
ivar-offload backup-resume  --target E:\Cards\260928_SONY_A    (or --journal <backup log>, for all of these)
ivar-offload backup-close   --target ...   end the backup: copies in flight are finished or removed, the rest is not copied
ivar-offload backup-verify  --target ...   re-read every copy on every destination
ivar-offload backup-status  --target ...   the summary of the backup on that destination
```

Without `--yes`, `backup` and `undo` only show the preview, and `run` stops with a usage error (use `preview` to look
first). `--list` names the files: those that move, those to copy (with each one's action on a top-up), or, for
`undo`, those already back or staying. `--top-up` adds the card's new files to its earlier backup and verifies the
whole card. Unlike the app, `backup` never adds `_2`: a folder that exists and is not empty is refused, so give a
second card with the same label a `--name`. Running `run` again after a job was ended mid-clip moves
the rest of the clip. The command line doesn't ask the memory-card question, but a sort of a card never exits 0.

| Exit code | Sort | Backup |
|---|---|---|
| 0 | done or ended, and nothing that should move is still in the source (`verify`: everything checked out) | every file verified on every destination (unlike green in the app, separate drives are not checked) |
| 1 | finished or ended with failures (`verify`: problems found) | files failed or a destination dropped out (`backup-verify`: problems found) |
| 2 | stopped, halted, or the job's log can't be used (open elsewhere, already finished, unreadable) | stopped, halted, or the backup's log can't be used |
| 3 | usage error | usage error |
| 4 | the plan or the undo can't run, the sort's log can't be reached, or there is nothing to move | the backup can't start (the preview says why) |
| 5 | done or ended, but something that should have moved is still in the source or went missing, the source couldn't be checked again, or the source is a memory card | done, but not everything on the card is in the backup |

### Known limits

- Open bugs are tracked in [Issues][issues]. A few can make a result green when it shouldn't be: a resumed backup
  doesn't check again the copies made before it was interrupted ([#5][i5]), **Check the copies again** can turn an
  amber backup green ([#6][i6]), names ending in a space or a dot are skipped ([#8][i8]), and so is a file reached
  through a junction inside a sort's target ([#9][i9]). On the command line, `backup` without `--yes` exits 0
  although nothing was copied ([#14][i14]).
- Real exFAT/FAT32 cards, real USB card readers, drive-letter changes and drives unplugged mid-job have only been
  tested by simulation so far ([#4][i4]; the camera index files
  for adding to a backup: [#3][i3]).
- A sort's log whose folder was moved far away (beyond the roughly 2-second, 5,000-folder search) shows as *log not
  found*; open it with **Open a log file...**.
- What an ended sort left behind is only taken along by a new sort of the same folder into the same target.
- Read-backs bypass the Windows cache, not the destination's own: a network share (NAS, SMB) or a RAID with a write
  cache may answer from its memory rather than its disks. On a file system that refuses unbuffered reads, read-backs
  are normal reads that Windows may serve from its cache; NTFS, exFAT and FAT32 accept unbuffered reads.
- Drives are told apart by volume, so two partitions of one physical disk count as separate drives and can give a
  green result. Put each copy on its own disk.
- A backup that can't read a source file's named data stream reports the error on the destinations, not the source.
- A card's ASC MHL history with its own ignore patterns is not continued; the backup is verified but gets no manifest
  (the result says so).
- Adding to a backup needs the same earlier backup on every chosen destination; a new destination gets a full backup
  of its own. After a top-up that added files, `ascmhl verify -dh` reports the folders that got new files (the
  reference tool compares folder hashes with the first generation); `ascmhl verify` passes.
- Moves on NTFS/ReFS are confirmed by file id, on exFAT/FAT by size and dates. An interrupted copy between drives
  restarts that file from the beginning.

---

## Development

### Building and testing

Requires the .NET 10 SDK, and for the ASC MHL tests the ASC's reference tool: `python -m pip install --user ascmhl`
(or leave those tests out: `dotnet test --filter Category!=ascmhl`).

```
dotnet build -c Release
dotnet test -c Release                                                    # 582 unit tests, ~40 s
powershell -File tools\Test-EndToEnd.ps1 -OtherVolumeRoot <folder>         # 45 scenarios, ~10 minutes
powershell -File tools\Test-Gui.ps1 -OtherVolumeRoot <folder>              # 11 scenarios; opens windows on the desktop
```

`-OtherVolumeRoot` is a folder on another drive than `%TEMP%`, for the scenarios that move files between drives;
leave it out to skip them. `dotnet test` builds neither program: run `dotnet build -c Release` before the end-to-end
and GUI tests. Don't use the mouse or keyboard while the GUI tests run.

- **Unit tests** cover the file-type rules per camera family, the preview's checks, journal damage, a crash at every
  step of every move, copy, backup and ASC MHL step followed by resume or ending the job, drives and cards that
  disappear, damaged copies, top-ups, undo, and the result wording. The `ascmhl` tests run the reference tool on every
  destination.
- **`tools/New-SampleIngest.ps1`** builds a synthetic ingest modelled on real card dumps (DJI, Fuji, Nikon, Sony,
  GoPro, Skydio, Olympus, Blackmagic, field recorders, P2/XF/XDCAM/RED cards, CinemaDNG, mapping missions, phones,
  libraries and projects) with edge cases (paths over 260 characters, emoji and å/ä/ö, read-only and hidden files,
  sync folders), and a manifest of the expected outcome.
- **`tools/Test-SampleIngest.ps1`** is the independent check, on Windows PowerShell / .NET Framework (another runtime
  and SHA-256): every file in exactly one expected place, identical, with its timestamps and attributes.
- **`tools/Test-EndToEnd.ps1`** runs the command line and kills it at every step of the move sequence, same drive and
  between drives, plus random kills, conflicts, locked files, vanishing sources and undo. `-Only <text>` runs some
  scenarios.
- **`tools/Test-Gui.ps1`** drives the real window through UI Automation: backups, sorts, stop and resume, a killed
  app, undo and the memory-card question. It closes only the windows it opened.
- **`tools/Capture-Screens.ps1`** screenshots every screen and popup, with a text dump of each, for GUI reviews.

Environment variables (tests only):

- `IVAROFFLOAD_DATA=<folder>`: settings and recent jobs go there instead of `%LOCALAPPDATA%\IVAROffload` (and the
  earlier versions' folders are not read).
- `IVAROFFLOAD_TEST_CRASH_AT=<point>[:n]`: the command line (and a backup in the app) kills itself the n-th time it
  reaches a step. Sort: `after-pre`, `after-rename`, `after-copy-journal`, `mid-copy`, `after-copy`, `after-copied`,
  `after-place-rename`, `after-placed`, `after-delete`. Backup: `after-copy-journal`, `mid-copy`, `after-copy`,
  `after-copied`, `after-place-rename`, `after-done`, `before-mhl`, `after-mhl`; top-ups also `after-aside-journal`,
  `after-aside`, `before-recheck-done`, `before-mhl-restart`, `after-mhl-restart`.
- `IVAROFFLOAD_TEST_HOLD_AT=<point>[:n]`: the command line (and a backup in the app) waits at a step until the file
  `test-hold.flag` it creates in the settings folder is deleted.
- `IVAROFFLOAD_TEST_CARD=<drive name>`: every source counts as a memory card with that name (e.g. `F: TEST_CARD`).

### Publishing

```
dotnet publish src\IvarOffload.App -c Release -r win-x64 -o publish\app-win-x64
dotnet publish src\IvarOffload.Cli -c Release -r win-x64 -o publish\cli-win-x64
dotnet publish src\IvarOffload.App -c Release -r win-arm64 -o publish\app-win-arm64
dotnet publish src\IvarOffload.Cli -c Release -r win-arm64 -o publish\cli-win-arm64
```

Each program becomes one self-contained, compressed exe (not trimmed; symbols embedded). Publish each into its own
folder: publishing into a folder removes what an earlier publish left there. A release zip per platform holds `IVAR
Offload.exe`, `ivar-offload.exe`, `README.md`, `LICENSE` and `THIRD-PARTY-NOTICES.md`, and the release notes list
the zips' SHA-256.

### What's next

1. **Test with real hardware** ([#4][i4]): exFAT and FAT32 cards, a write-protected card, a USB reader pulled
   mid-copy, two USB drives with one unplugged mid-backup, a reader that gives the card another letter. Confirm which
   index files Sony, Canon, Panasonic and AVCHD cameras rewrite with every shot ([#3][i3]), and DJI X7 / CineSSD
   CinemaDNG names.
2. **Signed releases** ([#1][i1]), through the Microsoft Store or a code-signing certificate, so that Windows no
   longer warns about the download.
3. **An About box** ([#2][i2]) with the version, the license and the third-party notices.

### Notes

- The journal stores absolute paths plus each drive's label and serial number. A drive back under another letter
  halts the job, which says which letter to give it back (Disk Management > Change Drive Letter); undo follows the
  drive by itself.
- The window uses software rendering on purpose: with virtual display adapters (Parsec), WPF's GPU path can leave the
  first frame blank.

## License

IVAR Offload is © 2026 IVAR Studios, licensed under the [GNU General Public License v3.0](LICENSE) (GPL-3.0-only).
You may use, study, change and share it; a changed version you distribute must be under the same license, with its
source.

The IVAR name and the IVAR Studios logo are trademarks of IVAR Studios and are not licensed under the GPL (section 7(e)
of the license): a changed version you distribute needs a name and icon of its own.

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) covers the .NET runtime that the published programs include.

[issues]: https://github.com/ivarstudios/ivar-offload/issues
[i1]: https://github.com/ivarstudios/ivar-offload/issues/1
[i2]: https://github.com/ivarstudios/ivar-offload/issues/2
[i3]: https://github.com/ivarstudios/ivar-offload/issues/3
[i4]: https://github.com/ivarstudios/ivar-offload/issues/4
[i5]: https://github.com/ivarstudios/ivar-offload/issues/5
[i6]: https://github.com/ivarstudios/ivar-offload/issues/6
[i8]: https://github.com/ivarstudios/ivar-offload/issues/8
[i9]: https://github.com/ivarstudios/ivar-offload/issues/9
[i14]: https://github.com/ivarstudios/ivar-offload/issues/14
