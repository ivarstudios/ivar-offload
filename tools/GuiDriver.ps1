<#
.SYNOPSIS
  Helpers to drive the IVAR Offload window through UI Automation and take screenshots (dot-source this file).
#>
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
if (-not ('GuiDriverNative' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class GuiDriverNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    /// <summary>The visible top-level windows of a process (main window, dialogs, popups), topmost first.</summary>
    public static List<IntPtr> VisibleWindows(int processId) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == processId && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
'@
    [void][GuiDriverNative]::SetProcessDpiAwarenessContext([IntPtr](-4))   # per-monitor v2: real pixel sizes
}

function Start-Gui([string] $Exe, [string] $DataFolder) {
    if ($DataFolder) { $env:IVAROFFLOAD_DATA = $DataFolder }
    $p = Start-Process -FilePath $Exe -PassThru
    for ($i = 0; $i -lt 100 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 100; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { throw 'The window did not appear.' }
    Start-Sleep -Milliseconds 800
    $p
}

function Get-Root($Process) { [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle) }

function Find-Element($Root, [string] $Id, [int] $TimeoutMs = 5000) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $e = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 100
    } while ($sw.ElapsedMilliseconds -lt $TimeoutMs)
    throw "Element '$Id' not found"
}

function Set-Text($Root, [string] $Id, [string] $Value) {
    (Find-Element $Root $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Get-Text($Root, [string] $Id, [int] $TimeoutMs = 5000) {
    $e = Find-Element $Root $Id $TimeoutMs
    $vp = $null
    if ($e.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    $e.Current.Name
}

# On a short screen the upper part of the window scrolls: scrolls the nearest scrollable parent until the element is
# on screen. True when it is.
function Show-Element($Element) {
    if (-not $Element.Current.IsOffscreen) { return $true }
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    for ($parent = $walker.GetParent($Element); $parent; $parent = $walker.GetParent($parent)) {
        $scroll = $null
        if (-not $parent.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -or -not $scroll.Current.VerticallyScrollable) { continue }
        foreach ($percent in 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100) {
            $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $percent)
            Start-Sleep -Milliseconds 100
            if (-not $Element.Current.IsOffscreen) { return $true }
        }
    }
    $false
}

function Invoke-Button($Root, [string] $Id) {
    $e = Find-Element $Root $Id
    Wait-Until { $e.Current.IsEnabled -and (Show-Element $e) } 10000 "button '$Id' to become enabled"
    $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# Opens a result's "More" actions (a toggle button); they are not in the UI Automation tree while closed.
function Open-More($Root, [string] $Id) {
    $toggle = (Find-Element $Root $Id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    Start-Sleep -Milliseconds 200
}

# Selects a radio button or a tab.
function Select-Item($Root, [string] $Id) {
    (Find-Element $Root $Id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Select-Radio($Root, [string] $Id) { Select-Item $Root $Id }

function Test-Selected($Root, [string] $Id) {
    (Find-Element $Root $Id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
}

# The element is shown (collapsed parts of the window are not in the UI Automation tree), scrolled into view if needed.
function Test-Visible($Root, [string] $Id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $e = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    [bool]($e -and (Show-Element $e))
}

# Some text on screen matches a -like pattern (for texts without an AutomationId of their own).
function Test-TextShown($Root, [string] $Pattern) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)
    foreach ($t in $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($t.Current.Name -like $Pattern -and -not $t.Current.IsOffscreen) { return $true }
    }
    $false
}

# A window of the process with this AutomationId (a dialog such as ChoiceDialog; owned windows can appear as
# children of their owner in the UI Automation tree).
function Find-Window($Process, [string] $Id, [int] $TimeoutMs = 5000) {
    $idCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $processCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)) {
            $found = if ($w.Current.AutomationId -eq $Id) { $w } else { $w.FindFirst([Windows.Automation.TreeScope]::Children, $idCondition) }
            if ($found) { return $found }
        }
        Start-Sleep -Milliseconds 150
    } while ($sw.ElapsedMilliseconds -lt $TimeoutMs)
    throw "Window '$Id' did not appear"
}

function Wait-Until([scriptblock] $Condition, [int] $TimeoutMs = 60000, [string] $What = 'condition') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $What"
}

# Answers a standard message box (#32770 dialog) shown by the process: 1 = OK, 2 = Cancel, 6 = Yes, 7 = No.
function Confirm-Dialog($Process, [int] $ButtonId = 1, [int] $TimeoutMs = 5000) {
    $classCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
    $processCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)) {
            $dialog = if ($w.Current.ClassName -eq '#32770') { $w } else { $w.FindFirst([Windows.Automation.TreeScope]::Children, $classCondition) }
            if ($dialog) {
                [void][GuiDriverNative]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle, 0x0111, [IntPtr]$ButtonId, [IntPtr]::Zero)   # WM_COMMAND
                return $dialog.Current.Name
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw 'No message box appeared'
}

# A picture of the window with its open dialogs and popups. Each window draws itself (PrintWindow with full content,
# which works for WPF's GPU rendering), so other windows on top or a window partly off the screen do not matter.
function Save-Screenshot($Process, [string] $Path) {
    Start-Sleep -Milliseconds 500
    $windows = @()
    foreach ($h in [GuiDriverNative]::VisibleWindows($Process.Id)) {
        $r = New-Object GuiDriverNative+RECT
        [void][GuiDriverNative]::GetWindowRect($h, [ref]$r)
        if (($r.Right - $r.Left) -ge 5 -and ($r.Bottom - $r.Top) -ge 5) { $windows += [pscustomobject]@{ Handle = $h; Rect = $r } }
    }
    if ($windows.Count -eq 0) { throw 'No window to capture' }
    # Topmost first in the list: draw from the bottom up, so dialogs and popups end up on top of the main window.
    [array]::Reverse($windows)
    $left = ($windows | ForEach-Object { $_.Rect.Left } | Measure-Object -Minimum).Minimum
    $top = ($windows | ForEach-Object { $_.Rect.Top } | Measure-Object -Minimum).Minimum
    $right = ($windows | ForEach-Object { $_.Rect.Right } | Measure-Object -Maximum).Maximum
    $bottom = ($windows | ForEach-Object { $_.Rect.Bottom } | Measure-Object -Maximum).Maximum
    $canvas = New-Object Drawing.Bitmap ([int]($right - $left)), ([int]($bottom - $top))
    $cg = [Drawing.Graphics]::FromImage($canvas)
    foreach ($w in $windows) {
        $width = $w.Rect.Right - $w.Rect.Left; $height = $w.Rect.Bottom - $w.Rect.Top
        $bmp = New-Object Drawing.Bitmap $width, $height
        $g = [Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][GuiDriverNative]::PrintWindow($w.Handle, $hdc, 2)   # PW_RENDERFULLCONTENT
        $g.ReleaseHdc($hdc); $g.Dispose()
        $cg.DrawImage($bmp, [int]($w.Rect.Left - $left), [int]($w.Rect.Top - $top), $width, $height)
        $bmp.Dispose()
    }
    $cg.Dispose()
    $canvas.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $canvas.Dispose()
    $Path
}
