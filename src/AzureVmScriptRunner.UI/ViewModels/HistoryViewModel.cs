using System.Collections.ObjectModel;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.History;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed class HistoryRowViewModel
{
    public required JobRecord Record { get; init; }

    public string When => Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string VmName => Record.VmName;
    public string DisplayName => Record.DisplayName;
    public string TypeText => Record.ExecutionType.ToString();
    public string StatusText => Record.Status.ToString();
    public string ExitText => Record.ExitCode?.ToString() ?? string.Empty;
    public string DurationText => Record.Duration is { } d ? $"{d.TotalSeconds:F0}s" : string.Empty;
    public string RequestedBy => Record.RequestedBy;
    public bool IsSuccess => Record.Status is Domain.Execution.VmExecutionStatus.Succeeded
        or Domain.Execution.VmExecutionStatus.SucceededRebootRequired;

    public string DetailText =>
        $"""
        {DisplayName} — {StatusText}{(Record.ExitCodeDescription is { } d ? $" ({d})" : "")}
        VM: {VmName}   RG: {Record.ResourceGroup}   By: {RequestedBy}   Provider: {Record.Provider}
        Started: {When}   Duration: {DurationText}

        ── Script ──
        {Record.ScriptContent}

        ── Output ──
        {(string.IsNullOrWhiteSpace(Record.Output) ? "(none)" : Record.Output)}

        ── Errors ──
        {(string.IsNullOrWhiteSpace(Record.Error) ? "(none)" : Record.Error)}
        """;
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IJobHistoryService _history;

    public ObservableCollection<HistoryRowViewModel> Records { get; } = new();

    public string[] RangeOptions { get; } = { "Last 24 hours", "Last 7 days", "Last 30 days", "All" };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _range = "Last 7 days";

    [ObservableProperty]
    private HistoryRowViewModel? _selectedRecord;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public HistoryViewModel(IJobHistoryService history) => _history = history;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var from = Range switch
        {
            "Last 24 hours" => DateTimeOffset.UtcNow.AddDays(-1),
            "Last 7 days" => DateTimeOffset.UtcNow.AddDays(-7),
            "Last 30 days" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => (DateTimeOffset?)null
        };

        var records = await _history.QueryAsync(new JobHistoryQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            From = from,
            MaxResults = 500
        });

        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(new HistoryRowViewModel { Record = record });
        }

        var failed = Records.Count(r => !r.IsSuccess);
        SummaryText = $"{Records.Count} record(s) · {failed} failed";
    }

    partial void OnRangeChanged(string value) => _ = RefreshAsync();
}
