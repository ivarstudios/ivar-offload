# Hardware testing plan

Status: planned, waiting for the devices. Covers issue #4 (real cards, card readers, drive-letter changes and USB
drives) and #3 (which camera files change between shots), and in general tests both modes against real hardware on
several machines.

So far every test fakes the hardware: the unit, end-to-end (`tools/Test-EndToEnd.ps1`) and GUI (`tools/Test-Gui.ps1`)
suites simulate cards, drive letters and unplugged drives. This plan is a kit that runs on a test machine with real
devices plugged in. It does as much as it can unattended, stops only for what needs a hand (a switch, a camera), and
writes a results bundle per machine.

## The bench (per machine)

Use **test media only**: the kit formats the cards and destination drives.

| Role | What | Why |
|---|---|---|
| CARD-A | SD card, exFAT | the common case; holds a clip of 4 GB or more |
| CARD-B | SD card, FAT32 | 2-second times, 4 GB file limit |
| CARD-WP | full-size SD card with a write-protect switch (can be CARD-A) | the switch is reported by the reader, not by Windows |
| CARD-MICRO | microSD card | for the internal slot of a laptop |
| CARD-CFX | CFexpress card, if available | fast readers often report the drive as Fixed, not Removable |
| READER-1 | cheap USB-A reader | |
| READER-2 | another model, ideally a fast USB-C UHS-II or CFexpress reader | a second letter for "the card came back at another letter" |
| internal slot | the laptop's own SD reader | usually PCIe (Realtek), behaves differently from USB readers |
| DEST-1, DEST-2 | two bus-powered USB SSDs or HDDs | Backup destinations, Sort cross-drive targets |
| HUB | switchable USB hub (see below) | turns "pull the card" and "unplug the drive" into a command |

Put a physical label on each device with its role name.

### Switchable USB hub

With a hub whose ports can be switched off by software, "pull the card reader when the copy is 40% done" becomes a
command at an exact moment: a real surprise removal, repeatable, with nobody at the desk. Without one, those checks
become guided pulls by hand.

| Device | Ports, speed | What it switches | Notes |
|---|---|---|---|
| **Yepkit YKUSH3** (recommended) | 3 x USB-A, 5 Gbps | a port's power | `ykushcmd` CLI for Windows, `-d <port>` off / `-u <port>` on, `-s <serial>` for several boards. Inexpensive. Only disconnects bus-powered devices, so DEST drives on it must be bus-powered. |
| Acroname USBHub3+ | 8 x USB-A, 5 Gbps | power, USB 2 data lines and SuperSpeed data lines, each separately | BrainStem API (Windows). Industrial price. Can also drop only the data lines, like a badly seated card. |
| Acroname USBHub3c | USB-C, 10 Gbps | per-port data and power | for fast CFexpress readers and SSDs at full speed |
| MCCI Connection Exerciser (2101 Type-A 5 Gbps, 2301 Type-A 10 Gbps; Type-C: 3101, 3102, 3201) | 2101/2301: one device; 3101: four devices, one connected at a time | the whole connection (2101: mechanical relays) | the most realistic "cable pulled"; made for driver plug/unplug testing; scripting support for Windows. About US$1,000 and up (3201). |
| Cambrionix hubs | many ports | per-port charge / sync / off | aimed at phone fleets; check a model has USB 3 data before buying |
| Cheap hubs that `uhubctl` supports | | | **do not work on Windows** (its USB driver refuses the requests); Linux/macOS only |

A 5 Gbps hub caps transfer speed below a fast SSD, so speed runs use the machine's own ports and the hub is only for
the fault checks.

### Machines

A spread matters more than a count:
1. a desktop PC;
2. a laptop with a built-in SD slot;
3. a Windows on Arm laptop (the arm64 build ships but has never touched real hardware);
4. an older Windows 10 machine with USB 2 ports.

