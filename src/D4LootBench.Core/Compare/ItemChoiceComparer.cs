using D4LootBench.Core.Models;

namespace D4LootBench.Core.Compare;

/// <summary>One affix on a candidate item; the roll value is optional but sharpens the verdict.</summary>
public sealed record CandidateAffix(uint AffixId, string Name, double? Value, bool IsGreater);

/// <summary>A dropped item the user is weighing, e.g. one of two pairs of gloves.</summary>
public sealed record CandidateItem(
    string Label,
    uint? ItemTypeId,
    uint? UniqueId,
    IReadOnlyList<CandidateAffix> Affixes);

/// <summary>
/// An unmet paragon rare-node threshold the character can close with gear stats — the paragon
/// board's contribution to item value (threshold requirements check the character TOTAL).
/// </summary>
public sealed record StatNeed(uint AffixId, string StatName, double Deficit, string NodeName);

public sealed record ScoreLine(double Points, string Reason);

public sealed record ItemScore(CandidateItem Item, double Total, IReadOnlyList<ScoreLine> Lines);

public sealed record CompareOutcome(IReadOnlyList<ItemScore> Scores, string Verdict);

/// <summary>
/// Scores candidate items against two objective references: the loaded filter (the build guide's
/// wish list — desired affixes per slot in priority order, GA wishes, target uniques) and the
/// paragon board (unmet rare-node threshold deficits that gear core stats can close).
/// Deterministic and explainable: every point comes with a reason line.
/// </summary>
public static class ItemChoiceComparer
{
    /// <summary>A guide's top-priority affix is worth this much; lower priorities step down.
    /// Shared with <see cref="GearPriorityExtractor"/> so both read the filter the same way.</summary>
    public const double TopAffixWeight = 4.0;

    /// <summary>Multiplier when the matched affix rolled as a Greater Affix.</summary>
    public const double GreaterAffixFactor = 1.5;

    /// <summary>Being the guide's target unique outweighs any single affix line.</summary>
    private const double TargetUniquePoints = 10.0;

    /// <summary>Fully closing one paragon threshold deficit is worth a top-priority affix.</summary>
    private const double ThresholdClosePoints = TopAffixWeight;

    /// <summary>Greater Affixes always roll the maximum natural value times this factor.</summary>
    public const double GreaterAffixRollFactor = 1.5;

