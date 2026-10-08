<#
.SYNOPSIS
  End-to-end tests of the command-line tool on generated sample ingests: normal runs, hard crashes at every step
  of the move sequence followed by resume, random kills, conflicts, locked files, reverse mode and early close,
  a source or target that disappears or is renamed (the job must halt, never skip), undo of a finished sort,
  closing a job whose originals cannot be removed, and a job logged by Ingest Sorter (one of the app's earlier names) in an
  _IngestSorter folder. Also: the receipt is in the source right after a crash; an original that disappears while its
  move is interrupted is never lost (close and resume keep its copy); a job whose source went away mid-file can be
  ended and its moved files undone; undo after the sorted folder and the source folder were renamed (and the source's
  name reused for another card).
  Every scenario on the sample ends with the independent check in Test-SampleIngest.ps1 (the scenarios that change
  files by hand use a small folder of their own and check the files directly).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Test-EndToEnd.ps1 -OtherVolumeRoot D:\temp\offload-e2e
#>
param(
    # Work area for same-drive tests (sample source and target are created here).
    [string] $WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'ivar-offload-e2e'),
    # A folder on a DIFFERENT drive, for copy-verify-delete tests. Omit to skip those scenarios.
    [string] $OtherVolumeRoot,
    [string] $Cli = (Join-Path $PSScriptRoot '..\src\IvarOffload.Cli\bin\Release\net10.0-windows\ivar-offload.exe'),
    [int] $LargeFileMB = 256,
    # Run only scenarios whose name contains one of these strings.
    [string[]] $Only
)

$ErrorActionPreference = 'Stop'
$env:IVAROFFLOAD_DATA = Join-Path $WorkRoot 'appdata'   # keep test jobs out of the user's recent-jobs list
$Cli = [IO.Path]::GetFullPath($Cli)
if (-not (Test-Path -LiteralPath $Cli)) { throw "CLI not found: $Cli (build it with: dotnet build -c Release)" }
$results = New-Object System.Collections.Generic.List[object]
$src = Join-Path $WorkRoot 'INGEST'
$sameTarget = Join-Path $WorkRoot 'INGEST-Target'
$crossTarget = if ($OtherVolumeRoot) { Join-Path $OtherVolumeRoot 'INGEST-Target' } else { $null }
$logFolder = '_IVAROffload'           # where new jobs write their logs (target) and receipts (source)
$legacyLogFolder = '_IngestSorter'   # the same, for jobs made by Ingest Sorter before the rename

function New-Sample([int] $LargeMB = $LargeFileMB) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-SampleIngest.ps1') -Path $src -LargeFileMB $LargeMB -Force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sample generation failed' }
}

function Reset-Folder([string] $path) {
    if (Test-Path -LiteralPath $path) {
        cmd /c "attrib -r -h -s `"$path\*`" /s /d" 2>$null | Out-Null
        # With the \\?\ prefix: Windows PowerShell's Remove-Item fails on the sample's paths of more than 260 characters.
        [IO.Directory]::Delete("\\?\$path", $true)
    }
}

function Invoke-Cli([string[]] $Arguments, [string] $CrashAt) {
    if ($CrashAt) { $env:IVAROFFLOAD_TEST_CRASH_AT = $CrashAt }
    try {
        $ErrorActionPreference = 'Continue'
        $out = & $Cli @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = 'Stop'
        Remove-Item Env:\IVAROFFLOAD_TEST_CRASH_AT -ErrorAction SilentlyContinue
    }
    [pscustomobject]@{ Code = $code; Output = $out; Crashed = [bool]($out -match '\[test\] simulated crash') }
}

function Assert-Oracle([string] $Target, [string] $Mode, [hashtable] $Extra = @{}) {
    # Jobs leave a receipt (and undo jobs their log) in the source's log folder. The oracle knows only the sample's
    # files, so those folders are set aside while it runs.
    $asideFolders = @()
    foreach ($name in $logFolder, $legacyLogFolder) {
        $logs = Join-Path $src $name
        if (-not (Test-Path -LiteralPath $logs)) { continue }
        $aside = Join-Path $WorkRoot "$name-source-logs"
        Reset-Folder $aside
        Move-Item -LiteralPath $logs -Destination $aside
        $asideFolders += , @($aside, $logs)
    }
    try {
        $out = & (Join-Path $PSScriptRoot 'Test-SampleIngest.ps1') -Source $src -Target $Target -Mode $Mode @Extra
        if ($LASTEXITCODE -ne 0) { throw ($out -join "`n") }
        $out | Select-Object -First 1
    } finally {
        foreach ($pair in $asideFolders) { Move-Item -LiteralPath $pair[0] -Destination $pair[1] }
    }
}

# Job logs of a target folder (new and old log folder).
function Get-Journals([string] $Target) {
    @(foreach ($name in $logFolder, $legacyLogFolder) { Get-ChildItem -Path (Join-Path $Target "$name\*.journal.jsonl") -ErrorAction SilentlyContinue })
}

# After an undo every sample file must be back in the source, exactly as it was, and nothing left in the target.
function Assert-AllBack([string] $Target, [string] $Mode) {
    $side = if ($Mode -eq 'videos') { 'video' } else { 'photo' }
    $moving = @(Import-Csv -LiteralPath "$src.manifest.csv" | Where-Object Expect -eq $side | ForEach-Object Rel)
    Assert-Oracle $Target $Mode @{ ExpectKeptInSource = $moving }
}

function Rename-Folder([string] $From, [string] $To) {
    for ($i = 0; ; $i++) {
        try { [IO.Directory]::Move($From, $To); return }
        catch { if ($i -ge 50) { throw }; Start-Sleep -Milliseconds 100 }   # an indexer or scanner may hold it briefly
    }
}

