using System.Windows;
using AzureVmScriptRunner.UI.ViewModels;

namespace AzureVmScriptRunner.UI;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The app starts disconnected; sign-in is an explicit user action (Connect).
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
    }
}
