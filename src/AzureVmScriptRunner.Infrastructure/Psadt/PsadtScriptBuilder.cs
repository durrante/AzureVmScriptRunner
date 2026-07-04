using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Infrastructure.Psadt;

/// <summary>
/// Generates the in-guest PowerShell that performs a PSADT deployment:
/// download (managed identity first, short-lived SAS fallback) → extract →
/// locate the toolkit entry point (v4 Invoke-AppDeployToolkit or v3
/// Deploy-Application) → execute → propagate exit code → cleanup.
/// Pure string building, fully unit-testable without Azure.
/// </summary>
public static class PsadtScriptBuilder
{
    /// <summary>
    /// Placeholder swapped for a real short-lived storage bearer token by the
    /// Automation runbook at fire time (scheduled runs only).
    /// </summary>
    public const string StorageTokenPlaceholder = "__AVSR_STORAGE_TOKEN__";

    public static string Build(
        PsadtPayload payload, Uri? fallbackSasUri, bool includeRunbookTokenFallback = false)
    {
        var arguments = $"-DeploymentType {payload.DeploymentType} -DeployMode {payload.DeployMode}";
        if (!string.IsNullOrWhiteSpace(payload.AdditionalArguments))
        {
            arguments += $" {payload.AdditionalArguments}";
        }

        var sasLiteral = fallbackSasUri is null ? "$null" : $"'{fallbackSasUri}'";
        var cleanup = payload.CleanupTemporaryFiles ? "$true" : "$false";
        var tokenLiteral = includeRunbookTokenFallback ? $"'{StorageTokenPlaceholder}'" : "$null";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $blobUrl = '{{payload.PackageUrl}}'
            $sasUrl = {{sasLiteral}}
            $schedulerToken = {{tokenLiteral}}
            $cleanup = {{cleanup}}
            $workDir = Join-Path $env:SystemDrive ('AVSR\' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $workDir -Force | Out-Null
            $zipPath = Join-Path $workDir 'package.zip'

            # Default to failure so an exception can never surface as exit code 0.
            $exitCode = 1
            try {
                # --- Download: managed identity preferred, SAS fallback ---
                $downloaded = $false
                try {
                    $tokenResponse = Invoke-RestMethod -Headers @{ Metadata = 'true' } -TimeoutSec 15 -Uri `
                        'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fstorage.azure.com%2F'
                    Invoke-WebRequest -UseBasicParsing -Uri $blobUrl -OutFile $zipPath -Headers @{
                        Authorization  = "Bearer $($tokenResponse.access_token)"
                        'x-ms-version' = '2021-08-06'
                    }
                    $downloaded = $true
                    Write-Output 'AVSR: package downloaded via managed identity.'
                }
                catch {
                    # A 400 from IMDS simply means this VM has no managed identity — expected.
                    Write-Output "AVSR: VM managed identity not available (this is fine) — trying the next download method."
                }

                # Fallback 2 (scheduled runs): short-lived storage token minted by the
                # scheduler's own identity at fire time — no per-VM setup required.
                if (-not $downloaded -and $schedulerToken -and $schedulerToken -notlike '__AVSR*') {
                    try {
                        Invoke-WebRequest -UseBasicParsing -Uri $blobUrl -OutFile $zipPath -Headers @{
                            Authorization  = "Bearer $schedulerToken"
                            'x-ms-version' = '2021-08-06'
                        }
                        $downloaded = $true
                        Write-Output 'AVSR: package downloaded via the scheduler identity token.'
                    }
                    catch {
                        Write-Output "AVSR: scheduler token download failed ($($_.Exception.Message))."
                    }
                }

                if (-not $downloaded) {
                    if (-not $sasUrl) {
                        throw 'Package download failed. Fix ONE of: (a) grant the Automation account identity ''Storage Blob Data Reader'' on the package storage account (recommended, one-time), or (b) enable a managed identity on this VM and grant it the same role.'
                    }
                    try {
                        Invoke-WebRequest -UseBasicParsing -Uri $sasUrl -OutFile $zipPath
                        Write-Output 'AVSR: package downloaded via time-limited SAS.'
                    }
                    catch {
                        # Surface the storage error code: AuthorizationPermissionMismatch
                        # means the SAS issuer lacks a data-plane role (Storage Blob Data
                        # Reader); AuthorizationFailure means the storage firewall is
                        # blocking this VM's network path.
                        $detail = $_.Exception.Message
                        $response = $_.Exception.Response
                        if ($response) {
                            try {
                                $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
                                $body = $reader.ReadToEnd()
                                if ($body -match '<Code>([^<]+)</Code>') { $detail = "$detail Storage error code: $($Matches[1])." }
                            } catch { }
                        }
                        throw "SAS download of '$blobUrl' failed: $detail"
                    }
                }

                $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
                Write-Output "AVSR: downloaded $zipMb MB from $blobUrl"

                # --- Extract ---
                Expand-Archive -Path $zipPath -DestinationPath $workDir -Force
                $fileCount = @(Get-ChildItem -Path $workDir -Recurse -File).Count
                Write-Output "AVSR: extracted $fileCount file(s) to $workDir"

                # --- Locate PSADT entry point (v4 first, then v3) ---
                $entry = Get-ChildItem -Path $workDir -Recurse -Filter 'Invoke-AppDeployToolkit.exe' |
                    Select-Object -First 1
                $toolkitVersion = 'v4'
                if (-not $entry) {
                    $entry = Get-ChildItem -Path $workDir -Recurse -Filter 'Deploy-Application.exe' |
                        Select-Object -First 1
                    $toolkitVersion = 'v3'
                }
                if (-not $entry) {
                    throw 'No PSADT entry point (Invoke-AppDeployToolkit.exe or Deploy-Application.exe) found in the package.'
                }
                Write-Output "AVSR: PSADT $toolkitVersion detected — executing $($entry.Name) {{arguments}}"

                # --- Execute and propagate the exit code ---
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $process = Start-Process -FilePath $entry.FullName -ArgumentList '{{arguments}}' `
                    -WorkingDirectory $entry.DirectoryName -Wait -PassThru
                $exitCode = $process.ExitCode
                Write-Output "AVSR: deployment finished with exit code $exitCode after $([int]$stopwatch.Elapsed.TotalSeconds)s"
            }
            catch {
                Write-Error "AVSR: deployment failed before execution completed: $($_.Exception.Message)"
                $exitCode = 1
            }
            finally {
                if ($cleanup) {
                    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            exit $exitCode
            """;
    }
}
