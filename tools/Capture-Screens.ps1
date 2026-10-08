<#
.SYNOPSIS
  Captures the screens of IVAR Offload for a GUI review: a screenshot and a dump of every visible text of each state,
  with every (i) popup and "How it works" opened once.

.DESCRIPTION
  Opens windows on the desktop: run it on a test PC, not while working. Walks Backup (first start, filled setup, name
  pattern, preview, same destination twice, running, the unfinished-backup banner after a crash) and Sort (first
  start, source chosen, photos, preview and its lists, done, the undo window). For the result states of a backup or a
  sort, run Test-Gui.ps1 as well (its screenshots go to its -ShotFolder).

  In each .txt, the text of an open popup comes first (the lines before "text: IVAR Offload"); "[scrolled out of view]"
  marks what needs scrolling, "[disabled]" what is greyed out.
#>
param(
    [string] $WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'ivar-offload-screens'),
    [string] $Out = (Join-Path $WorkRoot 'screens'),
    [string] $Exe = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\bin\Release\net10.0-windows\IVAR Offload.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GuiDriver.ps1')
$Exe = [IO.Path]::GetFullPath($Exe)
$src = Join-Path $WorkRoot 'INGEST'
$appData = Join-Path $WorkRoot 'appdata'
New-Item -ItemType Directory -Force -Path $WorkRoot, $Out | Out-Null
$started = New-Object System.Collections.Generic.List[int]

