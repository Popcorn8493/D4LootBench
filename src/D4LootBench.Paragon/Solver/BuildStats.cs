using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>One purchased node's threshold check: what it needs at its board's slot vs what the build has.</summary>
public sealed record ThresholdStatus(
    CellRef Cell,
    string NodeName,
    string Attribute,
    double Requirement,
    double Have,
    bool Met);

public sealed record BuildStatsReport(
    IReadOnlyDictionary<string, double> Totals,
    IReadOnlyList<ThresholdStatus> Thresholds)
{
    public int ThresholdsMet => Thresholds.Count(t => t.Met);
}

/// <summary>
/// Effective stat totals of a purchase set, including rare-node threshold bonuses. A threshold's
/// requirement scales with the board's attachment order (<c>valuesByBoardIndex[cell.BoardSlot]</c>
/// — slot order is the order the player attaches boards), and is checked against the character
/// total: a flat non-paragon offset (level + gear, user-supplied) plus the paragon-granted stat.
/// Met bonuses are added iteratively to a fixpoint, since a bonus can grant the core stat that
/// unlocks another node's threshold.
/// </summary>
public static class BuildStats
{
    public static BuildStatsReport Compute(
        ComposedGraph graph,
        IEnumerable<CellRef> purchased,
        ParagonData data,
        double nonParagonStat = 0,
        string? className = null)
        => Compute(graph, purchased, data, NonParagonStats.Uniform(nonParagonStat), className);

    /// <param name="cellMultipliers">
    /// Per-cell stat multipliers from glyph node buffs (see <see cref="GlyphNodeBuffs"/>);
    /// absent cells count ×1. Applied to everything the node grants, threshold bonuses included.
    /// </param>
    public static BuildStatsReport Compute(
        ComposedGraph graph,
        IEnumerable<CellRef> purchased,
        ParagonData data,
        NonParagonStats nonParagonStats,
        string? className = null,
        IReadOnlyDictionary<CellRef, double>? cellMultipliers = null)
    {
        var thresholdsBySnoId = data.Thresholds.ToDictionary(t => t.SnoId, StringComparer.OrdinalIgnoreCase);
        var purchasedSet = purchased as ISet<CellRef> ?? purchased.ToHashSet();

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(GraphVertex Vertex, ParagonThresholdDef Def)>();
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            var vertex = graph.Vertices[v];
            if (v != graph.StartVertex && !purchasedSet.Contains(vertex.Cell))
                continue;

            double factor = cellMultipliers?.GetValueOrDefault(vertex.Cell, 1.0) ?? 1.0;
            foreach (var attribute in vertex.Node.Attributes)
            {
                if (!attribute.IsThresholdBonus && attribute.Value is double value)
                    totals[attribute.Attribute] = totals.GetValueOrDefault(attribute.Attribute) + value * factor;
            }

            if (vertex.Node.Thresholds.Count == 0)
                continue;
            // A node can list one threshold per class group — take the one for this class.
            var defs = vertex.Node.Thresholds
                .Select(sno => thresholdsBySnoId.GetValueOrDefault(sno))
                .Where(def => def is not null)
                .ToList();
            var match = defs.FirstOrDefault(def =>
                    className is null || def!.Classes.Count == 0
                    || def.Classes.Contains(className, StringComparer.OrdinalIgnoreCase))
                ?? defs.FirstOrDefault();
            if (match is not null)
                candidates.Add((vertex, match));
        }

        // Fixpoint: activating a bonus can raise a core stat and unlock another threshold.
        var met = new HashSet<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (met.Contains(i))
                    continue;
                var (vertex, def) = candidates[i];
                if (!def.Requirements.All(r => Have(r.Attribute) >= RequirementAt(r, vertex.Cell.BoardSlot)))
                    continue;
                met.Add(i);
                changed = true;
                double factor = cellMultipliers?.GetValueOrDefault(vertex.Cell, 1.0) ?? 1.0;
                foreach (var attribute in vertex.Node.Attributes)
                {
                    if (attribute.IsThresholdBonus && attribute.Value is double value)
                        totals[attribute.Attribute] = totals.GetValueOrDefault(attribute.Attribute) + value * factor;
                }
            }
        }

        var statuses = new List<ThresholdStatus>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var (vertex, def) = candidates[i];
            var first = def.Requirements.FirstOrDefault();
            if (first is null)
                continue;
            statuses.Add(new ThresholdStatus(
                vertex.Cell,
                vertex.Node.Name ?? vertex.Node.InternalName,
                first.Attribute,
                RequirementAt(first, vertex.Cell.BoardSlot),
                Have(first.Attribute),
                met.Contains(i)));
        }
        return new BuildStatsReport(totals, statuses);

        // "Willpower_Total" counts every source; paragon grants "_Core", the rest is the offset.
        double Have(string requirementAttribute)
        {
            string paragonKey = requirementAttribute.EndsWith("_Total", StringComparison.Ordinal)
                ? requirementAttribute[..^"_Total".Length] + "_Core"
                : requirementAttribute;
            double have = totals.GetValueOrDefault(paragonKey) + totals.GetValueOrDefault(requirementAttribute);
            return requirementAttribute.EndsWith("_Total", StringComparison.Ordinal)
                ? have + nonParagonStats.For(requirementAttribute)
                : have;
        }
    }

    /// <summary>The requirement for a board at the given attachment slot; later slots cost more.</summary>
    public static double RequirementAt(ThresholdRequirement requirement, int boardSlot)
    {
        var values = requirement.ValuesByBoardIndex;
        if (boardSlot < values.Count && values[boardSlot] is double exact)
            return exact;
        // Past (or holes in) the table: the last resolvable value below the slot.
        for (int i = Math.Min(boardSlot, values.Count - 1); i >= 0; i--)
        {
            if (values[i] is double value)
                return value;
        }
        return double.PositiveInfinity;
    }
}
