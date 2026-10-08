<#
.SYNOPSIS
  Drives the real IVAR Offload window with UI Automation on generated samples and checks the results with the
  independent oracle. Screenshots of each step are saved to -ShotFolder.

.DESCRIPTION
  Opens windows on the desktop: run it on a test PC, not while working. Only the app processes this script starts
  are closed. The memory-card question is tested without a card through the test hook IVAROFFLOAD_TEST_CARD.
#>
param(
    [string] $WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'ivar-offload-gui'),
    [string] $OtherVolumeRoot,
    [string] $Exe = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\bin\Release\net10.0-windows\IVAR Offload.exe'),
    [string] $ShotFolder = (Join-Path $WorkRoot 'screenshots'),
    # Runs only the scenarios whose name contains this text.
    [string] $Only
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GuiDriver.ps1')
$Exe = [IO.Path]::GetFullPath($Exe)
$src = Join-Path $WorkRoot 'INGEST'
$appData = Join-Path $WorkRoot 'appdata'
New-Item -ItemType Directory -Force -Path $WorkRoot, $ShotFolder | Out-Null
$results = New-Object System.Collections.Generic.List[string]
$started = New-Object System.Collections.Generic.List[int]

function New-Sample([int] $LargeMB = 256) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-SampleIngest.ps1') -Path $src -LargeFileMB $LargeMB -Force | Out-Null
}
function Reset-Folder([string] $path) {
    # With the \\?\ prefix: Windows PowerShell's Remove-Item fails on the sample's paths of more than 260 characters.
    if (Test-Path -LiteralPath $path) { cmd /c "attrib -r -h -s `"$path\*`" /s /d" 2>$null | Out-Null; [IO.Directory]::Delete("\\?\$path", $true) }
}
function Assert-Oracle([string] $Target, [hashtable] $Extra = @{}) {
    $out = & (Join-Path $PSScriptRoot 'Test-SampleIngest.ps1') -Source $src -Target $Target -Mode videos @Extra
    if ($LASTEXITCODE -ne 0) { throw ($out -join "`n") }
    $out | Select-Object -First 1
}
# The sample's videos (and their companions): what a sort moves and an undo must bring back.
function Get-SampleVideos { @(Import-Csv -LiteralPath "$src.manifest.csv" | Where-Object Expect -eq 'video') }
function Shot($p, [string] $name) { Save-Screenshot $p (Join-Path $ShotFolder "$name.png") | Out-Null }
# The result card is collapsed (invisible to UI Automation) while a job runs, so "not there yet" is not an error.
function Result-Title($root) { try { Get-Text $root 'ResultTitle' -TimeoutMs 300 } catch { '' } }
# After a sort the source is checked again in the background; the buttons of the result card wait for it.
function Wait-Checked($root) { Open-More $root 'MoreButton'; Wait-Until { (Find-Element $root 'VerifyButton').Current.IsEnabled } 120000 'the check of the source after the job' }

# Starts the app. The Backup tab is shown first; the sort scenarios switch to the Sort tab (-Backup keeps Backup).
function Start-App([switch] $Backup) {
    $p = Start-Gui $Exe $appData
    $started.Add($p.Id)
    if (-not $Backup) {
        $r = Get-Root $p
        Select-Item $r 'SortTab'
        Wait-Until { Test-Visible $r 'ScanButton' } 5000 'the Sort tab'
    }
    $p
}

# A backup's copy must be the card, bit for bit, with its dates: an independent check with SHA-256. Through .NET with the
# \\?\ prefix: Windows PowerShell's Get-ChildItem and Get-FileHash fail on the sample's paths of more than 260 characters.
function Assert-BackupCopy([string] $Card, [string] $Copy) {
    $files = @([IO.Directory]::EnumerateFiles("\\?\$Card", '*', [IO.SearchOption]::AllDirectories))
    $sha = [Security.Cryptography.SHA256]::Create()
    function Get-Sha([string] $path) {
        $s = [IO.File]::OpenRead($path)
        try { [BitConverter]::ToString($sha.ComputeHash($s)) } finally { $s.Dispose() }
    }
    try {
        foreach ($f in $files) {
            $rel = $f.Substring("\\?\$Card".Length).TrimStart('\')
            $c = "\\?\" + (Join-Path $Copy $rel)
            if (-not [IO.File]::Exists($c)) { throw "missing in the backup: $rel" }
            if ((Get-Sha $f) -ne (Get-Sha $c)) { throw "content differs: $rel" }
            if ([IO.File]::GetLastWriteTimeUtc($c) -ne [IO.File]::GetLastWriteTimeUtc($f) -or [IO.File]::GetCreationTimeUtc($c) -ne [IO.File]::GetCreationTimeUtc($f)) { throw "dates differ: $rel" }
        }
    } finally { $sha.Dispose() }
    "$($files.Count) files identical"
}
# The backup folder's name, as "Saved as" shows it under the name (the folder on the first backup drive).
function Get-FolderName($root) { Split-Path -Leaf ((Get-Text $root 'BackupWhereLine') -split "`n")[0] }
function Backup-Title($root) { try { Get-Text $root 'BackupResultTitle' -TimeoutMs 300 } catch { '' } }
function Start-BackupPreview($root, [string[]] $Destinations) {
    Set-Text $root 'BackupSourceBox' $src
    Start-Sleep -Milliseconds 800
    for ($i = 0; $i -lt $Destinations.Count; $i++) {
        if ($i -gt 0) { Invoke-Button $root 'BackupAddDestination'; Start-Sleep -Milliseconds 300 }
        Set-Text $root "BackupDest$($i + 1)Box" $Destinations[$i]
        Start-Sleep -Milliseconds 800
    }
    # Destinations remembered from an earlier backup, beyond the ones this scenario wants, are removed.
    for ($n = 3; $n -gt $Destinations.Count; $n--) {
        if (Test-Visible $root "BackupDest${n}Box") { Invoke-Button $root "BackupRemoveDest$n"; Start-Sleep -Milliseconds 300 }
    }
    $script:BackupName = Get-FolderName $root   # the setup shrinks to one line after the scan
    Invoke-Button $root 'BackupScanButton'
    Wait-Until { Test-Visible $root 'BackupStartButton' } 60000 'the backup preview'
}

function Start-Preview($root, [string] $target) {
    Set-Text $root 'SourceBox' $src
    Start-Sleep -Milliseconds 800   # the text box commits after a short typing delay
    Set-Text $root 'TargetBox' $target
    Start-Sleep -Milliseconds 800
    Invoke-Button $root 'ScanButton'
    Wait-Until { Test-Visible $root 'ConfirmButton' } 60000 'the preview'
}

function Scenario([string] $Name, [scriptblock] $Body) {
    if ($Only -and $Name -notlike "*$Only*") { return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = & $Body; $line = "PASS  $Name ($([int]$sw.Elapsed.TotalSeconds)s) $detail" }
    catch { $line = "FAIL  ${Name}: $($_.Exception.Message)" }
    finally {
        # Only the windows this script opened are closed.
        foreach ($id in $started) { Get-Process -Id $id -ErrorAction SilentlyContinue | Stop-Process -Force }
        $started.Clear()
    }
    Write-Host $line -ForegroundColor $(if ($line.StartsWith('PASS')) { 'Green' } else { 'Red' })
    $results.Add($line)
}

Reset-Folder $appData

Scenario 'the window: IVAR Offload, Backup first, then Sort' {
    $p = Start-App -Backup; $root = Get-Root $p
    if ($root.Current.Name -ne 'IVAR Offload') { throw "window title is '$($root.Current.Name)'" }
    if ((Get-Text $root 'VersionText') -notmatch '^version \d+\.\d+') { throw "no version shown: '$(Get-Text $root 'VersionText')'" }
    if (-not (Test-Selected $root 'BackupTab')) { throw 'the Backup tab is not selected at start' }
    if (-not (Test-Visible $root 'BackupScanButton')) { throw 'the Backup controls are not shown' }
    if (Test-Visible $root 'ScanButton') { throw 'the Sort controls are shown on the Backup tab' }
    Shot $p '00-backup-tab'
    Select-Item $root 'SortTab'
    Wait-Until { Test-Visible $root 'ScanButton' } 5000 'the Sort tab'
    if (Test-Visible $root 'BackupScanButton') { throw 'the Backup controls are shown on the Sort tab' }
    Shot $p '00-sort-tab'
}

Scenario 'backup to two destinations: preview, copy, verified (one drive: never green), verify again, sort this backup' {
    New-Sample -LargeMB 64
    $d1 = Join-Path $WorkRoot 'BACKUP-1'; $d2 = Join-Path $WorkRoot 'BACKUP-2'; Reset-Folder $d1; Reset-Folder $d2
    New-Item -ItemType Directory -Force -Path $d1, $d2 | Out-Null
    $p = Start-App -Backup; $root = Get-Root $p
    Start-BackupPreview $root @($d1, $d2)
    $expected = @([IO.Directory]::EnumerateFiles("\\?\$src", '*', [IO.SearchOption]::AllDirectories)).Count
    $count = Get-Text $root 'BackupFilesCount'
    if ((($count -split ' ')[0] -replace '\D', '') -ne "$expected") { throw "preview shows '$count', expected $expected files" }
    $name = $script:BackupName
    if ($name -notmatch '^\d{6}_INGEST$') { throw "default name: '$name'" }
    if (Test-Path -LiteralPath (Join-Path $d1 $name)) { throw 'the preview created the backup folder - it must not change anything' }
    # Both destinations are on the sample's own drive: said next to Start, and the result is verified but never green.
    if ((Get-Text $root 'BackupStartWarn') -notlike 'Not a separate copy*') { throw "no same-drive line next to Start: '$(Get-Text $root 'BackupStartWarn')'" }
    Shot $p '20-backup-preview'
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { (Backup-Title $root) -ne '' } 180000 'the backup to finish'
    $title = Backup-Title $root
    if ($title -ne 'Copied, but not to separate drives') { throw "title: '$title'" }
    if ((Get-Text $root 'BackupResultLeftLine') -ne ("All {0:N0} files were copied and checked. The card was not changed." -f $expected)) { throw "left line: '$(Get-Text $root 'BackupResultLeftLine')'" }
    if ((Test-Visible $root 'BackupResultGoodIcon') -or -not (Test-Visible $root 'BackupResultWarningIcon')) { throw 'copies on one drive show as green' }
    Shot $p '21-backup-done'
    $a = Assert-BackupCopy $src (Join-Path $d1 $name); Assert-BackupCopy $src (Join-Path $d2 $name) | Out-Null
    Open-More $root 'BackupMoreButton'
    Invoke-Button $root 'BackupVerifyButton'
    Wait-Until { (Backup-Title $root) -like 'Checked again*' } 120000 'check again'
    # Not green (one drive): "Sort this backup" is under More.
    Invoke-Button $root 'BackupSortMoreButton'
    Wait-Until { Test-Selected $root 'SortTab' } 5000 'the Sort tab'
    if ((Get-Text $root 'SourceBox') -ne (Join-Path $d1 $name)) { throw "Sort source: '$(Get-Text $root 'SourceBox')'" }
    $a
}

Scenario 'stop a backup mid-file, then resume it from the banner' {
    New-Sample -LargeMB 64
    $d1 = Join-Path $WorkRoot 'BACKUP-3'; Reset-Folder $d1; New-Item -ItemType Directory -Force -Path $d1 | Out-Null
    $flag = Join-Path $appData 'test-hold.flag'; Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
    $env:IVAROFFLOAD_TEST_HOLD_AT = 'after-copy:3'   # test hook: the backup waits there until the flag file is deleted
    try { $p = Start-App -Backup } finally { Remove-Item Env:\IVAROFFLOAD_TEST_HOLD_AT -ErrorAction SilentlyContinue }
    $root = Get-Root $p
    Start-BackupPreview $root @($d1)
    $name = $script:BackupName
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { Test-Path -LiteralPath $flag } 60000 'the backup to reach the third file'
    Shot $p '22-backup-running'
    Invoke-Button $root 'BackupStopButton'
    Remove-Item -LiteralPath $flag
    Wait-Until { (Backup-Title $root) -like 'Backup stopped*' } 60000 'the backup to stop'
    Shot $p '23-backup-stopped'
    Wait-Until { Test-Visible $root 'BackupResumeButton' } 10000 'the banner for the stopped backup'
    Invoke-Button $root 'BackupResumeButton'
    # The app still has the hold hook: the resumed run stops at the same step again, so let it go on each time.
    Wait-Until { Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue; (Backup-Title $root) -like 'Copied*' } 180000 'the resumed backup to finish'
    if (Test-Visible $root 'BackupResumeButton') { throw 'the banner is still shown after the backup finished' }
    Assert-BackupCopy $src (Join-Path $d1 $name)
}

Scenario 'the same card again: a name pattern, and the preview offers to check it against its backup' {
    New-Sample -LargeMB 8
    $d1 = Join-Path $WorkRoot 'BACKUP-4'; Reset-Folder $d1; New-Item -ItemType Directory -Force -Path $d1 | Out-Null
    $p = Start-App -Backup; $root = Get-Root $p
    Start-BackupPreview $root @($d1)
    $first = $script:BackupName
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { (Backup-Title $root) -like 'Copied*' } 120000 'the first backup to finish'
    Invoke-Button $root 'BackupNewButton'
    Wait-Until { Test-Visible $root 'BackupDescriptionBox' } 5000 'the setup'
    $taken = Get-FolderName $root
    if ($taken -ne "${first}_2") { throw "the name after the first backup is '$taken', expected '${first}_2'" }
    # The name pattern: the name follows it at once, with an example; a wrong pattern is explained and not used.
    Invoke-Button $root 'BackupTemplateLink'
    Wait-Until { Test-Visible $root 'BackupTemplateBox' } 5000 'the name pattern box'
    Set-Text $root 'BackupTemplateBox' '{YYMMDD}_{nope}'
    Start-Sleep -Milliseconds 900
    if ((Get-Text $root 'BackupTemplateError') -notlike '*{nope} is not known*') { throw "no error for an unknown part: '$(Get-Text $root 'BackupTemplateError')'" }
    if ((Get-FolderName $root) -ne $taken) { throw 'a pattern that is not valid changed the name' }
    Set-Text $root 'BackupTemplateBox' 'Shoot {card} {YYYY-MM-DD} {HHMM}'
    Start-Sleep -Milliseconds 900
    $name = Get-FolderName $root
    if ($name -notmatch '^Shoot INGEST \d{4}-\d{2}-\d{2} \d{4}$') { throw "the name did not follow the pattern: '$name'" }
    # The example is made when it is shown, so its minutes may be one later than the name's.
    if ((Get-Text $root 'BackupTemplateExample') -notmatch '^For example: Shoot INGEST \d{4}-\d{2}-\d{2} \d{4}$') { throw "example: '$(Get-Text $root 'BackupTemplateExample')'" }
    Shot $p '24-backup-name-pattern'
    Invoke-Button $root 'BackupScanButton'
    Wait-Until { Test-Visible $root 'BackupStartButton' } 60000 'the preview of the same card'
    if (-not (Test-Visible $root 'BackupTopUpText')) { throw 'the preview does not say the card was already backed up' }
    if (-not (Test-Selected $root 'BackupTopUpChoice')) { throw 'checking against the earlier backup is not chosen' }
    if ((Get-Text $root 'BackupTopUpText') -notlike "Adding to the backup of *$first*no file is missing from it*") { throw "offer: '$(Get-Text $root 'BackupTopUpText')'" }
    if (-not (Test-TextShown $root 'Check all * files')) { throw 'Start does not say it only verifies' }
    Shot $p '25-backup-already-backed-up'
    # A new full backup instead: the folder name from the pattern.
    Select-Radio $root 'BackupFullChoice'
    Start-Sleep -Milliseconds 400
    if ((Get-Text $root 'BackupTopUpText') -notlike "This card was backed up on *$first*It holds every file of the card*") { throw "offer (full): '$(Get-Text $root 'BackupTopUpText')'" }
    if (-not (Test-TextShown $root 'Back up * files to 1 drive')) { throw 'choosing a new full backup did not change Start' }
    if ((Get-Text $root 'BackupStartLine') -notlike "*$name*") { throw "a new full backup does not go to '$name': '$(Get-Text $root 'BackupStartLine')'" }
    # Back to the default pattern, remembered for the scenarios after this one.
    Invoke-Button $root 'BackupChangeSetupButton'
    Invoke-Button $root 'BackupTemplateReset'
    Start-Sleep -Milliseconds 300
    if ((Get-FolderName $root) -ne $taken) { throw "the name after Reset is '$(Get-FolderName $root)'" }
    # A description is added to the end of the name, and taken off again.
    Set-Text $root 'BackupDescriptionBox' 'Kebnekaise Flight'
    Start-Sleep -Milliseconds 600
    if ((Get-FolderName $root) -notlike '*_INGEST_Kebnekaise Flight') { throw "the name with a description is '$(Get-FolderName $root)'" }
    Set-Text $root 'BackupDescriptionBox' ''
    Start-Sleep -Milliseconds 600
    $name
}

Scenario 'the same card shot on since: add the new files to its backup, verify the whole card' {
    New-Sample -LargeMB 8
    $shot = Join-Path $src 'DCIM\100TEST\IMG_0001.JPG'   # deleted in camera later, and its number used again
    New-Item -ItemType Directory -Force -Path (Split-Path $shot) | Out-Null
    Set-Content -LiteralPath $shot -Value 'the first shot' -NoNewline
    $d1 = Join-Path $WorkRoot 'BACKUP-5'; Reset-Folder $d1; New-Item -ItemType Directory -Force -Path $d1 | Out-Null
    $p = Start-App -Backup; $root = Get-Root $p
    Start-BackupPreview $root @($d1)
    $name = $script:BackupName
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { (Backup-Title $root) -like 'Copied*' } 120000 'the first backup to finish'
    # Shot on without formatting: two new files, and a new shot under the number of one deleted in camera.
    New-Item -ItemType Directory -Force -Path (Join-Path $src 'DCIM\NEW') | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $src 'DCIM\NEW\TOPUP_0001.JPG'), [byte[]](1..20000 | ForEach-Object { $_ % 251 }))
    [IO.File]::WriteAllBytes((Join-Path $src 'DCIM\NEW\TOPUP_0002.MP4'), [byte[]](1..90000 | ForEach-Object { $_ % 241 }))
    Set-Content -LiteralPath $shot -Value 'another shot with the same number' -NoNewline
    (Get-Item -LiteralPath $shot).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(5)

    Invoke-Button $root 'BackupNewButton'
    Wait-Until { Test-Visible $root 'BackupScanButton' } 5000 'the setup'
    Invoke-Button $root 'BackupScanButton'
    Wait-Until { Test-Visible $root 'BackupStartButton' } 60000 'the preview'
    if (-not (Test-Visible $root 'BackupTopUpText')) { throw 'no offer to add to the earlier backup' }
    if (-not (Test-Selected $root 'BackupTopUpChoice')) { throw 'adding to the earlier backup is not chosen by default' }
    $offer = Get-Text $root 'BackupTopUpText'
    if ($offer -notlike "Adding to the backup of *$name*2 new files are copied, 1 changed file is copied again*") { throw "offer: '$offer'" }
    if (-not (Test-TextShown $root 'Add 3 files, check all *')) { throw 'Start does not say what it adds' }
    if ((Get-Text $root 'BackupStartLine') -notlike "Adding to: *$name*") { throw "line above Start: '$(Get-Text $root 'BackupStartLine')'" }
    Shot $p '26-backup-top-up-offer'
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { (Backup-Title $root) -like 'Copied*' } 120000 'the top-up to finish'
    $title = Backup-Title $root
    # The sample is on the drive it is backed up to: verified, but not a separate copy (never green).
    $left = Get-Text $root 'BackupResultLeftLine'
    if ($title -ne 'Copied, but not to separate drives' -or $left -notlike '2 new files copied, 1 copied again, all * files checked*') { throw "result: '$title' / '$left'" }
    if (-not (Test-TextShown $root '*1 photo or clip changed on the card since the earlier backup (usually another shot with a reused number)*')) { throw 'the result does not name the changed shot' }
    Shot $p '27-backup-top-up-done'
    $copy = Join-Path $d1 $name
    $same = Assert-BackupCopy $src $copy
    $earlier = Join-Path $copy 'DCIM\100TEST\IMG_0001 (earlier).JPG'
    if (-not (Test-Path -LiteralPath $earlier) -or (Get-Content -LiteralPath $earlier -Raw) -ne 'the first shot') { throw 'the earlier shot is not next to the new one' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $copy '_IVAROffload') -Filter '*.backup.jsonl').Count -ne 2) { throw 'the top-up did not write a log of its own' }
    Open-More $root 'BackupMoreButton'
    Invoke-Button $root 'BackupVerifyButton'
    Wait-Until { (Backup-Title $root) -like 'Checked again*' } 120000 'check again'
    $same
}

