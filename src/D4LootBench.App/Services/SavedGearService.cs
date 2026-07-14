using System.IO;
using System.Text.Json;

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
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D4LootBench", "saved-gear.json");

    private readonly string _path;
    private readonly List<SavedGearItem> _items = [];

    public SavedGearService(string? path = null)
    {
        _path = path ?? DefaultPath;
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
        try
        {
            if (!File.Exists(_path))
                return;
            var stored = JsonSerializer.Deserialize<List<SavedGearItem>>(File.ReadAllText(_path));
            if (stored is not null)
                _items.AddRange(stored.Where(i => !string.IsNullOrWhiteSpace(i.Name)));
        }
        catch { /* corrupt file — start empty */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — the library just won't persist this change.
        }
    }
}
