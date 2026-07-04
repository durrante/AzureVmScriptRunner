using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;

namespace AzureVmScriptRunner.Application.Preflight;

/// <summary>
/// Run Command and extensions travel through the Azure VM guest agent; a NotReady
/// agent guarantees failure, so fail fast with an actionable message instead.
/// </summary>
public sealed class AgentHealthPreflightCheck : IPreflightCheck
{
    public string Name => "VM agent health";

    public Task<PreflightResult> CheckAsync(
        ExecutionRequest request, VmTarget target, CancellationToken cancellationToken)
    {
        if (request.Payload.Type == ExecutionType.PowerOperation)
        {
            return Task.FromResult(PreflightResult.Pass); // control plane bypasses the agent
        }

        return Task.FromResult(target.AgentStatus switch
        {
            VmAgentStatus.NotReady => PreflightResult.Fail(
                "Azure VM guest agent is not ready. Restart the 'Windows Azure Guest Agent' service or reboot the VM."),
            _ => PreflightResult.Pass
        });
    }
}