Scenario 'preview, confirm, verify, remove empty folders (same drive)' {
    New-Sample; $target = "$src-Video"; Reset-Folder $target
    $p = Start-App; $root = Get-Root $p
    Shot $p '01-start'
    Set-Text $root 'SourceBox' $src
    Start-Sleep -Milliseconds 900
    $suggested = Get-Text $root 'TargetBox'
    if ($suggested -ne $target) { throw "target was not suggested (got '$suggested')" }
    Invoke-Button $root 'ScanButton'
    Wait-Until { Test-Visible $root 'ConfirmButton' } 60000 'the preview'
    Shot $p '02-preview'
    $expected = (Get-SampleVideos).Count
    $count = Get-Text $root 'MoveCount'
    if ((($count -split ' ')[0] -replace '\D', '') -ne "$expected") { throw "preview shows '$count', expected $expected files" }
    if ((Get-Text $root 'DestinationLine') -notlike "*$target*") { throw "the line above Confirm does not name the target: '$(Get-Text $root 'DestinationLine')'" }
    if (Test-Path -LiteralPath $target) { throw 'the preview created the target folder - it must not change anything' }
    Invoke-Button $root 'ConfirmButton'
    Wait-Until { (Result-Title $root) -like 'Done*' } 120000 'the move to finish'
    Wait-Checked $root
    if (-not (Test-Visible $root 'ResultGoodIcon')) { throw "the result is not green: $(Result-Title $root) / $(Get-Text $root 'ResultLeftLine')" }
    if ((Get-Text $root 'ResultLeftLine') -notlike 'Left in the source: nothing that should move*') { throw "left line: '$(Get-Text $root 'ResultLeftLine')'" }
    Shot $p '03-done'
    Assert-Oracle $target | Out-Null
    Invoke-Button $root 'VerifyButton'
    Wait-Until { (Result-Title $root) -like 'Verified*' } 120000 'verification'
    Shot $p '04-verified'
    Open-More $root 'MoreButton'
    Invoke-Button $root 'RemoveEmptyButton'
    Start-Sleep -Milliseconds 500
    Confirm-Dialog $p 1 | Out-Null
    Wait-Until { (Get-Text $root 'StatusText') -like 'Removed*' } 10000 'empty folders to be removed'
    foreach ($gone in 'Stills\240111-FX3-Flat', 'Stills\240110-A7RIII-Portrait\PRIVATE', 'Video.Conflict1\240115 Ski Trip -VIDEO') {
        if (Test-Path -LiteralPath (Join-Path $src $gone)) { throw "empty folder was not removed: $gone" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $src 'Stills\240101-Mavic2-Coast-2\MISC\GIS'))) { throw 'a non-empty folder was removed' }
    Shot $p '05-cleaned'
    Assert-Oracle $target
}

