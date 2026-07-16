using System.IO;
using System.Text.Json;

namespace D4LootBench.App.Services;

/// <summary>
/// One real character: the durable facts imports and project loads must never overwrite —
/// level, sheet stats, paragon point pool, and the level of every glyph the character owns
/// (keyed by glyph internal name).
/// </summary>
public sealed record SavedCharacter(
    string Name,
    string ClassName,
    int Level,
    int TotalPoints,
    double SheetStrength,
    double SheetIntelligence,
    double SheetWillpower,
    double SheetDexterity,
    IReadOnlyDictionary<string, int> GlyphLevels)
{
    /// <summary>Gear ALWAYS-ON "+X% damage" sum as a percent (350 = +350%); 0 on pre-existing saves.</summary>
    public double SheetAdditiveDamage { get; init; }

    /// <summary>Gear conditional "+X% damage" sum as a percent (vulnerable/close/crit damage …).</summary>
    public double SheetSituationalDamage { get; init; }
}

/// <summary>
/// The character library for the paragon planner, persisted beside the other app settings.
/// The active character survives restarts; saving under an existing name overwrites it.
/// </summary>
public sealed class SavedCharacterService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D4LootBench", "saved-characters.json");

    private sealed record Library(string? Active, List<SavedCharacter> Characters);

    private readonly string _path;
    private readonly List<SavedCharacter> _characters = [];
    private string? _activeName;

    public SavedCharacterService(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<SavedCharacter> Characters => _characters;

    /// <summary>The character the planner should re-impose after imports; persisted.</summary>
    public string? ActiveName
    {
        get => _activeName;
        set
        {
            if (_activeName == value)
                return;
            _activeName = value;
            Persist();
        }
    }

    public SavedCharacter? Find(string name) => _characters.FirstOrDefault(c =>
        string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or overwrites (by case-insensitive name) and persists.</summary>
    public void Save(SavedCharacter character)
    {
        int existing = _characters.FindIndex(c =>
            string.Equals(c.Name, character.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _characters[existing] = character;
        else
            _characters.Add(character);
        Persist();
    }

    public bool Delete(string name)
    {
        if (_characters.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;
        if (string.Equals(_activeName, name, StringComparison.OrdinalIgnoreCase))
            _activeName = null;
        Persist();
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var stored = JsonSerializer.Deserialize<Library>(File.ReadAllText(_path));
            if (stored is null)
                return;
            _characters.AddRange(stored.Characters.Where(c => !string.IsNullOrWhiteSpace(c.Name)));
            _activeName = stored.Active;
        }
        catch { /* corrupt file — start empty */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Library(_activeName, _characters),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — the library just won't persist this change.
        }
    }
}
