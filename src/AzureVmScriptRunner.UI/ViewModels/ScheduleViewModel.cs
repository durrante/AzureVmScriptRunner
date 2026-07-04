using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Scheduling;
using AzureVmScriptRunner.Domain.Tasks;
using AzureVmScriptRunner.Infrastructure.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed record ScheduleSourceOption(string Label, SavedTask? Task)
{
    public override string ToString() => Label;
}

public sealed partial class DayOption : ObservableObject
{
    public required DayOfWeek Day { get; init; }
    public string Label => Day.ToString()[..3];

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>Editable key/value row for the resource-tags table.</summary>
public sealed class TagEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly IScheduleService _scheduleService;
    private readonly ISavedTaskRepository _savedTasks;
    private readonly IVmDiscoveryService _discovery;
    private readonly LocalSettingsStore _settingsStore;
    private readonly VmsViewModel _vms;
    private readonly string _requestedBy;
    private readonly Action<string> _setStatus;
    private readonly StringBuilder _logBuilder = new();

    public ObservableCollection<ScheduledJobInfo> Schedules { get; } = new();
    public ObservableCollection<ScheduleSourceOption> SourceOptions { get; } = new();
    public ObservableCollection<string> SelectedVmNames { get; } = new();
    public ObservableCollection<string> SubscriptionOptions { get; } = new();
    public ObservableCollection<DayOption> WeekDays { get; } = new(
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayOption { Day = d }));

    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } =
        TimeZoneInfo.GetSystemTimeZones().ToList();

    public string[] RegionOptions { get; } =
        { "uksouth", "ukwest", "westeurope", "northeurope", "eastus", "eastus2", "westus2", "australiaeast" };

    public ScheduleFrequency[] Frequencies { get; } =
        (ScheduleFrequency[])Enum.GetValues(typeof(ScheduleFrequency));

    // ── Existing environments (multi-admin: adopt instead of duplicating) ──
    public ObservableCollection<AutomationEnvironmentInfo> FoundEnvironments { get; } = new();

    [ObservableProperty]
    private AutomationEnvironmentInfo? _selectedEnvironment;

    [ObservableProperty]
    private bool _hasFoundEnvironments;

    /// <summary>Include every Automation account (custom naming conventions).</summary>
    [ObservableProperty]
    private bool _showAllAccounts;

    // ── Provisioning ──
    [ObservableProperty]
    private bool _isProvisioned;

    [ObservableProperty]
    private string _provisionStatus = "Checking...";

    [ObservableProperty]
    private string? _provisionSubscription;

    [ObservableProperty]
    private string _provisionRegion = "uksouth";

    [ObservableProperty]
    private string _resourceGroupName = "rg-avsr-automation";

    [ObservableProperty]
    private string _accountName = "aa-avsr";

    public ObservableCollection<TagEntry> Tags { get; } = new();

    [ObservableProperty]
    private string _userAssignedIdentityId = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // ── New schedule ──
    [ObservableProperty]
    private string _scheduleName = string.Empty;

    [ObservableProperty]
    private ScheduleSourceOption? _selectedSource;

    [ObservableProperty]
    private string _scriptText = string.Empty;

    [ObservableProperty]
    private bool _isCustomSource = true;

    [ObservableProperty]
    private DateTime _startDate = DateTime.Today.AddDays(1);

    [ObservableProperty]
    private string _startTime = "03:00";

    [ObservableProperty]
    private TimeZoneInfo _selectedTimeZone = TimeZoneInfo.Local;

    [ObservableProperty]
    private ScheduleFrequency _frequency = ScheduleFrequency.Once;

    [ObservableProperty]
    private int _interval = 1;

    [ObservableProperty]
    private DateTime? _expiryDate;

    [ObservableProperty]
    private bool _isWeekly;

    // ── Monthly recurrence (mirrors the Azure portal's options) ──
    [ObservableProperty]
    private bool _isMonthly;

    public string[] MonthlyModes { get; } = { "Days of month", "Week-day occurrence" };

    [ObservableProperty]
    private string _monthlyMode = "Days of month";

    [ObservableProperty]
    private bool _isMonthDayMode = true;

    [ObservableProperty]
    private string _monthDaysText = string.Empty;

    [ObservableProperty]
    private bool _runOnLastDay;

    public string[] OccurrenceOptions { get; } = { "First", "Second", "Third", "Fourth", "Last" };

    [ObservableProperty]
    private string _selectedOccurrence = "First";

    public DayOfWeek[] OccurrenceDays { get; } =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
          DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    [ObservableProperty]
    private DayOfWeek _selectedOccurrenceDay = DayOfWeek.Monday;

    [ObservableProperty]
    private string _targetsText = "No VMs selected";

    [ObservableProperty]
    private string _logText = string.Empty;

    public ScheduleViewModel(
        IScheduleService scheduleService,
        ISavedTaskRepository savedTasks,
        IVmDiscoveryService discovery,
        LocalSettingsStore settingsStore,
        VmsViewModel vms,
        string requestedBy,
        Action<string> setStatus)
    {
        _scheduleService = scheduleService;
        _savedTasks = savedTasks;
        _discovery = discovery;
        _settingsStore = settingsStore;
        _vms = vms;
        _requestedBy = requestedBy;
        _setStatus = setStatus;
        _vms.SelectionChanged += (_, _) => RefreshSelection();
        RefreshSelection();

        var settings = _settingsStore.Load();
        ResourceGroupName = settings.ResourceGroupName;
        AccountName = settings.AutomationAccountName;
        UserAssignedIdentityId = settings.UserAssignedIdentityResourceId ?? string.Empty;

        foreach (var tag in settings.ParseTags())
        {
            Tags.Add(new TagEntry { Key = tag.Key, Value = tag.Value });
        }
    }

    [RelayCommand]
    private void AddTag() => Tags.Add(new TagEntry());

    [RelayCommand]
    private void RemoveTag(TagEntry? entry)
    {
        if (entry is not null)
        {
            Tags.Remove(entry);
        }
    }

    public async Task InitializeAsync()
    {
        SourceOptions.Clear();
        SourceOptions.Add(new ScheduleSourceOption("Custom PowerShell script", null));
        foreach (var task in await _savedTasks.GetAllAsync())
        {
            SourceOptions.Add(new ScheduleSourceOption($"Task: {task.Name}", task));
        }

        SelectedSource = SourceOptions[0];

        // All subscriptions the user can see — the Automation account often lives in a
        // management subscription with no VMs in it.
        try
        {
            var subscriptions = await _discovery.GetSubscriptionsAsync();
            SubscriptionOptions.Clear();
            foreach (var subscription in subscriptions)
            {
                SubscriptionOptions.Add($"{subscription.Name} ({subscription.Id})");
            }

            ProvisionSubscription = SubscriptionOptions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log($"Could not list subscriptions: {ex.Message}", "WARN");
        }

        await RefreshStatusAsync();
        await RefreshSchedulesAsync();

        // Fresh install? Look for an environment a colleague already provisioned.
        if (!IsProvisioned)
        {
            await ScanEnvironmentsAsync();
        }
    }

    partial void OnShowAllAccountsChanged(bool value) => _ = ScanEnvironmentsAsync();

    [RelayCommand]
    private async Task ScanEnvironmentsAsync()
    {
        try
        {
            var environments = await _scheduleService.DiscoverEnvironmentsAsync(ShowAllAccounts);
            FoundEnvironments.Clear();
            foreach (var environment in environments)
            {
                FoundEnvironments.Add(environment);
            }

            HasFoundEnvironments = FoundEnvironments.Count > 0;
            SelectedEnvironment = FoundEnvironments.FirstOrDefault(e => e.HasRunbook)
                ?? FoundEnvironments.FirstOrDefault();

            if (HasFoundEnvironments && !IsProvisioned)
            {
                Log($"Found {FoundEnvironments.Count} existing AVSR environment(s) in this tenant — " +
                    "select one and click 'Use existing' instead of provisioning a duplicate.", "OK");
            }
        }
        catch (Exception ex)
        {
            Log($"Environment scan failed: {ex.Message}", "WARN");
        }
    }

    [RelayCommand]
    private async Task AdoptEnvironmentAsync()
    {
        if (SelectedEnvironment is null)
        {
            Log("Select an environment to adopt first.", "WARN");
            return;
        }

        IsBusy = true;
        try
        {
            Log($"Validating '{SelectedEnvironment.AccountName}' (identity, runbook, content, roles)...");
            var status = await _scheduleService.AdoptAsync(SelectedEnvironment);

            foreach (var line in status.Detail.Split('\n'))
            {
                Log(line, line.StartsWith('✖') ? "FAIL" : line.StartsWith('⚠') ? "WARN" : "OK");
            }

            // Show the full checklist so a "looks right" account can't be adopted blind.
            MessageBox.Show(
                $"Validation of '{SelectedEnvironment.AccountName}':\n\n{status.Detail}",
                status.IsProvisioned ? "Environment adopted — ready" : "Environment adopted — needs repair",
                MessageBoxButton.OK,
                status.IsProvisioned ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (status.Detail.StartsWith('✖') && !status.Detail.Contains("adopted"))
            {
                return; // unreachable account: nothing was changed
            }

            IsProvisioned = status.IsProvisioned;
            ProvisionStatus = status.IsProvisioned
                ? $"Ready — adopted '{SelectedEnvironment.AccountName}' in {SelectedEnvironment.ResourceGroup}."
                : "Adopted but needs repair — click Provision to fix the reported items (existing schedules are untouched).";
            ResourceGroupName = SelectedEnvironment.ResourceGroup;
            AccountName = SelectedEnvironment.AccountName;
            await RefreshSchedulesAsync();
        }
        catch (Exception ex)
        {
            Log($"Adoption failed: {ex.Message}", "FAIL");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshSelection()
    {
        var targets = _vms.SelectedTargets;
        SelectedVmNames.Clear();
        foreach (var target in targets)
        {
            SelectedVmNames.Add(target.Name);
        }

        TargetsText = targets.Count switch
        {
            0 => "No VMs selected — select targets on the VMs page first",
            1 => "1 VM selected",
            var n => $"{n} VMs selected"
        };
    }

    partial void OnSelectedSourceChanged(ScheduleSourceOption? value)
    {
        IsCustomSource = value?.Task is null;
        SuggestName();
    }

    partial void OnFrequencyChanged(ScheduleFrequency value)
    {
        IsWeekly = value == ScheduleFrequency.Weekly;
        IsMonthly = value == ScheduleFrequency.Monthly;
        SuggestName();
    }

    partial void OnMonthlyModeChanged(string value) =>
        IsMonthDayMode = value == "Days of month";

    /// <summary>Suggested convention: sched-avsr-&lt;what&gt;-&lt;frequency&gt;.</summary>
    private void SuggestName()
    {
        if (!string.IsNullOrWhiteSpace(ScheduleName) && !ScheduleName.StartsWith("sched-avsr-"))
        {
            return; // don't clobber a manually entered name
        }

        var what = SelectedSource?.Task?.Name.Replace(" ", string.Empty) ?? "Script";
        ScheduleName = $"sched-avsr-{what}-{Frequency}".ToLowerInvariant();
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _scheduleService.GetStatusAsync();
            IsProvisioned = status.IsProvisioned;
            ProvisionStatus = status.Detail;
        }
        catch (Exception ex)
        {
            IsProvisioned = false;
            ProvisionStatus = $"Status check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ProvisionAsync()
    {
        if (ProvisionSubscription is null)
        {
            Log("Choose the subscription that should host the Automation account.", "WARN");
            return;
        }

        SaveInfraSettings();
        var subscriptionId = ProvisionSubscription.Split('(').Last().TrimEnd(')');
        var identityText = string.IsNullOrWhiteSpace(UserAssignedIdentityId)
            ? "a new system-assigned managed identity"
            : "your user-assigned managed identity";

        var confirmed = MessageBox.Show(
            $"This creates (or verifies) in subscription {subscriptionId}, region {ProvisionRegion}:\n\n" +
            $"  • Resource group '{ResourceGroupName}'\n" +
            $"  • Automation account '{AccountName}' with {identityText}\n" +
            "  • One generic runbook 'Invoke-AvsrScheduledExecution' (published)\n" +
            "  • Registers the Microsoft.Automation resource provider if needed\n" +
            "  • Attempts to grant 'Virtual Machine Contributor' to the identity\n\n" +
            "Est. cost: Automation free tier covers 500 job-minutes/month.\n\nContinue?",
            "Provision scheduling infrastructure",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => Log(message));
            await _scheduleService.ProvisionAsync(subscriptionId, ProvisionRegion, progress);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log($"Provisioning failed: {ex.Message}", "FAIL");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeprovisionAsync()
    {
        var confirmed = MessageBox.Show(
            $"Delete resource group '{ResourceGroupName}' including the Automation account and ALL schedules?\n\n" +
            "This cannot be undone.",
            "Remove scheduling infrastructure",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _scheduleService.DeprovisionAsync(new Progress<string>(m => Log(m)));
            Schedules.Clear();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Log($"Removal failed: {ex.Message}", "FAIL");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveInfraSettings()
    {
        _settingsStore.Save(_settingsStore.Load() with
        {
            ResourceGroupName = string.IsNullOrWhiteSpace(ResourceGroupName) ? "rg-avsr-automation" : ResourceGroupName.Trim(),
            AutomationAccountName = string.IsNullOrWhiteSpace(AccountName) ? "aa-avsr" : AccountName.Trim(),
            TagsRaw = string.Join(", ", Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Key))
                .Select(t => $"{t.Key.Trim()}={t.Value.Trim()}")),
            UserAssignedIdentityResourceId =
                string.IsNullOrWhiteSpace(UserAssignedIdentityId) ? null : UserAssignedIdentityId.Trim()
        });
    }

    [RelayCommand]
    private async Task CreateScheduleAsync()
    {
        var targets = _vms.SelectedTargets;
        if (targets.Count == 0)
        {
            Log("Select at least one VM on the VMs page first.", "WARN");
            return;
        }

        if (string.IsNullOrWhiteSpace(ScheduleName))
        {
            Log("Give the schedule a name.", "WARN");
            return;
        }

        ExecutionPayload payload;
        string description;
        if (SelectedSource?.Task is { } task)
        {
            payload = task.Payload;
            description = $"task '{task.Name}'";
            if (payload is PsadtPayload psadtPayload &&
                !await ConfirmPsadtPrerequisitesAsync(psadtPayload))
            {
                Log("Schedule creation cancelled at the PSADT prerequisite check.", "WARN");
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ScriptText))
            {
                Log("Enter the PowerShell script to schedule.", "WARN");
                return;
            }

            payload = new PowerShellPayload(ScriptText);
            description = "custom PowerShell script";
        }

        if (!TimeSpan.TryParseExact(StartTime.Trim(), new[] { @"hh\:mm", @"h\:mm" },
                CultureInfo.InvariantCulture, out var timeOfDay))
        {
            Log("Start time must be 24-hour HH:mm — e.g. 16:30 for 4:30 PM, 03:00 for 3 AM.", "WARN");
            return;
        }

        var startLocal = new DateTimeOffset(StartDate.Date + timeOfDay,
            SelectedTimeZone.GetUtcOffset(StartDate.Date + timeOfDay));
        var selectedDays = WeekDays.Where(d => d.IsChecked).Select(d => d.Day).ToList();

        // Monthly recurrence details (mirrors the Azure portal options).
        var monthDays = new List<int>();
        MonthlyOccurrence? monthlyWeekDay = null;
        var frequencyText = Frequency.ToString();
        if (Frequency == ScheduleFrequency.Monthly)
        {
            if (IsMonthDayMode)
            {
                foreach (var part in MonthDaysText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var day) && day is >= 1 and <= 31)
                    {
                        monthDays.Add(day);
                    }
                    else
                    {
                        Log($"'{part}' is not a valid day of month (1–31).", "WARN");
                        return;
                    }
                }

                if (RunOnLastDay)
                {
                    monthDays.Add(-1);
                }

                if (monthDays.Count == 0)
                {
                    Log("Monthly schedule: enter at least one day of month (e.g. 1,15) or tick 'Last day'.", "WARN");
                    return;
                }

                frequencyText = $"Monthly on day(s) {MonthDaysText}{(RunOnLastDay ? " + last day" : "")}";
            }
            else
            {
                var occurrence = SelectedOccurrence switch
                {
                    "First" => 1, "Second" => 2, "Third" => 3, "Fourth" => 4, _ => -1
                };
                monthlyWeekDay = new MonthlyOccurrence(occurrence, SelectedOccurrenceDay);
                frequencyText = $"Monthly on the {SelectedOccurrence.ToLowerInvariant()} {SelectedOccurrenceDay}";
            }
        }
        else if (Frequency == ScheduleFrequency.Weekly && selectedDays.Count > 0)
        {
            frequencyText = $"Weekly on {string.Join(", ", selectedDays)}";
        }

        var listed = string.Join("\n", targets.Take(15).Select(t => $"  • {t.Name}"));
        var more = targets.Count > 15 ? $"\n  … and {targets.Count - 15} more" : string.Empty;
        var massWarning = targets.Count > 5
            ? $"⚠ MASS OPERATION: this schedule targets {targets.Count} VMs on a recurring basis. " +
              "Please check the target list carefully before proceeding.\n\n"
            : string.Empty;

        var confirmed = MessageBox.Show(
            $"{massWarning}Schedule {description}\n{frequencyText} starting {startLocal:g} ({SelectedTimeZone.Id})" +
            $"{(ExpiryDate is { } exp ? $", expires {exp:d}" : "")}\n\n" +
            $"Target VMs ({targets.Count}):\n{listed}{more}\n\nCreate schedule?",
            "Confirm schedule",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        var request = new ExecutionRequest
        {
            DisplayName = ScheduleName,
            Payload = payload,
            Targets = targets,
            RequestedBy = _requestedBy,
            Schedule = new ScheduleDefinition
            {
                StartTime = startLocal,
                Frequency = Frequency,
                Interval = Math.Max(1, Interval),
                TimeZoneId = SelectedTimeZone.Id,
                ExpiryTime = ExpiryDate is { } expiry
                    ? new DateTimeOffset(expiry.Date.AddHours(23).AddMinutes(59),
                        SelectedTimeZone.GetUtcOffset(expiry))
                    : null,
                DaysOfWeek = selectedDays,
                MonthDays = monthDays,
                MonthlyWeekDay = monthlyWeekDay
            }
        };

        IsBusy = true;
        try
        {
            var name = await _scheduleService.CreateScheduleAsync(request, ScheduleName);
            Log($"Schedule '{name}' created — {frequencyText} from {startLocal:g} {SelectedTimeZone.Id}.", "OK");
            _setStatus($"Schedule '{name}' created.");
            await RefreshSchedulesAsync();
        }
        catch (Exception ex)
        {
            Log($"Schedule creation failed: {ex.Message}", "FAIL");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSchedulesAsync()
    {
        try
        {
            var schedules = await _scheduleService.GetSchedulesAsync();
            Schedules.Clear();
            foreach (var schedule in schedules)
            {
                Schedules.Add(schedule);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not list schedules: {ex.Message}", "WARN");
        }
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduledJobInfo? schedule)
    {
        if (schedule is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Delete schedule '{schedule.ScheduleName}'?", "Delete schedule",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _scheduleService.DeleteScheduleAsync(schedule.ScheduleName);
            Log($"Schedule '{schedule.ScheduleName}' deleted.", "OK");
            await RefreshSchedulesAsync();
        }
        catch (Exception ex)
        {
            Log($"Delete failed: {ex.Message}", "FAIL");
        }
    }

    /// <summary>
    /// One requirement, checked and (where possible) fixed automatically: the
    /// scheduler's identity needs read access to the package storage account. No
    /// per-VM identity setup is required.
    /// </summary>
    private async Task<bool> ConfirmPsadtPrerequisitesAsync(PsadtPayload payload)
    {
        Log("Checking scheduler access to the package storage account...");
        StorageAccessResult access;
        try
        {
            access = await _scheduleService.EnsurePsadtStorageAccessAsync(payload.PackageUrl);
        }
        catch (Exception ex)
        {
            access = new StorageAccessResult(false, $"Access check failed: {ex.Message}");
        }

        Log(access.Detail, access.Granted ? "OK" : "WARN");

        var statusLine = access.Granted
            ? $"✔ READY: {access.Detail}"
            : $"⚠ ACTION NEEDED: {access.Detail}";

        return MessageBox.Show(
            "HOW SCHEDULED PSADT DEPLOYMENTS ACCESS THE PACKAGE\n\n" +
            "When the schedule fires, the scheduler (Automation account) mints a fresh, " +
            "short-lived storage token with ITS OWN identity and passes it to each VM for " +
            "the download.\n\n" +
            "That means exactly ONE thing must be true — once, for the whole environment:\n\n" +
            "    The scheduler's managed identity has 'Storage Blob Data Reader'\n" +
            "    on the storage account holding your packages.\n\n" +
            "The target VMs need NO managed identities and NO storage permissions.\n" +
            "(If a VM does have its own identity with access, it is used first.)\n\n" +
            statusLine + "\n\nContinue creating the schedule?",
            "PSADT schedule prerequisites",
            MessageBoxButton.YesNo,
            access.Granted ? MessageBoxImage.Question : MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
    }

    private void Log(string text, string level = "INFO")
    {
        var prefix = level switch
        {
            "OK" => "[OK]   ",
            "WARN" => "[WARN] ",
            "FAIL" => "[FAIL] ",
            _ => "[INFO] "
        };
        _logBuilder.AppendLine($"{DateTime.Now:HH:mm:ss}  {prefix}{text}");
        LogText = _logBuilder.ToString();
    }
}
