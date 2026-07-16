using System.IO;
using System.Text.Json;

namespace D4LootBench.App.Services;

/// <summary>One priority slot of the user's own compare wish list (top = weighs most).</summary>
public sealed record StoredReferenceAffix(uint AffixId, string Name, bool GreaterWanted);

/// <summary>The user-provided compare reference: whether it is active, the ordered affix
/// priorities, and an optional hunted unique.</summary>
public sealed record StoredCompareReference(
    bool UseCustom,
    IReadOnlyList<StoredReferenceAffix> Affixes,
    uint? TargetUniqueId)
{
    public static StoredCompareReference Empty { get; } = new(false, [], null);
}

/// <summary>
/// Persists the Item Compare window's custom reference beside the other app settings, so a
/// hand-built priority list survives closing the window (and the app).
/// </summary>
public sealed class CompareReferenceService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D4LootBench", "compare-reference.json");

    private readonly string _path;

    public CompareReferenceService(string? path = null)
    {
        _path = path ?? DefaultPath;
        Current = Load();
    }

    public StoredCompareReference Current { get; private set; }

    public void Save(StoredCompareReference reference)
    {
        Current = reference;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(reference,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — the reference just won't persist this change.
        }
    }

    private StoredCompareReference Load()
    {
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<StoredCompareReference>(File.ReadAllText(_path)) is { } stored)
                return stored.Affixes is null ? stored with { Affixes = [] } : stored;
        }
        catch
        {
            // Corrupt file — start empty.
        }
        return StoredCompareReference.Empty;
    }
}
