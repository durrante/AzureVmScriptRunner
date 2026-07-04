using System.Text.Json;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Infrastructure.Azure;

namespace AzureVmScriptRunner.Infrastructure.Discovery;

/// <summary>
/// VM discovery over Azure Resource Graph: one KQL query returns every Windows VM
/// visible to the signed-in user across all subscriptions in ~1s, versus minutes of
/// per-subscription ARM enumeration. Results are a point-in-time snapshot; ARG's
/// power state can lag reality by a few minutes.
/// </summary>
public sealed class ResourceGraphVmDiscoveryService : IVmDiscoveryService
{
    private const string BaseQuery = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | where tostring(properties.storageProfile.osDisk.osType) =~ 'Windows'
        | extend powerState = tostring(properties.extended.instanceView.powerState.code),
            hasSystemIdentity = identity.type contains 'SystemAssigned'
        | join kind=leftouter (
            ResourceContainers
            | where type =~ 'microsoft.resources/subscriptions'
            | project subscriptionId, subscriptionName = name) on subscriptionId
        | project id, name, resourceGroup, subscriptionId, subscriptionName, location, tags,
            powerState, hasSystemIdentity
        | order by name asc
        """;

    private readonly AzureSession _session;

    public ResourceGraphVmDiscoveryService(AzureSession session) => _session = session;

    public async Task<IReadOnlyList<VmTarget>> DiscoverAsync(
        VmDiscoveryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = _session.ArmClient.GetTenants().First();
        var results = new List<VmTarget>();
        string? skipToken = null;

        do
        {
            var content = new ResourceQueryContent(BaseQuery)
            {
                Options = new ResourceQueryRequestOptions { SkipToken = skipToken }
            };

            if (filter?.SubscriptionIds is { Count: > 0 } subs)
            {
                foreach (var sub in subs)
                {
                    content.Subscriptions.Add(sub);
                }
            }

            var response = await tenant.GetResourcesAsync(content, cancellationToken);
            using var json = JsonDocument.Parse(response.Value.Data.ToStream());

            foreach (var row in json.RootElement.EnumerateArray())
            {
                results.Add(MapRow(row));
            }

            skipToken = response.Value.SkipToken;
        } while (!string.IsNullOrEmpty(skipToken));

        return ApplyClientSideFilter(results, filter);
    }

    public async Task<IReadOnlyList<SubscriptionInfo>> GetSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        const string query = """
            ResourceContainers
            | where type =~ 'microsoft.resources/subscriptions'
            | project subscriptionId, name
            | order by name asc
            """;

        var tenant = _session.ArmClient.GetTenants().First();
        var response = await tenant.GetResourcesAsync(new ResourceQueryContent(query), cancellationToken);
        using var json = JsonDocument.Parse(response.Value.Data.ToStream());

        var results = new List<SubscriptionInfo>();
        foreach (var row in json.RootElement.EnumerateArray())
        {
            results.Add(new SubscriptionInfo(
                row.GetProperty("subscriptionId").GetString()!,
                row.GetProperty("name").GetString()!));
        }

        return results;
    }

    private static VmTarget MapRow(JsonElement row)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsElement.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
            }
        }

        return new VmTarget
        {
            ResourceId = row.GetProperty("id").GetString()!,
            Name = row.GetProperty("name").GetString()!,
            ResourceGroup = row.GetProperty("resourceGroup").GetString()!,
            SubscriptionId = row.GetProperty("subscriptionId").GetString()!,
            SubscriptionName = row.TryGetProperty("subscriptionName", out var subName)
                ? subName.GetString()
                : null,
            Region = row.GetProperty("location").GetString(),
            PowerState = ParsePowerState(row.GetProperty("powerState").GetString()),
            HasSystemAssignedIdentity =
                row.TryGetProperty("hasSystemIdentity", out var identity) &&
                identity.ValueKind == JsonValueKind.True,
            Tags = tags
        };
    }

    private static VmPowerState ParsePowerState(string? code) => code switch
    {
        "PowerState/running" => VmPowerState.Running,
        "PowerState/starting" => VmPowerState.Starting,
        "PowerState/stopping" => VmPowerState.Stopping,
        "PowerState/stopped" => VmPowerState.Stopped,
        "PowerState/deallocating" => VmPowerState.Deallocating,
        "PowerState/deallocated" => VmPowerState.Deallocated,
        _ => VmPowerState.Unknown
    };

    private static IReadOnlyList<VmTarget> ApplyClientSideFilter(
        List<VmTarget> vms, VmDiscoveryFilter? filter)
    {
        if (filter is null)
        {
            return vms;
        }

        IEnumerable<VmTarget> query = vms;

        if (filter.ResourceGroups is { Count: > 0 } groups)
        {
            query = query.Where(v => groups.Contains(v.ResourceGroup, StringComparer.OrdinalIgnoreCase));
        }

        if (filter.PowerState is { } state)
        {
            query = query.Where(v => v.PowerState == state);
        }

        if (filter.Tags is { Count: > 0 } tags)
        {
            query = query.Where(v => tags.All(t =>
                v.Tags.TryGetValue(t.Key, out var value) &&
                string.Equals(value, t.Value, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            query = query.Where(v =>
                v.Name.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) ||
                v.ResourceGroup.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }
}