Scenario 'a locked file: the result says it is still in the source, then Resume moves it' {
    New-Sample; $target = "$src-Video"; Reset-Folder $target
    $locked = Join-Path $src 'Edge cases\two-chunks.mov'
    $p = Start-App; $root = Get-Root $p
    Start-Preview $root $target
    $handle = [IO.File]::Open($locked, 'Open', 'Read', 'None')   # like a player or sync tool holding the file
    try {
        Invoke-Button $root 'ConfirmButton'
        Wait-Until { (Result-Title $root) -like 'Not finished*' } 180000 'the move to finish'
        Wait-Checked $root
        $title = Result-Title $root
        $left = Get-Text $root 'ResultLeftLine'
        if ($title -notlike 'Not finished - 1 video is still in the source*') { throw "title: '$title'" }
        if ($left -notlike 'Left in the source: 1 video (*') { throw "left line: '$left'" }
        if ((Test-Visible $root 'ResultGoodIcon') -or -not (Test-Visible $root 'ResultWarningIcon')) { throw 'the result is not amber' }
        Invoke-Button $root 'ShowStillInSourceButton'
        Wait-Until { (Get-Text $root 'MoveTab') -like 'Still in the source (1)*' } 5000 "the list of files still in the source (tab: '$(Get-Text $root 'MoveTab')')"
        Shot $p '11-locked-file-still-in-source'
    } finally { $handle.Dispose() }
    Invoke-Button $root 'ResumeButton'
    Wait-Until { (Result-Title $root) -like 'Done*' } 120000 'the resumed move to finish'
    Wait-Checked $root
    if (-not (Test-Visible $root 'ResultGoodIcon')) { throw "after resume the result is not green: $(Get-Text $root 'ResultLeftLine')" }
    Assert-Oracle $target
}

