namespace AzureVmScriptRunner.Domain.Targets;

public enum VmPowerState
{
    Unknown,
    Running,
    Starting,
    Stopping,
    Stopped,
    Deallocating,
    Deallocated
}

public enum VmAgentStatus
{
    Unknown,
    Ready,
    NotReady
}

/// <summary>
/// A discovered Azure Windows VM. Identity is the ARM resource ID; the remaining
/// properties are a point-in-time snapshot from discovery.
/// </summary>
public sealed record VmTarget
{
    public required string SubscriptionId { get; init; }

    /// <summary>Display name of the subscription (falls back to the ID when unresolved).</summary>
    public string? SubscriptionName { get; init; }

    public required string ResourceGroup { get; init; }
    public required string Name { get; init; }

    /// <summary>Full ARM resource ID.</summary>
    public required string ResourceId { get; init; }

    public string? Region { get; init; }
    public VmPowerState PowerState { get; init; } = VmPowerState.Unknown;

    /// <summary>True when the VM has a system-assigned managed identity (preferred for package downloads).</summary>
    public bool HasSystemAssignedIdentity { get; init; }

    public VmAgentStatus AgentStatus { get; init; } = VmAgentStatus.Unknown;
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>();

    public override string ToString() => $"{Name} ({ResourceGroup})";
}
