using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Weighted, unit-normalized value a purchase set holds in a focus's stats — the same scoring the
/// maximizer chases, so placement candidates are judged by what the user asked for. Counts the
/// focused attributes (per-attribute mean-normalized, focus-weighted, node-buff multiplied), the
/// defense basket in proportion to <see cref="MaximizeFocus.DefenseShare"/>, and the glyph
/// delivery term: source stat purchased inside an attribute-mapped glyph's radius counts again,
/// scaled by the delivery weight — mirroring <see cref="PointMaximizer"/>'s valuation, raw like
/// activation (the game converts purchased stat, not buffed stat).
/// </summary>
public static class FocusScore
{
    public static double Of(
        ComposedGraph graph, ISet<CellRef> purchased, MaximizeFocus focus,
        IReadOnlyDictionary<CellRef, double>? multipliers = null)
    {
        var attributes = focus.Attributes.Count > 0 ? focus.Attributes : MaximizeFocus.CoreStats;
        var attributeSet = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        var deliveries = focus.GlyphDeliveries ?? [];
        var sourceSet = new HashSet<string>(deliveries.Select(d => d.SourceAttribute), StringComparer.OrdinalIgnoreCase);
        var mean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value
                    || (!attributeSet.Contains(a.Attribute) && !sourceSet.Contains(a.Attribute)))
                    continue;
                var (sum, count) = mean.GetValueOrDefault(a.Attribute);
                mean[a.Attribute] = (sum + Math.Abs(value), count + 1);
            }
        }

        // Defense counts toward a candidate's worth in proportion to the survivability share,
        // so suggestions don't trade the defense slice away for marginal focus gains.
        var defenseMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (focus.DefenseShare > 0)
        {
            foreach (var vertex in graph.Vertices)
            {
                foreach (var a in vertex.Node.Attributes)
                {
                    if (a.IsThresholdBonus || a.Value is not double value || !MaximizeFocus.IsDefensive(a.Attribute))
                        continue;
                    var (sum, count) = defenseMean.GetValueOrDefault(a.Attribute);
                    defenseMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
                }
            }
        }

        // Bucket damping mirrors the maximizer: additive-damage attrs (and the delivery term,
        // whose destinations live in that bucket) count at their marginal real value.
        double additiveDamp = focus.AdditiveDamageFraction is double bucket ? 1.0 / (1.0 + bucket) : 1.0;
        double DampOf(string attribute) =>
            additiveDamp < 1.0 && DamageModel.Classify(attribute) == DamageBucket.AdditiveDamage
                ? additiveDamp
                : 1.0;

        double score = 0;
        foreach (var vertex in graph.Vertices)
        {
            if (!purchased.Contains(vertex.Cell))
                continue;
            double multiplier = multipliers?.GetValueOrDefault(vertex.Cell, 1.0) ?? 1.0;
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value)
                    continue;
                if (attributeSet.Contains(a.Attribute))
                {
                    var (sum, count) = mean[a.Attribute];
                    score += value * multiplier / (sum / count)
                        * (focus.Weights?.GetValueOrDefault(a.Attribute, 1.0) ?? 1.0)
                        * DampOf(a.Attribute);
                }
                if (focus.DefenseShare > 0 && defenseMean.TryGetValue(a.Attribute, out var dm))
                    score += value * multiplier / (dm.Sum / dm.Count) * focus.DefenseShare * 2;
                foreach (var delivery in deliveries)
                {
                    if (!string.Equals(a.Attribute, delivery.SourceAttribute, StringComparison.OrdinalIgnoreCase)
                        || vertex.Cell.BoardSlot != delivery.Socket.BoardSlot
                        || Math.Abs(vertex.Cell.X - delivery.Socket.X)
                            + Math.Abs(vertex.Cell.Y - delivery.Socket.Y) > delivery.Radius
                        || !mean.TryGetValue(a.Attribute, out var sm))
                        continue;
                    score += value / (sm.Sum / sm.Count) * delivery.Weight * additiveDamp;
                }
            }
        }
        return score;
    }
}