function Get-JournalCount([string] $Target, [string] $Type) {
    $journal = (Get-Journals $Target)[0].FullName
    @(Select-String -LiteralPath $journal -SimpleMatch -Pattern "`"t`":`"$Type`"").Count
}

# Starts the CLI so that it stops at a step and waits there (IVAROFFLOAD_TEST_HOLD_AT), for changing the disk underneath it.
function Start-Held([string[]] $Arguments, [string] $HoldAt) {
    $flag = Join-Path $env:IVAROFFLOAD_DATA 'test-hold.flag'
    Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
    $log = Join-Path $WorkRoot 'held.log'
    $env:IVAROFFLOAD_TEST_HOLD_AT = $HoldAt
    try {
        $argumentLine = ($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
        $p = Start-Process -FilePath $Cli -ArgumentList $argumentLine -WorkingDirectory $WorkRoot -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $null = $p.Handle   # without this, PowerShell loses the exit code
    } finally { Remove-Item Env:\IVAROFFLOAD_TEST_HOLD_AT -ErrorAction SilentlyContinue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $flag)) {
        if ($p.HasExited) { throw "the run ended before reaching $HoldAt (exit $($p.ExitCode)): $(Get-Content $log -Raw)" }
        if ($sw.Elapsed.TotalSeconds -gt 120) { $p.Kill(); throw "hold point $HoldAt was not reached" }
        Start-Sleep -Milliseconds 50
    }
    [pscustomobject]@{ Process = $p; Flag = $flag; Log = $log }
}

function Complete-Held($Held) {
    Remove-Item -LiteralPath $Held.Flag
    if (-not $Held.Process.WaitForExit(300000)) { $Held.Process.Kill(); throw 'the held run did not finish' }
    [pscustomobject]@{ Code = $Held.Process.ExitCode; Output = @(Get-Content -LiteralPath $Held.Log) + @(Get-Content -LiteralPath "$($Held.Log).err") }
}

# The source disappears while the job holds at a step: the job must halt (exit 2) without a single skip,
# and finish exactly after the source is back.
function Test-SourceVanishes([string] $Target, [string] $HoldAt) {
    New-Sample; Reset-Folder $Target
    $away = "$src-unplugged"
    Reset-Folder $away
    $held = Start-Held (Run-Args $Target) $HoldAt
    try { Rename-Folder $src $away } finally { $r = Complete-Held $held }
    # The drive is still there, only the folder went away: the message says so (never "not connected").
    if ($r.Code -ne 2 -or -not ($r.Output -match 'The source folder is missing') -or ($r.Output -match 'not connected')) { Rename-Folder $away $src; throw "expected a halt (exit 2), got exit $($r.Code): $($r.Output -join ' | ')" }
    $skips = Get-JournalCount $Target 'skip'
    Rename-Folder $away $src
    if ($skips -ne 0) { throw "$skips files were recorded as skipped while the source was missing" }
    $r = Invoke-Cli @('resume', '--target', $Target)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Assert-Oracle $Target 'videos'
}

function Test-Undo([string] $Target) {
    New-Sample; Reset-Folder $Target
    $r = Invoke-Cli (Run-Args $Target)
    if ($r.Code -ne 0) { throw "run failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    if (-not (Test-Path -Path (Join-Path $src "$logFolder\*.moved-out.csv"))) { throw 'no receipt was written in the source' }
    $u = Invoke-Cli @('undo', '--target', $Target, '--yes')
    if ($u.Code -ne 0) { throw "undo failed (exit $($u.Code)): $($u.Output -join ' | ')" }
    $j = Invoke-Cli @('jobs', '--target', $Target)
    if (-not ($j.Output -match 'undone \(completed')) { throw "jobs does not show the sort as undone: $($j.Output -join ' | ')" }
    $again = Invoke-Cli @('undo', '--target', $Target, '--yes')
    if ($again.Code -ne 4 -or @($again.Output -match 'already undone').Count -ne 1) { throw "a second undo was not refused once, in words (exit $($again.Code)): $($again.Output -join ' | ')" }
    Assert-AllBack $Target 'videos'
}

# Denies (or allows again) deleting the files of a folder, for the current user.
function Set-DenyDelete([string] $Folder, [bool] $Deny) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $fileRule = New-Object Security.AccessControl.FileSystemAccessRule($me, 'Delete', 'Deny')
    $folderRule = New-Object Security.AccessControl.FileSystemAccessRule($me, 'DeleteSubdirectoriesAndFiles', 'Deny')
    foreach ($f in @(Get-ChildItem -LiteralPath $Folder -File)) {
        $acl = Get-Acl -LiteralPath $f.FullName
        if ($Deny) { $acl.AddAccessRule($fileRule) } else { [void]$acl.RemoveAccessRule($fileRule) }
        Set-Acl -LiteralPath $f.FullName -AclObject $acl
    }
    $acl = Get-Acl -LiteralPath $Folder
    if ($Deny) { $acl.AddAccessRule($folderRule) } else { [void]$acl.RemoveAccessRule($folderRule) }
    Set-Acl -LiteralPath $Folder -AclObject $acl
}

# Copy path, originals that cannot be removed: the run halts with verified copies in place, and 'close' must end the job
# (exit 1: files failed) instead of halting again. The permission is taken away after the plan is written.
function Test-CloseWithOriginalsKept([string] $Target) {
    $roSource = Join-Path $WorkRoot 'RO-SOURCE'
    Reset-Folder $roSource; Reset-Folder $Target
    New-Item -ItemType Directory -Force -Path "$roSource\a" | Out-Null
    $rng = New-Object Random 7
    foreach ($n in 1..4) { $b = New-Object byte[] 50000; $rng.NextBytes($b); [IO.File]::WriteAllBytes("$roSource\a\C000$n.MOV", $b) }
    $held = Start-Held @('run', '--source', $roSource, '--target', $Target, '--yes') 'after-copy-journal:1'
    try {
        try { Set-DenyDelete "$roSource\a" $true } finally { $r = Complete-Held $held }
        if ($r.Code -ne 2 -or -not ($r.Output -match 'The job cannot remove the originals')) { throw "expected a halt (exit 2), got exit $($r.Code): $($r.Output -join ' | ')" }
        $c = Invoke-Cli @('close', '--target', $Target)
        if ($c.Code -ne 1 -or -not ($c.Output -match '^Closed: ')) { throw "close did not end the job (exit $($c.Code)): $($c.Output -join ' | ')" }
    } finally { Set-DenyDelete "$roSource\a" $false }
    if ((Get-JobState $Target) -ne 'ended') { throw 'the job was not ended' }
    $originals = @(Get-ChildItem -LiteralPath "$roSource\a" -Filter *.MOV).Count
    $copies = @(Get-ChildItem -LiteralPath "$Target\a" -Filter *.MOV -ErrorAction SilentlyContinue).Count
    if ($originals -ne 4 -or $copies -ne 2) { throw "expected 4 originals kept and 2 verified copies, found $originals and $copies" }
    Reset-Folder $roSource; Reset-Folder $Target
    'the job ended: 2 verified copies in the target, every original kept'
}

# A small folder of its own (four clips and a photo), for the scenarios that change files by hand.
function New-Clips([string] $Folder) {
    Reset-Folder $Folder
    New-Item -ItemType Directory -Force -Path "$Folder\a", "$Folder\b" | Out-Null
    $rng = New-Object Random 11
    foreach ($rel in 'a\C0001.MOV', 'a\C0002.MOV', 'b\C0003.MOV', 'b\C0004.MOV', 'a\DSC0001.JPG') {
        $b = New-Object byte[] 300000; $rng.NextBytes($b); [IO.File]::WriteAllBytes("$Folder\$rel", $b)
    }
}

function Get-Sha([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Relative path of the file whose move was interrupted (from the job log of a target folder).
function Get-InFlight([string] $Target) {
    $rel = @{}; $stage = @{}
    foreach ($line in Get-Content -LiteralPath (Get-Journals $Target)[0].FullName) {
        $record = $line | ConvertFrom-Json
        if ($record.t -eq 'item') { $rel[[int]$record.i] = $record.rel }
        elseif ($record.t -in 'pre', 'copy', 'copied', 'placed') { $stage[[int]$record.i] = $record.t }
        elseif ($record.t -in 'done', 'skip') { $stage.Remove([int]$record.i) }
    }
    if ($stage.Count -ne 1) { throw "expected one file in flight, found $($stage.Count)" }
    $rel[@($stage.Keys)[0]]
}

# Files under a folder (outside the log folders) with this content, under any name.
function Find-Copies([string] $Folder, [string] $Sha) {
    @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\_(IVAROffload|IVARIngest|IngestSorter)\\' -and (Get-Sha $_.FullName) -eq $Sha })
}

# Copy path: a hard crash at a step, then the original of the file in flight is removed by something else. Neither
# 'close' nor 'resume' may lose its copy, and it is reported as missing - never as still in the source.
function Test-OriginalGone([string] $Target, [string] $CrashAt, [string] $Command) {
    $clips = Join-Path $WorkRoot 'CLIPS'
    New-Clips $clips; Reset-Folder $Target
    $r = Invoke-Cli @('run', '--source', $clips, '--target', $Target, '--yes') $CrashAt
    if (-not $r.Crashed) { throw "crash point $CrashAt not reached (exit $($r.Code))" }
    $rel = Get-InFlight $Target
    $sha = Get-Sha "$clips\$rel"
    Remove-Item -LiteralPath "$clips\$rel" -Force
    $c = Invoke-Cli @($Command, '--target', $Target)
    if ($c.Code -eq 2) { throw "$Command halted: $($c.Output -join ' | ')" }
    $copies = Find-Copies $Target $sha
    if ($copies.Count -eq 0) { throw "the only copy of $rel was deleted ($Command, exit $($c.Code)): $($c.Output -join ' | ')" }
    if (@(Get-ChildItem -LiteralPath $Target -Recurse -Force -Filter '*.offload-partial').Count -ne 0) { throw 'a temporary file was left behind' }
    $summary = Get-Content (Get-ChildItem (Join-Path $Target "$logFolder\*.summary.txt")).FullName -Raw
    $left = if ($summary -match '(?s)Left in the source folder \(\d.*?(\r?\n\r?\n|$)') { $Matches[0] } else { '' }   # that section only
    if ($left -match [regex]::Escape("    $rel  (")) { throw "the summary lists $rel as left in the source:`n$summary" }
    Reset-Folder $clips; Reset-Folder $Target
    "$rel kept as $($copies[0].Name) (exit $($c.Code))"
}

# The source folder goes away (renamed) while a file is being copied: the run halts; 'close' must still end the job
# (keeping the unfinished copy), and once the folder is back the files that moved can be undone.
function Test-CloseWithoutSource([string] $Target) {
    $clips = Join-Path $WorkRoot 'CLIPS'
    New-Clips $clips; Reset-Folder $Target
    $before = @{}; foreach ($f in Get-ChildItem -LiteralPath $clips -Recurse -File) { $before[$f.FullName.Substring($clips.Length + 1)] = Get-Sha $f.FullName }
    $away = "$clips-renamed"; Reset-Folder $away
    $held = Start-Held @('run', '--source', $clips, '--target', $Target, '--yes') 'after-copy:2'
    try { Rename-Folder $clips $away } finally { $r = Complete-Held $held }
    if ($r.Code -ne 2) { Rename-Folder $away $clips; throw "expected a halt (exit 2), got exit $($r.Code): $($r.Output -join ' | ')" }
    try {
        $c = Invoke-Cli @('close', '--target', $Target)
        if ($c.Code -ne 1 -or -not ($c.Output -match '^Closed: ')) { throw "close did not end the job (exit $($c.Code)): $($c.Output -join ' | ')" }
        if ((Get-JobState $Target) -ne 'ended') { throw 'the job was not ended' }
        if (@(Get-ChildItem -LiteralPath $Target -Recurse -Force -Filter '*.unverified-copy').Count -ne 1) { throw 'the unfinished copy was not kept' }
    } finally { Rename-Folder $away $clips }
    $u = Invoke-Cli @('undo', '--target', $Target, '--yes')
    if ($u.Code -ne 0) { throw "undo failed (exit $($u.Code)): $($u.Output -join ' | ')" }
    foreach ($rel in $before.Keys) { if ((Get-Sha "$clips\$rel") -ne $before[$rel]) { throw "$rel is not back exactly" } }
    Reset-Folder $clips; Reset-Folder $Target
    'the job ended; the moved file went back'
}

function Get-JobState([string] $Target) {
    $state = 'none'
    foreach ($j in Get-Journals $Target) {
        $text = [IO.File]::ReadAllText($j.FullName)
        if ($text.Contains('"t":"end"')) { return 'ended' }
        if ($text.Contains('"t":"ready"')) { $state = 'resumable' }
    }
    $state
}

function Run-Args([string] $Target, [string] $Mode = 'videos') { @('run', '--source', $src, '--target', $Target, '--mode', $Mode, '--yes') }

function Scenario([string] $Name, [scriptblock] $Body) {
    if ($Only -and -not ($Only | Where-Object { $Name -like "*$_*" })) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = (& $Body) -join ' '; $ok = $true } catch { $detail = $_.Exception.Message; $ok = $false }
    $results.Add([pscustomobject]@{ Scenario = $Name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}  {1}  ({2:0.0}s)" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
    if (-not $ok) { Write-Host ("      " + ($detail -replace "`n", "`n      ")) -ForegroundColor DarkYellow }
}

# Crash the process at each listed point in turn (first on 'run', then on 'resume'), then resume to the end.
function Test-Crashes([string] $Target, [string] $Mode, [string[]] $Points) {
    New-Sample; Reset-Folder $Target
    $first = $true
    foreach ($point in $Points) {
        $r = if ($first) { Invoke-Cli (Run-Args $Target $Mode) $point } else { Invoke-Cli @('resume', '--target', $Target) $point }
        if (-not $r.Crashed) { throw "crash point '$point' was not reached (exit $($r.Code)): $($r.Output | Select-Object -Last 3)" }
        $first = $false
    }
    $r = Invoke-Cli @('resume', '--target', $Target)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Assert-Oracle $Target $Mode
}

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
if ($OtherVolumeRoot) { New-Item -ItemType Directory -Force -Path $OtherVolumeRoot | Out-Null }
Write-Host "CLI:          $Cli"
Write-Host "Same drive:   $WorkRoot"
Write-Host "Other drive:  $(if ($OtherVolumeRoot) { $OtherVolumeRoot } else { '(skipped)' })"

# ---------------------------------------------------------------------------------------------------------------------
Scenario 'same drive: move videos, verify, check' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget)
    if ($r.Code -ne 0) { throw "exit $($r.Code): $($r.Output -join ' | ')" }
    $v = Invoke-Cli @('verify', '--target', $sameTarget)
    if ($v.Code -ne 0) { throw "verify failed: $($v.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'   # also proves verify left every timestamp alone
}

Scenario 'same drive: running again finds nothing left to move' {
    $r = Invoke-Cli (Run-Args $sameTarget)
    if ($r.Code -ne 4 -or -not ($r.Output -match 'nothing to move')) { throw "expected 'nothing to move' (exit 4), got exit $($r.Code)" }
    'second run correctly found nothing'
}

foreach ($point in 'after-pre:1', 'after-pre:25', 'after-rename:1', 'after-rename:25', 'after-rename:59') {
    Scenario "same drive: crash at $point, resume" { Test-Crashes $sameTarget 'videos' @($point) }
}
Scenario 'same drive: crash twice (after-pre:10, then after-rename:5), resume' { Test-Crashes $sameTarget 'videos' @('after-pre:10', 'after-rename:5') }
Scenario 'same drive: move PHOTOS (reverse mode), crash at after-rename:10, resume' { Test-Crashes $sameTarget 'photos' @('after-rename:10') }

Scenario 'same drive: torn last journal line after a crash' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget) 'after-rename:7'
    if (-not $r.Crashed) { throw 'crash point not reached' }
    $journal = Get-Journals $sameTarget | Select-Object -First 1
    [IO.File]::AppendAllText($journal.FullName, '{"t":"done","i":7,"how":"ren')
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed: $($r.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'
}

Scenario 'same drive: conflicts are skipped, never overwritten' {
    New-Sample; Reset-Folder $sameTarget
    $identical = 'Stills\240101-Mavic2-Coast-2\DCIM\100MEDIA\DJI_0003.MOV'
    $different = 'Stills\240101-Mavic2-Coast-2\DCIM\100MEDIA\DJI_0004.MOV'
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $sameTarget $identical)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $src $identical) -Destination (Join-Path $sameTarget $identical)
    [IO.File]::WriteAllText((Join-Path $sameTarget $different), 'a different file that must not be overwritten')
    $shaIdentical = (Get-FileHash -LiteralPath (Join-Path $sameTarget $identical) -Algorithm SHA256).Hash.ToLowerInvariant()
    $shaDifferent = (Get-FileHash -LiteralPath (Join-Path $sameTarget $different) -Algorithm SHA256).Hash.ToLowerInvariant()
    $r = Invoke-Cli (Run-Args $sameTarget)
    # 5: the different file (held back by the preview, with its clip's companions) is still (only) in the source.
    if ($r.Code -ne 5) { throw "exit $($r.Code): $($r.Output -join ' | ')" }
    $summary = Get-Content (Get-ChildItem (Join-Path $sameTarget "$logFolder\*.summary.txt")).FullName -Raw
    if ($summary -notmatch 'identical copy is already in the target folder' -or ($summary + ($r.Output -join "`n")) -notmatch 'different file') { throw "summary does not explain the conflicts:`n$summary" }
    if ($summary -notmatch 'Left in the source folder \(not part of this sort\)' -or $summary -match 'no files to move|none of the files that this sort planned to move') { throw "summary does not list the files held back:`n$summary" }
    # The clip's thumbnail and screennail in MISC\THM\100 belong to it, so they stay in the source with it.
    $heldWithClip = 'Stills\240101-Mavic2-Coast-2\MISC\THM\100\DJI_0004.THM', 'Stills\240101-Mavic2-Coast-2\MISC\THM\100\DJI_0004.SCR'
    Assert-Oracle $sameTarget 'videos' @{ ExpectKeptInSource = @($identical, $different) + $heldWithClip; PreexistingInTarget = @{ $identical = $shaIdentical; $different = $shaDifferent } }
}