    public static CompareOutcome Compare(
        IReadOnlyList<CandidateItem> items,
        FilterRuleset? reference,
        IReadOnlyList<StatNeed> statNeeds)
    {
        // GA rolls are deterministic in-game (max natural roll × 1.5), so a GA line without an
        // entered value derives one from the best natural roll entered for the same affix.
        var bestNaturalByAffix = items
            .SelectMany(i => i.Affixes)
            .Where(a => !a.IsGreater && a.Value is > 0)
            .GroupBy(a => a.AffixId)
            .ToDictionary(g => g.Key, g => g.Max(a => a.Value!.Value));

        double? Effective(CandidateAffix a) => a.Value
            ?? (a.IsGreater && bestNaturalByAffix.TryGetValue(a.AffixId, out double natural)
                ? natural * GreaterAffixRollFactor
                : null);

        // Relative roll quality: the same desired affix on several candidates is scaled by the
        // best (entered or derived) value, so a higher roll of the same stat objectively wins.
        var bestValueByAffix = items
            .SelectMany(i => i.Affixes)
            .Select(a => (a.AffixId, Value: Effective(a)))
            .Where(t => t.Value is > 0)
            .GroupBy(t => t.AffixId)
            .ToDictionary(g => g.Key, g => g.Max(t => t.Value!.Value));

        var scores = items.Select(item => ScoreItem(item, reference, statNeeds, bestValueByAffix, Effective)).ToList();

        var ranked = scores.OrderByDescending(s => s.Total).ToList();
        string verdict;
        if (ranked.Count < 2 || Math.Abs(ranked[0].Total - ranked[1].Total) < 0.05)
            verdict = $"Too close to call — both score {ranked[0].Total:0.#} point(s). " +
                      "Enter roll values (or load a filter / open the paragon planner) to sharpen the comparison.";
        else
            verdict = $"{ranked[0].Item.Label} is the better choice: " +
                      $"{ranked[0].Total:0.#} vs {string.Join(" vs ", ranked.Skip(1).Select(s => $"{s.Total:0.#}"))} point(s).";
        return new CompareOutcome(scores, verdict);
    }

    private static ItemScore ScoreItem(
        CandidateItem item,
        FilterRuleset? reference,
        IReadOnlyList<StatNeed> statNeeds,
        IReadOnlyDictionary<uint, double> bestValueByAffix,
        Func<CandidateAffix, double?> effective)
    {
        var lines = new List<ScoreLine>();

        var (slotRule, desired, optional, greaterWanted) = DesiredAffixesFor(item, reference);
        if (reference is null)
            lines.Add(new ScoreLine(0, "No filter loaded — scoring uses paragon threshold needs only."));
        else if (slotRule is null && item.UniqueId is null)
            lines.Add(new ScoreLine(0, "No filter rule covers this item type — affixes score against paragon needs only."));

        if (item.UniqueId is uint uniqueId && reference is not null)
        {
            var uniqueRule = reference.Rules.FirstOrDefault(r =>
                r.IsEnabled && r.Visibility == Visibility.Show
                && r.Conditions.OfType<SpecificUniqueCondition>().Any(c => c.UniqueIds.Contains(uniqueId)));
            lines.Add(uniqueRule is not null
                ? new ScoreLine(TargetUniquePoints, $"Target unique of the build (rule '{uniqueRule.Name}').")
                : new ScoreLine(0, "A unique, but not one the build is hunting."));
        }

        var needByAffix = statNeeds
            .GroupBy(n => n.AffixId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.Deficit).First());

        foreach (var affix in item.Affixes)
        {
            bool scored = false;

            int position = desired.IndexOf(affix.AffixId);
            bool isOptional = position < 0 && optional.Contains(affix.AffixId);
            if (position >= 0 || isOptional)
            {
                double weight = position >= 0 ? Math.Max(1.0, TopAffixWeight - position) : 1.0;
                double ratio = effective(affix) is double value && bestValueByAffix.TryGetValue(affix.AffixId, out double best)
                    && best > 0
                    ? value / best
                    : 1.0;
                double points = weight * ratio * (affix.IsGreater ? GreaterAffixFactor : 1.0);
                string rank = position >= 0 ? $"priority {position + 1}" : "nice-to-have";
                string detail = $"{affix.Name}: {rank} affix of the guide";
                if (affix.IsGreater)
                    detail += greaterWanted.Contains(affix.AffixId)
                        ? ", Greater Affix exactly where the guide wants one"
                        : ", Greater Affix";
                if (affix.IsGreater && affix.Value is null && effective(affix) is not null)
                    detail += $", roll derived as max × {GreaterAffixRollFactor}";
                if (ratio < 1.0)
                    detail += $", rolled {ratio:P0} of the best candidate";
                lines.Add(new ScoreLine(points, detail + "."));
                scored = true;
            }

            if (needByAffix.TryGetValue(affix.AffixId, out var need))
            {
                if (effective(affix) is double value && value > 0)
                {
                    double closed = Math.Min(value / need.Deficit, 1.0);
                    lines.Add(new ScoreLine(ThresholdClosePoints * closed,
                        $"{affix.Name}: +{value:0} {need.StatName} closes {closed:P0} of the " +
                        $"{need.NodeName} paragon threshold deficit ({need.Deficit:0} short)."));
                }
                else
                {
                    lines.Add(new ScoreLine(ThresholdClosePoints / 2,
                        $"{affix.Name}: feeds the {need.NodeName} paragon threshold " +
                        $"({need.Deficit:0} {need.StatName} short) — enter its value to weigh it exactly."));
                }
                scored = true;
            }

            if (!scored)
                lines.Add(new ScoreLine(0, $"{affix.Name}: not sought by the build."));
        }

        return new ItemScore(item, lines.Sum(l => l.Points), lines);
    }

    /// <summary>
    /// The guide's wish list for this item's slot: the first enabled Show rule whose item-type
    /// condition covers the item. Required affixes keep the rule's priority order; optional
    /// (any-of) affixes count as nice-to-have.
    /// </summary>
    private static (FilterRule? Rule, List<uint> Desired, HashSet<uint> Optional, HashSet<uint> GreaterWanted)
        DesiredAffixesFor(CandidateItem item, FilterRuleset? reference)
    {
        if (reference is null || item.ItemTypeId is not uint typeId)
            return (null, [], [], []);

        var rule = reference.Rules.FirstOrDefault(r =>
            r.IsEnabled && r.Visibility == Visibility.Show
            && r.Conditions.OfType<ItemTypeCondition>().Any(c => c.TypeIds.Contains(typeId)));
        if (rule is null)
            return (null, [], [], []);

        var desired = rule.Conditions.OfType<AffixCondition>().SelectMany(c => c.AffixIds).ToList();
        var optional = rule.Conditions.OfType<OptionalAffixCondition>().SelectMany(c => c.AffixIds).ToHashSet();
        var greaterWanted = rule.Conditions.OfType<AffixCondition>().SelectMany(c => c.GreaterEntries)
            .Concat(rule.Conditions.OfType<OptionalAffixCondition>().SelectMany(c => c.GreaterEntries))
            .Select(e => e.AffixId)
            .ToHashSet();
        return (rule, desired, optional, greaterWanted);
    }
}
