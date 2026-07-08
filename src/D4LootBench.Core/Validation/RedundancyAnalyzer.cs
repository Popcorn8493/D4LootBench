using D4LootBench.Core.Models;

namespace D4LootBench.Core.Validation;

/// <summary>
/// Detects redundant structure in a ruleset: rules that duplicate an earlier rule,
/// and catch-all rules (no conditions) that shadow every rule below them.
/// Comparison is structural — names never matter, color matters only for Recolor rules,
/// and ID lists compare as sets so ordering differences don't defeat detection.
/// </summary>
public static class RedundancyAnalyzer
{
    /// <summary>Rule at <see cref="Index"/> duplicates the earlier rule at <see cref="DuplicateOfIndex"/>.</summary>
    public sealed record DuplicateRule(int Index, int DuplicateOfIndex);

    /// <summary>
    /// Finds rules that duplicate an earlier rule and therefore never take effect.
    /// A later rule is a duplicate unless the earlier rule is disabled while the later
    /// one is enabled (the later rule is then the one that actually applies).
    /// </summary>
    public static IReadOnlyList<DuplicateRule> FindDuplicateRules(FilterRuleset ruleset)
    {
        var duplicates = new List<DuplicateRule>();
        var duplicateIndices = new HashSet<int>();

        for (var j = 1; j < ruleset.Rules.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                // Compare against originals only, so chains of copies all point at the first.
                if (duplicateIndices.Contains(i)) continue;
                if (!AreRulesEquivalent(ruleset.Rules[i], ruleset.Rules[j])) continue;
                if (!ruleset.Rules[i].IsEnabled && ruleset.Rules[j].IsEnabled) continue;

                duplicates.Add(new DuplicateRule(j, i));
                duplicateIndices.Add(j);
                break;
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Index of the first enabled rule with no conditions (matches every item) that still has
    /// enabled rules below it, or null when no rule is shadowed that way.
    /// </summary>
    public static int? FindShadowingCatchAll(FilterRuleset ruleset)
    {
        for (var i = 0; i < ruleset.Rules.Count; i++)
        {
            var rule = ruleset.Rules[i];
            if (!rule.IsEnabled || rule.Conditions.Count > 0) continue;
            return ruleset.Rules.Skip(i + 1).Any(r => r.IsEnabled) ? i : null;
        }
        return null;
    }

    /// <summary>Number of enabled rules below the given index.</summary>
    public static int CountEnabledRulesBelow(FilterRuleset ruleset, int index) =>
        ruleset.Rules.Skip(index + 1).Count(r => r.IsEnabled);

    public static bool AreRulesEquivalent(FilterRule a, FilterRule b) =>
        a.Visibility == b.Visibility &&
        (a.Visibility != Visibility.Recolor || a.Color == b.Color) &&
        ConditionSetsEquivalent(a.Conditions, b.Conditions);

    private static bool ConditionSetsEquivalent(IReadOnlyList<Condition> a, IReadOnlyList<Condition> b)
    {
        if (a.Count != b.Count) return false;

        var unmatched = b.ToList();
        foreach (var cond in a)
        {
            var match = unmatched.FindIndex(other => AreConditionsEquivalent(cond, other));
            if (match < 0) return false;
            unmatched.RemoveAt(match);
        }
        return true;
    }

    public static bool AreConditionsEquivalent(Condition a, Condition b) => (a, b) switch
    {
        (ItemTypeCondition x, ItemTypeCondition y) => SetEqual(x.TypeIds, y.TypeIds),

        (AffixCondition x, AffixCondition y) =>
            x.MinimumCount == y.MinimumCount &&
            SetEqual(x.AffixIds, y.AffixIds) &&
            SetEqual(x.GreaterEntries.Select(g => g.AffixId), y.GreaterEntries.Select(g => g.AffixId)),

        (OptionalAffixCondition x, OptionalAffixCondition y) =>
            x.MinimumCount == y.MinimumCount &&
            SetEqual(x.AffixIds, y.AffixIds) &&
            SetEqual(x.GreaterEntries.Select(g => g.AffixId), y.GreaterEntries.Select(g => g.AffixId)),

        (SpecificUniqueCondition x, SpecificUniqueCondition y) => SetEqual(x.UniqueIds, y.UniqueIds),

        (TalismanSetCondition x, TalismanSetCondition y) =>
            SetEqual(x.SetIds, y.SetIds) && SetEqual(x.SetEntries, y.SetEntries),

        (UnknownCondition x, UnknownCondition y) =>
            x.ConditionType == y.ConditionType && x.RawBytes.AsSpan().SequenceEqual(y.RawBytes),

        // Remaining condition types carry only scalar fields — record equality is structural.
        _ => a.Equals(b)
    };

    private static bool SetEqual<T>(IEnumerable<T> a, IEnumerable<T> b) =>
        a.ToHashSet().SetEquals(b.ToHashSet());
}
