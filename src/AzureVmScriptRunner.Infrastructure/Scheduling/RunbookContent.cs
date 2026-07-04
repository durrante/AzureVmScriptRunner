namespace AzureVmScriptRunner.Infrastructure.Scheduling;

/// <summary>
/// The single generic runbook imported into the Automation account. It authenticates
/// with the account's system-assigned managed identity via the sandbox identity
/// endpoint and calls the ARM Run Command API directly — zero Az module dependencies,
/// so it runs on any Automation runtime without module import/version drift.
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
            executes it on each VM via the Azure Run Command API, using this Automation
            account's system-assigned managed identity. One runbook serves every
            schedule; schedules differ only in their parameter.
        #>
        param(
            [Parameter(Mandatory = $true)]
            [string]$RequestB64
        )

        $ErrorActionPreference = 'Stop'
        $request = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestB64)) | ConvertFrom-Json
        Write-Output "AVSR scheduled execution: $($request.displayName) (created by $($request.requestedBy))"
        Write-Output "Targets: $($request.targets.Count) VM(s)"

        # Managed identity token via the Automation sandbox identity endpoint (no Az modules needed).
        $tokenUri = "$($env:IDENTITY_ENDPOINT)?resource=https%3A%2F%2Fmanagement.azure.com%2F&api-version=2019-08-01"
        if ($request.identityClientId) { $tokenUri += "&client_id=$($request.identityClientId)" }
        $tokenResponse = Invoke-RestMethod -Method Get -Uri $tokenUri `
            -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $headers = @{ Authorization = "Bearer $($tokenResponse.access_token)" }

        # PSADT deployments: mint a short-lived storage token with THIS account's
        # identity and inject it into the in-guest script, so target VMs need no
        # storage permissions of their own.
        if ($request.needsStorageToken) {
            $storageTokenUri = "$($env:IDENTITY_ENDPOINT)?resource=https%3A%2F%2Fstorage.azure.com%2F&api-version=2019-08-01"
            if ($request.identityClientId) { $storageTokenUri += "&client_id=$($request.identityClientId)" }
            $storageToken = (Invoke-RestMethod -Method Get -Uri $storageTokenUri `
                -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }).access_token
            $request.script = $request.script.Replace('__AVSR_STORAGE_TOKEN__', $storageToken)
            Write-Output 'Storage access token minted for package download.'
        }

        $failed = 0
        foreach ($target in $request.targets) {
            Write-Output "--- $($target.name) ---"
            try {
                $uri = "https://management.azure.com$($target.resourceId)/runCommand?api-version=2024-07-01"
                $body = @{
                    commandId = 'RunPowerShellScript'
                    script    = @($request.script -split "`r?`n")
                } | ConvertTo-Json -Depth 4

                $response = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers `
                    -Body $body -ContentType 'application/json' -UseBasicParsing

                $operationUrl = @($response.Headers['Azure-AsyncOperation'])[0]
                if (-not $operationUrl) { $operationUrl = @($response.Headers['Location'])[0] }

                $deadline = (Get-Date).AddSeconds([int]$request.timeoutSeconds)
                do {
                    Start-Sleep -Seconds 15
                    $operation = Invoke-RestMethod -Uri $operationUrl -Headers $headers
                } while ($operation.status -eq 'InProgress' -and (Get-Date) -lt $deadline)

                Write-Output "Status: $($operation.status)"
                foreach ($item in $operation.properties.output.value) {
                    if ($item.message) { Write-Output $item.message }
                }

                if ($operation.status -ne 'Succeeded') { $failed++ }
            }
            catch {
                $failed++
                Write-Error "Failed on $($target.name): $($_.Exception.Message)" -ErrorAction Continue
            }
        }

        Write-Output "AVSR scheduled execution finished. Failed: $failed of $($request.targets.Count)."
        if ($failed -gt 0) { throw "$failed target(s) failed — see output above." }
        """;
}