function Reset-Folder([string] $path) {
    # With the \\?\ prefix: Windows PowerShell's Remove-Item fails on the sample's paths of more than 260 characters.
    if (Test-Path -LiteralPath $path) { cmd /c "attrib -r -h -s `"$path\*`" /s /d" 2>$null | Out-Null; [IO.Directory]::Delete("\\?\$path", $true) }
}
function New-Sample([int] $LargeMB = 8) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-SampleIngest.ps1') -Path $src -LargeFileMB $LargeMB -Force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sample generation failed' }
}
function Start-App([switch] $Fresh) {
    if ($Fresh) { Reset-Folder $appData }
    $p = Start-Gui $Exe $appData
    $started.Add($p.Id)
    $p
}
# Only the windows this script opened are closed.
function Stop-Apps { foreach ($id in $started) { Get-Process -Id $id -ErrorAction SilentlyContinue | Stop-Process -Force }; $started.Clear() }

function Shot($Process, [string] $Name) {
    Save-Screenshot $Process (Join-Path $Out "$Name.png") | Out-Null
    Save-Texts $Process $Name
    Write-Host "  $Name"
}

# Every text, button, field and tab of the process's windows (open popups included), in screen order.
function Save-Texts($Process, [string] $Name) {
    $lines = New-Object System.Collections.Generic.List[string]
    $types = @{
        'ControlType.Text' = 'text'; 'ControlType.Button' = 'button'; 'ControlType.Edit' = 'field'; 'ControlType.CheckBox' = 'checkbox'
        'ControlType.RadioButton' = 'radio'; 'ControlType.TabItem' = 'tab'; 'ControlType.Hyperlink' = 'link'; 'ControlType.HeaderItem' = 'column'
        'ControlType.ComboBox' = 'dropdown'; 'ControlType.Window' = 'window'; 'ControlType.ProgressBar' = 'progress'
    }
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    function Walk($e, [int] $depth) {
        $c = $walker.GetFirstChild($e)
        while ($c) {
            $ct = $c.Current.ControlType.ProgrammaticName
            if ($types.ContainsKey($ct)) {
                $n = $c.Current.Name
                $v = ''
                $vp = $null
                if ($c.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $v = $vp.Current.Value }
                $state = ''
                $tp = $null
                if ($c.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { $state = " [$($tp.Current.ToggleState)]" }
                $sp = $null
                if ($c.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp) -and $sp.Current.IsSelected) { $state += ' [selected]' }
                if (-not $c.Current.IsEnabled) { $state += ' [disabled]' }
                if ($c.Current.IsOffscreen) { $state += ' [scrolled out of view]' }
                if ($n -or $v) {
                    $text = "$n" + $(if ($v -and $v -ne $n) { " = '$v'" } else { '' })
                    $lines.Add(('  ' * [Math]::Min($depth, 12)) + "$($types[$ct]): $text$state")
                }
            }
            # Lists: only their first rows.
            if ($ct -eq 'ControlType.DataItem') { $script:rows++; if ($script:rows -gt 6) { $c = $walker.GetNextSibling($c); continue } }
            Walk $c ($depth + 1)
            $c = $walker.GetNextSibling($c)
        }
    }
    $pc = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $pc)) {
        $script:rows = 0
        $lines.Add("=== window: $($w.Current.Name)")
        Walk $w 1
    }
    $lines | Set-Content -LiteralPath (Join-Path $Out "$Name.txt") -Encoding UTF8
}

# Opens each (i) button, "How it works" and "Notes" on screen in turn, with a shot of each.
function Shot-Tips($Process, [string] $Prefix) {
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty, $true)
    $tips = @((Get-Root $Process).FindAll([Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and -not $_.Current.IsOffscreen
    })
    $i = 0
    foreach ($tip in $tips) {
        $i++
        $label = ($tip.Current.Name -replace '[^\w]+', '-').Trim('-')
        try {
            [void](Show-Element $tip)
            $toggle = $tip.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            $toggle.Toggle()
            Start-Sleep -Milliseconds 500
            Shot $Process "$Prefix-tip$i-$label"
            if ($toggle.Current.ToggleState -eq 'On') { $toggle.Toggle() }
            Start-Sleep -Milliseconds 300
        } catch { Write-Host "  tip $label failed: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}
function Scroll-Top($root) {
    foreach ($id in 'BackupScroll', 'UpperScroll') {
        try {
            $e = Find-Element $root $id 300
            $sp = $null
            if ($e.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$sp) -and $sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 0) }
        } catch {}
    }
}
function Step([string] $Name, [scriptblock] $Body) {
    Write-Host $Name
    try { & $Body } catch { Write-Host "FAIL $Name : $($_.Exception.Message)" -ForegroundColor Red } finally { Stop-Apps }
}

New-Sample
$d1 = Join-Path $WorkRoot 'BACKUP-1'; $d2 = Join-Path $WorkRoot 'BACKUP-2'

Step 'Backup and Sort: first start' {
    $p = Start-App -Fresh; $root = Get-Root $p
    Shot $p 'B01-backup-first-start'
    Shot-Tips $p 'B01'
    Select-Item $root 'SortTab'; Start-Sleep -Milliseconds 800
    Shot $p 'S01-sort-first-start'
    Shot-Tips $p 'S01'
}

Step 'Backup: setup filled in, name pattern, preview' {
    Reset-Folder $d1; Reset-Folder $d2; New-Item -ItemType Directory -Force -Path $d1, $d2 | Out-Null
    $p = Start-App; $root = Get-Root $p
    Set-Text $root 'BackupSourceBox' $src; Start-Sleep -Milliseconds 900
    Set-Text $root 'BackupDest1Box' $d1; Start-Sleep -Milliseconds 600
    Invoke-Button $root 'BackupAddDestination'; Start-Sleep -Milliseconds 400
    Set-Text $root 'BackupDest2Box' $d2; Start-Sleep -Milliseconds 900
    Scroll-Top $root
    Shot $p 'B02-backup-setup-filled'
    Shot-Tips $p 'B02'
    Invoke-Button $root 'BackupTemplateLink'; Start-Sleep -Milliseconds 500
    Shot $p 'B03-backup-name-pattern-open'
    Invoke-Button $root 'BackupScanButton'
    Wait-Until { Test-Visible $root 'BackupStartButton' } 60000 'the preview'
    Scroll-Top $root
    Shot $p 'B04-backup-preview'
    Shot-Tips $p 'B04'
}

Step 'Backup: the same destination twice' {
    Reset-Folder $d1; New-Item -ItemType Directory -Force -Path $d1 | Out-Null
    $p = Start-App; $root = Get-Root $p
    Set-Text $root 'BackupSourceBox' $src; Start-Sleep -Milliseconds 900
    Set-Text $root 'BackupDest1Box' $d1; Start-Sleep -Milliseconds 600
    if (-not (Test-Visible $root 'BackupDest2Box')) { Invoke-Button $root 'BackupAddDestination'; Start-Sleep -Milliseconds 400 }
    Set-Text $root 'BackupDest2Box' $d1; Start-Sleep -Milliseconds 900
    Shot $p 'B05-backup-same-destination-twice-setup'
    try { Invoke-Button $root 'BackupScanButton'; Start-Sleep -Milliseconds 4000 } catch { Write-Host '  the scan could not start' }
    Shot $p 'B06-backup-same-destination-twice-after-scan'
}

Step 'Backup: running, then the banner after a crash' {
    Reset-Folder $d1; Reset-Folder $d2; New-Item -ItemType Directory -Force -Path $d1, $d2 | Out-Null
    New-Sample -LargeMB 64
    $flag = Join-Path $appData 'test-hold.flag'; Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
    $env:IVAROFFLOAD_TEST_HOLD_AT = 'after-copy:3'   # test hook: the backup waits there until the flag file is deleted
    try { $p = Start-App } finally { Remove-Item Env:\IVAROFFLOAD_TEST_HOLD_AT -ErrorAction SilentlyContinue }
    $root = Get-Root $p
    Set-Text $root 'BackupSourceBox' $src; Start-Sleep -Milliseconds 900
    Set-Text $root 'BackupDest1Box' $d1; Start-Sleep -Milliseconds 600
    if (-not (Test-Visible $root 'BackupDest2Box')) { Invoke-Button $root 'BackupAddDestination'; Start-Sleep -Milliseconds 400 }
    Set-Text $root 'BackupDest2Box' $d2; Start-Sleep -Milliseconds 900
    Invoke-Button $root 'BackupScanButton'
    Wait-Until { Test-Visible $root 'BackupStartButton' } 60000 'the preview'
    Invoke-Button $root 'BackupStartButton'
    Wait-Until { Test-Path -LiteralPath $flag } 60000 'the backup to reach the third file'
    Shot $p 'B07-backup-running'
    Stop-Apps   # like a crash or a power cut
    Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
    $p = Start-App; Start-Sleep -Milliseconds 2500
    Shot $p 'B08-backup-unfinished-banner-next-start'
    Shot-Tips $p 'B08'
}

Step 'Sort: setup, photos, preview and its lists, done, undo window' {
    New-Sample
    $target = "$src-Video"; Reset-Folder $target
    $p = Start-App; $root = Get-Root $p
    Select-Item $root 'SortTab'; Start-Sleep -Milliseconds 600
    Set-Text $root 'SourceBox' $src; Start-Sleep -Milliseconds 900
    Shot $p 'S02-sort-source-filled'
    Shot-Tips $p 'S02'
    Select-Radio $root 'ModePhotos'; Start-Sleep -Milliseconds 700
    Shot $p 'S03-sort-photos-mode'
    Select-Radio $root 'ModeVideos'; Start-Sleep -Milliseconds 700
    Set-Text $root 'TargetBox' $target; Start-Sleep -Milliseconds 900
    Invoke-Button $root 'ScanButton'
    Wait-Until { Test-Visible $root 'ConfirmButton' } 60000 'the preview'
    Scroll-Top $root
    Shot $p 'S04-sort-preview'
    Shot-Tips $p 'S04'
    foreach ($tab in 'FolderTab', 'TypeTab', 'StayTab') {
        try { Select-Item $root $tab; Start-Sleep -Milliseconds 600; Shot $p "S05-sort-preview-$tab" } catch {}
    }
    Invoke-Button $root 'ConfirmButton'
    Wait-Until { $(try { Get-Text $root 'ResultTitle' -TimeoutMs 300 } catch { '' }) -ne '' } 120000 'the sort to finish'
    Open-More $root 'MoreButton'
    Wait-Until { (Find-Element $root 'VerifyButton').Current.IsEnabled } 120000 'the check of the source'
    Scroll-Top $root
    Shot $p 'S06-sort-done'
    Invoke-Button $root 'UndoPreviousButton'
    Start-Sleep -Milliseconds 1500
    Shot $p 'S07-undo-window'
}

Write-Host "Screens: $Out"
