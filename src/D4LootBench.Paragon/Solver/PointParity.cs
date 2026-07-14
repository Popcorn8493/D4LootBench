using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>The point-matched snapshots plus notes describing what was adjusted (empty = equal already).</summary>
public sealed record ParityResult(BuildSnapshot A, BuildSnapshot B, IReadOnlyList<string> Notes);

/// <summary>
/// Equalizes two builds' point spend so a comparison measures build QUALITY, not budget. The
/// smaller build is grown first — its own emphasis (what its allocation demonstrably stacks,
/// via <see cref="BuildReference.EmphasisOf"/>) drives a maximizer spend of the missing
/// points, which adds to the real allocation without touching it — and only when its boards
/// can't absorb the whole deficit is the larger build trimmed by its least-valued expendable
/// leaves (never rares/legendaries/sockets, never one half of a purchased gate pair, always
/// connectivity-safe). Whatever difference neither side can move is reported, not hidden.
/// </summary>
public static class PointParity
{
    public static ParityResult MatchPoints(BuildSnapshot a, BuildSnapshot b, ParagonData data)
    {
        var nodes = data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase);
        var graphA = ComposedGraph.Build(a.Layout, nodes);
        var graphB = ComposedGraph.Build(b.Layout, nodes);
        int pointsA = CountPoints(graphA, a.AllocatedCells);
        int pointsB = CountPoints(graphB, b.AllocatedCells);
        if (pointsA == pointsB)
            return new ParityResult(a, b, []);

        bool aIsSmall = pointsA < pointsB;
        var (small, smallGraph) = aIsSmall ? (a, graphA) : (b, graphB);
        var (large, largeGraph) = aIsSmall ? (b, graphB) : (a, graphA);
        int deficit = Math.Abs(pointsA - pointsB);

        var grownSmall = Grow(smallGraph, small, deficit, out int granted);
        int removed = 0;
        var droppedNames = new List<string>();
        var trimmedLarge = granted < deficit
            ? Trim(largeGraph, large, deficit - granted, out removed, droppedNames)
            : large;

