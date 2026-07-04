using System.Collections.ObjectModel;
using System.Windows;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed class TaskRowViewModel
{
    public required SavedTask Task { get; init; }

    public string Name => Task.Name;
    public string Category => Task.Category ?? string.Empty;
    public string Description => Task.Description ?? string.Empty;
    public string TypeText => Task.Payload.Type switch
    {
        ExecutionType.PowerShell => "PowerShell",
        ExecutionType.Cmd => "CMD",
        ExecutionType.PsadtDeployment => "PSADT",
        var t => t.ToString()
    };
    public string Origin => Task.IsBuiltIn ? "Built-in" : "Custom";
}

public sealed partial class TasksViewModel : ObservableObject
{
    private readonly ISavedTaskRepository _repository;
    private readonly string _userUpn;
    private readonly Action<SavedTask> _loadIntoRunner;
    private readonly Action<string> _setStatus;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    [ObservableProperty]
    private TaskRowViewModel? _selectedTask;

    public TasksViewModel(
        ISavedTaskRepository repository,
        string userUpn,
        Action<SavedTask> loadIntoRunner,
        Action<string> setStatus)
    {
        _repository = repository;
        _userUpn = userUpn;
        _loadIntoRunner = loadIntoRunner;
        _setStatus = setStatus;
    }

    public async Task RefreshAsync()
    {
        var tasks = await _repository.GetAllAsync();
        Tasks.Clear();
        foreach (var task in tasks.OrderBy(t => t.Category).ThenBy(t => t.Name))
        {
            Tasks.Add(new TaskRowViewModel { Task = task });
        }
    }

    /// <summary>
    /// Loads the task into the Run (or Deploy) page rather than executing directly —
    /// the administrator always reviews targets and confirms there.
    /// </summary>
    [RelayCommand]
    private void LoadSelected()
    {
        if (SelectedTask is null)
        {
            _setStatus("Select a task first.");
            return;
        }

        _loadIntoRunner(SelectedTask.Task);
    }

    [RelayCommand]
    private async Task NewTaskAsync()
    {
        var editor = new Views.TaskEditorWindow { Owner = System.Windows.Application.Current.MainWindow };
        if (editor.ShowDialog() == true && editor.Result is { } task)
        {
            await _repository.SaveAsync(task with { ModifiedBy = _userUpn });
            await RefreshAsync();
            _setStatus($"Task '{task.Name}' saved.");
        }
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var editor = new Views.TaskEditorWindow(SelectedTask.Task)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (editor.ShowDialog() == true && editor.Result is { } task)
        {
            await _repository.SaveAsync(task with
            {
                ModifiedAt = DateTimeOffset.UtcNow,
                ModifiedBy = _userUpn
            });
            await RefreshAsync();
            _setStatus($"Task '{task.Name}' updated.");
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Delete task '{SelectedTask.Name}'?", "Delete task",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        await _repository.DeleteAsync(SelectedTask.Task.Id);
        await RefreshAsync();
        _setStatus("Task deleted.");
    }
}
