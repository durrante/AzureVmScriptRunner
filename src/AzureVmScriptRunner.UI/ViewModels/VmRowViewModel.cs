using AzureVmScriptRunner.Domain.Targets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureVmScriptRunner.UI.ViewModels;

/// <summary>Grid row wrapper adding selection state to a discovered VM.</summary>
public sealed partial class VmRowViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public VmTarget Target { get; }

    [ObservableProperty]
    private bool _isSelected;

    public VmRowViewModel(VmTarget target, Action selectionChanged)
    {
        Target = target;
        _selectionChanged = selectionChanged;
    }

    public string Name => Target.Name;
    public string Subscription => Target.SubscriptionName ?? Target.SubscriptionId;
    public string ResourceGroup => Target.ResourceGroup;
    public string Region => Target.Region ?? string.Empty;
    public string PowerText => Target.PowerState.ToString();
    public bool IsRunning => Target.PowerState == VmPowerState.Running;
    public string IdentityText => Target.HasSystemAssignedIdentity ? "✔" : string.Empty;
    public string TagsText => string.Join(", ", Target.Tags.Select(t => $"{t.Key}={t.Value}"));

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
