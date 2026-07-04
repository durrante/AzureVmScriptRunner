using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Domain.History;

/// <summary>
/// Persistent history entry for one execution against one VM. Deliberately flat and
/// denormalised: history must remain readable even after VMs or saved tasks are deleted.
/// Includes the resolved script content — the Azure Activity Log records that a Run
/// Command happened but hides the script body, so this is where the detail lives.
/// </summary>
public sealed record JobRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }
    public required string RequestedBy { get; init; }
    public required string DisplayName { get; init; }
    public required ExecutionType ExecutionType { get; init; }
    public ExecutionProviderKind? Provider { get; init; }

    public required string VmName { get; init; }
    public required string SubscriptionId { get; init; }
    public required string ResourceGroup { get; init; }
    public string? VmResourceId { get; init; }

    /// <summary>The exact script/command dispatched, after saved-task resolution.</summary>
    public string? ScriptContent { get; init; }

    public required VmExecutionStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public string? ExitCodeDescription { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public TimeSpan? Duration => CompletedAt is { } end ? end - StartedAt : null;
}
