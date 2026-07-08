using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed partial class SpecificUniqueConditionViewModel : ConditionViewModel
{
    private readonly IFilterDataService _data;

    public PickerViewModel Picker { get; }

    public SpecificUniqueConditionViewModel(IFilterDataService data)
    {
        _data = data;
        var displayNameToSnoIds = _data.Uniques.Released
            .ToLookup(e => e.Name, e => e.SnoId);

        var source = displayNameToSnoIds
            .Select(g => new PickerEntry(g.First(), g.Key))
            .OrderBy(e => e.DisplayName)
            .ToList();

        Picker = new PickerViewModel(source)
        {
            MaxSelectionCount = SpecificUniqueCondition.MaxSelectionCount
        };
        Picker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public SpecificUniqueConditionViewModel(IFilterDataService data, SpecificUniqueCondition m) : this(data)
    {
        var seen = new HashSet<string>();
        foreach (var id in m.UniqueIds)
        {
            var name = _data.Uniques.GetDisplayName(id);
            if (seen.Add(name))
                Picker.Selected.Add(new PickerEntry(id, name));
        }
    }

    // Filter by display name, not SNO ID — the picker holds one representative SNO per
    // deduplicated display name, and a name's variants can carry different derived classes.
    public override void ApplyAllowedClasses(IReadOnlySet<string>? allowedClasses)
    {
        if (allowedClasses is null)
        {
            Picker.SourceFilter = null;
            return;
        }

        var visibleNames = _data.Uniques.Released
            .Where(e => ConditionViewModelHelpers.MatchesClasses(e.Classes, allowedClasses))
            .Select(e => e.Name)
            .ToHashSet();
        Picker.SourceFilter = e => visibleNames.Contains(e.DisplayName);
    }

    public override string TypeName => "Specific Unique";
    public override string Summary =>
        $"{Picker.Selected.Count} unique{(Picker.Selected.Count == 1 ? "" : "s")}";

    public override Condition BuildModel() =>
        new SpecificUniqueCondition(
            Picker.Selected
                .Select(e => e.Hash)
                .ToList());
}
