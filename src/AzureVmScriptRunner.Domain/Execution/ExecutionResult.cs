using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Domain.Execution;

public enum VmExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    /// <summary>Completed successfully but the VM needs a reboot (e.g. exit code 3010/1641).</summary>
    SucceededRebootRequired,
    Failed,
    PreflightFailed,
    TimedOut,
    Cancelled
}

/// <summary>Result of one execution against one VM.</summary>
public sealed record VmExecutionResult
{
    public required VmTarget Target { get; init; }
    public required VmExecutionStatus Status { get; init; }
    public ExecutionProviderKind? Provider { get; init; }
    public int? ExitCode { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan? Duration =>
        CompletedAt is { } end ? end - StartedAt : null;

    public bool IsSuccess =>
        Status is VmExecutionStatus.Succeeded or VmExecutionStatus.SucceededRebootRequired;
}

/// <summary>Aggregate outcome of an execution request across all targets.</summary>
public sealed record ExecutionReport
{
    public required Guid RequestId { get; init; }
    public required IReadOnlyList<VmExecutionResult> Results { get; init; }

    public int SucceededCount => Results.Count(r => r.IsSuccess);
    public int FailedCount => Results.Count(r => !r.IsSuccess);
    public bool AllSucceeded => Results.All(r => r.IsSuccess);
}