Scenario 'same drive: file locked by another program fails, then succeeds on resume' {
    New-Sample; Reset-Folder $sameTarget
    $locked = Join-Path $src 'Edge cases\two-chunks.mov'
    $handle = [IO.File]::Open($locked, 'Open', 'Read', 'None')
    try {
        $r = Invoke-Cli (Run-Args $sameTarget)
        if ($r.Code -ne 1 -or -not ($r.Output -match 'failed 1')) { throw "expected exactly one failure (exit 1), got exit $($r.Code): $($r.Output | Select-Object -Last 3)" }
    } finally { $handle.Dispose() }
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed: $($r.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'
}

Scenario 'same drive: source vanishes mid-run: halt, reconnect, resume' { Test-SourceVanishes $sameTarget 'after-rename:10' }

Scenario 'same drive: source missing at resume: halt, reconnect, resume' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget) 'after-rename:12'
    if (-not $r.Crashed) { throw 'crash point not reached' }
    $away = "$src-unplugged"; Reset-Folder $away
    Rename-Folder $src $away
    try {
        $r = Invoke-Cli @('resume', '--target', $sameTarget)
        if ($r.Code -ne 2 -or -not ($r.Output -match 'The source folder is missing')) { throw "expected a halt (exit 2), got exit $($r.Code): $($r.Output -join ' | ')" }
        if ((Get-JournalCount $sameTarget 'skip') + (Get-JournalCount $sameTarget 'fail') -ne 0) { throw 'files were skipped or failed while the source was missing' }
    } finally { Rename-Folder $away $src }
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'
}

