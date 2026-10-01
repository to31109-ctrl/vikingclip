<#
.SYNOPSIS
  Renders the VikingClip app icon (assets\icon.ico + assets\icon.png) from code so it can be tweaked in git.
  A dark rounded square with a teal "V" chevron - matches the app theme.
#>
param([string] $OutDir = (Join-Path $PSScriptRoot '..\assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # background: rounded square
    $r = [float]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $w = [float]$size
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1C, 0x21, 0x28))
    $g.FillPath($bg, $path)

    # "V" chevron
    $stroke = [float]($size * 0.17)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x2F, 0xD1, 0xB0)), $stroke
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $pts = @(
        (New-Object System.Drawing.PointF ($size * 0.26), ($size * 0.30)),
        (New-Object System.Drawing.PointF ($size * 0.50), ($size * 0.74)),
        (New-Object System.Drawing.PointF ($size * 0.74), ($size * 0.30))
    )
    $g.DrawLines($pen, $pts)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose(); $ms.Dispose()
}

# ICO container with PNG-compressed entries (supported since Vista)
$ico = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.ico'), $ico.ToArray())
Write-Host "Wrote $(Join-Path $OutDir 'icon.ico') and icon.png"
