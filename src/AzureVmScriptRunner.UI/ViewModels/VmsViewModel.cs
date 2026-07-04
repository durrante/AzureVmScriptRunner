using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AzureVmScriptRunner.Application.Abstractions;
using AzureVmScriptRunner.Domain.Targets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureVmScriptRunner.UI.ViewModels;

public sealed partial class VmsViewModel : ObservableObject
{
    private readonly IVmDiscoveryService _discovery;
    private readonly Action<string> _setStatus;

    public ObservableCollection<VmRowViewModel> Vms { get; } = new();
    public ICollectionView VmsView { get; }

    // Azure-portal-style multi-select filters; empty selection = All, and the
    // checked state survives Refresh.
    public MultiSelectFilter SubscriptionFilter { get; }
    public MultiSelectFilter ResourceGroupFilter { get; }
    public MultiSelectFilter RegionFilter { get; }
    public MultiSelectFilter PowerFilter { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = "Not loaded — press Refresh.";

    [ObservableProperty]
    private string _selectionText = "0 selected";

    public VmsViewModel(IVmDiscoveryService discovery, Action<string> setStatus)
    {
        _discovery = discovery;
        _setStatus = setStatus;
        VmsView = CollectionViewSource.GetDefaultView(Vms);
        VmsView.Filter = MatchesFilter;

        SubscriptionFilter = new MultiSelectFilter("Subscription", RefreshView);
        ResourceGroupFilter = new MultiSelectFilter("Resource group", RefreshView);
        RegionFilter = new MultiSelectFilter("Region", RefreshView);
        PowerFilter = new MultiSelectFilter("Power", RefreshView);
    }

    public IReadOnlyList<VmTarget> SelectedTargets =>
        Vms.Where(v => v.IsSelected).Select(v => v.Target).ToList();

    public event EventHandler? SelectionChanged;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        _setStatus("Discovering VMs via Azure Resource Graph...");
        try
        {
            var targets = await _discovery.DiscoverAsync();
            Vms.Clear();
            foreach (var target in targets)
            {
                Vms.Add(new VmRowViewModel(target, OnRowSelectionChanged));
            }

            // Rebuild filter options; previously checked values are preserved.
            SubscriptionFilter.Rebuild(Vms.Select(v => v.Subscription));
            ResourceGroupFilter.Rebuild(Vms.Select(v => v.ResourceGroup));
            RegionFilter.Rebuild(Vms.Select(v => v.Region).Where(r => r.Length > 0));
            PowerFilter.Rebuild(Vms.Select(v => v.PowerText));

            RefreshView();
            SummaryText = $"{Vms.Count} Windows VM(s) · {Vms.Count(v => v.IsRunning)} running";
            _setStatus($"Discovered {Vms.Count} VMs.");
        }
        catch (Exception ex)
        {
            SummaryText = "Discovery failed.";
            _setStatus($"Discovery failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            OnRowSelectionChanged();
        }
    }

    [RelayCommand]
    private void SelectVisible()
    {
        foreach (var row in VmsView.Cast<VmRowViewModel>())
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Vms)
        {
            row.IsSelected = false;
        }
    }

    private void OnRowSelectionChanged()
    {
        var count = Vms.Count(v => v.IsSelected);
        SelectionText = $"{count} selected";
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshView() => VmsView.Refresh();

    partial void OnSearchTextChanged(string value) => VmsView.Refresh();

    private bool MatchesFilter(object item)
    {
        if (item is not VmRowViewModel row)
        {
            return false;
        }

        if (!SubscriptionFilter.Matches(row.Subscription) ||
            !ResourceGroupFilter.Matches(row.ResourceGroup) ||
            !RegionFilter.Matches(row.Region) ||
            !PowerFilter.Matches(row.PowerText))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               row.ResourceGroup.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               row.Subscription.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               row.TagsText.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}
