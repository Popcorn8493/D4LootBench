using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace D4LootBench.App.ViewModels.Conditions;

public sealed record PickerEntry(uint Hash, string DisplayName);

public sealed partial class PickerViewModel : ObservableObject
{
    private IReadOnlyList<PickerEntry> _source;

    public ObservableCollection<PickerEntry> Selected { get; } = [];
    public ObservableCollection<PickerEntry> Available { get; } = [];

    [ObservableProperty]
    private Func<PickerEntry, bool>? _sourceFilter;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _hasAvailableSelection;

    [ObservableProperty]
    private bool _hasCurrentSelection;

    /// <summary>Optional game-enforced limit on total selected items.</summary>
    public int? MaxSelectionCount { get; init; }

    /// <summary>
    /// False for pickers whose selection is a flag over another picker's entries (e.g. the
    /// greater-affix picker). Drag-and-drop treats drops into or out of a non-owning picker
    /// as a copy instead of a move, so flagging an affix never removes it elsewhere.
    /// </summary>
    public bool OwnsEntries { get; init; } = true;

    /// <summary>Optional external limit check (e.g., shared limit across pickers).</summary>
    public Func<bool>? ExternalAtMax { get; set; }

    public bool IsAtMax =>
        (MaxSelectionCount.HasValue && Selected.Count >= MaxSelectionCount.Value) ||
        (ExternalAtMax is not null && ExternalAtMax());

    public string SelectionCountDisplay =>
        MaxSelectionCount.HasValue
            ? $"{Selected.Count} / {MaxSelectionCount.Value}"
            : $"{Selected.Count}";

    public PickerViewModel(IEnumerable<PickerEntry> source)
    {
        _source = source.OrderBy(e => e.DisplayName).ToList();
        Selected.CollectionChanged += (_, _) =>
        {
            RefreshAvailable();
            OnPropertyChanged(nameof(IsAtMax));
            OnPropertyChanged(nameof(SelectionCountDisplay));
            ClearAllCommand.NotifyCanExecuteChanged();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SourceFilter) or nameof(SearchText))
                RefreshAvailable();
        };
        RefreshAvailable();
    }

    /// <summary>Called by view code-behind when the Available ListBox selection changes.</summary>
    public void SyncAvailableSelection(IList items)
    {
        HasAvailableSelection = items.Count > 0 && !IsAtMax;
    }

    /// <summary>Called by view code-behind when the Selected ListBox selection changes.</summary>
    public void SyncCurrentSelection(IList items)
    {
        HasCurrentSelection = items.Count > 0;
    }

    /// <summary>
    /// Whether a dragged entry can be dropped into this picker's selection: the entry must
    /// exist in this picker's source catalog, not already be selected, and fit under the limit.
    /// </summary>
    public bool CanAccept(PickerEntry entry) =>
        !IsAtMax &&
        Selected.All(s => s.Hash != entry.Hash) &&
        _source.Any(s => s.Hash == entry.Hash);

    /// <summary>Called by view code-behind with a pre-captured snapshot of SelectedItems.</summary>
    public void AddItems(IReadOnlyList<PickerEntry> items)
    {
        foreach (var item in items)
        {
            if (IsAtMax) break;
            Selected.Add(item);
        }
    }

    /// <summary>Called by view code-behind with a pre-captured snapshot of SelectedItems.</summary>
    public void RemoveItems(IReadOnlyList<PickerEntry> items)
    {
        foreach (var item in items)
            Selected.Remove(item);
    }

    /// <summary>Replaces the available-item source, removing stale selections.</summary>
    public void ReplaceSource(IEnumerable<PickerEntry> newSource)
    {
        var newList = newSource.OrderBy(e => e.DisplayName).ToList();
        var validHashes = newList.Select(e => e.Hash).ToHashSet();

        for (var i = Selected.Count - 1; i >= 0; i--)
        {
            if (!validHashes.Contains(Selected[i].Hash))
                Selected.RemoveAt(i);
        }

        _source = newList;
        RefreshAvailable();
    }

    private void RefreshAvailable()
    {
        var selectedHashes = Selected.Select(e => e.Hash).ToHashSet();
        var items = _source.Where(e => !selectedHashes.Contains(e.Hash));
        if (SourceFilter is not null)
            items = items.Where(e => SourceFilter(e));
        if (!string.IsNullOrWhiteSpace(SearchText))
            items = items.Where(e => e.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        Available.Clear();
        foreach (var item in items)
            Available.Add(item);
    }

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private void ClearAll()
    {
        Selected.Clear();
    }

    private bool CanClearAll() => Selected.Count > 0;
}
