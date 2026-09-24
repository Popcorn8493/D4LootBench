namespace D4LootBench.App.Services;

/// <summary>One stored affix line of a saved gear piece (value kept as entered, may be empty).</summary>
public sealed record SavedGearAffix(uint AffixId, string Name, string Value, bool IsGreater, bool IsTransfigured);

/// <summary>A gear piece saved from the compare window, e.g. the currently equipped gloves.</summary>
public sealed record SavedGearItem(
    string Name,
    uint? ItemTypeId,
    uint? UniqueId,
    IReadOnlyList<SavedGearAffix> Affixes);

/// <summary>
/// The user's gear library for Item Compare, persisted beside the other app settings so an
/// equipped piece only has to be typed in (or scanned) once. Saving under an existing name
/// overwrites that entry.
/// </summary>
public sealed class SavedGearService
{
    private readonly JsonFileStore<List<SavedGearItem>> _store;
    private readonly List<SavedGearItem> _items = [];

    public SavedGearService(string? path = null)
    {
        _store = new JsonFileStore<List<SavedGearItem>>(path ?? JsonFileStore.AppDataPath("saved-gear.json"));
        Load();
    }

    public IReadOnlyList<SavedGearItem> Items => _items;

    /// <summary>Adds or overwrites (by case-insensitive name) and persists.</summary>
    public void Save(SavedGearItem item)
    {
        int existing = _items.FindIndex(i =>
            string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _items[existing] = item;
        else
            _items.Add(item);
        Persist();
    }

    public SavedGearItem? Find(string name) => _items.FirstOrDefault(i =>
        string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Delete(string name)
    {
        if (_items.RemoveAll(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;
        Persist();
        return true;
    }

    private void Load()
    {
        // A corrupt file is moved aside by the store — start empty.
        if (_store.Load() is { } stored)
            _items.AddRange(stored.Where(i => !string.IsNullOrWhiteSpace(i?.Name)));
    }

    // Best-effort — on failure the library just won't persist this change (the store logs it).
    private void Persist() => _store.Save(_items);
}
