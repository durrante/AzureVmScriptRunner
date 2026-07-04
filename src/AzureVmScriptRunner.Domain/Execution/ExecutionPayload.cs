using System.Text.Json.Serialization;

namespace AzureVmScriptRunner.Domain.Execution;

/// <summary>
/// The "what to run" half of an execution request. One payload hierarchy covers every
/// execution type so orchestration, history and scheduling share a single workflow.
/// Polymorphic JSON attributes allow saved tasks and scheduled requests to round-trip
/// through persistence without a bespoke serializer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PowerShellPayload), "powershell")]
[JsonDerivedType(typeof(CmdPayload), "cmd")]
[JsonDerivedType(typeof(PsadtPayload), "psadt")]
[JsonDerivedType(typeof(SavedTaskPayload), "savedTask")]
[JsonDerivedType(typeof(PowerOperationPayload), "powerOperation")]
public abstract record ExecutionPayload
{
    [JsonIgnore]
    public abstract ExecutionType Type { get; }
}

public sealed record PowerShellPayload(string Script) : ExecutionPayload
{
    public override ExecutionType Type => ExecutionType.PowerShell;
}

public sealed record CmdPayload(string CommandLine) : ExecutionPayload
{
    public override ExecutionType Type => ExecutionType.Cmd;
}

/// <summary>
/// Reference to a stored task. Resolved to the underlying payload by the orchestrator
/// before dispatch, so providers never see this type.
/// </summary>
public sealed record SavedTaskPayload(Guid SavedTaskId) : ExecutionPayload
{
    public override ExecutionType Type => ExecutionType.SavedTask;
}

public enum PowerOperation
{
    Start,
    Restart,
    PowerOff,
    Deallocate
}

/// <summary>
/// Control-plane power operation (ARM), preferred over in-guest scripts for
/// restart/stop because it works even when the guest agent is unhealthy and is
/// natively recorded in the Azure Activity Log.
/// </summary>
public sealed record PowerOperationPayload(PowerOperation Operation) : ExecutionPayload
{
    public override ExecutionType Type => ExecutionType.PowerOperation;
}
