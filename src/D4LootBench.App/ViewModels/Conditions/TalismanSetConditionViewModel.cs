using System.Collections.Specialized;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed partial class TalismanSetConditionViewModel : ConditionViewModel
{
    private readonly IFilterDataService _data;

    public PickerViewModel SetPicker { get; }
    public PickerViewModel ItemPicker { get; }

    public TalismanSetConditionViewModel(IFilterDataService data)
    {
        _data = data;
        SetPicker = new PickerViewModel(
            _data.TalismanSets.All.Select(e => new PickerEntry(e.Hash, e.Name)))
        {
            MaxSelectionCount = TalismanSetCondition.MaxSelectionCount
        };
        ItemPicker = new PickerViewModel([]);

        SetPicker.Selected.CollectionChanged += OnSetsChanged;
        ItemPicker.Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public TalismanSetConditionViewModel(IFilterDataService data, TalismanSetCondition m) : this(data)
    {
        foreach (var id in m.SetIds)
            SetPicker.Selected.Add(new PickerEntry(id, _data.TalismanSets.GetSetName(id)));

        foreach (var entry in m.SetEntries)
        {
            var name = _data.TalismanSets.ItemToSetHash.TryGetValue(entry.ItemId, out var setHash)
                && _data.TalismanSets.ByHash.TryGetValue(setHash, out var setInfo)
                && setInfo.Items.FirstOrDefault(i => i.Hash == entry.ItemId) is { } item
                    ? $"{setInfo.Name} → {item.Name}"
                    : _data.TalismanSets.GetItemName(entry.ItemId);
            ItemPicker.Selected.Add(new PickerEntry(entry.ItemId, name));
        }

        UpdateItemSource();
    }

    private void OnSetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateItemSource();
        OnPropertyChanged(nameof(Summary));
    }

    private void UpdateItemSource()
    {
        var items = new List<PickerEntry>();
        var sets = SetPicker.Selected.Count > 0
            ? SetPicker.Selected
            : _data.TalismanSets.All.Select(s => new PickerEntry(s.Hash, s.Name));

        foreach (var setPk in sets)
        {
            if (_data.TalismanSets.ByHash.TryGetValue(setPk.Hash, out var setInfo))
            {
                foreach (var item in setInfo.Items)
                    items.Add(new PickerEntry(item.Hash, $"{setInfo.Name} → {item.Name}"));
            }
        }
        ItemPicker.ReplaceSource(items);
    }

    public override void ApplyAllowedClasses(IReadOnlySet<string>? allowedClasses)
    {
        if (allowedClasses is null)
        {
            SetPicker.SourceFilter = null;
            return;
        }

        var visible = _data.TalismanSets.All
            .Where(s => ConditionViewModelHelpers.MatchesClasses(s.Classes, allowedClasses))
            .Select(s => s.Hash)
            .ToHashSet();
        SetPicker.SourceFilter = e => visible.Contains(e.Hash);
    }

    public override string TypeName => "Talisman Set";
    public override string Summary
    {
        get
        {
            if (SetPicker.Selected.Count == 0 && ItemPicker.Selected.Count == 0)
                return "any set";
            var parts = new List<string>();
            if (SetPicker.Selected.Count > 0)
                parts.Add($"{SetPicker.Selected.Count} set{(SetPicker.Selected.Count == 1 ? "" : "s")}");
            if (ItemPicker.Selected.Count > 0)
                parts.Add($"{ItemPicker.Selected.Count} item{(ItemPicker.Selected.Count == 1 ? "" : "s")}");
            return string.Join(", ", parts);
        }
    }

    public override Condition BuildModel() => new TalismanSetCondition
    {
        SetIds = SetPicker.Selected.Select(e => e.Hash).ToList(),
        SetEntries = ItemPicker.Selected
            .Select(e => new TalismanSetEntry(
                _data.TalismanSets.GetSetHashForItem(e.Hash), e.Hash))
            .ToList()
    };
}
