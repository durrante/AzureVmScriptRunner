namespace AzureVmScriptRunner.Infrastructure.Scheduling;

/// <summary>
/// The single generic runbook imported into the Automation account. It authenticates
/// with the account's managed identity via the sandbox identity endpoint and calls the
/// ARM Run Command API directly — zero Az module dependencies, so it runs on any
/// Automation runtime without module import/version drift.
/// Executes targets in configurable parallel batches: each VM in a batch is started
/// asynchronously, then all are polled to completion before the next batch begins.
/// </summary>
public static class RunbookContent
{
    public const string RunbookName = "Invoke-AvsrScheduledExecution";

    public const string Script = """
        <#
        .SYNOPSIS
            Azure VM Script Runner — generic scheduled execution runbook.
        .DESCRIPTION
            Receives a base64-encoded JSON execution request (script + target VMs) and
            executes it via the Azure Run Command API in parallel batches, using this
            Automation account's managed identity. One runbook serves every schedule;
            schedules differ only in their parameter. Managed by the AVSR app.
        #>
        param(
            [Parameter(Mandatory = $true)]
            [string]$RequestB64
        )

        $ErrorActionPreference = 'Stop'

        function Write-Log([string]$Message) {
            Write-Output "[$(Get-Date -Format 'HH:mm:ss')] $Message"
        }

        $request = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestB64)) | ConvertFrom-Json
        $targets = @($request.targets)
        $batchSize = [Math]::Max(1, [int]$request.maxParallelism)
        $timeoutSeconds = [Math]::Max(120, [int]$request.timeoutSeconds)

        Write-Log "AVSR scheduled execution: $($request.displayName)"
        Write-Log "Created by: $($request.requestedBy)"
        Write-Log "Targets: $($targets.Count) VM(s) | Batch size: $batchSize | Per-VM timeout: ${timeoutSeconds}s"
        if ($request.packageUrl) { Write-Log "Package: $($request.packageUrl)" }

        # Managed identity token via the Automation sandbox identity endpoint (no Az modules needed).
        $tokenUri = "$($env:IDENTITY_ENDPOINT)?resource=https%3A%2F%2Fmanagement.azure.com%2F&api-version=2019-08-01"
        if ($request.identityClientId) { $tokenUri += "&client_id=$($request.identityClientId)" }
        $tokenResponse = Invoke-RestMethod -Method Get -Uri $tokenUri `
            -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $headers = @{ Authorization = "Bearer $($tokenResponse.access_token)" }
        Write-Log 'Authenticated to Azure with the Automation managed identity.'

        # PSADT deployments: mint a short-lived storage token with THIS account's
        # identity and inject it into the in-guest script, so target VMs need no
        # storage permissions of their own.
        if ($request.needsStorageToken) {
            $storageTokenUri = "$($env:IDENTITY_ENDPOINT)?resource=https%3A%2F%2Fstorage.azure.com%2F&api-version=2019-08-01"
            if ($request.identityClientId) { $storageTokenUri += "&client_id=$($request.identityClientId)" }
            $storageToken = (Invoke-RestMethod -Method Get -Uri $storageTokenUri `
                -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }).access_token
            $request.script = $request.script.Replace('__AVSR_STORAGE_TOKEN__', $storageToken)
            Write-Log 'Storage access token minted for the package download (valid ~1 hour).'
        }

        $scriptLines = @($request.script -split "`r?`n")
        $runStart = Get-Date
        $results = New-Object System.Collections.ArrayList
        $batchTotal = [Math]::Ceiling($targets.Count / $batchSize)

        for ($offset = 0; $offset -lt $targets.Count; $offset += $batchSize) {
            $batchEnd = [Math]::Min($offset + $batchSize, $targets.Count) - 1
            $batch = @($targets[$offset..$batchEnd])
            $batchNumber = [int]($offset / $batchSize) + 1
            Write-Log "--- Batch $batchNumber of ${batchTotal}: $(($batch | ForEach-Object { $_.name }) -join ', ') ---"

            # Start every VM in the batch asynchronously.
            $pending = New-Object System.Collections.ArrayList
            foreach ($target in $batch) {
                try {
                    $uri = "https://management.azure.com$($target.resourceId)/runCommand?api-version=2024-07-01"
                    $body = @{ commandId = 'RunPowerShellScript'; script = $scriptLines } | ConvertTo-Json -Depth 4
                    $response = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers `
                        -Body $body -ContentType 'application/json' -UseBasicParsing
                    $operationUrl = @($response.Headers['Azure-AsyncOperation'])[0]
                    if (-not $operationUrl) { $operationUrl = @($response.Headers['Location'])[0] }

                    Write-Log "$($target.name): started."
                    $null = $pending.Add(@{
                        Name     = $target.name
                        OpUrl    = $operationUrl
                        Started  = Get-Date
                        Deadline = (Get-Date).AddSeconds($timeoutSeconds)
                    })
                }
                catch {
                    Write-Log "$($target.name): FAILED to start — $($_.Exception.Message)"
                    $null = $results.Add([pscustomobject]@{ Name = $target.name; Status = 'FailedToStart'; Seconds = 0 })
                }
            }

            # Poll the whole batch until every VM completes or times out.
            while ($pending.Count -gt 0) {
                Start-Sleep -Seconds 15
                for ($j = $pending.Count - 1; $j -ge 0; $j--) {
                    $item = $pending[$j]
                    $operation = $null
                    try { $operation = Invoke-RestMethod -Uri $item.OpUrl -Headers $headers } catch { }

                    if ($operation -and $operation.status -ne 'InProgress') {
                        $seconds = [int]((Get-Date) - $item.Started).TotalSeconds
                        Write-Log "$($item.Name): $($operation.status) after ${seconds}s"
                        foreach ($entry in $operation.properties.output.value) {
                            if ($entry.message) {
                                foreach ($line in ($entry.message -split "`r?`n")) {
                                    if ($line.Trim()) { Write-Output "    $line" }
                                }
                            }
                        }
                        $null = $results.Add([pscustomobject]@{ Name = $item.Name; Status = $operation.status; Seconds = $seconds })
                        $pending.RemoveAt($j)
                    }
                    elseif ((Get-Date) -gt $item.Deadline) {
                        $seconds = [int]((Get-Date) - $item.Started).TotalSeconds
                        Write-Log "$($item.Name): TIMED OUT after ${seconds}s (limit ${timeoutSeconds}s) — the script may still be running on the VM."
                        $null = $results.Add([pscustomobject]@{ Name = $item.Name; Status = 'TimedOut'; Seconds = $seconds })
                        $pending.RemoveAt($j)
                    }
                }
            }
        }

        # ── Summary ──
        $totalSeconds = [int]((Get-Date) - $runStart).TotalSeconds
        $succeeded = @($results | Where-Object Status -eq 'Succeeded').Count
        $failed = $results.Count - $succeeded

        Write-Output ''
        Write-Log '═══ Summary ═══'
        foreach ($result in $results) {
            $marker = if ($result.Status -eq 'Succeeded') { '[OK]  ' } else { '[FAIL]' }
            Write-Output "    $marker $($result.Name)  —  $($result.Status) ($($result.Seconds)s)"
        }
        Write-Log "Total: $succeeded succeeded, $failed failed of $($results.Count) VM(s) in ${totalSeconds}s ($batchTotal batch(es) of up to $batchSize)."

        if ($failed -gt 0) { throw "$failed target(s) failed — see output above." }
        """;
}
