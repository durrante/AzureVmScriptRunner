using System.Windows.Controls;

namespace AzureVmScriptRunner.UI.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) =>
        LogBox.ScrollToEnd();
}
