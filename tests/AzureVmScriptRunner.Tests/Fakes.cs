using System.Collections.Concurrent;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.History;
using AzureVmScriptRunner.Domain.Targets;
using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.Tests;

public sealed class FakeProvider : IExecutionProvider
{
    private int _inFlight;

    public ExecutionProviderKind Kind { get; init; } = ExecutionProviderKind.RunCommand;
    public Func<ExecutionRequest, bool> CanExecutePredicate { get; init; } = _ => true;
    public Func<ExecutionRequest, VmTarget, CancellationToken, Task<VmExecutionResult>>? OnExecute { get; init; }
    public TimeSpan ExecutionDelay { get; init; } = TimeSpan.Zero;

    public int MaxObservedConcurrency { get; private set; }
    public ConcurrentBag<VmTarget> ExecutedTargets { get; } = new();
    public ConcurrentBag<ExecutionPayload> SeenPayloads { get; } = new();

    public bool CanExecute(ExecutionRequest request) => CanExecutePredicate(request);

    public async Task<VmExecutionResult> ExecuteAsync(
        ExecutionRequest request, VmTarget target, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _inFlight);
        lock (ExecutedTargets)
        {
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, now);
        }

        try
        {
            ExecutedTargets.Add(target);
            SeenPayloads.Add(request.Payload);

            if (ExecutionDelay > TimeSpan.Zero)
            {
                await Task.Delay(ExecutionDelay, cancellationToken);
            }

            if (OnExecute is not null)
            {
                return await OnExecute(request, target, cancellationToken);
            }

            return new VmExecutionResult
            {
                Target = target,
                Status = VmExecutionStatus.Succeeded,
                Provider = Kind,
                ExitCode = 0,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

public sealed class FakeHistory : IJobHistoryService
{
    public ConcurrentBag<JobRecord> Records { get; } = new();
    public bool ThrowOnRecord { get; set; }

    public Task RecordAsync(JobRecord record, CancellationToken cancellationToken = default)
    {
        if (ThrowOnRecord)
        {
            throw new InvalidOperationException("History store unavailable.");
        }

        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobRecord>> QueryAsync(
        JobHistoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobRecord>>(Records.ToList());
}

public sealed class FakeSavedTasks : ISavedTaskRepository
{
    private readonly Dictionary<Guid, SavedTask> _tasks = new();

    public void Add(SavedTask task) => _tasks[task.Id] = task;

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
        _tasks.Remove(id);
        return Task.CompletedTask;
    }
}

public static class TestData
{
    public static VmTarget Vm(
        string name = "vm-01",
        VmPowerState power = VmPowerState.Running,
        VmAgentStatus agent = VmAgentStatus.Ready) => new()
    {
        SubscriptionId = "00000000-0000-0000-0000-000000000001",
        ResourceGroup = "rg-test",
        Name = name,
        ResourceId = $"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/{name}",
        PowerState = power,
        AgentStatus = agent
    };

    public static ExecutionRequest Request(
        ExecutionPayload? payload = null,
        IReadOnlyList<VmTarget>? targets = null,
        ExecutionOptions? options = null) => new()
    {
        DisplayName = "Test execution",
        Payload = payload ?? new PowerShellPayload("Get-Service"),
        Targets = targets ?? new[] { Vm() },
        RequestedBy = "admin@contoso.com",
        Options = options ?? ExecutionOptions.Default
    };
}
