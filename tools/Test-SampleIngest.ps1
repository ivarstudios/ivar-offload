<#
.SYNOPSIS
  Independent check of a finished job against the manifest written by New-SampleIngest.ps1.

.DESCRIPTION
  For every file in the manifest: it must exist in exactly the expected place (target if it belongs to the moved
  side, otherwise the source), with identical size, creation / modification / last-access times, attributes and
  SHA-256. Also: nothing unexpected in either folder (apart from IVAR Offload's own log folders, _IVAROffload and the older
  _IVARIngest and _IngestSorter), and no temporary files left behind.
  Runs on Windows PowerShell 5.1 (.NET Framework), i.e. a different runtime and SHA-256 implementation than the tool.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Target,
    [Parameter(Mandatory)] [ValidateSet('videos', 'photos')] [string] $Mode,
    [string] $Manifest,
    # Files on the moving side that must nevertheless still be in the source (e.g. deliberate conflicts).
    [string[]] $ExpectKeptInSource = @(),
    # Files that were placed in the target before the run (conflicts): relative path -> expected SHA-256.
    [hashtable] $PreexistingInTarget = @{},
    # After a job was closed early: files on the moving side may be in either place (never both, never partial).
    [switch] $Partial,
    # Mid-job (after a hard kill): the one file that was being copied may have its temporary file in the target.
    [switch] $AllowOneTempFile,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
if (-not $Manifest) { $Manifest = "$Source.manifest.csv" }
$rows = Import-Csv -LiteralPath $Manifest
$sha = [Security.Cryptography.SHA256]::Create()
$failures = New-Object System.Collections.Generic.List[string]
$checked = 0
$movingSide = if ($Mode -eq 'videos') { 'video' } else { 'photo' }
$kept = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($k in $ExpectKeptInSource) { [void]$kept.Add($k) }

function Ext([string] $p) { '\\?\' + $p }
function Fail([string] $message) { $script:failures.Add($message) }
function Hash([string] $full) {
    $path = Ext $full
    $accessed = [IO.File]::GetLastAccessTimeUtc($path)
    $s = [IO.File]::Open($path, 'Open', 'Read', 'Read')
    try { [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-', '').ToLowerInvariant() } finally { $s.Dispose() }
    # Reading updated the last-access time (it was already checked above); put it back so this check can be repeated.
    $attributes = [IO.File]::GetAttributes($path)
    $readOnly = ($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0
    if ($readOnly) { [IO.File]::SetAttributes($path, $attributes -band -bnot [IO.FileAttributes]::ReadOnly) }
    [IO.File]::SetLastAccessTimeUtc($path, $accessed)
    if ($readOnly) { [IO.File]::SetAttributes($path, $attributes) }
}
$attrMask = [IO.FileAttributes]'ReadOnly,Hidden,System'

$expectedInTarget = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$expectedInSource = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($k in $PreexistingInTarget.Keys) { [void]$expectedInTarget.Add($k) }

foreach ($r in $rows) {
    $moves = ($r.Expect -eq $movingSide) -and -not $kept.Contains($r.Rel)
    if ($Partial -and $moves -and -not [IO.File]::Exists((Ext (Join-Path $Target $r.Rel)))) { $moves = $false }
    $here = if ($moves) { Join-Path $Target $r.Rel } else { Join-Path $Source $r.Rel }
    $there = if ($moves) { Join-Path $Source $r.Rel } else { Join-Path $Target $r.Rel }
    if ($moves) { [void]$expectedInTarget.Add($r.Rel) } else { [void]$expectedInSource.Add($r.Rel) }
    $checked++

    $info = New-Object IO.FileInfo (Ext $here)
    if (-not $info.Exists) { Fail "MISSING  $($r.Rel)  (expected in $(if ($moves) {'target'} else {'source'}))"; continue }
    if ([IO.File]::Exists((Ext $there)) -and -not $PreexistingInTarget.ContainsKey($r.Rel)) { Fail "DUPLICATE $($r.Rel) exists in both source and target" }

    # Metadata first: reading the content afterwards may update the last-access time.
    if ($info.Length -ne [long]$r.Size) { Fail "SIZE     $($r.Rel): $($info.Length) != $($r.Size)" }
    if ($info.CreationTimeUtc.Ticks -ne [long]$r.CreationTicks) { Fail "CREATED  $($r.Rel): $($info.CreationTimeUtc.ToString('o')) != $(([datetime][long]$r.CreationTicks).ToString('o'))" }
    if ($info.LastWriteTimeUtc.Ticks -ne [long]$r.WriteTicks) { Fail "MODIFIED $($r.Rel): $($info.LastWriteTimeUtc.ToString('o')) != $(([datetime][long]$r.WriteTicks).ToString('o'))" }
    if ($info.LastAccessTimeUtc.Ticks -ne [long]$r.AccessTicks) { Fail "ACCESSED $($r.Rel): $($info.LastAccessTimeUtc.ToString('o')) != $(([datetime][long]$r.AccessTicks).ToString('o'))" }
    $expectedAttr = if ($r.Attributes) { [IO.FileAttributes]$r.Attributes -band $attrMask } else { [IO.FileAttributes]0 }
    if (($info.Attributes -band $attrMask) -ne $expectedAttr) { Fail "ATTRIBS  $($r.Rel): $($info.Attributes) != $expectedAttr" }
    $actual = Hash $here
    if ($actual -ne $r.Sha256) { Fail "CONTENT  $($r.Rel): sha256 differs" }
}

foreach ($rel in $PreexistingInTarget.Keys) {
    $full = Join-Path $Target $rel
    if (-not [IO.File]::Exists((Ext $full))) { Fail "MISSING  pre-existing target file $rel" }
    elseif ((Hash $full) -ne $PreexistingInTarget[$rel]) { Fail "CHANGED  pre-existing target file $rel was modified" }
}

# The tool's own log folders: _IVAROffload, and _IVARIngest and _IngestSorter from jobs made under its earlier names.
function Test-LogFolder([string] $rel) { $rel -like '_IVAROffload\*' -or $rel -like '_IVARIngest\*' -or $rel -like '_IngestSorter\*' }
function Enumerate([string] $folder) {
    if (-not [IO.Directory]::Exists((Ext $folder))) { return @() }
    [IO.Directory]::EnumerateFiles((Ext $folder), '*', 'AllDirectories') | ForEach-Object { $_.Substring($folder.Length + 5) }
}
foreach ($rel in Enumerate $Target) {
    if (Test-LogFolder $rel) { continue }
    if ($rel -like '*.offload-partial') {
        $temps = 1 + [int]$script:tempsSeen; $script:tempsSeen = $temps
        if (-not $AllowOneTempFile -or $temps -gt 1) { Fail "LEFTOVER temporary file in target: $rel" }
        continue
    }
    if (-not $expectedInTarget.Contains($rel)) { Fail "UNEXPECTED file in target: $rel" }
}
foreach ($rel in Enumerate $Source) {
    if (Test-LogFolder $rel) { continue }   # the receipt of what left this folder
    if ($rel -like '*.offload-partial') { Fail "LEFTOVER temporary file in source: $rel"; continue }
    if (-not $expectedInSource.Contains($rel)) { Fail "UNEXPECTED file in source: $rel" }
}

if ($failures.Count -eq 0) {
    if (-not $Quiet) { "PASS  $checked files verified (location, size, 3 timestamps, attributes, SHA-256); no leftovers" }
    exit 0
}
"FAIL  $($failures.Count) problem(s) in $checked files:"
$failures | Select-Object -First 60 | ForEach-Object { "  $_" }
exit 1
