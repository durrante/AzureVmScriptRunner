using System.Windows.Controls;

namespace AzureVmScriptRunner.UI.Views;

public partial class FilterDropdown : UserControl
{
    public FilterDropdown() => InitializeComponent();

    private void Popup_Closed(object sender, EventArgs e) => DropButton.IsChecked = false;
}
