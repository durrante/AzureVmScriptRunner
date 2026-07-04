namespace AzureVmScriptRunner.Application.Execution;

/// <summary>
/// Thrown by providers for failures worth retrying (HTTP 429/503, conflict while a
/// previous run command is being deleted). Non-transient failures should throw any
/// other exception type and will fail the VM immediately.
/// </summary>
public sealed class TransientExecutionException : Exception
{
    public TransientExecutionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
