using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>How hard a reference build stacks one attribute (see <see cref="BuildReference"/>).</summary>
public sealed record ReferenceEmphasis(string Attribute, double Score);

/// <summary>
/// Distills a reference build's paragon allocation into per-stat emphasis scores, so the
/// point maximizer can be told to chase what a proven build actually stacks instead of a
/// hand-picked checkbox list. Scores are unit-normalized like the maximizer's own values —
/// each node's grant is measured against that attribute's average per-node magnitude — and
/// weighted by node kind: normal nodes are mostly travel (×0.25), magic nodes are choices
/// (×1), rare and legendary nodes are the build's deliberate picks (×1.5). A score reads
/// roughly as "weighted average-nodes' worth of the stat".
/// </summary>
public static class BuildReference
{
    public static double KindWeight(ParagonNodeKind kind) => kind switch
    {
        ParagonNodeKind.Normal => 0.25,
        ParagonNodeKind.Magic => 1.0,
        ParagonNodeKind.Rare => 1.5,
        ParagonNodeKind.Legendary => 1.5,
        _ => 0,
    };

    /// <summary>
    /// Consensus across several references: each reference's scores are first made relative to
    /// its own tier best (core stats vs everything else — different builds have different node
    /// counts, so absolute scores don't compare), then averaged. A stat every guide stacks
    /// keeps a high relative score; a single guide's pet stat is diluted by the others' zeros.
    /// </summary>
    public static IReadOnlyList<ReferenceEmphasis> Combine(
        IReadOnlyList<IReadOnlyList<ReferenceEmphasis>> references,
        IReadOnlyCollection<string> coreStats)
    {
        if (references.Count == 0)
            return [];
        var coreSet = new HashSet<string>(coreStats, StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            double coreTop = reference.Where(e => coreSet.Contains(e.Attribute))
                .Select(e => e.Score).DefaultIfEmpty(0).Max();
            double secondaryTop = reference.Where(e => !coreSet.Contains(e.Attribute))
                .Select(e => e.Score).DefaultIfEmpty(0).Max();
            foreach (var e in reference)
            {
                double tierTop = coreSet.Contains(e.Attribute) ? coreTop : secondaryTop;
                if (tierTop <= 0)
                    continue;
                totals[e.Attribute] = totals.GetValueOrDefault(e.Attribute) + e.Score / tierTop;
            }
        }
        return totals
            .Select(kv => new ReferenceEmphasis(kv.Key, kv.Value / references.Count))
            .OrderByDescending(e => e.Score)
            .ToList();
    }

    /// <summary>In how many of the references this attribute carries real weight (≥30% of its tier).</summary>
    public static int AgreementCount(
        IReadOnlyList<IReadOnlyList<ReferenceEmphasis>> references,
        string attribute, IReadOnlyCollection<string> coreStats)
    {
        var coreSet = new HashSet<string>(coreStats, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var reference in references)
        {
            double tierTop = reference
                .Where(e => coreSet.Contains(e.Attribute) == coreSet.Contains(attribute))
                .Select(e => e.Score).DefaultIfEmpty(0).Max();
            double score = reference.FirstOrDefault(e =>
                string.Equals(e.Attribute, attribute, StringComparison.OrdinalIgnoreCase))?.Score ?? 0;
            if (tierTop > 0 && score >= tierTop * 0.30)
                count++;
        }
        return count;
    }

    public static IReadOnlyList<ReferenceEmphasis> EmphasisOf(
        ComposedGraph graph, IReadOnlyCollection<CellRef> allocated)
    {
        var mean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value)
                    continue;
                var (sum, count) = mean.GetValueOrDefault(a.Attribute);
                mean[a.Attribute] = (sum + Math.Abs(value), count + 1);
            }
        }

        var allocatedSet = allocated as ISet<CellRef> ?? allocated.ToHashSet();
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            if (!allocatedSet.Contains(vertex.Cell))
                continue;
            double kindWeight = KindWeight(vertex.Node.Kind);
            if (kindWeight <= 0)
                continue;
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value)
                    continue;
                var (sum, count) = mean[a.Attribute];
                scores[a.Attribute] = scores.GetValueOrDefault(a.Attribute) + value / (sum / count) * kindWeight;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ReferenceEmphasis(kv.Key, kv.Value))
            .ToList();
    }
}
