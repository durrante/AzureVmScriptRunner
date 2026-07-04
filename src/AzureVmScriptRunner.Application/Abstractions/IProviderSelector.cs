using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Application.Abstractions;

/// <summary>Chooses the execution provider for a request.</summary>
public interface IProviderSelector
{
    /// <exception cref="InvalidOperationException">No registered provider can execute the request.</exception>
    IExecutionProvider Select(ExecutionRequest request);
}
