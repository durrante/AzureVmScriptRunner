using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Execution;

public enum ExecutionProgressKind
{
    VmStarted,
    VmRetrying,
    VmCompleted
}

/// <summary>Per-VM progress event surfaced to the UI during a bulk execution.</summary>
public sealed record ExecutionProgress
{
    public required ExecutionProgressKind Kind { get; init; }
    public required VmTarget Target { get; init; }
    public required int CompletedCount { get; init; }
    public required int TotalCount { get; init; }
    public VmExecutionResult? Result { get; init; }
    public string? Message { get; init; }
}
