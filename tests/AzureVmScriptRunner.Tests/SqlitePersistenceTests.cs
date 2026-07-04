using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.History;
using AzureVmScriptRunner.Domain.Tasks;
using AzureVmScriptRunner.Infrastructure.Persistence;

namespace AzureVmScriptRunner.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"avsr-test-{Guid.NewGuid():N}.db");

    private readonly SqliteDatabase _database;

    public SqlitePersistenceTests() => _database = new SqliteDatabase(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task JobRecord_round_trips_with_all_fields()
    {
        var service = new SqliteJobHistoryService(_database);
        var record = new JobRecord
        {
            RequestId = Guid.NewGuid(),
            RequestedBy = "admin@contoso.com",
            DisplayName = "Install 7-Zip",
            ExecutionType = ExecutionType.PsadtDeployment,
            Provider = ExecutionProviderKind.RunCommand,
            VmName = "vm-01",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            VmResourceId = "/subscriptions/sub-1/.../vm-01",
            ScriptContent = "PSADT Install (Silent) from https://x/y.zip",
            Status = VmExecutionStatus.SucceededRebootRequired,
            ExitCode = 3010,
            ExitCodeDescription = "Success – reboot required",
            Output = "installed",
            Error = null,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow
        };

        await service.RecordAsync(record);
        var results = await service.QueryAsync(new JobHistoryQuery());

        var loaded = Assert.Single(results);
        Assert.Equal(record, loaded);
    }

    [Fact]
    public async Task Query_filters_by_vm_search_and_time()
    {
        var service = new SqliteJobHistoryService(_database);
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await service.RecordAsync(new JobRecord
            {
                RequestId = Guid.NewGuid(),
                RequestedBy = "admin@contoso.com",
                DisplayName = i % 2 == 0 ? "Flush DNS" : "GPUpdate",
                ExecutionType = ExecutionType.Cmd,
                VmName = $"vm-{i % 2}",
                SubscriptionId = "sub-1",
                ResourceGroup = "rg-1",
                Status = VmExecutionStatus.Succeeded,
                StartedAt = baseTime.AddHours(-i)
            });
        }

        var byVm = await service.QueryAsync(new JobHistoryQuery { VmName = "VM-1" });
        Assert.Equal(2, byVm.Count);

        var bySearch = await service.QueryAsync(new JobHistoryQuery { SearchText = "flush" });
        Assert.Equal(3, bySearch.Count);

        var byTime = await service.QueryAsync(new JobHistoryQuery { From = baseTime.AddHours(-1.5) });
        Assert.Equal(2, byTime.Count);

        var ordered = await service.QueryAsync(new JobHistoryQuery());
        Assert.Equal(ordered.OrderByDescending(r => r.StartedAt), ordered);
    }

    [Fact]
    public async Task SavedTask_round_trips_including_polymorphic_payload()
    {
        var repository = new SqliteSavedTaskRepository(_database);
        var task = new SavedTask
        {
            Name = "Deploy 7-Zip",
            Category = "Deployments",
            Payload = new PsadtPayload
            {
                PackageUrl = new Uri("https://store.blob.core.windows.net/packages/7zip.zip"),
                DeploymentType = PsadtDeploymentType.Install,
                AdditionalArguments = "-AllowRebootPassThru"
            },
            Options = ExecutionOptions.Default with { MaxParallelism = 5 }
        };

        await repository.SaveAsync(task);
        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Name, loaded.Name);
        Assert.Equal(5, loaded.Options.MaxParallelism);
        var payload = Assert.IsType<PsadtPayload>(loaded.Payload);
        Assert.Equal(task.Payload, payload);
    }

    [Fact]
    public async Task SavedTask_update_and_delete()
    {
        var repository = new SqliteSavedTaskRepository(_database);
        var task = new SavedTask { Name = "Flush DNS", Payload = new CmdPayload("ipconfig /flushdns") };

        await repository.SaveAsync(task);
        await repository.SaveAsync(task with
        {
            Name = "Flush DNS (renamed)",
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = "admin@contoso.com"
        });

        var all = await repository.GetAllAsync();
        Assert.Equal("Flush DNS (renamed)", Assert.Single(all).Name);

        await repository.DeleteAsync(task.Id);
        Assert.Empty(await repository.GetAllAsync());
    }
}
