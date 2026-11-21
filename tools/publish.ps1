# Builds a single-file, self-contained VRChatPhotoSync.exe (win-x64) into <repo>\publish\.
# No trimming: WinForms doesn't support it.
# Usage: powershell -File tools\publish.ps1 [-Output <folder>]
param([string]$Output)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo 'publish' }
$project = Join-Path $repo 'src\VRChatPhotoSync\VRChatPhotoSync.csproj'

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $Output 'VRChatPhotoSync.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }
$size = (Get-Item $exe).Length
Write-Host ('{0}: {1:N1} MB ({2:N0} bytes)' -f $exe, ($size / 1MB), $size)
Get-ChildItem $Output | Where-Object { $_.Name -ne 'VRChatPhotoSync.exe' } |
    ForEach-Object { Write-Host "  also: $($_.Name)" }
