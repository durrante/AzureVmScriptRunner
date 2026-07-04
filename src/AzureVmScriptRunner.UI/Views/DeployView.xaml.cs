using System.Windows.Controls;

namespace AzureVmScriptRunner.UI.Views;

public partial class DeployView : UserControl
{
    public DeployView() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) =>
        LogBox.ScrollToEnd();
}
