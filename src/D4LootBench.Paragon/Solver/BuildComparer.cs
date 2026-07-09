using System.Globalization;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>A complete paragon build: layout, every allocated cell, and glyph assignments.</summary>
public sealed record BuildSnapshot(
    string Name,
    ParagonLayout Layout,
    IReadOnlyList<CellRef> AllocatedCells,
    IReadOnlyList<MaxrollGlyphAssignment> Glyphs);

/// <summary>
/// Side-by-side comparison of two builds: points, node-kind mix, glyphs, per-attribute stat
/// totals, and a metric-by-metric verdict. Stats keep their own units (flat vs percent), so the
/// verdict counts per-metric wins instead of pretending they share a scale.
/// </summary>
public static class BuildComparer
{
    public static string Compare(BuildSnapshot a, BuildSnapshot b, ParagonData data, double nonParagonStat = 0)
        => Compare(a, b, data, NonParagonStats.Uniform(nonParagonStat));

    public static string Compare(BuildSnapshot a, BuildSnapshot b, ParagonData data, NonParagonStats nonParagonStats)
    {
        var summaryA = Summarize(a, data, nonParagonStats);
        var summaryB = Summarize(b, data, nonParagonStats);

        var lines = new List<string>
        {
            $"{a.Name}: {Describe(summaryA)}",
            $"{b.Name}: {Describe(summaryB)}",
        };

        var boardsA = a.Layout.Boards.Select(p => p.Board.Name ?? p.Board.InternalName).ToHashSet();
        var boardsB = b.Layout.Boards.Select(p => p.Board.Name ?? p.Board.InternalName).ToHashSet();
        var onlyA = boardsA.Except(boardsB).ToList();
        var onlyB = boardsB.Except(boardsA).ToList();
        if (onlyA.Count > 0 || onlyB.Count > 0)
        {
            lines.Add($"Boards only in {a.Name}: {(onlyA.Count > 0 ? string.Join(", ", onlyA) : "none")}; " +
                      $"only in {b.Name}: {(onlyB.Count > 0 ? string.Join(", ", onlyB) : "none")}.");
        }

        // Per-attribute totals, biggest absolute difference first.
        var statKeys = summaryA.Stats.Keys.Union(summaryB.Stats.Keys).ToList();
        int leadA = 0, leadB = 0;
        var diffs = new List<(double Magnitude, string Line)>();
        foreach (var key in statKeys)
        {
            double va = summaryA.Stats.GetValueOrDefault(key);
            double vb = summaryB.Stats.GetValueOrDefault(key);
            if (Math.Abs(va - vb) < 1e-9)
                continue;
            if (va > vb) leadA++; else leadB++;
            string leader = va > vb ? a.Name : b.Name;
            diffs.Add((Math.Abs(va - vb),
                $"  {key}: {FormatValue(va)} vs {FormatValue(vb)} (+{FormatValue(Math.Abs(va - vb))} {leader})"));
        }
        if (diffs.Count > 0)
        {
            lines.Add($"Stat totals ({a.Name} vs {b.Name}), biggest differences first:");
            lines.AddRange(diffs.OrderByDescending(d => d.Magnitude).Take(10).Select(d => d.Line));
            if (diffs.Count > 10)
                lines.Add($"  … and {diffs.Count - 10} more differing stat(s).");
        }

        // Metric-by-metric verdict.
        var verdicts = new List<string>();
        int winsA = 0, winsB = 0;
        void Judge(string metric, double va, double vb, bool lowerIsBetter = false)
        {
            if (Math.Abs(va - vb) < 1e-9)
                return;
            bool aWins = lowerIsBetter ? va < vb : va > vb;
            if (aWins) winsA++; else winsB++;
            verdicts.Add($"{(aWins ? a.Name : b.Name)} {metric}");
        }
        Judge($"spends fewer points ({Math.Min(summaryA.Points, summaryB.Points)} vs {Math.Max(summaryA.Points, summaryB.Points)})",
            summaryA.Points, summaryB.Points, lowerIsBetter: true);
        Judge($"takes more rare nodes ({Math.Max(summaryA.Rares, summaryB.Rares)} vs {Math.Min(summaryA.Rares, summaryB.Rares)})",
            summaryA.Rares, summaryB.Rares);
        Judge($"takes more legendary nodes ({Math.Max(summaryA.Legendaries, summaryB.Legendaries)} vs {Math.Min(summaryA.Legendaries, summaryB.Legendaries)})",
            summaryA.Legendaries, summaryB.Legendaries);
        Judge($"sockets more glyphs ({Math.Max(summaryA.GlyphCount, summaryB.GlyphCount)} vs {Math.Min(summaryA.GlyphCount, summaryB.GlyphCount)})",
            summaryA.GlyphCount, summaryB.GlyphCount);
        Judge($"activates more threshold bonuses ({Math.Max(summaryA.ThresholdsMet, summaryB.ThresholdsMet)} vs {Math.Min(summaryA.ThresholdsMet, summaryB.ThresholdsMet)})",
            summaryA.ThresholdsMet, summaryB.ThresholdsMet);
        Judge($"has a higher glyph delivery score ({Math.Max(summaryA.GlyphDelivery, summaryB.GlyphDelivery):0.#} vs {Math.Min(summaryA.GlyphDelivery, summaryB.GlyphDelivery):0.#})",
            summaryA.GlyphDelivery, summaryB.GlyphDelivery);
        Judge($"leads in more stats ({Math.Max(leadA, leadB)} vs {Math.Min(leadA, leadB)})", leadA, leadB);

        if (verdicts.Count == 0)
            lines.Add("Verdict: the builds are equivalent on every compared metric.");
        else
        {
            lines.Add("Verdict: " + string.Join("; ", verdicts) + ".");
            lines.Add(winsA == winsB
                ? "Overall: a trade-off — neither build wins more metrics than the other."
                : $"Overall: {(winsA > winsB ? a.Name : b.Name)} looks better " +
                  $"({Math.Max(winsA, winsB)} of {winsA + winsB} decided metrics).");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record BuildSummary(
        int Points, int Rares, int Legendaries, int GlyphCount, string GlyphList,
        int ThresholdsMet, int ThresholdCount, double GlyphDelivery,
        IReadOnlyDictionary<string, double> Stats);

    private static BuildSummary Summarize(BuildSnapshot build, ParagonData data, NonParagonStats nonParagonStats)
    {
        var graph = ComposedGraph.Build(
            build.Layout, data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase));

        // Threshold requirements scale with each board's attachment slot; met bonuses count.
        var report = BuildStats.Compute(graph, build.AllocatedCells, data,
            nonParagonStats, build.Layout.Boards[0].Board.ClassName);
        var metCells = report.Thresholds.Where(t => t.Met).Select(t => t.Cell).ToHashSet();

        int points = 0, rares = 0, legendaries = 0;
        var stats = new Dictionary<string, double>();
        foreach (var cell in build.AllocatedCells.Distinct())
        {
            if (!graph.TryGetVertex(cell, out int vertex))
                continue;
            var node = graph.Vertices[vertex].Node;
            if (node.Kind == ParagonNodeKind.Start)
                continue;
            points++;
            if (node.Kind == ParagonNodeKind.Rare) rares++;
            if (node.Kind == ParagonNodeKind.Legendary) legendaries++;
            foreach (var attribute in node.Attributes)
            {
                if (attribute.Value is not double value
                    || (attribute.IsThresholdBonus && !metCells.Contains(cell)))
                    continue;
                string key = ParagonDisplay.FormatAttributeName(attribute.Attribute)
                             + (attribute.Param is int param ? $" [{param}]" : "");
                stats[key] = stats.GetValueOrDefault(key) + value;
            }
        }

        // Delivery: scalar(level) × source stat purchased in radius, summed across glyphs.
        double delivery = 0;
        var sockets = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);
        var allocatedSet = build.AllocatedCells.ToHashSet();
        foreach (var assignment in build.Glyphs)
        {
            var glyph = data.Glyphs.FirstOrDefault(d =>
                string.Equals(d.InternalName, assignment.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
            if (glyph is null || GlyphInfo.PrimarySourceAttribute(glyph) is not string source
                || !sockets.TryGetValue(assignment.BoardSlot, out var socket))
                continue;
            int level = assignment.Level ?? 100;
            var totals = GlyphRadius.AttributeTotalsInRange(
                graph, socket, allocatedSet, GlyphRadius.RadiusForLevel(level), GlyphRadius.GameMetric);
            delivery += GlyphInfo.DeliveredBonus(glyph, level, totals.GetValueOrDefault(source)) ?? 0;
        }

        var glyphNames = build.Glyphs
            .Select(g => data.Glyphs.FirstOrDefault(d =>
                string.Equals(d.InternalName, g.GlyphInternalName, StringComparison.OrdinalIgnoreCase))?.Name
                ?? g.GlyphInternalName)
            .ToList();
        return new BuildSummary(points, rares, legendaries, glyphNames.Count,
            glyphNames.Count > 0 ? string.Join(", ", glyphNames) : "none",
            report.ThresholdsMet, report.Thresholds.Count, delivery, stats);
    }

    private static string Describe(BuildSummary s) =>
        $"{s.Points} points — {s.Rares} rare, {s.Legendaries} legendary, " +
        $"{s.ThresholdsMet}/{s.ThresholdCount} threshold bonus(es) active, glyphs: {s.GlyphList}.";

    /// <summary>Fractional values are percentages (matching <see cref="ParagonDisplay.FormatAttribute"/>).</summary>
    private static string FormatValue(double value) =>
        Math.Abs(value) < 1 && value != 0
            ? (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
