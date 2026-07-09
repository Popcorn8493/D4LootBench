using CommunityToolkit.Mvvm.ComponentModel;

namespace D4LootBench.App.ViewModels;

/// <summary>A selectable stat the point maximizer can focus on, with a per-stat priority.</summary>
public partial class FocusStatViewModel : ObservableObject
{
    public static IReadOnlyList<string> Priorities { get; } = ["High", "Normal", "Low"];

    public FocusStatViewModel(string attribute, string displayName)
    {
        Attribute = attribute;
        DisplayName = displayName;
    }

    public string Attribute { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>High / Normal / Low — mapped to the maximizer weight via <see cref="Weight"/>.</summary>
    [ObservableProperty]
    private string _priority = "Normal";

    /// <summary>The maximizer chases a weight-3 stat three times as hard as a weight-1 stat.</summary>
    public double Weight => Priority switch
    {
        "High" => 3.0,
        "Low" => 0.3,
        _ => 1.0,
    };

    public static string PriorityForWeight(double weight) =>
        weight >= 2 ? "High" : weight <= 0.5 ? "Low" : "Normal";
}
