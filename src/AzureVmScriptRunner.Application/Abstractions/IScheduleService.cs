using AzureVmScriptRunner.Domain.Execution;

namespace AzureVmScriptRunner.Application.Abstractions;

public sealed record SchedulingStatus(bool IsProvisioned, string Detail);

/// <summary>An AVSR scheduling environment discovered in the tenant.</summary>
public sealed record AutomationEnvironmentInfo(
    string SubscriptionId,
    string ResourceGroup,
    string AccountName,
    string Region,
    bool HasRunbook)
{
    public override string ToString() =>
        $"{AccountName} · {ResourceGroup} · sub {SubscriptionId[..8]}… " +
        (HasRunbook ? "(ready)" : "(runbook missing — needs re-provision)");
}

public sealed record ScheduledJobInfo
{
    public required Guid JobScheduleId { get; init; }
    public required string ScheduleName { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public string? Frequency { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Scheduling via Azure Automation: one generic published runbook receives a
/// serialized execution request; each schedule is a cheap Schedule + JobSchedule pair
/// linked to that runbook — never one runbook per task. Scheduling is request-level
/// (provision once, fire on a timer), which is why it is a separate service rather
/// than an <see cref="IExecutionProvider"/> in the per-VM dispatch loop.
/// </summary>
public interface IScheduleService
{
    Task<SchedulingStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates/verifies the Automation infrastructure (resource group, Automation
    /// account with system-assigned managed identity, published generic runbook).
    /// Idempotent.
    /// </summary>
    Task ProvisionAsync(
        string subscriptionId,
        string region,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the Automation schedule for the request. The request must have Schedule set.</summary>
    Task<string> CreateScheduleAsync(
        ExecutionRequest request,
        string scheduleName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledJobInfo>> GetSchedulesAsync(CancellationToken cancellationToken = default);

    Task DeleteScheduleAsync(string scheduleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the scheduling infrastructure (deletes the resource group and
    /// everything in it — all schedules included).
    /// </summary>
    Task DeprovisionAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds AVSR scheduling environments already deployed in the tenant (e.g. by a
    /// colleague), so a fresh install can adopt one instead of provisioning a duplicate.
    /// With <paramref name="includeAllAccounts"/>, lists every Automation account the
    /// user can see (for environments created under custom naming conventions).
    /// </summary>
    Task<IReadOnlyList<AutomationEnvironmentInfo>> DiscoverEnvironmentsAsync(
        bool includeAllAccounts = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Points this installation at an existing environment after validating it.
    /// Returns the resulting status (not provisioned + detail if validation failed).
    /// </summary>
    Task<SchedulingStatus> AdoptAsync(
        AutomationEnvironmentInfo environment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the scheduler's managed identity can read the PSADT package storage
    /// account (grants Storage Blob Data Reader if the caller has rights). One grant
    /// covers every VM in every schedule — no per-VM identity setup needed.
    /// </summary>
    Task<StorageAccessResult> EnsurePsadtStorageAccessAsync(
        Uri packageUrl, CancellationToken cancellationToken = default);
}

public sealed record StorageAccessResult(bool Granted, string Detail);
