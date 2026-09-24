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
    private readonly JsonFileStore<StoredCompareReference> _store;

    public CompareReferenceService(string? path = null)
    {
        _store = new JsonFileStore<StoredCompareReference>(
            path ?? JsonFileStore.AppDataPath("compare-reference.json"));
        Current = Load();
    }

    public StoredCompareReference Current { get; private set; }

    public void Save(StoredCompareReference reference)
    {
        Current = reference;
        // Best-effort — on failure the reference just won't persist this change (the store logs it).
        _store.Save(reference);
    }

    private StoredCompareReference Load()
    {
        // A corrupt file is moved aside by the store — start empty.
        if (_store.Load() is { } stored)
            return stored.Affixes is null ? stored with { Affixes = [] } : stored;
        return StoredCompareReference.Empty;
    }
}
