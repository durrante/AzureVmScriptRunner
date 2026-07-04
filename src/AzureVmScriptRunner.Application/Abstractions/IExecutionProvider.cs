using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Abstractions;

/// <summary>
/// Executes a resolved payload against a single VM. Implementations wrap one Azure
/// mechanism (Managed Run Command, Automation runbook, ARM control plane) and must
/// honour cancellation; the orchestrator owns parallelism, timeout and retries.
/// </summary>
public interface IExecutionProvider
{
    ExecutionProviderKind Kind { get; }

    /// <summary>Whether this provider can execute the given request (payload + timing).</summary>
    bool CanExecute(ExecutionRequest request);

    Task<VmExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        VmTarget target,
        CancellationToken cancellationToken);
}
