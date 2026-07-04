using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Infrastructure.Azure;

namespace AzureVmScriptRunner.Infrastructure.Scheduling;

/// <summary>
/// <see cref="IScheduleService"/> over the Azure Automation REST API. Uses raw ARM
/// calls (with the signed-in credential) for full control over the small resource set
/// we manage: one resource group, one Automation account with a system-assigned
/// managed identity, one published generic runbook, and N schedule/jobSchedule pairs.
/// </summary>
public sealed class AutomationScheduleService : IScheduleService, IDisposable
{
    private const string AutomationApiVersion = "2023-11-01";
    private const string SchedulePrefix = "AVSR-";

    // Virtual Machine Contributor — lets the runbook's managed identity call Run Command.
    private const string VmContributorRoleId = "9980e02c-c2be-4d73-94e8-173b1dc7cf3c";

    // Storage Blob Data Reader — lets the scheduler identity mint tokens/read packages.
    private const string StorageBlobDataReaderRoleId = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1";

    private static readonly TokenRequestContext ArmScope =
        new(new[] { "https://management.azure.com/.default" });

    private readonly AzureSession _session;
    private readonly LocalSettingsStore _settingsStore;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://management.azure.com") };

    public AutomationScheduleService(AzureSession session, LocalSettingsStore settingsStore)
    {
        _session = session;
        _settingsStore = settingsStore;
    }

    private LocalSettings Settings => _settingsStore.Load();

    private string? AutomationSubscriptionId => Settings.AutomationSubscriptionId;

    private string AccountPath
    {
        get
        {
            var settings = Settings;
            return $"/subscriptions/{settings.AutomationSubscriptionId}/resourceGroups/{settings.ResourceGroupName}" +
                   $"/providers/Microsoft.Automation/automationAccounts/{settings.AutomationAccountName}";
        }
    }

