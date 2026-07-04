using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Abstractions;

public sealed record VmDiscoveryFilter
{
    public string? SearchText { get; init; }
    public IReadOnlyList<string>? SubscriptionIds { get; init; }
    public IReadOnlyList<string>? ResourceGroups { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
    public VmPowerState? PowerState { get; init; }
}

public sealed record SubscriptionInfo(string Id, string Name);

/// <summary>
/// Discovers Azure Windows VMs. Implemented over Azure Resource Graph (one cross-
/// subscription query) rather than per-subscription ARM enumeration.
/// </summary>
public interface IVmDiscoveryService
{
    Task<IReadOnlyList<VmTarget>> DiscoverAsync(
        VmDiscoveryFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>All subscriptions visible to the signed-in user (with or without VMs).</summary>
    Task<IReadOnlyList<SubscriptionInfo>> GetSubscriptionsAsync(
        CancellationToken cancellationToken = default);
}
