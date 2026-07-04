using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Preflight;

/// <summary>
/// In-guest execution requires a running VM. Power operations are exempt — starting a
/// deallocated VM is the whole point.
/// </summary>
public sealed class PowerStatePreflightCheck : IPreflightCheck
{
    public string Name => "Power state";

    public Task<PreflightResult> CheckAsync(
        ExecutionRequest request, VmTarget target, CancellationToken cancellationToken)
    {
        if (request.Payload.Type == ExecutionType.PowerOperation)
        {
            return Task.FromResult(PreflightResult.Pass);
        }

        return Task.FromResult(target.PowerState switch
        {
            VmPowerState.Running => PreflightResult.Pass,
            VmPowerState.Unknown => PreflightResult.Pass, // don't block on stale discovery data
            var state => PreflightResult.Fail($"VM is {state}; in-guest execution requires a running VM.")
        });
    }
}
