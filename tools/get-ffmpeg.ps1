<#
.SYNOPSIS
  Downloads the pinned ffmpeg build VikingClip ships with and places ffmpeg.exe next to the app.

  Run once after cloning (and whenever the pin below changes):
      powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1

  The release workflow runs this too, so every installer carries the exact same ffmpeg.
  The binary is GPL (BtbN build) - VikingClip itself is GPL-3.0, see LICENSE.
#>
param(
    # Reuse an already-downloaded zip instead of fetching it again.
    [string] $ZipPath,
    [string] $Destination = (Join-Path $PSScriptRoot '..\src\VikingClip.App\ffmpeg')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- pin ------------------------------------------------------------------
$Tag     = 'autobuild-2026-09-30-13-08'
$Asset   = 'ffmpeg-n8.1.3-9-g29e619e767-win64-gpl-8.1.zip'
$Sha256  = '7B801CDD3A1A0BB54AE6F572187E68B4ED54F52086CFEE133FAB2180BFB429FA'
# ---------------------------------------------------------------------------

$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Tag/$Asset"
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$exe = Join-Path $Destination 'ffmpeg.exe'
$stamp = Join-Path $Destination 'PINNED.txt'
if ((Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $Sha256)) {
    Write-Host "ffmpeg already present and matches pin: $exe"
    exit 0
}

if (-not $ZipPath) {
    $ZipPath = Join-Path ([IO.Path]::GetTempPath()) $Asset
    if (-not (Test-Path $ZipPath)) {
        Write-Host "Downloading $Asset (~185 MB)..."
        Invoke-WebRequest -Uri $Url -OutFile $ZipPath -UseBasicParsing
    }
}

$actual = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
if ($actual -ne $Sha256) {
    Remove-Item $ZipPath -Force
    throw "SHA-256 mismatch for $ZipPath`n expected $Sha256`n got      $actual`nThe download was corrupted or the pin is wrong. Deleted the zip; run again."
}

Write-Host "Extracting ffmpeg.exe -> $Destination"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    foreach ($name in @('bin/ffmpeg.exe', 'LICENSE.txt')) {
        $entry = $zip.Entries | Where-Object { $_.FullName -like "*/$name" } | Select-Object -First 1
        if (-not $entry) { throw "Entry $name not found in zip" }
        $target = Join-Path $Destination ([IO.Path]::GetFileName($name))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally { $zip.Dispose() }

Set-Content -Path $stamp -Value $Sha256 -Encoding ascii
Write-Host "Done. $(& $exe -version | Select-Object -First 1)"
