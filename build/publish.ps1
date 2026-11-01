# Publish WinIDM (win-x64, framework dependent) into artifacts\WinIDM,
# verify the published exe with --smoke-test, and write a short install readme.
#
# Usage (from the repository root):
#   powershell -ExecutionPolicy Bypass -File build\publish.ps1
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src\WinIDM.App'
$out = Join-Path $root 'artifacts\WinIDM'

Write-Host "Publishing WinIDM ($Configuration, win-x64) to $out ..."
dotnet publish $app -c $Configuration -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (code $LASTEXITCODE)." }

$exe = Join-Path $out 'WinIDM.App.exe'
if (-not (Test-Path $exe)) { throw "File $exe was not found after publishing." }

# The smoke test uses a temporary data folder so real user data is never touched.
$smokeData = Join-Path ([IO.Path]::GetTempPath()) ("winidm-smoke-" + [guid]::NewGuid().ToString('N'))
try {
    & $exe --smoke-test --data-dir $smokeData
    $code = $LASTEXITCODE
}
finally {
    Remove-Item -Recurse -Force $smokeData -ErrorAction SilentlyContinue
}
if ($code -ne 0) { throw "Smoke test of the published exe failed (code $code)." }
Write-Host "Smoke test of the published exe passed (code 0)."

$readme = @'
# WinIDM - quick install

WinIDM is a download manager for Windows 10 (19041) and Windows 11, built with .NET 9 (WPF).

## Requirements

1. **.NET 9 Desktop Runtime (x64)**. This build is framework dependent (not self-contained).
   Install it from https://dotnet.microsoft.com/download/dotnet/9.0 if it is missing.
2. **Microsoft Edge WebView2 Runtime**. Required by the embedded browser. Most Windows 11
   installations already have it. If not, download the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/.
3. **FFmpeg (optional)**. Only needed to merge HLS/DASH segments and separate media tracks.
   Install FFmpeg and make sure `ffmpeg.exe` is on PATH. Without FFmpeg, regular downloads still work.

## Installation

1. Copy this folder to a location of your choice, for example `C:\Program Files\WinIDM`.
2. Run `WinIDM.App.exe`.

User data is stored in `%LOCALAPPDATA%\WinIDM`. Use `--data-dir <folder>` to choose another location.

## Quick check

`WinIDM.App.exe --smoke-test --data-dir <temporary-folder>` builds every window without showing it,
then exits with code 0 when it succeeds.

## Privacy

WinIDM sends no telemetry. It only makes network connections for downloads, the browser,
and updates of the filter lists you enable yourself. In-app update checking is not available in this build.
'@
Set-Content -Path (Join-Path $out 'README-INSTALL.txt') -Value $readme -Encoding UTF8

Write-Host "Done: $out"
