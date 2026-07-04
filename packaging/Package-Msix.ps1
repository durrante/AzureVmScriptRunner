<#
.SYNOPSIS
    Builds a signed MSIX package of Azure VM Script Runner.

.DESCRIPTION
    1. Publishes the WPF app self-contained (no .NET install needed on targets).
    2. Stages the AppxManifest + visual assets.
    3. Packs with makeappx (auto-downloaded from the Microsoft.Windows.SDK.BuildTools
       NuGet package if the Windows SDK is not installed).
    4. Signs with your certificate, or creates a self-signed dev certificate.

    MSIX packages MUST be signed and the certificate trusted on target machines.
    For production, sign with your organisation's code-signing certificate and
    ensure the manifest Publisher matches its Subject; for Intune distribution,
    upload the .msix as a Line-of-business app.

.EXAMPLE
    ./Package-Msix.ps1 -Version 1.0.0.0                          # dev self-signed
    ./Package-Msix.ps1 -Version 1.2.0.0 -CertPath corp.pfx -CertPassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$CertPath,
    [securestring]$CertPassword,
    [switch]$SkipSigning,

    # Exports the signing certificate's PUBLIC part (.cer) next to the .msix, ready
    # to deploy via Intune (Trusted certificate profile) so installs are prompt-free.
    [switch]$ExportCer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $PSScriptRoot 'staging'
$outDir = Join-Path $PSScriptRoot 'output'
$msixPath = Join-Path $outDir "AzureVmScriptRunner_$Version.msix"

# ── 1. Publish ────────────────────────────────────────────────────────────────
Write-Host "Publishing (Release, win-x64, self-contained)..." -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish (Join-Path $root 'src/AzureVmScriptRunner.UI') -c Release -r win-x64 `
    --self-contained true -o $staging -v minimal /p:Version=$($Version.Substring(0, $Version.LastIndexOf('.')))
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ── 2. Stage manifest + assets ────────────────────────────────────────────────
Write-Host 'Staging manifest and assets...' -ForegroundColor Cyan
(Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw) -replace '__VERSION__', $Version |
    Set-Content (Join-Path $staging 'AppxManifest.xml')
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $staging 'Assets') -Recurse -Force

# ── 3. Locate or fetch makeappx/signtool ─────────────────────────────────────
function Get-SdkTool([string]$name) {
    $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if ($sdk) { return $sdk.FullName }

    $toolsDir = Join-Path $PSScriptRoot '.tools'
    function Find-Cached([string]$toolName) {
        Get-ChildItem $toolsDir -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    }

    $cached = Find-Cached $name
    if ($cached) { return $cached.FullName }

    Write-Host "Windows SDK not found — downloading Microsoft.Windows.SDK.BuildTools from NuGet..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    $nupkg = Join-Path $toolsDir 'sdk-buildtools.zip'
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools' -OutFile $nupkg
    Expand-Archive $nupkg -DestinationPath (Join-Path $toolsDir 'sdk') -Force
    Remove-Item $nupkg
    $tool = Find-Cached $name
    if (-not $tool) { throw "$name not found even after downloading the SDK build tools." }
    return $tool.FullName
}

$makeappx = Get-SdkTool 'makeappx.exe'
$signtool = Get-SdkTool 'signtool.exe'

# ── 4. Pack ───────────────────────────────────────────────────────────────────
Write-Host 'Packing MSIX...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $msixPath) { Remove-Item $msixPath }
& $makeappx pack /d $staging /p $msixPath /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }
Write-Host "Packed: $msixPath" -ForegroundColor Green

# ── 5. Sign ───────────────────────────────────────────────────────────────────
if ($SkipSigning) {
    Write-Warning 'Package is UNSIGNED — it cannot be installed until signed.'
    return
}

if ($CertPath) {
    Write-Host "Signing with $CertPath..." -ForegroundColor Cyan
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($CertPassword))
    & $signtool sign /fd SHA256 /f $CertPath /p $plain /tr 'http://timestamp.digicert.com' /td SHA256 $msixPath
}
else {
    # Self-signed certificate: subject must match the manifest Publisher
    # (CN=ModernWorkspaceHub). Free and prompt-free in a managed estate — deploy the
    # exported .cer to devices via Intune, then the MSIX installs silently. See README.md.
    Write-Host 'No certificate supplied — using the self-signed AVSR certificate.' -ForegroundColor Yellow
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq 'CN=ModernWorkspaceHub' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        # 5-year validity so device trust doesn't need frequent re-deployment;
        # timestamping (below) keeps packages valid even after it expires.
        $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=ModernWorkspaceHub' `
            -KeyUsage DigitalSignature -FriendlyName 'AVSR signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(5) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        Write-Host "Created signing certificate $($cert.Thumbprint) (valid 5 years)."
    }

    # Timestamped so the signature outlives the certificate's own validity.
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr 'http://timestamp.digicert.com' /td SHA256 $msixPath
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }

    if ($ExportCer) {
        $cerPath = Join-Path $outDir 'ModernWorkspaceHub-Signing.cer'
        Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
        Write-Host "Public certificate exported: $cerPath" -ForegroundColor Green
        Write-Host 'Deploy it via Intune (Trusted certificate profile → Local Machine / Trusted People),' -ForegroundColor Yellow
        Write-Host 'then the MSIX installs with no prompts. Full steps: packaging/README.md.' -ForegroundColor Yellow
    }
    else {
        Write-Host 'Tip: re-run with -ExportCer to export the .cer for Intune deployment (prompt-free installs).' -ForegroundColor Yellow
    }
}

if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
Write-Host "Done: $msixPath" -ForegroundColor Green
