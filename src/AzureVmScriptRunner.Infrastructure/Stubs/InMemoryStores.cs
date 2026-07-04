using System.Collections.Concurrent;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.History;
using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.Infrastructure.Stubs;

/// <summary>Placeholder until the SQLite history store lands in Phase 3.</summary>
public sealed class InMemoryJobHistoryService : IJobHistoryService
{
    private readonly ConcurrentBag<JobRecord> _records = new();

    public Task RecordAsync(JobRecord record, CancellationToken cancellationToken = default)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobRecord>> QueryAsync(
        JobHistoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobRecord>>(
            _records.OrderByDescending(r => r.StartedAt).Take(query.MaxResults).ToList());
}

/// <summary>Placeholder until the persistent saved-task store lands in Phase 3.</summary>
public sealed class InMemorySavedTaskRepository : ISavedTaskRepository
{
    private readonly ConcurrentDictionary<Guid, SavedTask> _tasks = new();

    public Task<SavedTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks.GetValueOrDefault(id));

    public Task<IReadOnlyList<SavedTask>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedTask>>(_tasks.Values.ToList());

    public Task SaveAsync(SavedTask task, CancellationToken cancellationToken = default)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _tasks.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
