using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Application.Execution;

/// <summary>
/// Policy: power operations → control plane; scheduled requests → Automation runbook;
/// everything immediate → Run Command. Encoded as an ordered preference list evaluated
/// against each provider's own <see cref="IExecutionProvider.CanExecute"/>, so adding a
/// provider does not require changing the selector.
/// </summary>
public sealed class ProviderSelector : IProviderSelector
{
    private readonly IReadOnlyList<IExecutionProvider> _providers;

    public ProviderSelector(IEnumerable<IExecutionProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IExecutionProvider Select(ExecutionRequest request)
    {
        var candidates = _providers.Where(p => p.CanExecute(request)).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No execution provider can handle request '{request.DisplayName}' " +
                $"(type: {request.Payload.Type}, scheduled: {request.IsScheduled}).");
        }

        return candidates.OrderBy(p => Preference(p.Kind, request)).First();
    }

    private static int Preference(ExecutionProviderKind kind, ExecutionRequest request) =>
        (kind, request) switch
        {
            (ExecutionProviderKind.ControlPlane, { Payload.Type: ExecutionType.PowerOperation }) => 0,
            (ExecutionProviderKind.AutomationRunbook, { IsScheduled: true }) => 1,
            (ExecutionProviderKind.RunCommand, { IsScheduled: false }) => 2,
            _ => 100
        };
}
