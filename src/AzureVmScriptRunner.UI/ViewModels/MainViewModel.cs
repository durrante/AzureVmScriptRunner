using System.Collections.ObjectModel;
using System.Reflection;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Application.Execution;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;
using AzureVmScriptRunner.Infrastructure.Azure;
using AzureVmScriptRunner.Infrastructure.DependencyInjection;
using AzureVmScriptRunner.Infrastructure.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed record NavItem(string Title, string Glyph, object ViewModel);

public sealed partial class PlaceholderViewModel : ObservableObject
{
    public required string Title { get; init; }
    public required string Note { get; init; }
}

public sealed class PrepViewModel
{
}

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Informational version (csproj &lt;Version&gt;, overridden by the MSIX build).</summary>
    public static string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public string WindowTitle { get; } = $"Azure VM Script Runner  v{AppVersion}";

    public string FooterBrand { get; } =
        $"Azure VM Script Runner v{AppVersion} — provided as-is, without warranty of any kind — modernworkspacehub.com";

    [ObservableProperty]
    private string _statusText = "Not connected — click Connect to sign in to Azure.";

    [ObservableProperty]
    private string _userDisplay = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    private RunViewModel? _run;
    private DeployViewModel? _deploy;
    private TasksViewModel? _tasks;
    private ScheduleViewModel? _schedule;

    public MainViewModel()
    {
        // Prep guidance is useful before signing in, so it's available immediately.
        NavItems.Add(new NavItem("Prep", "", new PrepViewModel()));
        SelectedNavItem = NavItems[0];
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    /// <summary>
    /// Deliberate, always-interactive sign-in — the browser prompt appears every
    /// launch by design; no account is silently reused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsConnecting = true;
        StatusText = "Signing in to Azure — complete the browser prompt...";
        try
        {
            var session = await AzureSession.CreateInteractiveAsync();
            UserDisplay = session.UserPrincipalName;
            IsConnected = true;

            var services = new ServiceCollection()
                .AddLocalPersistence()
                .AddAzureVmScriptRunner(session)
                .BuildServiceProvider();

            void SetStatus(string text) => StatusText = text;

            var savedTasks = services.GetRequiredService<ISavedTaskRepository>();
            await BuiltInTaskSeeder.EnsureSeededAsync(savedTasks);

            var vms = new VmsViewModel(services.GetRequiredService<IVmDiscoveryService>(), SetStatus);
            var orchestrator = services.GetRequiredService<ExecutionOrchestrator>();

            _run = new RunViewModel(orchestrator, vms, session.UserPrincipalName, SetStatus);
            _deploy = new DeployViewModel(
                orchestrator, vms, session.UserPrincipalName, SetStatus,
                savedTasks, async () =>
                {
                    await _tasks!.RefreshAsync();
                    if (_schedule is not null)
                    {
                        await _schedule.RefreshSourcesAsync();
                    }
                });
            _tasks = new TasksViewModel(
                savedTasks, session.UserPrincipalName, LoadTaskIntoRunner, SetStatus);
            await _tasks.RefreshAsync();

            var history = new HistoryViewModel(services.GetRequiredService<IJobHistoryService>());
            _schedule = new ScheduleViewModel(
                services.GetRequiredService<IScheduleService>(),
                savedTasks,
                services.GetRequiredService<IVmDiscoveryService>(),
                services.GetRequiredService<Infrastructure.Scheduling.LocalSettingsStore>(),
                vms,
                session.UserPrincipalName,
                SetStatus);

            NavItems.Insert(0, new NavItem("VMs", "", vms));
            NavItems.Insert(1, new NavItem("Run", "", _run));
            NavItems.Insert(2, new NavItem("Deploy", "", _deploy));
            NavItems.Insert(3, new NavItem("Tasks", "", _tasks));
            NavItems.Insert(4, new NavItem("History", "", history));
            NavItems.Insert(5, new NavItem("Schedule", "", _schedule));

            SelectedNavItem = NavItems[0];
            IsReady = true;
            StatusText = "Connected — loading VM inventory...";
            await vms.RefreshCommand.ExecuteAsync(null);
            await history.RefreshAsync();
            await _schedule.InitializeAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>Routes a saved task to the right page (Run for scripts, Deploy for PSADT).</summary>
    private void LoadTaskIntoRunner(SavedTask task)
    {
        if (task.Payload is PsadtPayload)
        {
            _deploy!.LoadTask(task);
            SelectedNavItem = NavItems.First(n => n.ViewModel == _deploy);
            StatusText = $"Task '{task.Name}' loaded into Deploy — review targets and deploy.";
        }
        else
        {
            _run!.LoadTask(task);
            SelectedNavItem = NavItems.First(n => n.ViewModel == _run);
            StatusText = $"Task '{task.Name}' loaded into Run — review targets and run.";
        }
    }

    /// <summary>Drops the signed-in session and returns the app to its pre-Connect state.</summary>
    [RelayCommand]
    private void Disconnect()
    {
        for (var i = NavItems.Count - 1; i >= 0; i--)
        {
            if (NavItems[i].ViewModel is not PrepViewModel)
            {
                NavItems.RemoveAt(i);
            }
        }

        _run = null;
        _deploy = null;
        _tasks = null;
        _schedule = null;
        UserDisplay = string.Empty;
        IsConnected = false;
        IsReady = false;
        SelectedNavItem = NavItems[0];
        StatusText = "Disconnected — click Connect to sign in.";
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        CurrentView = value?.ViewModel;

        // Data views refresh themselves when opened.
        if (value?.ViewModel is HistoryViewModel history)
        {
            _ = history.RefreshAsync();
        }

        // Tasks saved elsewhere (task editor, Deploy 'Save as Task') appear without a restart.
        if (value?.ViewModel is ScheduleViewModel schedule)
        {
            _ = schedule.RefreshSourcesAsync();
        }
    }
}
