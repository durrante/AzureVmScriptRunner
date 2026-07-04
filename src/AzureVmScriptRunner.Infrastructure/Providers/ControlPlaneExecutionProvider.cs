using Azure;
using Azure.Core;
using Azure.ResourceManager.Compute;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Infrastructure.Azure;

namespace AzureVmScriptRunner.Infrastructure.Providers;

/// <summary>
/// Power operations via the ARM control plane rather than in-guest scripts: works even
/// when the guest agent is unhealthy and is natively audited in the Activity Log.
/// </summary>
public sealed class ControlPlaneExecutionProvider : IExecutionProvider
{
    private readonly AzureSession _session;

    public ControlPlaneExecutionProvider(AzureSession session) => _session = session;

    public ExecutionProviderKind Kind => ExecutionProviderKind.ControlPlane;

    public bool CanExecute(ExecutionRequest request) =>
        !request.IsScheduled && request.Payload.Type == ExecutionType.PowerOperation;

    public async Task<VmExecutionResult> ExecuteAsync(
        ExecutionRequest request, VmTarget target, CancellationToken cancellationToken)
    {
        var operation = ((PowerOperationPayload)request.Payload).Operation;
        var startedAt = DateTimeOffset.UtcNow;
        var vm = _session.ArmClient.GetVirtualMachineResource(new ResourceIdentifier(target.ResourceId));

        try
        {
            switch (operation)
            {
                case PowerOperation.Start:
                    await vm.PowerOnAsync(WaitUntil.Completed, cancellationToken);
                    break;
                case PowerOperation.Restart:
                    await vm.RestartAsync(WaitUntil.Completed, cancellationToken);
                    break;
                case PowerOperation.PowerOff:
                    await vm.PowerOffAsync(WaitUntil.Completed, skipShutdown: false, cancellationToken);
                    break;
                case PowerOperation.Deallocate:
                    await vm.DeallocateAsync(WaitUntil.Completed, cancellationToken: cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Power operation {operation} is not supported.");
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 408 or 429 or 500 or 502 or 503 or 504)
        {
            throw new TransientExecutionException(
                $"Azure API returned {ex.Status} ({ex.ErrorCode}) for {target.Name}.", ex);
        }

        return new VmExecutionResult
        {
            Target = target,
            Status = VmExecutionStatus.Succeeded,
            Provider = ExecutionProviderKind.ControlPlane,
            Output = $"{operation} completed.",
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
