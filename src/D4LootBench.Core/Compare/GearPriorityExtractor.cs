using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.Core.Compare;

/// <summary>One affix the loaded filter hunts, with its accumulated guide weight.</summary>
public sealed record GearAffixPriority(uint AffixId, string Name, double Weight);

/// <summary>
/// Distills the loaded filter into per-affix priority weights — the gear-side counterpart of
/// what <see cref="Compare.ItemChoiceComparer"/> reads per slot. Every enabled Show rule
/// contributes its required affixes at the comparer's positional weights (top affix 4,
/// stepping down to 1), optional (any-of) affixes at nice-to-have weight 1, and a Greater
/// Affix wish multiplies by the comparer's GA factor. The same affix wanted on several slots
/// stacks, so a stat the whole build hunts outweighs a single slot's filler line.
/// </summary>
public static class GearPriorityExtractor
{
    public static IReadOnlyList<GearAffixPriority> Extract(FilterRuleset ruleset)
    {
        var totals = new Dictionary<uint, double>();
        foreach (var rule in ruleset.Rules)
        {
            if (!rule.IsEnabled || rule.Visibility != Visibility.Show)
                continue;

            var greaterWanted = rule.Conditions.OfType<AffixCondition>().SelectMany(c => c.GreaterEntries)
                .Concat(rule.Conditions.OfType<OptionalAffixCondition>().SelectMany(c => c.GreaterEntries))
                .Select(e => e.AffixId)
                .ToHashSet();

            var seen = new HashSet<uint>();
            int position = 0;
            foreach (uint id in rule.Conditions.OfType<AffixCondition>().SelectMany(c => c.AffixIds))
            {
                if (!seen.Add(id))
                    continue;
                double weight = Math.Max(1.0, ItemChoiceComparer.TopAffixWeight - position)
                    * (greaterWanted.Contains(id) ? ItemChoiceComparer.GreaterAffixFactor : 1.0);
                totals[id] = totals.GetValueOrDefault(id) + weight;
                position++;
            }

            foreach (uint id in rule.Conditions.OfType<OptionalAffixCondition>().SelectMany(c => c.AffixIds))
            {
                if (!seen.Add(id))
                    continue;
                totals[id] = totals.GetValueOrDefault(id)
                    + (greaterWanted.Contains(id) ? ItemChoiceComparer.GreaterAffixFactor : 1.0);
            }
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new GearAffixPriority(kv.Key, AffixDatabase.GetDisplayName(kv.Key), kv.Value))
            .ToList();
    }
}
