using AzureVmScriptRunner.Domain.History;

namespace AzureVmScriptRunner.Application.Abstractions;

public sealed record JobHistoryQuery
{
    public string? SearchText { get; init; }
    public string? VmName { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int MaxResults { get; init; } = 500;
}

/// <summary>
/// Job history persistence. Local SQLite in v1; the interface is storage-agnostic so a
/// shared store (Table Storage) can replace it without touching orchestration or UI.
/// </summary>
public interface IJobHistoryService
{
    Task RecordAsync(JobRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> QueryAsync(JobHistoryQuery query, CancellationToken cancellationToken = default);
}
