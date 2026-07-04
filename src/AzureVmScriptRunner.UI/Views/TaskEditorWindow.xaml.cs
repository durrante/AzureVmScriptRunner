using System.Windows;
using System.Windows.Controls;
using AzureVmScriptRunner.Domain.Execution;
using AzureVmScriptRunner.Domain.Tasks;

namespace AzureVmScriptRunner.UI.Views;

/// <summary>Editor for PowerShell, CMD and PSADT saved tasks.</summary>
public partial class TaskEditorWindow : Window
{
    private readonly SavedTask? _original;

    public SavedTask? Result { get; private set; }

    public TaskEditorWindow(SavedTask? existing = null)
    {
        InitializeComponent();
        _original = existing;

        if (existing is null)
        {
            return;
        }

        HeaderText.Text = "Edit Task";
        Title = $"Saved Task — {existing.Name}";
        TxtName.Text = existing.Name;
        TxtCategory.Text = existing.Category ?? string.Empty;
        TxtDescription.Text = existing.Description ?? string.Empty;

        switch (existing.Payload)
        {
            case PowerShellPayload ps:
                CmbShell.SelectedIndex = 0;
                TxtScript.Text = ps.Script;
                break;
            case CmdPayload cmd:
                CmbShell.SelectedIndex = 1;
                TxtScript.Text = cmd.CommandLine;
                break;
            case PsadtPayload psadt:
                CmbShell.SelectedIndex = 2;
                TxtPackageUrl.Text = psadt.PackageUrl.ToString();
                SelectByContent(CmbPsadtType, psadt.DeploymentType.ToString());
                SelectByContent(CmbPsadtMode, psadt.DeployMode.ToString());
                TxtPsadtArgs.Text = psadt.AdditionalArguments ?? string.Empty;
                ChkCleanup.IsChecked = psadt.CleanupTemporaryFiles;
                break;
        }
    }

    private bool IsPsadtSelected =>
        (CmbShell.SelectedItem as ComboBoxItem)?.Content as string == "PSADT Deployment";

    private void CmbShell_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelScript is null || PanelPsadt is null)
        {
            return; // fires during InitializeComponent
        }

        PanelScript.Visibility = IsPsadtSelected ? Visibility.Collapsed : Visibility.Visible;
        PanelPsadt.Visibility = IsPsadtSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show("Name is required.", "Saved Task", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecutionPayload payload;
        if (IsPsadtSelected)
        {
            if (!Uri.TryCreate(TxtPackageUrl.Text, UriKind.Absolute, out var packageUri) ||
                !packageUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Enter a valid https:// package URL.", "Saved Task",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            payload = new PsadtPayload
            {
                PackageUrl = packageUri,
                DeploymentType = Enum.Parse<PsadtDeploymentType>(
                    (string)((ComboBoxItem)CmbPsadtType.SelectedItem).Content),
                DeployMode = Enum.Parse<PsadtDeployMode>(
                    (string)((ComboBoxItem)CmbPsadtMode.SelectedItem).Content),
                AdditionalArguments =
                    string.IsNullOrWhiteSpace(TxtPsadtArgs.Text) ? null : TxtPsadtArgs.Text.Trim(),
                CleanupTemporaryFiles = ChkCleanup.IsChecked == true
            };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TxtScript.Text))
            {
                MessageBox.Show("Script is required.", "Saved Task",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var isCmd = (CmbShell.SelectedItem as ComboBoxItem)?.Content as string == "CMD";
            payload = isCmd
                ? new CmdPayload(TxtScript.Text)
                : new PowerShellPayload(TxtScript.Text);
        }

        Result = (_original ?? new SavedTask { Name = TxtName.Text, Payload = payload }) with
        {
            Name = TxtName.Text.Trim(),
            Category = string.IsNullOrWhiteSpace(TxtCategory.Text) ? null : TxtCategory.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim(),
            Payload = payload
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectByContent(ComboBox comboBox, string content)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if ((string)item.Content == content)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }
}
