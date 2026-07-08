using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels;

public abstract partial class ConditionViewModel : ObservableObject
{
    public abstract string TypeName { get; }
    public virtual string Summary => "";
    public abstract Condition BuildModel();

    /// <summary>
    /// Whether the card body is shown. Conditions loaded from an existing rule start
    /// collapsed (summary only); newly added conditions start expanded for editing.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// Called when the global class filter changes. Override in subclasses whose picker
    /// source depends only on the global class selection (e.g. item types).
    /// </summary>
    public virtual void ApplyClassFilter(PlayerClass playerClass) { }

    /// <summary>
    /// Called when the rule's effective class restriction changes — the global class filter
    /// intersected with the classes implied by the rule's Item Type selection. Null means
    /// unrestricted; entries tagged "All" always remain visible. Override in subclasses
    /// with class-tagged picker sources (affixes, uniques, talisman sets).
    /// </summary>
    public virtual void ApplyAllowedClasses(IReadOnlySet<string>? allowedClasses) { }
}
