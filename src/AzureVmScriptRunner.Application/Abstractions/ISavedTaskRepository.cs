using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.Application.Abstractions;

public interface ISavedTaskRepository
{
    Task<SavedTask?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedTask>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SavedTask task, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
