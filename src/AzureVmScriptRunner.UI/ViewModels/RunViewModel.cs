using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed partial class ExecutionRowViewModel : ObservableObject
{
    public required string VmName { get; init; }

    [ObservableProperty]
    private string _status = "Queued";

    [ObservableProperty]
    private string _exitCode = string.Empty;

    [ObservableProperty]
    private string _duration = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private bool _isFailure;
}

public sealed partial class RunViewModel : ExecutionHostViewModel
{
    public string[] ShellTypes { get; } = { "PowerShell", "CMD" };

    [ObservableProperty]
    private string _shellType = "PowerShell";

    [ObservableProperty]
    private string _scriptText = string.Empty;

    [ObservableProperty]
    private int _timeoutMinutes = 30;

    [ObservableProperty]
    private int _maxParallelism = 10;

    [ObservableProperty]
    private string _loadedTaskName = string.Empty;

    public RunViewModel(
        ExecutionOrchestrator orchestrator,
        VmsViewModel vms,
        string requestedBy,
        Action<string> setStatus)
        : base(orchestrator, vms, requestedBy, setStatus)
    {
    }

    /// <summary>Pre-fills the editor from a saved task (invoked from the Tasks page).</summary>
    public void LoadTask(SavedTask task)
    {
        switch (task.Payload)
        {
            case PowerShellPayload ps:
                ShellType = "PowerShell";
                ScriptText = ps.Script;
                break;
            case CmdPayload cmd:
                ShellType = "CMD";
                ScriptText = cmd.CommandLine;
                break;
            default:
                return;
        }

        LoadedTaskName = $"Loaded task: {task.Name}";
        TimeoutMinutes = (int)task.Options.Timeout.TotalMinutes is > 0 and <= 90 and var m ? m : 30;
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var targets = SelectedTargets;
        if (targets.Count == 0)
        {
            Log("Select at least one VM on the VMs page first.", "WARN");
            return;
        }

        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            Log("Enter a command or script to run.", "WARN");
            return;
        }

        var displayName = string.IsNullOrEmpty(LoadedTaskName)
            ? $"{ShellType} command"
            : LoadedTaskName.Replace("Loaded task: ", string.Empty);

        var preview = ScriptText.Length > 120 ? ScriptText[..120] + "…" : ScriptText;
        if (!ConfirmExecution($"Run {ShellType}:\n{preview}", targets))
        {
            Log("Execution cancelled at confirmation.", "WARN");
            return;
        }

        ExecutionPayload payload = ShellType == "CMD"
            ? new CmdPayload(ScriptText)
            : new PowerShellPayload(ScriptText);

        var request = new ExecutionRequest
        {
            DisplayName = $"{displayName} on {targets.Count} VM(s)",
            Payload = payload,
            Targets = targets,
            RequestedBy = RequestedBy,
            Options = ExecutionOptions.Default with
            {
                Timeout = TimeSpan.FromMinutes(Math.Clamp(TimeoutMinutes, 3, 90)),
                MaxParallelism = Math.Clamp(MaxParallelism, 1, 50)
            }
        };

        await ExecuteAsync(request);
    }

    protected override void OnRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();
}
