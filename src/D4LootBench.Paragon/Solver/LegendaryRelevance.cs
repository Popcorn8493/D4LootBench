using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// What the build is known to do, for judging whether a legendary node's CONDITIONAL "[x]"
/// multiplier applies: the focused stats and any skill text a guide import carried (bar-skill names + descriptions). Everything folds into
/// one normalized blob — lowercase letters only — so tag matching is substring containment.
/// </summary>
public sealed class LegendaryContext
{
    private LegendaryContext(string text) => Text = text;

    /// <summary>Normalized (a–z only) concatenation of every context source.</summary>
    public string Text { get; }

    public static LegendaryContext From(MaximizeFocus? focus, IEnumerable<string>? skillTexts)
    {
        var parts = new List<string>();
        if (focus is not null)
        {
            foreach (string attribute in focus.Attributes)
            {
                if ((focus.Weights?.GetValueOrDefault(attribute, 1.0) ?? 1.0) <= 0)
                    continue; // a zero weight means the stat isn't chased
                parts.Add(attribute);
            }
        }
        if (skillTexts is not null)
            parts.AddRange(skillTexts);
        return new LegendaryContext(Normalize(string.Join(" ", parts)));
    }

    internal static string Normalize(string text) =>
        new(text.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').ToArray());

}

/// <summary>
/// Credits legendary nodes' conditional "×%" multipliers in placement judging only when the
/// build is known to meet their condition. A node's tags name its condition (Keyword_DustDevils,
/// Keyword_Vulnerable, Skill_Shout, ResourceFury …): any tag found in the build's
/// <see cref="LegendaryContext"/> → full credit; no condition beyond generic tags → half credit
/// (reads unconditional, but unverified); a specific condition the build doesn't show → none.
/// Deliberately conservative — a wrong guess would steer board swaps toward legendaries the
/// build can't use.
/// </summary>
public static class LegendaryRelevance
{
    /// <summary>Tags that describe a node's effect, not a condition the build must meet.</summary>
    private static readonly HashSet<string> GenericTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Damage", "Intelligence", "Willpower", "Strength", "Dexterity",
    };

    /// <summary>Extra search tokens where the tag word and the build's wording differ.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = ["crit"],
        ["bleed"] = ["bleed", "dot", "damageovertime"],
        ["burn"] = ["burn", "dot", "damageovertime"],
        ["shadowdot"] = ["shadow", "dot", "damageovertime"],
        ["poison"] = ["poison"],
        ["fortify"] = ["fortif"],
        ["berserk"] = ["berserk"],
        ["healthy"] = ["healthy", "highhealth"],
        ["injured"] = ["injured", "lowhealth"],
        ["attackspeed"] = ["attackspeed"],
        ["cooldown"] = ["cooldown"],
        ["crowdcontrol"] = ["crowdcontrol", "vscc", "stun", "freeze", "immobilize", "slow"],
        ["minion"] = ["minion", "summon", "golem", "skeleton"],
        ["corpse"] = ["corpse"],
        ["ultimate"] = ["ultimate"],
        ["luckyhit"] = ["luckyhit", "combateffectchance"],
        ["dodge"] = ["dodge"],
        ["block"] = ["block"],
    };

    /// <summary>Enemy-state conditions that together hold for most of a fight (Weapons Master:
    /// "to Injured and Healthy enemies") — treated as met.</summary>
    private static readonly HashSet<string> MostlyMetTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Keyword_Healthy", "Keyword_Injured",
    };

    /// <summary>0, 0.5 or 1 — see the class summary.</summary>
    public static double Of(ParagonNodeDef node, LegendaryContext context)
    {
        if (node.Tags.Any(MostlyMetTags.Contains))
            return 1.0;
        // Resource tags (ResourceFury …) name what the power touches, not its condition — every
        // build of the class spends that resource, so they'd make every such node "match".
        var conditions = node.Tags
            .Where(t => !GenericTags.Contains(t) && !t.StartsWith("Resource", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (conditions.Count == 0)
            return 0.5;
        foreach (string tag in conditions)
        {
            foreach (string token in TokensOf(tag))
            {
                if (token.Length >= 3 && context.Text.Contains(token, StringComparison.Ordinal))
                    return 1.0;
            }
        }
        return 0;
    }

    /// <summary>
    /// ∏(1 + relevance × headline ×%) over the purchased legendary nodes — the damage factor the
    /// build's legendaries are credited with. 1 when there's no context or nothing applies.
    /// </summary>
    public static double Factor(ComposedGraph graph, ISet<CellRef> purchased, LegendaryContext? context)
    {
        if (context is null)
            return 1.0;
        double factor = 1.0;
        foreach (var vertex in graph.Vertices)
        {
            if (vertex.Node.Kind != ParagonNodeKind.Legendary || !purchased.Contains(vertex.Cell))
                continue;
            if (LegendaryNodeInfo.HeadlineMultiplierPercent(vertex.Node) is double percent)
                factor *= 1 + Of(vertex.Node, context) * percent / 100;
        }
        return factor;
    }

    /// <summary>A board's legendary value for swap-candidate ranking: relevance × headline ×%.</summary>
    public static double BoardValue(ParagonBoardDef board, LegendaryContext context)
    {
        double best = 0;
        foreach (var placement in board.Nodes)
        {
            if (!Data.ParagonDatabase.NodesBySnoId.TryGetValue(placement.Node, out var node)
                || LegendaryNodeInfo.HeadlineMultiplierPercent(node) is not double percent)
                continue;
            best = Math.Max(best, Of(node, context) * percent);
        }
        return best;
    }

    /// <summary>Normalized search tokens for a node tag: the stripped tag plus its aliases and,
    /// for multi-part skill tags ("Skill_Nature_Magic_Storm"), the most specific part.</summary>
    private static IEnumerable<string> TokensOf(string tag)
    {
        string stripped = tag;
        foreach (string prefix in new[] { "Keyword_", "Skill_Primary_", "Skill_", "Copy_", "SEARCH_", "Subpower_" })
        {
            if (stripped.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = stripped[prefix.Length..];
                break;
            }
        }
        if (stripped.StartsWith("FILTER_Flex_", StringComparison.OrdinalIgnoreCase))
            stripped = stripped.Split('_').Last();

        string normalized = LegendaryContext.Normalize(stripped);
        yield return normalized;
        // Plural keywords ("DustDevils") match the singular in skill text too.
        if (normalized.EndsWith('s') && normalized.Length > 4)
            yield return normalized[..^1];
        string lastPart = LegendaryContext.Normalize(stripped.Split('_').Last());
        if (lastPart != normalized)
            yield return lastPart;
        if (Aliases.TryGetValue(normalized, out var aliases))
        {
            foreach (string alias in aliases)
                yield return alias;
        }
    }
}
