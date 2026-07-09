using System.IO;
using System.Text.Json;

namespace D4LootBench.App.Services;

/// <summary>
/// Most-recently-used paragon project files, persisted beside the other app settings so the
/// planner's Recent menu survives restarts. Newest first, capped at <see cref="MaxEntries"/>.
/// </summary>
public sealed class RecentProjectsService
{
    private const int MaxEntries = 10;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D4LootBench", "recent-paragon-projects.json");

    private readonly List<string> _paths = [];

    public RecentProjectsService() => Load();

    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Moves (or inserts) the path to the top and persists the list.</summary>
    public void Touch(string path)
    {
        _paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, path);
        if (_paths.Count > MaxEntries)
            _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
        Save();
    }

    /// <summary>Drops a path (e.g. the file was deleted or moved).</summary>
    public void Remove(string path)
    {
        if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SettingsPath));
            if (stored is not null)
                _paths.AddRange(stored.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxEntries));
        }
        catch { /* corrupt file — start empty */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — the menu just won't persist this change.
        }
    }
}
