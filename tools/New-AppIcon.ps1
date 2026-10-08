<#
.SYNOPSIS
  Builds the IVAR Offload app icon and the in-app logo from Assets\ivar-offload-icon.svg.

.DESCRIPTION
  - ivar-offload-icon.png: the SVG drawn at 2000 px high by Microsoft Edge (headless, on a transparent background);
    everything below is made from it. Kept in git, so the icon can be rebuilt without Edge (-SkipRender).
  - app.ico: the icon as is (dark ink, white inside the hexagon), so it reads on light and dark taskbars.
  - ivar-offload-mask.png: the ink only, as white with alpha. The window tints it with the theme's text colour
    (as an opacity mask), so it is dark on a light theme and light on a dark theme.
#>
param(
    [string] $Svg = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\Assets\ivar-offload-icon.svg'),
    [string] $Rendered = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\Assets\ivar-offload-icon.png'),
    [string] $Icon = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\app.ico'),
    [string] $Mask = (Join-Path $PSScriptRoot '..\src\IvarOffload.App\Assets\ivar-offload-mask.png'),
    [int] $MaskHeight = 400,
    [int] $RenderHeight = 2000,
    [switch] $SkipRender
)

$ErrorActionPreference = 'Stop'
$Svg = [IO.Path]::GetFullPath($Svg); $Rendered = [IO.Path]::GetFullPath($Rendered)

# ---- The SVG drawn large, on a transparent background ------------------------------------------------------------
if (-not $SkipRender) {
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $edge) { throw 'Microsoft Edge was not found; run with -SkipRender to use the PNG in git.' }
    [xml] $doc = Get-Content -LiteralPath $Svg -Raw
    $box = $doc.svg.viewBox -split '\s+' | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $width = [int][Math]::Round($RenderHeight * $box[2] / $box[3])
    $page = Join-Path ([IO.Path]::GetTempPath()) "ivar-offload-icon-$PID.html"
    $shot = Join-Path ([IO.Path]::GetTempPath()) "ivar-offload-icon-$PID.png"
    $svgUrl = ([Uri]$Svg).AbsoluteUri
    Set-Content -LiteralPath $page -Encoding UTF8 -Value ("<!doctype html><html><body style=`"margin:0;background:transparent`">" +
        "<img src=`"$svgUrl`" style=`"display:block;width:${width}px;height:${RenderHeight}px`"></body></html>")
    $edgeData = Join-Path ([IO.Path]::GetTempPath()) "ivar-offload-icon-edge-$PID"
    # Edge reports "... bytes written" on stderr, which Windows PowerShell would take for an error.
    $ErrorActionPreference = 'Continue'
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 `
        "--user-data-dir=$edgeData" "--window-size=$width,$RenderHeight" "--screenshot=$shot" ([Uri]$page).AbsoluteUri 2>&1 | Out-Null
    $ErrorActionPreference = 'Stop'
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $shot) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $shot)) { throw 'Edge did not draw the icon.' }
    Move-Item -LiteralPath $shot -Destination $Rendered -Force
    Remove-Item -LiteralPath $page -Force
    Remove-Item -LiteralPath $edgeData -Recurse -Force -ErrorAction SilentlyContinue
    "Wrote $Rendered (${width}x$RenderHeight)"
}

Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::FromFile($Rendered)

function Resize([Drawing.Image] $image, [int] $width, [int] $height, [int] $canvasWidth, [int] $canvasHeight) {
    $bmp = New-Object Drawing.Bitmap $canvasWidth, $canvasHeight, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.CompositingQuality = 'HighQuality'
    $attributes = New-Object Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY) # no dark fringe at the edges
    $x = [int](($canvasWidth - $width) / 2); $y = [int](($canvasHeight - $height) / 2)
    $g.DrawImage($image, (New-Object Drawing.Rectangle $x, $y, $width, $height), 0, 0, $image.Width, $image.Height, 'Pixel', $attributes)
    $g.Dispose()
    $bmp
}

# ---- Icon: the drawing centred on a square, transparent canvas ------------------------------------------------------
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $h = $size; $w = [Math]::Max(1, [int][Math]::Round($size * $source.Width / $source.Height))
    $bmp = Resize $source $w $h $size $size
    $ms = New-Object IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Classic 32-bit DIB frame (bottom-up BGRA plus a 1-bit AND mask): read by every icon loader.
        $bw = New-Object IO.BinaryWriter $ms
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($size * $size * 4 + $maskStride * $size)); $bw.Write([int32]0); $bw.Write([int32]0)
        $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $size))) # all zero: the alpha channel decides
        $bw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: DIB frames for the small sizes, PNG-compressed for 256
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngs[$i].Length); $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write($png) }
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Icon), $out.ToArray())
"Wrote $Icon ($($out.Length) bytes, sizes $($sizes -join ', '))"

# ---- Mask: ink coverage as alpha --------------------------------------------------------------------------------
$maskWidth = [int][Math]::Round($MaskHeight * $source.Width / $source.Height)
$small = Resize $source $maskWidth $MaskHeight $maskWidth $MaskHeight
$rect = New-Object Drawing.Rectangle 0, 0, $small.Width, $small.Height
$data = $small.LockBits($rect, 'ReadWrite', ([Drawing.Imaging.PixelFormat]::Format32bppArgb))
$bytes = New-Object byte[] ($data.Stride * $data.Height)
[Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$ink = 51 # the icon's #333333
for ($i = 0; $i -lt $bytes.Length; $i += 4) {
    # BGRA. Ink is dark, the inside of the hexagon is white: how dark a pixel is says how much ink it holds.
    $luma = (0.114 * $bytes[$i] + 0.587 * $bytes[$i + 1] + 0.299 * $bytes[$i + 2])
    $coverage = [Math]::Min(1.0, [Math]::Max(0.0, (255 - $luma) / (255 - $ink)))
    $bytes[$i + 3] = [byte][Math]::Round($bytes[$i + 3] * $coverage)
    $bytes[$i] = 255; $bytes[$i + 1] = 255; $bytes[$i + 2] = 255
}
[Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
$small.UnlockBits($data)
$small.Save([IO.Path]::GetFullPath($Mask), [Drawing.Imaging.ImageFormat]::Png)
$small.Dispose()
$source.Dispose()
"Wrote $Mask (${maskWidth}x$MaskHeight)"
