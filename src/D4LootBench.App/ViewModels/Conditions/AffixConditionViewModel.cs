using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed partial class AffixConditionViewModel : ConditionViewModel
{
    private readonly IFilterDataService _data;
    private readonly Dictionary<uint, uint> _preservedGreaterValues = [];
    private readonly int _preservedField5;

    public PickerViewModel Picker { get; }
    public PickerViewModel GreaterPicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _minimumCount = 1;

    /// <summary>The Greater Affixes sub-picker is tucked away unless it has selections.</summary>
    [ObservableProperty]
    private bool _isGreaterExpanded;

    public AffixConditionViewModel(IFilterDataService data)
    {
        _data = data;
        _preservedField5 = 0;
        Picker = MakePicker();
        GreaterPicker = MakeGreaterPicker();
        Picker.Selected.CollectionChanged += OnPickerSelectionChanged;
        Picker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        GreaterPicker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public AffixConditionViewModel(IFilterDataService data, AffixCondition m)
    {
        _data = data;
        _minimumCount = m.MinimumCount;
        _preservedField5 = m.Field5;
        _isGreaterExpanded = m.GreaterEntries.Count > 0;
        foreach (var ge in m.GreaterEntries)
        {
            if (!_preservedGreaterValues.ContainsKey(ge.AffixId))
                _preservedGreaterValues[ge.AffixId] = ge.AffixIdEcho;
        }

        Picker = MakePicker();
        foreach (var id in m.AffixIds)
            Picker.Selected.Add(new PickerEntry(id, _data.Affixes.GetDisplayName(id)));

        GreaterPicker = MakeGreaterPicker();
        GreaterPicker.ReplaceSource(Picker.Selected.Select(p => new PickerEntry(p.Hash, p.DisplayName)));
        foreach (var ge in m.GreaterEntries)
            GreaterPicker.Selected.Add(ToPickerEntry(ge));

        Picker.Selected.CollectionChanged += OnPickerSelectionChanged;
        Picker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        GreaterPicker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    private void OnPickerSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var source = Picker.Selected.Select(p => new PickerEntry(p.Hash, p.DisplayName));
        GreaterPicker.ReplaceSource(source);
    }

    private PickerViewModel MakePicker() =>
        new(_data.Affixes.ByHash.Select(kv => new PickerEntry(kv.Key, kv.Value.Name)))
        {
            MaxSelectionCount = AffixCondition.MaxSelectionCount
        };

    private PickerViewModel MakeGreaterPicker() =>
        new(Enumerable.Empty<PickerEntry>())
        {
            MaxSelectionCount = AffixCondition.MaxSelectionCount,
            OwnsEntries       = false
        };

    private PickerEntry ToPickerEntry(GreaterAffixEntry ge) =>
        new(ge.AffixId, _data.Affixes.GetDisplayName(ge.AffixId));

    public override void ApplyAllowedClasses(IReadOnlySet<string>? allowedClasses)
    {
        if (allowedClasses is null)
        {
            Picker.SourceFilter = null;
            return;
        }

        var visible = _data.Affixes.All
            .Where(a => ConditionViewModelHelpers.MatchesClasses(a.Classes, allowedClasses))
            .Select(a => a.Hash)
            .ToHashSet();
        Picker.SourceFilter = e => visible.Contains(e.Hash);
    }

    public override string TypeName => "Required Affixes";

    public override string Summary
    {
        get
        {
            var ga = GreaterPicker.Selected.Count;
            var s = $"min {MinimumCount} of {Picker.Selected.Count}";
            return ga > 0 ? $"{s}, {ga} greater" : s;
        }
    }

    public override Condition BuildModel() =>
        new AffixCondition(Picker.Selected.Select(e => e.Hash).ToList(), MinimumCount)
        {
            GreaterEntries = GreaterPicker.Selected
                .Select(e => new GreaterAffixEntry(e.Hash,
                    _preservedGreaterValues.TryGetValue(e.Hash, out var v) ? v : 0))
                .ToList(),
            Field5 = _preservedField5
        };
}