    public async Task<SchedulingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(AutomationSubscriptionId))
        {
            return new SchedulingStatus(false, "Not provisioned — choose a subscription and region, then provision.");
        }

        var response = await SendAsync(HttpMethod.Get,
            $"{AccountPath}/runbooks/{RunbookContent.RunbookName}?api-version={AutomationApiVersion}",
            null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new SchedulingStatus(false, "Automation account or runbook missing — run provisioning.");
        }

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var state = json.RootElement.GetProperty("properties").GetProperty("state").GetString();

        var settings = Settings;
        return state == "Published"
            ? new SchedulingStatus(true,
                $"Ready — account '{settings.AutomationAccountName}' in {settings.ResourceGroupName} " +
                $"(subscription {settings.AutomationSubscriptionId}).")
            : new SchedulingStatus(false, $"Runbook exists but is in state '{state}' — re-run provisioning.");
    }

    public async Task ProvisionAsync(
        string subscriptionId,
        string region,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _settingsStore.Save(_settingsStore.Load() with
        {
            AutomationSubscriptionId = subscriptionId,
            AutomationRegion = region
        });
        var settings = Settings;
        var tags = settings.ParseTags();

        progress?.Report("Checking the Microsoft.Automation resource provider registration...");
        await EnsureResourceProviderRegisteredAsync(subscriptionId, progress, cancellationToken);

        progress?.Report($"Creating resource group {settings.ResourceGroupName}...");
        await EnsureSuccessAsync(HttpMethod.Put,
            $"/subscriptions/{subscriptionId}/resourcegroups/{settings.ResourceGroupName}?api-version=2021-04-01",
            new { location = region, tags }, cancellationToken);

        object identity = settings.UserAssignedIdentityResourceId is { Length: > 0 } uaId
            ? new
            {
                type = "UserAssigned",
                userAssignedIdentities = new Dictionary<string, object> { [uaId] = new() }
            }
            : new { type = "SystemAssigned" };

        progress?.Report($"Creating Automation account {settings.AutomationAccountName} " +
            (settings.UserAssignedIdentityResourceId is null ? "(system-assigned identity)..." : "(user-assigned identity)..."));
        await EnsureSuccessAsync(HttpMethod.Put,
            $"{AccountPath}?api-version={AutomationApiVersion}",
            new
            {
                location = region,
                identity,
                tags,
                properties = new { sku = new { name = "Basic" } }
            }, cancellationToken);

        progress?.Report("Importing generic runbook...");
        await EnsureSuccessAsync(HttpMethod.Put,
            $"{AccountPath}/runbooks/{RunbookContent.RunbookName}?api-version={AutomationApiVersion}",
            new
            {
                location = region,
                properties = new
                {
                    runbookType = "PowerShell",
                    logVerbose = false,
                    logProgress = false,
                    description = "Azure VM Script Runner — generic scheduled execution. Managed by the AVSR app; do not edit."
                }
            }, cancellationToken);

        progress?.Report("Uploading runbook content...");
        await PutDraftContentAsync(cancellationToken);

        progress?.Report("Publishing runbook...");
        var publish = await SendAsync(HttpMethod.Post,
            $"{AccountPath}/runbooks/{RunbookContent.RunbookName}/publish?api-version={AutomationApiVersion}",
            null, cancellationToken);
        publish.EnsureSuccessStatusCode();
        await WaitForPublishedAsync(cancellationToken);

        progress?.Report("Granting the Automation identity access to VMs (Virtual Machine Contributor)...");
        var granted = await TryAssignVmContributorAsync(subscriptionId, cancellationToken);
        progress?.Report(granted
            ? $"Role granted on subscription {subscriptionId}."
            : "Could not assign the role automatically (needs Owner/User Access Administrator). " +
              $"Grant 'Virtual Machine Contributor' to the '{settings.AutomationAccountName}' managed identity manually.");

        progress?.Report("Provisioning complete.");
    }

    public async Task DeprovisionAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        if (string.IsNullOrEmpty(settings.AutomationSubscriptionId))
        {
            progress?.Report("Nothing to remove — no provisioned subscription recorded.");
            return;
        }

        progress?.Report($"Deleting resource group {settings.ResourceGroupName} " +
            "(Automation account, runbook and ALL schedules)...");
        var response = await SendAsync(HttpMethod.Delete,
            $"/subscriptions/{settings.AutomationSubscriptionId}/resourcegroups/{settings.ResourceGroupName}?api-version=2021-04-01",
            null, cancellationToken);

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }

        _settingsStore.Save(settings with { AutomationSubscriptionId = null });

        progress?.Report("Deletion started (runs in the background in Azure).");
        progress?.Report(settings.UserAssignedIdentityResourceId is { Length: > 0 }
            ? "Note: your user-assigned identity and its role assignments are NOT removed — clean those up yourself if no longer needed."
            : "Note: role assignments left by the deleted system identity appear as 'Identity not found' in IAM — remove them for hygiene.");
    }

    private async Task EnsureResourceProviderRegisteredAsync(
        string subscriptionId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stateResponse = await SendAsync(HttpMethod.Get,
            $"/subscriptions/{subscriptionId}/providers/Microsoft.Automation?api-version=2021-04-01",
            null, cancellationToken);
        stateResponse.EnsureSuccessStatusCode();

        using (var json = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync(cancellationToken)))
        {
            if (json.RootElement.GetProperty("registrationState").GetString() == "Registered")
            {
                return;
            }
        }

        progress?.Report("Registering the Microsoft.Automation resource provider (one-time, can take a minute)...");
        await EnsureSuccessAsync(HttpMethod.Post,
            $"/subscriptions/{subscriptionId}/providers/Microsoft.Automation/register?api-version=2021-04-01",
            null, cancellationToken);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var poll = await SendAsync(HttpMethod.Get,
                $"/subscriptions/{subscriptionId}/providers/Microsoft.Automation?api-version=2021-04-01",
                null, cancellationToken);
            if (poll.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.GetProperty("registrationState").GetString() == "Registered")
                {
                    progress?.Report("Resource provider registered.");
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            "Microsoft.Automation resource provider registration did not complete in time — retry provisioning shortly.");
    }

    public async Task<string> CreateScheduleAsync(
        ExecutionRequest request, string scheduleName, CancellationToken cancellationToken = default)
    {
        if (request.Schedule is not { } schedule)
        {
            throw new ArgumentException("Request has no schedule definition.", nameof(request));
        }

        var status = await GetStatusAsync(cancellationToken);
        if (!status.IsProvisioned)
        {
            throw new InvalidOperationException($"Scheduling infrastructure not ready: {status.Detail}");
        }

        // Best-effort: make sure the runbook identity can reach VMs in every target subscription.
        foreach (var subscription in request.Targets.Select(t => t.SubscriptionId).Distinct())
        {
            await TryAssignVmContributorAsync(subscription, cancellationToken);
        }

        var fullName = SchedulePrefix + Sanitize(scheduleName);

        await EnsureSuccessAsync(HttpMethod.Put,
            $"{AccountPath}/schedules/{Uri.EscapeDataString(fullName)}?api-version={AutomationApiVersion}",
            new
            {
                name = fullName,
                properties = AutomationScheduleMapper.ToScheduleProperties(schedule)
            }, cancellationToken);

        await EnsureSuccessAsync(HttpMethod.Put,
            $"{AccountPath}/jobSchedules/{Guid.NewGuid()}?api-version={AutomationApiVersion}",
            new
            {
                properties = new
                {
                    schedule = new { name = fullName },
                    runbook = new { name = RunbookContent.RunbookName },
                    parameters = new
                    {
                        RequestB64 = ScheduledRequestSerializer.ToRunbookParameter(
                            request, (await GetAccountIdentityAsync(cancellationToken)).ClientId)
                    }
                }
            }, cancellationToken);

        return fullName;
    }

    public async Task<IReadOnlyList<ScheduledJobInfo>> GetSchedulesAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(AutomationSubscriptionId))
        {
            return Array.Empty<ScheduledJobInfo>();
        }

        var response = await SendAsync(HttpMethod.Get,
            $"{AccountPath}/jobSchedules?api-version={AutomationApiVersion}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<ScheduledJobInfo>();
        }

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        var results = new List<ScheduledJobInfo>();
        foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
        {
            var properties = item.GetProperty("properties");
            if (properties.GetProperty("runbook").GetProperty("name").GetString() != RunbookContent.RunbookName)
            {
                continue;
            }

            var scheduleName = properties.GetProperty("schedule").GetProperty("name").GetString()!;
            var detail = await GetScheduleDetailAsync(scheduleName, cancellationToken);

            results.Add(new ScheduledJobInfo
            {
                JobScheduleId = Guid.Parse(properties.GetProperty("jobScheduleId").GetString()!),
                ScheduleName = scheduleName,
                NextRun = detail.NextRun,
                Frequency = detail.Frequency,
                Description = detail.Description
            });
        }

        return results.OrderBy(r => r.NextRun ?? DateTimeOffset.MaxValue).ToList();
    }

    public async Task DeleteScheduleAsync(string scheduleName, CancellationToken cancellationToken = default)
    {
        // Deleting the schedule also removes its job-schedule link.
        var response = await SendAsync(HttpMethod.Delete,
            $"{AccountPath}/schedules/{Uri.EscapeDataString(scheduleName)}?api-version={AutomationApiVersion}",
            null, cancellationToken);

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public void Dispose() => _http.Dispose();

    public async Task<IReadOnlyList<AutomationEnvironmentInfo>> DiscoverEnvironmentsAsync(
        bool includeAllAccounts = false,
        CancellationToken cancellationToken = default)
    {
        // Default: accounts we tagged or using the default name. "All": any Automation
        // account the user can see, for custom-named environments — adoption runs a
        // deep validation regardless.
        var filter = includeAllAccounts
            ? string.Empty
            : "| where tags['managedBy'] =~ 'AzureVmScriptRunner' or name startswith 'aa-avsr'";
        var query = $"""
            Resources
            | where type =~ 'microsoft.automation/automationaccounts'
            {filter}
            | project name, resourceGroup, subscriptionId, location
            | order by name asc
            """;

        var tenant = _session.ArmClient.GetTenants().First();
        var response = await tenant.GetResourcesAsync(new ResourceQueryContent(query), cancellationToken);
        using var json = JsonDocument.Parse(response.Value.Data.ToStream());

        var results = new List<AutomationEnvironmentInfo>();
        foreach (var row in json.RootElement.EnumerateArray().Take(20))
        {
            var subscriptionId = row.GetProperty("subscriptionId").GetString()!;
            var resourceGroup = row.GetProperty("resourceGroup").GetString()!;
            var accountName = row.GetProperty("name").GetString()!;

            // "Ready" means our generic runbook exists and is published in the account.
            var runbook = await SendAsync(HttpMethod.Get,
                $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
                $"/providers/Microsoft.Automation/automationAccounts/{accountName}" +
                $"/runbooks/{RunbookContent.RunbookName}?api-version={AutomationApiVersion}",
                null, cancellationToken);

            var hasRunbook = false;
            if (runbook.IsSuccessStatusCode)
            {
                using var runbookJson = JsonDocument.Parse(
                    await runbook.Content.ReadAsStringAsync(cancellationToken));
                hasRunbook = runbookJson.RootElement.GetProperty("properties")
                    .GetProperty("state").GetString() == "Published";
            }

            results.Add(new AutomationEnvironmentInfo(
                subscriptionId,
                resourceGroup,
                accountName,
                row.GetProperty("location").GetString() ?? string.Empty,
                hasRunbook));
        }

        return results;
    }

    /// <summary>
    /// Deep validation before adopting: an account that merely *looks* right (name)
    /// isn't enough — we verify identity, runbook presence, published state, the
    /// actual runbook content, and the VM role assignment.
    /// </summary>
    public async Task<SchedulingStatus> AdoptAsync(
        AutomationEnvironmentInfo environment, CancellationToken cancellationToken = default)
    {
        var accountPath =
            $"/subscriptions/{environment.SubscriptionId}/resourceGroups/{environment.ResourceGroup}" +
            $"/providers/Microsoft.Automation/automationAccounts/{environment.AccountName}";
        var report = new List<string>();

        // 1. Account reachable + managed identity present.
        var accountResponse = await SendAsync(HttpMethod.Get,
            $"{accountPath}?api-version={AutomationApiVersion}", null, cancellationToken);
        if (!accountResponse.IsSuccessStatusCode)
        {
            return new SchedulingStatus(false,
                $"✖ Automation account '{environment.AccountName}' is not reachable " +
                $"({(int)accountResponse.StatusCode}) — nothing was changed.");
        }

        report.Add($"✔ Automation account '{environment.AccountName}' found.");

        string? principalId = null;
        using (var accountJson = JsonDocument.Parse(
            await accountResponse.Content.ReadAsStringAsync(cancellationToken)))
        {
            if (accountJson.RootElement.TryGetProperty("identity", out var identity))
            {
                if (identity.TryGetProperty("principalId", out var p) &&
                    p.ValueKind == JsonValueKind.String)
                {
                    principalId = p.GetString();
                }
                else if (identity.TryGetProperty("userAssignedIdentities", out var ua))
                {
                    principalId = ua.EnumerateObject()
                        .Select(e => e.Value.TryGetProperty("principalId", out var pid) ? pid.GetString() : null)
                        .FirstOrDefault(id => id is not null);
                }
            }
        }

        report.Add(principalId is not null
            ? "✔ Managed identity present."
            : "✖ No managed identity on the account — scheduled runs cannot authenticate. Run Provision to fix.");

        // 2. Runbook exists and is published.
        var runbookOk = false;
        var runbookResponse = await SendAsync(HttpMethod.Get,
            $"{accountPath}/runbooks/{RunbookContent.RunbookName}?api-version={AutomationApiVersion}",
            null, cancellationToken);
        if (!runbookResponse.IsSuccessStatusCode)
        {
            report.Add($"✖ Runbook '{RunbookContent.RunbookName}' is missing — run Provision to import it.");
        }
        else
        {
            using var runbookJson = JsonDocument.Parse(
                await runbookResponse.Content.ReadAsStringAsync(cancellationToken));
            var state = runbookJson.RootElement.GetProperty("properties").GetProperty("state").GetString();
            if (state == "Published")
            {
                report.Add("✔ Runbook present and published.");
                runbookOk = true;
            }
            else
            {
                report.Add($"✖ Runbook exists but is in state '{state}' — run Provision to publish it.");
            }
        }

        // 3. Runbook content matches this app's version (a same-named runbook with
        //    different content would silently misbehave).
        var contentOk = false;
        if (runbookOk)
        {
            var contentResponse = await SendAsync(HttpMethod.Get,
                $"{accountPath}/runbooks/{RunbookContent.RunbookName}/content?api-version={AutomationApiVersion}",
                null, cancellationToken);
            if (contentResponse.IsSuccessStatusCode)
            {
                var actual = Normalize(await contentResponse.Content.ReadAsStringAsync(cancellationToken));
                contentOk = actual == Normalize(RunbookContent.Script);
                report.Add(contentOk
                    ? "✔ Runbook content matches this app's version."
                    : "⚠ Runbook content DIFFERS from this app's version (older version or manual edits) — run Provision to update it.");
            }
            else
            {
                report.Add("⚠ Could not read the runbook content to verify it.");
            }
        }

        // 4. VM Contributor role somewhere in the hosting subscription (warning only —
        //    it may legitimately be scoped to other subscriptions).
        if (principalId is not null)
        {
            var rolesResponse = await SendAsync(HttpMethod.Get,
                $"/subscriptions/{environment.SubscriptionId}/providers/Microsoft.Authorization/roleAssignments" +
                $"?$filter=principalId eq '{principalId}'&api-version=2022-04-01",
                null, cancellationToken);
            if (rolesResponse.IsSuccessStatusCode)
            {
                using var rolesJson = JsonDocument.Parse(
                    await rolesResponse.Content.ReadAsStringAsync(cancellationToken));
                var hasVmContributor = rolesJson.RootElement.GetProperty("value").EnumerateArray()
                    .Any(a => a.GetProperty("properties").GetProperty("roleDefinitionId").GetString()!
                        .EndsWith(VmContributorRoleId, StringComparison.OrdinalIgnoreCase));
                report.Add(hasVmContributor
                    ? "✔ Identity has 'Virtual Machine Contributor' in the hosting subscription."
                    : "⚠ No 'Virtual Machine Contributor' assignment found in the hosting subscription — " +
                      "it is granted automatically per target subscription when schedules are created, " +
                      "or grant it manually.");
            }
        }

        // Core checks passed well enough to adopt (repairs are idempotent via Provision).
        _settingsStore.Save(_settingsStore.Load() with
        {
            AutomationSubscriptionId = environment.SubscriptionId,
            AutomationRegion = environment.Region,
            ResourceGroupName = environment.ResourceGroup,
            AutomationAccountName = environment.AccountName
        });

        var ready = principalId is not null && runbookOk && contentOk;
        report.Add(ready
            ? "Environment adopted and ready."
            : "Environment adopted, but repairs are needed — click Provision (idempotent, fixes the items above without touching existing schedules).");

        return new SchedulingStatus(ready, string.Join("\n", report));
    }

    private static string Normalize(string script) =>
        script.Replace("\r\n", "\n").Trim();

    public async Task<StorageAccessResult> EnsurePsadtStorageAccessAsync(
        Uri packageUrl, CancellationToken cancellationToken = default)
    {
        var storageAccountName = packageUrl.Host.Split('.')[0];

        var (principalId, _) = await GetAccountIdentityAsync(cancellationToken);
        if (principalId is null)
        {
            return new StorageAccessResult(false,
                "Scheduling infrastructure not provisioned yet — provision first, then re-create the schedule.");
        }

        // Locate the storage account (it can live in any visible subscription).
        var query = $"""
            Resources
            | where type =~ 'microsoft.storage/storageaccounts'
            | where name =~ '{storageAccountName}'
            | project id
            """;
        var tenant = _session.ArmClient.GetTenants().First();
        var response = await tenant.GetResourcesAsync(new ResourceQueryContent(query), cancellationToken);
        using var json = JsonDocument.Parse(response.Value.Data.ToStream());
        var storageId = json.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } row
            ? row.GetProperty("id").GetString()
            : null;

        if (storageId is null)
        {
            return new StorageAccessResult(false,
                $"Storage account '{storageAccountName}' was not found in any subscription you can see — " +
                "grant the scheduler identity 'Storage Blob Data Reader' on it manually.");
        }

        var assignmentName = DeterministicGuid($"avsr-storage-{storageId}-{principalId}");
        var assignment = await SendAsync(HttpMethod.Put,
            $"{storageId}/providers/Microsoft.Authorization/roleAssignments/{assignmentName}?api-version=2022-04-01",
            new
            {
                properties = new
                {
                    roleDefinitionId =
                        $"{storageId}/providers/Microsoft.Authorization/roleDefinitions/{StorageBlobDataReaderRoleId}",
                    principalId,
                    principalType = "ServicePrincipal"
                }
            }, cancellationToken);

        if (assignment.IsSuccessStatusCode || assignment.StatusCode == HttpStatusCode.Conflict)
        {
            return new StorageAccessResult(true,
                $"Scheduler identity has 'Storage Blob Data Reader' on '{storageAccountName}' " +
                "(granted/verified just now). No per-VM setup needed; allow a few minutes for RBAC propagation.");
        }

        return new StorageAccessResult(false,
            $"Could not grant the role automatically ({(int)assignment.StatusCode} — you need Owner/User Access " +
            $"Administrator on the storage account). Grant 'Storage Blob Data Reader' on '{storageAccountName}' " +
            $"to the '{Settings.AutomationAccountName}' managed identity manually before the schedule fires.");
    }

    private async Task<(DateTimeOffset? NextRun, string? Frequency, string? Description)> GetScheduleDetailAsync(
        string scheduleName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get,
            $"{AccountPath}/schedules/{Uri.EscapeDataString(scheduleName)}?api-version={AutomationApiVersion}",
            null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, null, null);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var properties = json.RootElement.GetProperty("properties");

        DateTimeOffset? nextRun = properties.TryGetProperty("nextRun", out var next) &&
            next.ValueKind == JsonValueKind.String
                ? next.GetDateTimeOffset()
                : null;
        var frequency = properties.TryGetProperty("frequency", out var freq) ? freq.GetString() : null;
        var interval = properties.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number
            ? iv.GetInt32()
            : (int?)null;

        var frequencyText = frequency == "OneTime" || interval is null or 1
            ? frequency
            : $"Every {interval} {frequency}s";

        return (nextRun, frequencyText, null);
    }

    private async Task PutDraftContentAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{AccountPath}/runbooks/{RunbookContent.RunbookName}/draft/content?api-version={AutomationApiVersion}")
        {
            Content = new StringContent(RunbookContent.Script)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/powershell");
        await AuthorizeAsync(request, cancellationToken);

        var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // 202 means the upload is processed asynchronously; give it a moment before publish.
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task WaitForPublishedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var response = await SendAsync(HttpMethod.Get,
                $"{AccountPath}/runbooks/{RunbookContent.RunbookName}?api-version={AutomationApiVersion}",
                null, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.GetProperty("properties").GetProperty("state").GetString() == "Published")
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("Runbook did not reach the Published state in time.");
    }

    private async Task<bool> TryAssignVmContributorAsync(
        string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var principalId = await GetAccountPrincipalIdAsync(cancellationToken);
            if (principalId is null)
            {
                return false;
            }

            // Deterministic assignment name → idempotent PUT (409 means it already exists).
            var assignmentName = DeterministicGuid($"avsr-{subscriptionId}-{principalId}");
            var scope = $"/subscriptions/{subscriptionId}";

            var response = await SendAsync(HttpMethod.Put,
                $"{scope}/providers/Microsoft.Authorization/roleAssignments/{assignmentName}?api-version=2022-04-01",
                new
                {
                    properties = new
                    {
                        roleDefinitionId =
                            $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{VmContributorRoleId}",
                        principalId,
                        principalType = "ServicePrincipal"
                    }
                }, cancellationToken);

            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<string?> GetAccountPrincipalIdAsync(CancellationToken cancellationToken) =>
        (await GetAccountIdentityAsync(cancellationToken)).PrincipalId;

    /// <summary>Principal + client IDs for either identity flavour on the account.</summary>
    private async Task<(string? PrincipalId, string? ClientId)> GetAccountIdentityAsync(
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get,
            $"{AccountPath}?api-version={AutomationApiVersion}", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("identity", out var identity))
        {
            return (null, null);
        }

        // System-assigned: principalId at the top level, token endpoint needs no client_id.
        if (identity.TryGetProperty("principalId", out var principal) &&
            principal.ValueKind == JsonValueKind.String)
        {
            return (principal.GetString(), null);
        }

        // User-assigned: IDs live per-identity in the dictionary.
        if (identity.TryGetProperty("userAssignedIdentities", out var userAssigned))
        {
            foreach (var entry in userAssigned.EnumerateObject())
            {
                var principalId = entry.Value.TryGetProperty("principalId", out var p) ? p.GetString() : null;
                var clientId = entry.Value.TryGetProperty("clientId", out var c) ? c.GetString() : null;
                return (principalId, clientId);
            }
        }

        return (null, null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, pathAndQuery);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        await AuthorizeAsync(request, cancellationToken);
        return await _http.SendAsync(request, cancellationToken);
    }

    private async Task EnsureSuccessAsync(
        HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        var response = await SendAsync(method, pathAndQuery, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Azure API {method} {pathAndQuery} failed ({(int)response.StatusCode}): {Truncate(detail)}");
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _session.Credential.GetTokenAsync(ArmScope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