Scenario 'undo from the result card puts every file back' {
    New-Sample; $target = "$src-Video"; Reset-Folder $target
    $p = Start-App; $root = Get-Root $p
    Start-Preview $root $target
    Invoke-Button $root 'ConfirmButton'
    Wait-Until { (Result-Title $root) -like 'Done*' } 120000 'the move to finish'
    Wait-Checked $root
    Assert-Oracle $target | Out-Null
    Invoke-Button $root 'UndoButton'
    $question = Confirm-Dialog $p 1 10000   # "Move ... back to ...?" - OK
    Wait-Until { (Result-Title $root) -like 'Undone*' } 120000 "the undo to finish (asked: $question)"
    Shot $p '12-undone'
    if (Test-Visible $root 'UndoButton') { throw 'an undone sort still offers "Undo this sort"' }
    Assert-Oracle $target @{ ExpectKeptInSource = @(Get-SampleVideos | ForEach-Object Rel) }
}

Scenario 'the memory-card question: Back up first opens Backup with the card, Sort anyway sorts' {
    New-Sample; $target = "$src-Video"; Reset-Folder $target
    $env:IVAROFFLOAD_TEST_CARD = 'F: TEST_CARD'   # test hook: the source is flagged as a memory card
    try { $p = Start-App } finally { Remove-Item Env:\IVAROFFLOAD_TEST_CARD -ErrorAction SilentlyContinue }
    $root = Get-Root $p
    Start-Preview $root $target
    # The card warning sits next to Confirm (never scrolled away below the other messages); the target is on the "card" too.
    if ((Get-Text $root 'CardLine') -notlike '*memory card or camera drive (F: TEST_CARD)*nothing leaves the card*') { throw "card line: '$(Get-Text $root 'CardLine')'" }
    Invoke-Button $root 'ConfirmButton'
    $dialog = Find-Window $p 'ChoiceDialog'
    # The question says to back the card up first; the safe answer opens the Backup tab with the card chosen.
    $message = Get-Text $dialog 'ChoiceMessage'
    if ($message -notlike '*memory card or camera drive (F: TEST_CARD)*Back the card up to another drive first, then sort the backup*' -or $message -like '*coming soon*') { throw "question: '$message'" }
    Shot $p '13-card-question'
    Invoke-Button $dialog 'ChoicePrimaryButton'   # Back up this card first (the safe default)
    Wait-Until { Test-Selected $root 'BackupTab' } 5000 'the Backup tab'
    if ((Get-Text $root 'BackupSourceBox') -ne $src) { throw "the Backup tab did not get the card: '$(Get-Text $root 'BackupSourceBox')'" }
    if (Test-Path -LiteralPath $target) { throw '"Back up this card first" moved files' }
    Select-Item $root 'SortTab'
    Invoke-Button $root 'ConfirmButton'
    Invoke-Button (Find-Window $p 'ChoiceDialog') 'ChoiceSecondaryButton'   # Sort anyway
    Wait-Until { (Result-Title $root) -like 'Done*' } 120000 'the move to finish'
    Wait-Checked $root
    # Never plain green after sorting a card: it still holds everything (the target is on it), so it says to back it up.
    if (Test-Visible $root 'ResultGoodIcon') { throw 'a sorted memory card shows the green result' }
    if ((Get-Text $root 'ResultAlarm') -notlike 'F: TEST_CARD looks like a memory card*copy the card to another drive and check the copy before you format it.') { throw "alarm: '$(Get-Text $root 'ResultAlarm')'" }
    Shot $p '14-card-result'
    Assert-Oracle $target
}

