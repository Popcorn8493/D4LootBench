using D4LootBench.Core.Data;

namespace D4LootBench.Ai;

/// <summary>
/// Resolves human-readable names (as output by the LLM) to hash IDs using the live catalogs.
/// Falls back to case-insensitive partial matching for suggestions when exact lookup fails.
/// </summary>
public sealed class NameResolver(IFilterDataService data)
{
    public bool TryResolveAffix(string name, out uint hash, out IReadOnlyList<string> suggestions)
    {
        if (data.Affixes.TryGetByName(name, out var entry))
        {
            hash = entry.Hash; suggestions = []; return true;
        }
        var candidates = data.Affixes.All.Select(a => a.Name).ToList();
        if (TryFuzzyResolve(name, candidates, out var resolved))
        {
            data.Affixes.TryGetByName(resolved, out var fuzzy);
            hash = fuzzy.Hash; suggestions = []; return true;
        }
        hash = 0; suggestions = FindSuggestions(name, candidates); return false;
    }

    public bool TryResolveItemType(string name, out uint hash, out IReadOnlyList<string> suggestions)
    {
        if (data.ItemTypes.TryGetByName(name, out var entry))
        {
            hash = entry.Hash; suggestions = []; return true;
        }
        var candidates = data.ItemTypes.All.Select(t => t.Name).ToList();
        if (TryFuzzyResolve(name, candidates, out var resolved))
        {
            data.ItemTypes.TryGetByName(resolved, out var fuzzy);
            hash = fuzzy.Hash; suggestions = []; return true;
        }
        hash = 0; suggestions = FindSuggestions(name, candidates); return false;
    }

    public bool TryResolveUnique(string name, out uint hash, out IReadOnlyList<string> suggestions)
    {
        if (data.Uniques.TryGetByName(name, out var entry))
        {
            hash = entry.SnoId; suggestions = []; return true;
        }
        var candidates = data.Uniques.Released.Select(u => u.Name).ToList();
        if (TryFuzzyResolve(name, candidates, out var resolved))
        {
            data.Uniques.TryGetByName(resolved, out var fuzzy);
            hash = fuzzy.SnoId; suggestions = []; return true;
        }
        hash = 0; suggestions = FindSuggestions(name, candidates); return false;
    }

    public bool TryResolveTalismanSet(string name, out uint hash, out IReadOnlyList<string> suggestions)
    {
        var match = data.TalismanSets.All
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            hash = match.Hash; suggestions = []; return true;
        }
        var candidates = data.TalismanSets.All.Select(s => s.Name).ToList();
        if (TryFuzzyResolve(name, candidates, out var resolved))
        {
            hash = data.TalismanSets.All.First(s => s.Name == resolved).Hash;
            suggestions = []; return true;
        }
        hash = 0; suggestions = FindSuggestions(name, candidates); return false;
    }

    public bool TryResolveTalismanItem(string name, out uint itemHash, out uint setHash,
        out IReadOnlyList<string> suggestions)
    {
        var allItems = data.TalismanSets.All.SelectMany(s => s.Items);
        var match    = allItems.FirstOrDefault(
            i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            itemHash    = match.Hash;
            setHash     = data.TalismanSets.GetSetHashForItem(match.Hash);
            suggestions = [];
            return true;
        }
        itemHash    = 0;
        setHash     = 0;
        suggestions = FindSuggestions(name, allItems.Select(i => i.Name));
        return false;
    }

    /// <summary>
    /// The auto-resolve ladder, tried in order of confidence; each step must be unambiguous:
    /// 1. punctuation-insensitive equality — "+Armor" == "armor", "Paingorger's" == "paingorgers",
    ///    and re-released duplicates ("Azurewrath" vs "Azurewrath (Crucible)") pick the exact base;
    /// 2. containment with exactly one distinct match (duplicate catalog names collapse first);
    /// 3. token cover with exactly one distinct match — "+Heartseeker" from "ranks to heartseeker".
    /// </summary>
    private static bool TryFuzzyResolve(string query, IEnumerable<string> candidates, out string resolved)
    {
        var unique = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string queryNorm = Normalize(query);

        var exact = unique.FirstOrDefault(c => Normalize(c) == queryNorm);
        if (exact is not null) { resolved = exact; return true; }

        var contains = unique
            .Where(c =>
            {
                string cn = Normalize(c);
                return cn.Contains(queryNorm, StringComparison.Ordinal)
                    || queryNorm.Contains(cn, StringComparison.Ordinal);
            })
            .Take(2).ToList();
        if (contains.Count == 1) { resolved = contains[0]; return true; }

        var queryTokens = Tokens(queryNorm);
        var covered = unique
            .Where(c => Tokens(Normalize(c)) is { Count: > 0 } tokens && tokens.IsSubsetOf(queryTokens))
            .Take(2).ToList();
        if (covered.Count == 1) { resolved = covered[0]; return true; }

        resolved = ""; return false;
    }

    /// <summary>Lowercase, punctuation dropped ('+', apostrophes, parentheses), spacing collapsed.</summary>
    private static string Normalize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' or '/') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Meaning-bearing words of a normalized name (filler like "of"/"to"/"ranks" dropped).</summary>
    private static HashSet<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !TokenStopwords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> TokenStopwords =
        new(StringComparer.Ordinal) { "of", "to", "the", "a", "an", "rank", "ranks" };

    private static IReadOnlyList<string> FindSuggestions(string query, IEnumerable<string> candidates, int max = 5)
    {
        string queryNorm = Normalize(query);
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c =>
            {
                string cn = Normalize(c);
                return cn.Contains(queryNorm, StringComparison.Ordinal)
                    || queryNorm.Contains(cn, StringComparison.Ordinal);
            })
            .Take(max)
            .ToList();
    }
}
