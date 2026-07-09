namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Character stats granted by everything except the paragon board (level, gear, item bonuses),
/// counted toward rare-node threshold requirements like "Willpower_Total". Either a uniform
/// value applied to every stat (the legacy single "sheet stats" number) or per-stat values
/// keyed by core stat name ("Strength", "Intelligence", "Willpower", "Dexterity" — the
/// "_Total"/"_Core" suffixes are ignored when matching).
/// </summary>
public sealed class NonParagonStats
{
    public static readonly NonParagonStats None = new(0, null);

    private readonly double _uniform;
    private readonly Dictionary<string, double>? _byStat;

    private NonParagonStats(double uniform, Dictionary<string, double>? byStat)
    {
        _uniform = uniform;
        _byStat = byStat;
    }

    /// <summary>The same offset for every stat.</summary>
    public static NonParagonStats Uniform(double value) => value == 0 ? None : new(value, null);

    /// <summary>Per-stat offsets; stats not in the map contribute nothing.</summary>
    public static NonParagonStats PerStat(IEnumerable<KeyValuePair<string, double>> byStat)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attribute, value) in byStat)
            map[BaseName(attribute)] = value;
        return new NonParagonStats(0, map);
    }

    /// <summary>The offset counted toward a requirement on the given attribute.</summary>
    public double For(string requirementAttribute) =>
        _byStat is null ? _uniform : _byStat.GetValueOrDefault(BaseName(requirementAttribute));

    private static string BaseName(string attribute) => attribute
        .Replace("_Total", "", StringComparison.Ordinal)
        .Replace("_Core", "", StringComparison.Ordinal);
}