if ($OtherVolumeRoot) {
    Scenario 'stop mid-copy, then resume from the banner (other drive)' {
        New-Sample -LargeMB 3072; $target = Join-Path $OtherVolumeRoot 'INGEST-Video'; Reset-Folder $target
        $p = Start-App; $root = Get-Root $p
        Start-Preview $root $target
        Shot $p '06-preview-other-drive'
        Invoke-Button $root 'ConfirmButton'
        Start-Sleep -Milliseconds 1500
        Shot $p '07-running'
        Invoke-Button $root 'StopButton'
        Wait-Until { (Result-Title $root) -like 'Stopped*' } 60000 'the stop'
        Wait-Until { Test-Visible $root 'ResumeButton' } 5000 'the resume banner'
        if ((Get-Text $root 'StatusText') -notlike '*safe to unplug*') { throw "status after stop: '$(Get-Text $root 'StatusText')'" }
        Shot $p '08-stopped'
        Invoke-Button $root 'ResumeButton'
        Wait-Until { (Result-Title $root) -like 'Done*' } 300000 'the resumed move to finish'
        Shot $p '09-resumed-done'
        Assert-Oracle $target
    }

    Scenario 'app killed mid-copy, relaunched, resumed from the startup banner (other drive)' {
        New-Sample -LargeMB 3072; $target = Join-Path $OtherVolumeRoot 'INGEST-Video'; Reset-Folder $target
        $p = Start-App; $root = Get-Root $p
        Start-Preview $root $target
        Invoke-Button $root 'ConfirmButton'
        Start-Sleep -Milliseconds 2000
        $p | Stop-Process -Force   # hard kill, like a crash or power loss for the app
        Start-Sleep -Milliseconds 500
        $partials = @(Get-ChildItem -LiteralPath $target -Recurse -Force -Filter '*.offload-partial' -ErrorAction SilentlyContinue).Count
        $p = Start-App; $root = Get-Root $p
        Wait-Until { Test-Visible $root 'ResumeButton' } 10000 'the resume banner at startup'
        Shot $p '10-banner-after-crash'
        Invoke-Button $root 'ResumeButton'
        Wait-Until { (Result-Title $root) -like 'Done*' } 300000 'the resumed move to finish'
        Assert-Oracle $target
        "($partials partial file(s) were left by the kill and cleaned up)"
    }
}

Write-Host ''
$results | ForEach-Object { Write-Host $_ }
Write-Host "Screenshots: $ShotFolder"
exit $(if ($results | Where-Object { $_.StartsWith('FAIL') }) { 1 } else { 0 })
