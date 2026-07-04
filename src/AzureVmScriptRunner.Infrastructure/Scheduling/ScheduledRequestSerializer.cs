using System.Text;
using System.Text.Json;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Infrastructure.Providers;
using AzureVmScriptRunner.Infrastructure.Psadt;

namespace AzureVmScriptRunner.Infrastructure.Scheduling;

/// <summary>
/// Converts an execution request into the base64 JSON parameter consumed by the
/// generic runbook. The in-guest script is fully rendered here, so the runbook stays
/// a dumb executor with no knowledge of payload types.
/// </summary>
public static class ScheduledRequestSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToRunbookParameter(ExecutionRequest request, string? identityClientId = null)
    {
        // Scheduled PSADT runs can't carry a SAS (it would expire before firing).
        // Instead the script gets a placeholder that the runbook swaps for a fresh
        // storage token minted by the scheduler's identity at fire time.
        var isPsadt = request.Payload is PsadtPayload;
        var script = request.Payload is PsadtPayload psadt
            ? PsadtScriptBuilder.Build(psadt, fallbackSasUri: null, includeRunbookTokenFallback: true)
            : RunCommandExecutionProvider.BuildScript(request.Payload);

        var model = new
        {
            displayName = request.DisplayName,
            requestedBy = request.RequestedBy,
            timeoutSeconds = (int)request.Options.Timeout.TotalSeconds,
            // Runbook executes targets in parallel batches of this size.
            maxParallelism = Math.Max(1, request.Options.MaxParallelism),
            // For logging only — the plain blob URL, never a SAS.
            packageUrl = (request.Payload as PsadtPayload)?.PackageUrl.ToString(),
            // Set when the Automation account uses a user-assigned identity; the
            // sandbox token endpoint needs the client_id to pick it.
            identityClientId,
            // Tells the runbook to mint a storage token and inject it into the script.
            needsStorageToken = isPsadt,
            script,
            targets = request.Targets.Select(t => new
            {
                name = t.Name,
                resourceId = t.ResourceId
            })
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, JsonOptions)));
    }
}
