using CommunityToolkit.Mvvm.ComponentModel;

namespace D4LootBench.App.ViewModels;

/// <summary>A selectable stat the point maximizer can focus on.</summary>
public partial class FocusStatViewModel : ObservableObject
{
    public FocusStatViewModel(string attribute, string displayName)
    {
        Attribute = attribute;
        DisplayName = displayName;
    }

    public string Attribute { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected;
}
