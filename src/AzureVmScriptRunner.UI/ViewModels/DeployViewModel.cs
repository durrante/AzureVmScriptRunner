using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

/// <summary>PSADT package deployment against the current VM selection.</summary>
public sealed partial class DeployViewModel : ExecutionHostViewModel
{
    public PsadtDeploymentType[] DeploymentTypes { get; } =
        (PsadtDeploymentType[])Enum.GetValues(typeof(PsadtDeploymentType));

    public PsadtDeployMode[] DeployModes { get; } =
        (PsadtDeployMode[])Enum.GetValues(typeof(PsadtDeployMode));

    [ObservableProperty]
    private string _packageUrl = string.Empty;

    [ObservableProperty]
    private PsadtDeploymentType _deploymentType = PsadtDeploymentType.Install;

    [ObservableProperty]
    private PsadtDeployMode _deployMode = PsadtDeployMode.Silent;

    [ObservableProperty]
    private string _additionalArguments = string.Empty;

    [ObservableProperty]
    private int _timeoutMinutes = 60;

    [ObservableProperty]
    private bool _cleanupTemporaryFiles = true;

    /// <summary>Deployments run in waves of this many VMs at once.</summary>
    [ObservableProperty]
    private int _maxParallelism = 10;

    private readonly ISavedTaskRepository _savedTasks;
    private readonly Func<Task> _onTaskSaved;

    public DeployViewModel(
        ExecutionOrchestrator orchestrator,
        VmsViewModel vms,
        string requestedBy,
        Action<string> setStatus,
        ISavedTaskRepository savedTasks,
        Func<Task> onTaskSaved)
        : base(orchestrator, vms, requestedBy, setStatus)
    {
        _savedTasks = savedTasks;
        _onTaskSaved = onTaskSaved;
    }

    /// <summary>Saves the current deployment form as a reusable PSADT task.</summary>
    [RelayCommand]
    private async Task SaveAsTaskAsync()
    {
        if (!Uri.TryCreate(PackageUrl, UriKind.Absolute, out var packageUri))
        {
            Log("Enter a valid package URL before saving as a task.", "WARN");
            return;
        }

        var packageName = packageUri.Segments.LastOrDefault()?.TrimEnd('/') ?? "package";
        var suggestedName = $"{DeploymentType}: {System.IO.Path.GetFileNameWithoutExtension(packageName)}";
        var chosenName = Views.PromptWindow.Show(
            "Save as Task", "Task name:", suggestedName);
        if (chosenName is null)
        {
            Log("Save as task cancelled.", "WARN");
            return;
        }

        var task = new SavedTask
        {
            Name = chosenName,
            Category = "Deployments",
            Description = $"PSADT {DeploymentType} ({DeployMode}) of {packageName}",
            Payload = new PsadtPayload
            {
                PackageUrl = packageUri,
                DeploymentType = DeploymentType,
                DeployMode = DeployMode,
                AdditionalArguments =
                    string.IsNullOrWhiteSpace(AdditionalArguments) ? null : AdditionalArguments,
                CleanupTemporaryFiles = CleanupTemporaryFiles
            },
            ModifiedBy = RequestedBy
        };

        await _savedTasks.SaveAsync(task);
        await _onTaskSaved();
        Log($"Saved as task '{task.Name}' — rename or edit it on the Tasks page.", "OK");
    }

    public void LoadTask(SavedTask task)
    {
        if (task.Payload is not PsadtPayload psadt)
        {
            return;
        }

        PackageUrl = psadt.PackageUrl.ToString();
        DeploymentType = psadt.DeploymentType;
        DeployMode = psadt.DeployMode;
        AdditionalArguments = psadt.AdditionalArguments ?? string.Empty;
        CleanupTemporaryFiles = psadt.CleanupTemporaryFiles;
    }

    private bool CanDeploy() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployAsync()
    {
        var targets = SelectedTargets;
        if (targets.Count == 0)
        {
            Log("Select at least one VM on the VMs page first.", "WARN");
            return;
        }

        if (!Uri.TryCreate(PackageUrl, UriKind.Absolute, out var packageUri) ||
            !packageUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            Log("Enter a valid https:// blob URL for the PSADT package (no SAS token needed).", "WARN");
            return;
        }

        var packageName = packageUri.Segments.LastOrDefault()?.TrimEnd('/') ?? "package";
        if (!ConfirmExecution(
            $"PSADT {DeploymentType} ({DeployMode}):\n{packageName}", targets))
        {
            Log("Deployment cancelled at confirmation.", "WARN");
            return;
        }

        var request = new ExecutionRequest
        {
            DisplayName = $"PSADT {DeploymentType}: {packageName}",
            Payload = new PsadtPayload
            {
                PackageUrl = packageUri,
                DeploymentType = DeploymentType,
                DeployMode = DeployMode,
                AdditionalArguments =
                    string.IsNullOrWhiteSpace(AdditionalArguments) ? null : AdditionalArguments,
                CleanupTemporaryFiles = CleanupTemporaryFiles
            },
            Targets = targets,
            RequestedBy = RequestedBy,
            Options = ExecutionOptions.Default with
            {
                Timeout = TimeSpan.FromMinutes(Math.Clamp(TimeoutMinutes, 5, 90)),
                MaxParallelism = Math.Clamp(MaxParallelism, 1, 50)
            }
        };

        await ExecuteAsync(request);
    }

    protected override void OnRunningChanged(bool value) => DeployCommand.NotifyCanExecuteChanged();
}