Each gets the kit folder (the published exes from `publish\app-<rid>` and `publish\cli-<rid>`, plus `tools\`),
copied from a USB stick. The kit runs in an **elevated** PowerShell (formatting and drive-letter changes need it).
The kit runs alone and the bundle is brought back.

## Safety rules for the kit

Test machines often hold real footage too, so the kit is built around not touching anything it wasn't given:
- A device is only used after **registration**: the operator confirms its role, the kit records its disk serial
  number, and writes a marker file (`IVAR-TEST-DEVICE.json`: role, serial, registration date) to its root.
- Before formatting or writing, the kit checks all three: the disk serial is registered, the marker is on the volume
  and matches, and the disk is **not** the system or boot disk. Any mismatch stops the run.
- Formatting is only ever done by drive letter after that check, never by disk number alone.
- Test jobs use `IVAROFFLOAD_DATA` pointed at the kit's work folder, so they stay out of the recent-jobs list.

## What runs unattended

1. **Registration** (once per machine): list every disk with serial, bus type (USB / SD / NVMe / SATA), model, reader
   chipset and driver, and the drive type Windows reports (Removable / Fixed). The operator confirms roles.
2. **Machine profile**: processor type (x64 / Arm64), Windows build, RAM, USB controllers, Defender real-time state,
   BitLocker state of each destination.
3. **Card preparation**: format (exFAT / FAT32) and fill with `tools\New-SampleIngest.ps1` (a clip of 4 GB or more on
   exFAT only) and with the real card layouts collected by the camera session (below).
4. **CLI suite** for each combination of card x reader x destination x file system:
   - Sort (same drive on the card, and card to laptop), Verify, Undo; checked with the existing independent oracle,
     plus `attrib` and timestamps (exFAT 10 ms, FAT32 2 s);
   - Backup to DEST-1 + DEST-2: green result, `ascmhl verify` on both;
   - transfer speed per reader and destination.
5. **Fault injection**: the kit follows the job's journal and triggers at a set point:
   - hub cuts READER power mid-copy: the job halts, nothing skipped; power back on, Resume finishes, card unchanged;
   - hub cuts DEST-2 mid-backup: DEST-1 finishes; Resume completes DEST-2;
   - **drive-letter change** (`Set-Partition -NewDriveLetter`) between stop and Resume: the banner / resume names the
     new letter; the letter restored, Resume finishes; Undo after a letter change finds the card by serial;
   - **another card at the same letter** (two readers, letters swapped): the job halts and never works on the other
     card;
   - a sorted folder moved to another folder on DEST-1: Undo finds it again (or says why not).

   Without a hub, Windows can fake a removal (disabling the device, or forcing a dismount). The first run on real
   readers checks whether that behaves like a real pull before any result relies on it.
6. **GUI on real drives** with `tools\GuiDriver.ps1` and without the `IVAROFFLOAD_TEST_CARD` hook: the card buttons
   appear with the right camera make, the drive line under the box, the memory-card question in Sort, a
   write-protected card refused by Sort.
7. **Field conditions** worth one run each: Explorer AutoPlay opening the card, Defender scanning during a copy, the
   laptop going to sleep during a long backup, a BitLocker To Go destination.

## What needs a hand (about 20-30 minutes per machine)

A guided script says what to do, beeps, and waits until it detects the event:
- write-protect switch on: Sort refuses it, Backup reads it normally;
- a card pulled from the **internal slot** mid-copy (no hub can reach it), and once from a USB reader by hand, to
  confirm the hub cut behaves the same as a real pull;
- the card moved to the other reader or port before Resume;
- DJI X7 / CineSSD footage, if available (each clip folder one "CinemaDNG clip", real folder and frame names).

### Camera session (#3)

Per camera, the script guides and records:
1. format the card in the camera, shoot a few photos and clips;
2. the kit runs the backup;
3. shoot two more, delete the last shot in the camera, shoot one more, rate or protect an earlier shot;
4. the kit runs `backup --top-up --list`, records every `REPLACE` line, whether the number was reused and whether a
   top-up was offered, then runs it with `--yes`;
5. a **card snapshot** before and after: the folder tree with names, sizes and dates, no media content.

The snapshots feed the index-file lists in `src\IvarOffload.Core\Backup\EarlierBackups.cs` (`IndexFileNames`,
`IndexExtensions`, `IndexFolders`) and become card layouts for `tools\New-SampleIngest.ps1`, so every camera tested
once stays tested.

## Results

Each run writes a bundle (zip): machine profile, device list, per-check pass/fail mapped to the checkboxes of #3
and #4, the job logs (`_IVAROffload`), screenshots, speeds. `Merge-HardwareResults.ps1` combines the bundles into one
table per issue checkbox, and each failure becomes an issue of its own.

## Scripts to build (`tools\hw\`)

| Script | Does |
|---|---|
| `Register-TestDevices.ps1` | registration and machine profile, writes the markers |
| `Invoke-HardwareSuite.ps1` | card preparation, CLI suite, fault injection, GUI checks (unattended) |
| `Invoke-GuidedChecks.ps1` | the hand checks, with prompts and event detection |
| `Invoke-CameraCheck.ps1` | the #3 camera session |
| `Save-CardSnapshot.ps1` | folder tree of a card without its media |
| `Merge-HardwareResults.ps1` | combines bundles from all machines into one table per issue checkbox |
| `HubControl.ps1` | one interface for the hub (YKUSH3 first), with a by-hand fallback |

They reuse the oracle, the sample generator and the GUI driver already in `tools\`.

## Before starting

- [ ] Switchable hub bought (YKUSH3 recommended).
- [ ] Test cards and two bus-powered destination drives that may be wiped.
- [ ] Machines chosen (see above).
- [ ] Shared folder for the result bundles.
- [ ] Release build published (`publish\app-<rid>`, `publish\cli-<rid>`) and `ascmhl` installed on each machine.
