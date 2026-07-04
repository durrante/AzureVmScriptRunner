<#
.SYNOPSIS
    Builds the portable (free) distribution: a single-file AzureVmScriptRunner.exe
    zipped for GitHub Releases, with SHA256 hashes for release notes and winget.

.DESCRIPTION
    Publishes self-contained single-file win-x64 (no .NET install needed on target
    machines), zips it, and writes a .sha256 file. Unsigned by design — this is the
    zero-cost distribution channel; users see one SmartScreen prompt on first manual
    run, and winget installs verify the hash automatically. See README.md.

.EXAMPLE
    ./Package-Portable.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $PSScriptRoot 'staging-portable'
$outDir = Join-Path $PSScriptRoot 'output'
$zipPath = Join-Path $outDir "AzureVmScriptRunner_v${Version}_win-x64.zip"

Write-Host "Publishing single-file (Release, win-x64, self-contained)..." -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish (Join-Path $root 'src/AzureVmScriptRunner.UI') -c Release -r win-x64 `
    --self-contained true -o $staging -v minimal `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Trim publish debris; ship only what's needed to run.
Get-ChildItem $staging -Filter '*.pdb' | Remove-Item

Write-Host 'Zipping...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$exeHash = (Get-FileHash (Join-Path $staging 'AzureVmScriptRunner.exe') -Algorithm SHA256).Hash
@"
AzureVmScriptRunner v$Version — SHA256
$([System.IO.Path]::GetFileName($zipPath)) : $zipHash
AzureVmScriptRunner.exe : $exeHash
"@ | Set-Content "$zipPath.sha256"

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $zipPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "ZIP SHA256: $zipHash"
Write-Host ''
Write-Host 'Release checklist:' -ForegroundColor Yellow
Write-Host '  1. Create a GitHub Release, upload the .zip and .sha256, paste hashes into the notes.'
Write-Host '  2. Update packaging/winget/*.yaml with the version, release URL and ZIP hash,'
Write-Host '     then PR the three files to github.com/microsoft/winget-pkgs under'
Write-Host "     manifests/m/ModernWorkspaceHub/AzureVmScriptRunner/$Version/"
