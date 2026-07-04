using AzureVmScriptRunner.Domain.Scheduling;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Domain.Execution;

public enum ExecutionProviderKind
{
    RunCommand,
    AutomationRunbook,
    ControlPlane
}

public sealed record ExecutionOptions
{
    public static readonly ExecutionOptions Default = new();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(90);

    /// <summary>Maximum VMs executed concurrently within one request.</summary>
    public int MaxParallelism { get; init; } = 10;

    /// <summary>Skip power-state / agent preflight validation (advanced use).</summary>
    public bool SkipPreflight { get; init; }

    /// <summary>Retries per VM on transient provider failures (throttling, timeouts at the API layer).</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record ExecutionRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Human-readable label shown in progress UI and job history.</summary>
    public required string DisplayName { get; init; }

    public required ExecutionPayload Payload { get; init; }

    public required IReadOnlyList<VmTarget> Targets { get; init; }

    /// <summary>Null means run immediately.</summary>
    public ScheduleDefinition? Schedule { get; init; }

    public ExecutionOptions Options { get; init; } = ExecutionOptions.Default;

    /// <summary>UPN of the signed-in administrator, recorded for auditing.</summary>
    public required string RequestedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsScheduled => Schedule is not null;
}