Scenario 'same drive: target renamed before resume: halt, nothing recreated' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget) 'after-rename:12'
    if (-not $r.Crashed) { throw 'crash point not reached' }
    $renamed = "$sameTarget renamed"; Reset-Folder $renamed
    Rename-Folder $sameTarget $renamed
    try {
        $r = Invoke-Cli @('resume', '--target', $renamed)
        if ($r.Code -ne 2 -or -not ($r.Output -match 'its log is now in')) { throw "expected a halt (exit 2), got exit $($r.Code): $($r.Output -join ' | ')" }
        if (Test-Path -LiteralPath $sameTarget) { throw 'the old target folder was recreated' }
    } finally { Rename-Folder $renamed $sameTarget }
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'
}

Scenario 'same drive: undo after a run puts every file back' { Test-Undo $sameTarget }

# The source is checked again after a run, as the app does: an online-only clip (a cloud placeholder that is never
# read) keeps the exit code at 5, is named in the output, and is in the job's summary.
Scenario 'same drive: an online-only clip keeps exit 5 and is in the summary' {
    $clips = Join-Path $WorkRoot 'CLIPS'; $target = Join-Path $WorkRoot 'CLIPS-Target'
    New-Clips $clips; Reset-Folder $target
    $placeholder = Get-Item -LiteralPath "$clips\b\C0004.MOV"
    $placeholder.Attributes = $placeholder.Attributes -bor [IO.FileAttributes]::Offline
    $r = Invoke-Cli @('run', '--source', $clips, '--target', $target, '--yes')
    if ($r.Code -ne 5) { throw "expected exit 5, got $($r.Code): $($r.Output -join ' | ')" }
    if (-not ($r.Output -match [regex]::Escape('STILL IN SOURCE b\C0004.MOV'))) { throw "the online-only clip is not named: $($r.Output -join ' | ')" }
    $s = Invoke-Cli @('status', '--target', $target)
    if (-not ($s.Output -match 'In the source folder at the check after the job') -or -not ($s.Output -match [regex]::Escape('b\C0004.MOV'))) {
        throw "the summary does not list the online-only clip: $($s.Output -join ' | ')"
    }
    # "Verify again" speaks for the moved files, and checks the source again too: it never says all is well meanwhile.
    $v = Invoke-Cli @('verify', '--target', $target)
    if ($v.Code -ne 1 -or -not ($v.Output -match [regex]::Escape('STILL IN SOURCE b\C0004.MOV'))) { throw "verify: expected exit 1 naming the clip, got $($v.Code): $($v.Output -join ' | ')" }
    $placeholder.Attributes = $placeholder.Attributes -band -bnot [IO.FileAttributes]::Offline
    Reset-Folder $clips; Reset-Folder $target
    'exit 5, verify exit 1; the clip is named in the output and in the summary'
}

