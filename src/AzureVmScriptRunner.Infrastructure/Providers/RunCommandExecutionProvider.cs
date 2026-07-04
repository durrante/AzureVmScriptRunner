using Azure;
using Azure.Core;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Infrastructure.Azure;

namespace AzureVmScriptRunner.Infrastructure.Providers;

/// <summary>
/// Immediate in-guest execution via Managed Run Commands (the ARM `runCommands` child
/// resource), chosen over the legacy action Run Command because it supports concurrent
/// executions per VM, real timeouts, cancellation (resource delete) and full output.
/// The run command resource is deleted after completion so VMs don't accumulate them.
/// </summary>
public sealed class RunCommandExecutionProvider : IExecutionProvider
{
    // ARM constraint for managed run command timeoutInSeconds.
    private const int MinTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 5400;

    private readonly AzureSession _session;
    private readonly Psadt.IPsadtScriptFactory _psadtScriptFactory;

    public RunCommandExecutionProvider(AzureSession session, Psadt.IPsadtScriptFactory psadtScriptFactory)
    {
        _session = session;
        _psadtScriptFactory = psadtScriptFactory;
    }

    public ExecutionProviderKind Kind => ExecutionProviderKind.RunCommand;

    public bool CanExecute(ExecutionRequest request) =>
        !request.IsScheduled &&
        request.Payload.Type is ExecutionType.PowerShell or ExecutionType.Cmd or ExecutionType.PsadtDeployment;

    public async Task<VmExecutionResult> ExecuteAsync(
        ExecutionRequest request, VmTarget target, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var vm = _session.ArmClient.GetVirtualMachineResource(new ResourceIdentifier(target.ResourceId));
        var commandName = $"AVSR-{Guid.NewGuid():N}"[..24];

        var script = request.Payload is PsadtPayload psadt
            ? await _psadtScriptFactory.CreateScriptAsync(psadt, target, cancellationToken)
            : BuildScript(request.Payload);

        var data = new VirtualMachineRunCommandData(new AzureLocation(target.Region ?? "westeurope"))
        {
            Source = new VirtualMachineRunCommandScriptSource { Script = script },
            TimeoutInSeconds = (int)Math.Clamp(
                request.Options.Timeout.TotalSeconds, MinTimeoutSeconds, MaxTimeoutSeconds),
            AsyncExecution = false
        };

        var runCommands = vm.GetVirtualMachineRunCommands();

        try
        {
            await CreateAsync(runCommands, commandName, data, cancellationToken);

            // The create LRO completing does not guarantee the instance view is
            // populated; fetch it explicitly with $expand.
            var final = await runCommands.GetAsync(commandName, expand: "instanceView", cancellationToken);
            return MapResult(target, final.Value.Data.InstanceView, startedAt);
        }
        catch (RequestFailedException ex) when (IsTransient(ex))
        {
            throw new TransientExecutionException(
                $"Azure API returned {ex.Status} ({ex.ErrorCode}) for {target.Name}.", ex);
        }
        finally
        {
            // Best-effort cleanup. On cancellation this is also what aborts the
            // running script, so it must not use the (already cancelled) token.
            await TryDeleteAsync(runCommands, commandName);
        }
    }

    private static async Task CreateAsync(
        VirtualMachineRunCommandCollection runCommands,
        string commandName,
        VirtualMachineRunCommandData data,
        CancellationToken cancellationToken)
    {
        await runCommands.CreateOrUpdateAsync(WaitUntil.Completed, commandName, data, cancellationToken);
    }

    private static async Task TryDeleteAsync(
        VirtualMachineRunCommandCollection runCommands, string commandName)
    {
        try
        {
            var existing = await runCommands.GetAsync(commandName);
            await existing.Value.DeleteAsync(WaitUntil.Started);
        }
        catch (RequestFailedException)
        {
        }
    }

    /// <summary>
    /// Managed run commands execute scripts with PowerShell on Windows. CMD payloads
    /// are wrapped and base64-encoded so quoting in the user's command line survives
    /// the trip, with the exit code propagated.
    /// </summary>
    internal static string BuildScript(ExecutionPayload payload) => payload switch
    {
        PowerShellPayload ps => ps.Script,
        CmdPayload cmd =>
            $"""
            $command = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(cmd.CommandLine))}'))
            cmd.exe /d /c $command
            exit $LASTEXITCODE
            """,
        _ => throw new NotSupportedException($"Payload type {payload.Type} is not supported by Run Command.")
    };

    private static VmExecutionResult MapResult(
        VmTarget target,
        VirtualMachineRunCommandInstanceView? view,
        DateTimeOffset startedAt)
    {
        var exitCode = view?.ExitCode;
        var status = view?.ExecutionState?.ToString() switch
        {
            "Canceled" => VmExecutionStatus.Cancelled,
            "TimedOut" => VmExecutionStatus.TimedOut,
            _ when exitCode is { } code => ExitCodeClassifier.Classify(code),
            "Succeeded" => VmExecutionStatus.Succeeded,
            _ => VmExecutionStatus.Failed
        };

        return new VmExecutionResult
        {
            Target = target,
            Status = status,
            Provider = ExecutionProviderKind.RunCommand,
            ExitCode = exitCode,
            Output = view?.Output,
            Error = view?.Error,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool IsTransient(RequestFailedException ex) =>
        ex.Status is 408 or 429 or 500 or 502 or 503 or 504 ||
        string.Equals(ex.ErrorCode, "Conflict", StringComparison.OrdinalIgnoreCase);
}
