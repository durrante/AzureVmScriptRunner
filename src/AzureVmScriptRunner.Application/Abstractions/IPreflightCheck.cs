using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Abstractions;

public sealed record PreflightResult(bool Passed, string? Reason = null)
{
    public static readonly PreflightResult Pass = new(true);
    public static PreflightResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// A validation run against each target before dispatch (power state, agent health,
/// payload sanity). Checks run in registration order; the first failure stops the
/// pipeline for that VM and the VM is reported as PreflightFailed without executing.
/// </summary>
public interface IPreflightCheck
{
    string Name { get; }

    Task<PreflightResult> CheckAsync(
        ExecutionRequest request,
        VmTarget target,
        CancellationToken cancellationToken);
}
