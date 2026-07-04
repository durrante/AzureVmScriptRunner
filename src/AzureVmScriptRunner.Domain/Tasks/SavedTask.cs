using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Domain.Tasks;

/// <summary>
/// A reusable, editable execution definition (e.g. "Flush DNS", "Restart Spooler").
/// Wraps any payload type except <see cref="SavedTaskPayload"/> (no nesting).
/// </summary>
public sealed record SavedTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public required ExecutionPayload Payload { get; init; }
    public ExecutionOptions Options { get; init; } = ExecutionOptions.Default;
    public bool IsBuiltIn { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ModifiedAt { get; init; }
    public string? ModifiedBy { get; init; }
}
