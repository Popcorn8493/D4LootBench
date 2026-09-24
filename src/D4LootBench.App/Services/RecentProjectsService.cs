namespace D4LootBench.App.Services;

/// <summary>
/// Most-recently-used paragon project files, persisted beside the other app settings so the
/// planner's Recent menu survives restarts. Newest first, capped at <see cref="MaxEntries"/>.
/// </summary>
public sealed class RecentProjectsService
{
    private const int MaxEntries = 10;

    private readonly JsonFileStore<List<string>> _store;
    private readonly List<string> _paths = [];

    public RecentProjectsService(string? path = null)
    {
        _store = new JsonFileStore<List<string>>(
            path ?? JsonFileStore.AppDataPath("recent-paragon-projects.json"), JsonFileStore.Compact);
        Load();
    }

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
        // A corrupt file is moved aside by the store — start empty.
        if (_store.Load() is { } stored)
            _paths.AddRange(stored.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxEntries));
    }

    // Best-effort — on failure the menu just won't persist this change (the store logs it).
    private void Save() => _store.Save(_paths);
}
