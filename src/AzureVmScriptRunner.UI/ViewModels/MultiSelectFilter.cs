using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed partial class FilterOption : ObservableObject
{
    private readonly Action _changed;

    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    public FilterOption(string label, bool isChecked, Action changed)
    {
        Label = label;
        _isChecked = isChecked;
        _changed = changed;
    }

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>
/// Azure-portal-style multi-select filter: none or all checked means "All"; the
/// checked set survives option rebuilds (refresh keeps your filters).
/// </summary>
public sealed partial class MultiSelectFilter : ObservableObject
{
    private readonly Action _changed;

    public string Header { get; }

    public ObservableCollection<FilterOption> Options { get; } = new();

    [ObservableProperty]
    private string _summary = "All";

    public MultiSelectFilter(string header, Action changed)
    {
        Header = header;
        _changed = changed;
    }

    /// <summary>Checked labels, or empty set meaning "no filtering".</summary>
    public ISet<string> ActiveSet
    {
        get
        {
            var selected = Options.Where(o => o.IsChecked).Select(o => o.Label)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return selected.Count == 0 || selected.Count == Options.Count
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : selected;
        }
    }

    public bool Matches(string value)
    {
        var active = ActiveSet;
        return active.Count == 0 || active.Contains(value);
    }

    /// <summary>Rebuilds the option list, preserving previously checked labels.</summary>
    public void Rebuild(IEnumerable<string> values)
    {
        var previouslyChecked = Options.Where(o => o.IsChecked).Select(o => o.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Options.Clear();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            Options.Add(new FilterOption(value, previouslyChecked.Contains(value), OnOptionChanged));
        }

        UpdateSummary();
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var option in Options)
        {
            option.IsChecked = false;
        }
    }

    private void OnOptionChanged()
    {
        UpdateSummary();
        _changed();
    }

    private void UpdateSummary()
    {
        var active = ActiveSet;
        Summary = active.Count == 0 ? "All" : $"{active.Count} selected";
    }
}