# A memory card or camera drive still holds the only copy of what stayed: as in the app (never green), a sort of one
# never ends with exit 0.
Scenario 'same drive: a sorted memory card never exits 0' {
    $clips = Join-Path $WorkRoot 'CLIPS'; $target = Join-Path $WorkRoot 'CLIPS-Target'
    New-Clips $clips; Reset-Folder $target
    $env:IVAROFFLOAD_TEST_CARD = 'F: TEST_CARD'
    try { $r = Invoke-Cli @('run', '--source', $clips, '--target', $target, '--yes') } finally { Remove-Item Env:\IVAROFFLOAD_TEST_CARD }
    if ($r.Code -ne 5 -or -not ($r.Output -match 'looks like a memory card or camera drive')) { throw "expected exit 5 with the card line, got $($r.Code): $($r.Output -join ' | ')" }
    Reset-Folder $clips; Reset-Folder $target
    'exit 5, with the card line'
}

# "End job" in the middle of a CinemaDNG clip: on their own, the frames left behind look like photos. A new run of the
# same folder (the command line's "Move the remaining") moves them, as the app does.
Scenario 'same drive: after a job ended mid-clip, running again moves the rest of the clip' {
    $clips = Join-Path $WorkRoot 'DNG'; $target = Join-Path $WorkRoot 'DNG-Target'
    Reset-Folder $clips; Reset-Folder $target
    New-Item -ItemType Directory -Force -Path "$clips\A001_C003" | Out-Null
    $rng = New-Object Random 5
    foreach ($name in (0..11 | ForEach-Object { 'A001_C003_{0:D6}.dng' -f $_ }) + 'A001_C003.wav') {
        $b = New-Object byte[] 3000; $rng.NextBytes($b); [IO.File]::WriteAllBytes("$clips\A001_C003\$name", $b)
    }
    $r = Invoke-Cli @('run', '--source', $clips, '--target', $target, '--yes') 'after-rename:9'
    if (-not $r.Crashed) { throw "crash point not reached (exit $($r.Code))" }
    $c = Invoke-Cli @('close', '--target', $target)
    if ($c.Code -ne 5) { throw "close: expected exit 5, got $($c.Code): $($c.Output -join ' | ')" }
    $again = Invoke-Cli @('run', '--source', $clips, '--target', $target, '--yes')
    if ($again.Code -ne 0) { throw "the second run left something (exit $($again.Code)): $($again.Output -join ' | ')" }
    $moved = @(Get-ChildItem -LiteralPath "$target\A001_C003" -File).Count
    if ($moved -ne 13 -or @(Get-ChildItem -LiteralPath "$clips\A001_C003" -File -ErrorAction SilentlyContinue).Count -ne 0) { throw "expected the whole clip (13 files) in the target, found $moved" }
    Reset-Folder $clips; Reset-Folder $target
    'the second run moved the 4 files the ended job left'
}

