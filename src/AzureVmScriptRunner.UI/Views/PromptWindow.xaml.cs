using System.Windows;

namespace AzureVmScriptRunner.UI.Views;

/// <summary>Small single-value input dialog (suite-styled InputBox replacement).</summary>
public partial class PromptWindow : Window
{
    public string? Value { get; private set; }

    public PromptWindow(string header, string prompt, string initialValue = "")
    {
        InitializeComponent();
        HeaderText.Text = header;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    public static string? Show(string header, string prompt, string initialValue = "")
    {
        var window = new PromptWindow(header, prompt, initialValue)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show("Enter a value or press Cancel.", "Input required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Value = InputBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
