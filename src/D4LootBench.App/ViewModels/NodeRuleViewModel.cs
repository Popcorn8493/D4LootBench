using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>A rule row for one node group of the current layout (e.g. all "Life" magic nodes).</summary>
public partial class NodeRuleViewModel : ObservableObject
{
    private static readonly IReadOnlyList<NodeRuleMode> AllModes =
        [NodeRuleMode.Allow, NodeRuleMode.Avoid, NodeRuleMode.Exclude, NodeRuleMode.Limit, NodeRuleMode.Minimal];

    public NodeRuleViewModel(NodeGroup group)
    {
        Group = group;
    }

    public NodeGroup Group { get; }
    public string DisplayName => $"{Group.DisplayName} ×{Group.CellCount}";
    public IReadOnlyList<NodeRuleMode> Modes => AllModes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLimit))]
    private NodeRuleMode _mode = NodeRuleMode.Allow;

    /// <summary>Max nodes of this group to take when <see cref="Mode"/> is Limit or Minimal.</summary>
    [ObservableProperty]
    private int _limit = 2;

    public bool IsLimit => Mode is NodeRuleMode.Limit or NodeRuleMode.Minimal;

    /// <summary>How many nodes of this group the current allocation holds, e.g. "2 in path".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverLimit))]
    private int _usedCount;

    public string UsedText => UsedCount == 0 ? "" : $"{UsedCount} in path";
    public bool IsOverLimit => IsLimit && UsedCount > Limit;

    partial void OnUsedCountChanged(int value) => OnPropertyChanged(nameof(UsedText));
    partial void OnModeChanged(NodeRuleMode value) => OnPropertyChanged(nameof(IsOverLimit));
    partial void OnLimitChanged(int value) => OnPropertyChanged(nameof(IsOverLimit));
}
