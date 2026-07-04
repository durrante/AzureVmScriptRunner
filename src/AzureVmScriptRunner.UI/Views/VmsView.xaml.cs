using System.Windows;
using System.Windows.Controls;
using AzureVmScriptRunner.UI.ViewModels;

namespace AzureVmScriptRunner.UI.Views;

public partial class VmsView : UserControl
{
    public VmsView() => InitializeComponent();

    private void SelectAll_Checked(object sender, RoutedEventArgs e) =>
        (DataContext as VmsViewModel)?.SelectVisibleCommand.Execute(null);

    private void SelectAll_Unchecked(object sender, RoutedEventArgs e) =>
        (DataContext as VmsViewModel)?.ClearSelectionCommand.Execute(null);
}
