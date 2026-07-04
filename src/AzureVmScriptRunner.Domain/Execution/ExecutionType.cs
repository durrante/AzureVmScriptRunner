namespace AzureVmScriptRunner.Domain.Execution;

public enum ExecutionType
{
    PowerShell,
    Cmd,
    PsadtDeployment,
    SavedTask,
    PowerOperation
}
