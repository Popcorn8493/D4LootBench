namespace D4LootBench.Core.Compare;

/// <summary>
/// A stat gear can show that the loot-filter affix catalog has no single entry for — e.g.
/// "+All Stats" (a Horadric transfiguration staple on weapons) is all four core stats at once.
/// Compare-only: the synthetic id lives outside the game hash space, and scoring expands the
/// stat to its component catalog affixes (each credited the full value, matching in-game math).
/// </summary>
public sealed record AggregateStat(uint Id, string Name, IReadOnlyList<string> ComponentAffixNames)
{
    /// <summary>Loose name equality for OCR text ("165 All Stats" → "All Stats").</summary>
    public bool NameMatches(string text) => Normalize(text) == Normalize(Name);

    private static string Normalize(string name) => string.Join(' ',
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public static class AggregateStats
{
    /// <summary>Synthetic ids start here — far above any real affix hash in d4-data.json.</summary>
    public const uint AllStatsId = 0xFF00_0001;

    public static IReadOnlyList<AggregateStat> All { get; } =
    [
        new(AllStatsId, "All Stats", ["Strength", "Intelligence", "Willpower", "Dexterity"]),
    ];

    public static bool TryGet(uint id, out AggregateStat stat)
    {
        stat = All.FirstOrDefault(s => s.Id == id)!;
        return stat is not null;
    }

    public static AggregateStat? MatchName(string text) => All.FirstOrDefault(s => s.NameMatches(text));
}