Scenario 'same drive: a hard crash leaves the receipt in the source and the reports next to the log' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget) 'after-rename:12'
    if (-not $r.Crashed) { throw 'crash point not reached' }
    if (-not (Test-Path -Path (Join-Path $src "$logFolder\*.moved-out.csv"))) { throw 'no receipt in the source after the crash' }
    $receipt = Get-Content (Get-ChildItem (Join-Path $src "$logFolder\*.moved-out.txt")).FullName -Raw
    if ($receipt -notmatch 'The job is not finished') { throw "the receipt does not say the job is unfinished:`n$receipt" }
    foreach ($suffix in 'manifest.csv', 'summary.txt') { if (-not (Test-Path -Path (Join-Path $sameTarget "$logFolder\*.$suffix"))) { throw "no $suffix next to the log after the crash" } }
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos'
}

# The sorted folder is renamed (Card-Video -> Smith-Video), the source folder too (Card -> Smith), and a new folder
# with the source's old name holds the next card. The receipt still finds the job, and the undo puts the files back into
# the renamed folder - never into the new one.
Scenario 'same drive: undo after both folders were renamed goes to the renamed source, not the new folder' {
    $clips = Join-Path $WorkRoot 'CARD'; $sorted = Join-Path $WorkRoot 'CARD-Video'
    $smith = Join-Path $WorkRoot 'Smith'; $smithVideo = Join-Path $WorkRoot 'Smith-Video'
    New-Clips $clips; foreach ($f in $sorted, $smith, $smithVideo) { Reset-Folder $f }
    $before = @{}; foreach ($f in Get-ChildItem -LiteralPath $clips -Recurse -File) { $before[$f.FullName.Substring($clips.Length + 1)] = Get-Sha $f.FullName }
    $r = Invoke-Cli @('run', '--source', $clips, '--target', $sorted, '--yes')
    if ($r.Code -ne 0) { throw "run failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Rename-Folder $sorted $smithVideo
    Rename-Folder $clips $smith
    New-Item -ItemType Directory -Force -Path "$clips\a" | Out-Null
    [IO.File]::WriteAllText("$clips\a\C0001.MOV", 'the next card')
    $receipt = (Get-ChildItem (Join-Path $smith "$logFolder\*.moved-out.txt")).FullName
    $j = Invoke-Cli @('jobs', '--target', $smith)
    if (-not ($j.Output -match ' completed ') -or ($j.Output -match 'not connected')) { throw "jobs does not find the sort: $($j.Output -join ' | ')" }
    $u = Invoke-Cli @('undo', '--journal', $receipt, '--yes')
    if ($u.Code -ne 0) { throw "undo failed (exit $($u.Code)): $($u.Output -join ' | ')" }
    foreach ($rel in $before.Keys) { if ((Get-Sha "$smith\$rel") -ne $before[$rel]) { throw "$rel is not back in the renamed folder" } }
    if ([IO.File]::ReadAllText("$clips\a\C0001.MOV") -ne 'the next card' -or @(Get-ChildItem -LiteralPath $clips -Recurse -Force).Count -ne 2) { throw 'the new folder with the old name was changed' }
    foreach ($f in $clips, $smith, $smithVideo) { Reset-Folder $f }
    'the files went back to the renamed folder; the new one was not touched'
}

# A backup copy of the card folder (with the receipt in it) is where the user opens the receipt. The original folder is
# still in its place: the files go back there, and the copy is left as it is.
Scenario 'same drive: undo opened from the receipt in a copy of the folder puts the files back into the original' {
    $clips = Join-Path $WorkRoot 'CARD'; $sorted = Join-Path $WorkRoot 'CARD-Video'; $copy = Join-Path $WorkRoot 'CARD-archive'
    New-Clips $clips; foreach ($f in $sorted, $copy) { Reset-Folder $f }
    $before = @{}; foreach ($f in Get-ChildItem -LiteralPath $clips -Recurse -File) { $before[$f.FullName.Substring($clips.Length + 1)] = Get-Sha $f.FullName }
    $r = Invoke-Cli @('run', '--source', $clips, '--target', $sorted, '--yes')
    if ($r.Code -ne 0) { throw "run failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    Copy-Item -LiteralPath $clips -Destination $copy -Recurse -Force
    $copyBefore = @(Get-ChildItem -LiteralPath $copy -Recurse -File -Force | ForEach-Object { "$($_.FullName.Substring($copy.Length + 1))=$(Get-Sha $_.FullName)" })
    $receipt = (Get-ChildItem (Join-Path $copy "$logFolder\*.moved-out.txt")).FullName
    $u = Invoke-Cli @('undo', '--journal', $receipt, '--yes')
    if ($u.Code -ne 0) { throw "undo failed (exit $($u.Code)): $($u.Output -join ' | ')" }
    if (($u.Output -join "`n") -match 'renamed or moved') { throw "the undo says the original was renamed: $($u.Output -join ' | ')" }
    foreach ($rel in $before.Keys) { if ((Get-Sha "$clips\$rel") -ne $before[$rel]) { throw "$rel is not back in the original folder" } }
    $copyAfter = @(Get-ChildItem -LiteralPath $copy -Recurse -File -Force | ForEach-Object { "$($_.FullName.Substring($copy.Length + 1))=$(Get-Sha $_.FullName)" })
    if (Compare-Object $copyBefore $copyAfter) { throw 'the copy of the folder was changed' }
    foreach ($f in $clips, $sorted, $copy) { Reset-Folder $f }
    'the files went back to the original folder; the copy was not touched'
}

# Jobs made before the rename are logged in _IngestSorter folders (made here by renaming the log folders after a
# crash). Such a job must resume, verify and undo like any other, and keep writing into its old folders.
Scenario 'same drive: a job logged by Ingest Sorter in _IngestSorter resumes, verifies and undoes' {
    New-Sample; Reset-Folder $sameTarget
    $r = Invoke-Cli (Run-Args $sameTarget) 'after-rename:12'
    if (-not $r.Crashed) { throw 'crash point not reached' }
    Rename-Folder (Join-Path $sameTarget $logFolder) (Join-Path $sameTarget $legacyLogFolder)
    if (Test-Path -LiteralPath (Join-Path $src $logFolder)) { Rename-Folder (Join-Path $src $logFolder) (Join-Path $src $legacyLogFolder) }
    $r = Invoke-Cli @('resume', '--target', $sameTarget)
    if ($r.Code -ne 0) { throw "resume failed (exit $($r.Code)): $($r.Output -join ' | ')" }
    if (Test-Path -LiteralPath (Join-Path $sameTarget $logFolder)) { throw "the resumed job started a second log folder ($logFolder) in the target" }
    if (-not (Test-Path -Path (Join-Path $src "$legacyLogFolder\*.moved-out.csv"))) { throw "the receipt was not written into the source's $legacyLogFolder folder" }
    $v = Invoke-Cli @('verify', '--target', $sameTarget)
    if ($v.Code -ne 0) { throw "verify failed: $($v.Output -join ' | ')" }
    Assert-Oracle $sameTarget 'videos' | Out-Null
    $u = Invoke-Cli @('undo', '--target', $sameTarget, '--yes')
    if ($u.Code -ne 0) { throw "undo failed (exit $($u.Code)): $($u.Output -join ' | ')" }
    Assert-AllBack $sameTarget 'videos'
}

# ---------------------------------------------------------------------------------------------------------------------
if ($crossTarget) {
    Scenario 'other drive: copy + verify + delete videos, verify, check' {
        New-Sample; Reset-Folder $crossTarget
        $r = Invoke-Cli (Run-Args $crossTarget)
        if ($r.Code -ne 0) { throw "exit $($r.Code): $($r.Output -join ' | ')" }
        $v = Invoke-Cli @('verify', '--target', $crossTarget)
        if ($v.Code -ne 0) { throw "verify failed: $($v.Output -join ' | ')" }
        Assert-Oracle $crossTarget 'videos'
    }
    foreach ($point in 'after-copy-journal:3', 'mid-copy:2', 'mid-copy:40', 'after-copy:5', 'after-copied:5', 'after-place-rename:5', 'after-placed:5', 'after-delete:5', 'after-delete:59') {
        Scenario "other drive: crash at $point, resume" { Test-Crashes $crossTarget 'videos' @($point) }
    }
    Scenario 'other drive: crash three times (mid-copy:10, after-placed:3, after-copied:2), resume' {
        Test-Crashes $crossTarget 'videos' @('mid-copy:10', 'after-placed:3', 'after-copied:2')
    }
    Scenario 'other drive: move PHOTOS (reverse mode), crash at after-copied:4, resume' { Test-Crashes $crossTarget 'photos' @('after-copied:4') }

    Scenario 'other drive: close a job interrupted mid-copy (rolls back, removes partial file)' {
        New-Sample; Reset-Folder $crossTarget
        $r = Invoke-Cli (Run-Args $crossTarget) 'mid-copy:30'
        if (-not $r.Crashed) { throw 'crash point not reached' }
        # With the \\?\ prefix: Windows PowerShell's Get-ChildItem fails on the sample's paths of more than 260 characters.
        $partials = @([IO.Directory]::EnumerateFiles("\\?\$crossTarget", '*.offload-partial', [IO.SearchOption]::AllDirectories))
        if ($partials.Count -ne 1) { throw "expected exactly one partial file after the crash, found $($partials.Count)" }
        $r = Invoke-Cli @('close', '--target', $crossTarget)
        # 5: the job ended with files not moved (they are still in the source), and says so.
        if ($r.Code -ne 5 -or -not ($r.Output -match '^Closed: .*still in source [1-9]')) { throw "close failed (exit $($r.Code)): $($r.Output -join ' | ')" }
        $journal = (Get-Journals $crossTarget)[0].FullName
        if (-not (Select-String -LiteralPath $journal -Pattern '"t":"end","what":"closed"' -Quiet)) { throw 'journal has no closed end record' }
        Assert-Oracle $crossTarget 'videos' @{ Partial = $true }
    }

    Scenario 'other drive: source vanishes after a verified copy: halt, reconnect, resume' { Test-SourceVanishes $crossTarget 'after-copied:5' }

    Scenario 'other drive: undo after a run copies every file back' { Test-Undo $crossTarget }

    foreach ($case in @(@('after-copy:2', 'close'), @('after-copy:2', 'resume'), @('after-copied:2', 'close'), @('after-copied:2', 'resume'))) {
        Scenario "other drive: crash at $($case[0]), original removed, $($case[1]): its copy is kept" { Test-OriginalGone (Join-Path $OtherVolumeRoot 'GONE-Target') $case[0] $case[1] }
    }

    Scenario 'other drive: source renamed while a file is copied: close ends the job, undo after it is back' { Test-CloseWithoutSource (Join-Path $OtherVolumeRoot 'AWAY-Target') }

    Scenario 'other drive: originals cannot be removed: halt, then close ends the job' { Test-CloseWithOriginalsKept (Join-Path $OtherVolumeRoot 'RO-Target') }

    Scenario 'other drive: random hard kills, state checked between kills, then finish' {
        New-Sample -LargeMB 1024; Reset-Folder $crossTarget
        $rng = New-Object Random 42
        $kills = 0; $checks = 0
        $log = Join-Path $WorkRoot 'random-kill.log'
        while ($kills -lt 40 -and (Get-JobState $crossTarget) -ne 'ended') {
            # Until a complete plan exists (a kill can land while it is written), start over with 'run'.
            $arguments = if ((Get-JobState $crossTarget) -eq 'resumable') { "resume --target `"$crossTarget`"" } else { "run --source `"$src`" --target `"$crossTarget`" --mode videos --yes" }
            $p = Start-Process -FilePath $Cli -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
            $null = $p.Handle   # without this, PowerShell loses the exit code
            # Mostly short runs, sometimes long enough for the 1 GB file to get through copy + read-back.
            Start-Sleep -Milliseconds $(if ($rng.NextDouble() -lt 0.9) { $rng.Next(150, 900) } else { $rng.Next(1500, 5000) })
            if (-not $p.HasExited) {
                $p.Kill(); $p.WaitForExit(); $kills++
                if ($kills % 10 -eq 0) {
                    Assert-Oracle $crossTarget 'videos' @{ Partial = $true; AllowOneTempFile = $true; Quiet = $true } | Out-Null   # consistent after every 10th kill
                    $checks++
                }
            }
            else { $p.WaitForExit(); if ($p.ExitCode -ne 0) { throw "exited with $($p.ExitCode): $(Get-Content $log -Raw) $(Get-Content "$log.err" -Raw)" } }
        }
        if ((Get-JobState $crossTarget) -ne 'ended') {
            $r = Invoke-Cli @('resume', '--target', $crossTarget)
            if ($r.Code -ne 0) { throw "final resume failed: $($r.Output -join ' | ')" }
        }
        Assert-Oracle $crossTarget 'videos'
        "survived $kills hard kills ($checks intermediate consistency checks passed)"
    }
}

# ---------------------------------------------------------------------------------------------------------------------
Write-Host ''
$results | Format-Table Result, Seconds, Scenario -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ("{0} scenarios, {1} failed" -f $results.Count, $failed) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
exit $(if ($failed) { 1 } else { 0 })
