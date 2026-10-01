<#
.SYNOPSIS
  Builds assets\icon.ico (all Windows sizes) and assets\icon.png (256 px) from assets\icon-source.png.
  Replace icon-source.png with any square-ish PNG and run this script to change the app icon.
      powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\assets\icon-source.png'),
    [string] $OutDir = (Join-Path $PSScriptRoot '..\assets')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$Source = [IO.Path]::GetFullPath($Source)
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

$src = [System.Drawing.Image]::FromFile($Source)
try {
    # Pad to a square canvas (transparent) so non-square sources are not distorted.
    $side = [math]::Max($src.Width, $src.Height)
    $square = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($square)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, [int](($side - $src.Width) / 2), [int](($side - $src.Height) / 2), $src.Width, $src.Height)
    $g.Dispose()

    function Resize([System.Drawing.Image] $img, [int] $size) {
        $b = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gg = [System.Drawing.Graphics]::FromImage($b)
        $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $gg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $gg.Clear([System.Drawing.Color]::Transparent)
        $gg.DrawImage($img, 0, 0, $size, $size)
        $gg.Dispose()
        return $b
    }

    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = @{}
    foreach ($s in $sizes) {
        $bmp = Resize $square $s
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs[$s] = $ms.ToArray()
        if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        $bmp.Dispose(); $ms.Dispose()
    }
    $square.Dispose()

    # ICO container with PNG-compressed entries (supported since Vista)
    $ico = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ico
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($s in $sizes) {
        $data = $pngs[$s]
        $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
    $bw.Flush()
    [IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.ico'), $ico.ToArray())
    Write-Host "Wrote $(Join-Path $OutDir 'icon.ico') and icon.png from $Source ($($src.Width)x$($src.Height))"
}
finally { $src.Dispose() }