        var parts = new List<string>();
        if (granted > 0)
            parts.Add($"{small.Name} was granted {granted} point(s), spent by its own stat priorities");
        if (removed > 0)
        {
            parts.Add($"{large.Name} lost its {removed} least-valued node(s) " +
                      $"({string.Join(", ", droppedNames.Distinct().Take(3))}" +
                      $"{(droppedNames.Distinct().Count() > 3 ? ", …" : "")})");
        }
        var notes = new List<string>();
        if (parts.Count > 0)
            notes.Add($"Point parity: {string.Join("; ", parts)}.");
        int residual = deficit - granted - removed;
        if (residual > 0)
        {
            notes.Add($"Point parity: {residual} point(s) of difference remain — {small.Name}'s boards " +
                      $"are exhausted and {large.Name} has no more safely removable nodes.");
        }
        return aIsSmall
            ? new ParityResult(grownSmall, trimmedLarge, notes)
            : new ParityResult(trimmedLarge, grownSmall, notes);
    }

    /// <summary>In-game spend of an allocation: cells minus the start node minus gate-pair credits.</summary>
    public static int CountPoints(ComposedGraph graph, IReadOnlyCollection<CellRef> allocated)
    {
        var set = allocated as HashSet<CellRef> ?? allocated.ToHashSet();
        int cells = set.Count(c =>
            graph.TryGetVertex(c, out int v) && graph.Vertices[v].Node.Kind != ParagonNodeKind.Start);
        return cells - GateCrossings.FreeCredits(graph, set);
    }

    private static BuildSnapshot Grow(ComposedGraph graph, BuildSnapshot build, int deficit, out int granted)
    {
        var purchased = build.AllocatedCells.ToHashSet();
        int before = CountPoints(graph, purchased);
        var outcome = PointMaximizer.Extend(graph, purchased, deficit,
            FocusFrom(BuildReference.EmphasisOf(graph, purchased)), new PlanRequest { Targets = [] });
        granted = CountPoints(graph, purchased) - before;
        return granted == 0
            ? build
            : build with { AllocatedCells = build.AllocatedCells.Concat(outcome.AddedCells).Distinct().ToList() };
    }

    /// <summary>The build's own stacked stats as a maximizer focus, weighted by emphasis.</summary>
    private static MaximizeFocus FocusFrom(IReadOnlyList<ReferenceEmphasis> emphasis)
    {
        if (emphasis.Count == 0)
            return new MaximizeFocus([], PreferRare: true);
        double top = emphasis[0].Score;
        var kept = emphasis.Where(e => e.Score >= top * 0.2).Take(12).ToList();
        return new MaximizeFocus(kept.Select(e => e.Attribute).ToList(), PreferRare: true)
        {
            Weights = kept.ToDictionary(e => e.Attribute, e => e.Score / top, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static BuildSnapshot Trim(
        ComposedGraph graph, BuildSnapshot build, int count, out int removed, List<string> droppedNames)
    {
        var set = build.AllocatedCells.ToHashSet();
        var gatePair = GateCrossings.PairMap(graph);

        // Value by the build's OWN priorities: unit-normalized grant × emphasis weight, so the
        // first nodes to go are the ones granting stats this build doesn't stack.
        var emphasis = BuildReference.EmphasisOf(graph, set);
        double top = emphasis.Count > 0 ? emphasis[0].Score : 1;
        var weight = emphasis.ToDictionary(e => e.Attribute, e => e.Score / top, StringComparer.OrdinalIgnoreCase);
        var mean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value)
                    continue;
                var (sum, n) = mean.GetValueOrDefault(a.Attribute);
                mean[a.Attribute] = (sum + Math.Abs(value), n + 1);
            }
        }
        double ValueOf(int v)
        {
            double total = 0;
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value
                    || !mean.TryGetValue(a.Attribute, out var m))
                    continue;
                total += value / (m.Sum / m.Count) * weight.GetValueOrDefault(a.Attribute, 0);
            }
            return total;
        }

        var tree = new HashSet<int> { graph.StartVertex };
        foreach (var cell in set)
        {
            if (graph.TryGetVertex(cell, out int v))
                tree.Add(v);
        }

        bool StaysConnectedWithout(int candidate)
        {
            var seen = new HashSet<int> { graph.StartVertex };
            var queue = new Queue<int>();
            queue.Enqueue(graph.StartVertex);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int u in graph.Adjacency[v])
                {
                    if (u != candidate && tree.Contains(u) && seen.Add(u))
                        queue.Enqueue(u);
                }
            }
            return seen.Count == tree.Count - 1;
        }

        removed = 0;
        while (removed < count)
        {
            int worst = -1;
            double worstValue = double.MaxValue;
            foreach (int v in tree)
            {
                var node = graph.Vertices[v].Node;
                if (v == graph.StartVertex
                    || node.Kind is not (ParagonNodeKind.Normal or ParagonNodeKind.Magic or ParagonNodeKind.Gate))
                    continue;
                // Removing one half of a purchased gate pair refunds nothing in-game (the pair
                // costs one point and the survivor still costs it) — never trim it.
                int pair = gatePair[v];
                if (pair >= 0 && tree.Contains(pair))
                    continue;
                if (!StaysConnectedWithout(v))
                    continue;
                double value = ValueOf(v);
                if (value < worstValue)
                {
                    worst = v;
                    worstValue = value;
                }
            }
            if (worst < 0)
                break;
            tree.Remove(worst);
            set.Remove(graph.Vertices[worst].Cell);
            droppedNames.Add(graph.Vertices[worst].Node.Name ?? graph.Vertices[worst].Node.InternalName);
            removed++;
        }
        return removed == 0
            ? build
            : build with { AllocatedCells = build.AllocatedCells.Where(set.Contains).Distinct().ToList() };
    }
}
