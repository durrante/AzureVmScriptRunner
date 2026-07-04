using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Targets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

/// <summary>
/// Shared machinery for views that execute against the current VM selection (Run,
/// Deploy): live selected-VM list, explicit confirmation dialog, per-VM progress rows,
/// activity log, cancellation.
/// </summary>
public abstract partial class ExecutionHostViewModel : ObservableObject
{
    private readonly ExecutionOrchestrator _orchestrator;
    private readonly VmsViewModel _vms;
    private readonly string _requestedBy;
    private readonly Action<string> _setStatus;
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ExecutionRowViewModel> Rows { get; } = new();

    /// <summary>Names of currently selected VMs, always visible so nothing runs against a stale selection.</summary>
    public ObservableCollection<string> SelectedVmNames { get; } = new();

    [ObservableProperty]
    private string _targetsText = "No VMs selected";

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _logText = string.Empty;

    protected ExecutionHostViewModel(
        ExecutionOrchestrator orchestrator,
        VmsViewModel vms,
        string requestedBy,
        Action<string> setStatus)
    {
        _orchestrator = orchestrator;
        _vms = vms;
        _requestedBy = requestedBy;
        _setStatus = setStatus;
        _vms.SelectionChanged += (_, _) => RefreshSelection();
        RefreshSelection();
    }

    protected IReadOnlyList<VmTarget> SelectedTargets => _vms.SelectedTargets;
    protected string RequestedBy => _requestedBy;

    private void RefreshSelection()
    {
        var targets = _vms.SelectedTargets;
        SelectedVmNames.Clear();
        foreach (var target in targets)
        {
            SelectedVmNames.Add(target.Name);
        }

        HasSelection = targets.Count > 0;
        TargetsText = targets.Count switch
        {
            0 => "No VMs selected — select targets on the VMs page first",
            1 => "1 VM selected",
            var n => $"{n} VMs selected"
        };
    }

    /// <summary>Explicit confirmation listing every target, so bulk mistakes are caught before dispatch.</summary>
    protected static bool ConfirmExecution(string action, IReadOnlyList<VmTarget> targets)
    {
        var listed = targets.Take(15).Select(t => $"  • {t.Name}  ({t.ResourceGroup})");
        var more = targets.Count > 15 ? $"\n  … and {targets.Count - 15} more" : string.Empty;
        var massWarning = targets.Count > 5
            ? $"⚠ MASS OPERATION: you are about to target {targets.Count} VMs at once. " +
              "Before proceeding, please carefully check the target list below — a wrong " +
              "filter or leftover selection affects every one of them.\n\n"
            : string.Empty;

        var message =
            $"{massWarning}{action}\n\nTarget VMs ({targets.Count}):\n{string.Join("\n", listed)}{more}\n\nContinue?";

        return MessageBox.Show(message, targets.Count > 5 ? "Confirm MASS execution" : "Confirm execution",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    protected async Task ExecuteAsync(ExecutionRequest request)
    {
        Rows.Clear();
        var rowByVm = new Dictionary<string, ExecutionRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in request.Targets)
        {
            var row = new ExecutionRowViewModel { VmName = target.Name };
            rowByVm[target.ResourceId] = row;
            Rows.Add(row);
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        Log($"─── {request.DisplayName} ───");
        _setStatus($"Executing on {request.Targets.Count} VM(s)...");

        var progress = new Progress<ExecutionProgress>(p => OnProgress(p, rowByVm));

        try
        {
            var report = await _orchestrator.ExecuteAsync(request, progress, _cts.Token);
            Log($"Completed: {report.SucceededCount} succeeded, {report.FailedCount} failed.",
                report.AllSucceeded ? "OK" : "WARN");
            _setStatus($"Done — {report.SucceededCount}/{report.Results.Count} succeeded.");
        }
        catch (Exception ex)
        {
            Log($"Execution error: {ex.Message}", "FAIL");
            _setStatus("Execution failed.");
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Lets derived view models refresh their command CanExecute states.</summary>
    protected virtual void OnRunningChanged(bool value)
    {
    }

    partial void OnIsRunningChanged(bool value) => OnRunningChanged(value);

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Log("Cancelling — running scripts are being aborted...", "WARN");
        _cts?.Cancel();
    }

    private void OnProgress(
        ExecutionProgress progress, IReadOnlyDictionary<string, ExecutionRowViewModel> rows)
    {
        if (!rows.TryGetValue(progress.Target.ResourceId, out var row))
        {
            return;
        }

        switch (progress.Kind)
        {
            case ExecutionProgressKind.VmStarted:
                row.Status = "Running";
                break;

            case ExecutionProgressKind.VmRetrying:
                row.Status = "Retrying";
                Log($"{progress.Target.Name}: {progress.Message}", "WARN");
                break;

            case ExecutionProgressKind.VmCompleted when progress.Result is { } result:
                row.Status = result.Status.ToString();
                row.ExitCode = result.ExitCode?.ToString() ?? string.Empty;
                row.Duration = result.Duration is { } d ? $"{d.TotalSeconds:F0}s" : string.Empty;
                row.IsSuccess = result.IsSuccess;
                row.IsFailure = !result.IsSuccess;

                var level = result.IsSuccess ? "OK" : "FAIL";
                var detail = result.ExitCode is { } code
                    ? $"exit {code} ({ExitCodeClassifier.Describe(code)})"
                    : result.Error ?? result.Status.ToString();
                Log($"{progress.Target.Name}: {result.Status} — {detail}", level);

                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    Log($"{progress.Target.Name} output:\n{result.Output.Trim()}");
                }

                if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.Error))
                {
                    Log($"{progress.Target.Name} error:\n{result.Error.Trim()}", "FAIL");
                }

                break;
        }
    }

    protected void Log(string text, string level = "INFO")
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
