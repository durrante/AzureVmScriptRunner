using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Application.Preflight;
using AzureVmScriptRunner.Infrastructure.Azure;
using AzureVmScriptRunner.Infrastructure.Discovery;
using AzureVmScriptRunner.Infrastructure.Persistence;
using AzureVmScriptRunner.Infrastructure.Providers;
using AzureVmScriptRunner.Infrastructure.Psadt;
using AzureVmScriptRunner.Infrastructure.Scheduling;
using AzureVmScriptRunner.Infrastructure.Stubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureVmScriptRunner.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the execution pipeline and Azure-backed services against an
    /// established session. Storage registrations use TryAdd so real persistence
    /// (Phase 3) can be registered ahead of this call without changes here.
    /// </summary>
    public static IServiceCollection AddAzureVmScriptRunner(
        this IServiceCollection services, AzureSession session)
    {
        services.AddSingleton(session);

        services.AddSingleton<IVmDiscoveryService, ResourceGraphVmDiscoveryService>();
        services.AddSingleton<IPsadtScriptFactory, PsadtScriptFactory>();
        services.AddSingleton<LocalSettingsStore>();
        services.AddSingleton<IScheduleService, AutomationScheduleService>();
        services.AddSingleton<IExecutionProvider, RunCommandExecutionProvider>();
        services.AddSingleton<IExecutionProvider, ControlPlaneExecutionProvider>();
        services.AddSingleton<IProviderSelector, ProviderSelector>();

        services.AddSingleton<IPreflightCheck, PowerStatePreflightCheck>();
        services.AddSingleton<IPreflightCheck, AgentHealthPreflightCheck>();

        services.TryAddSingleton<IJobHistoryService, InMemoryJobHistoryService>();
        services.TryAddSingleton<ISavedTaskRepository, InMemorySavedTaskRepository>();

        services.AddSingleton<ExecutionOrchestrator>();

        return services;
    }

    /// <summary>
    /// Registers the local SQLite persistence (job history + saved tasks). Call before
    /// <see cref="AddAzureVmScriptRunner"/> so these win over the in-memory stubs.
    /// </summary>
    public static IServiceCollection AddLocalPersistence(
        this IServiceCollection services, string? databasePath = null)
    {
        services.AddSingleton(new SqliteDatabase(databasePath));
        services.AddSingleton<IJobHistoryService, SqliteJobHistoryService>();
        services.AddSingleton<ISavedTaskRepository, SqliteSavedTaskRepository>();
        return services;
    }
}
