using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>The point-matched snapshots plus notes describing what was adjusted (empty = equal already).</summary>
public sealed record ParityResult(BuildSnapshot A, BuildSnapshot B, IReadOnlyList<string> Notes);

/// <summary>
/// A build fitted to a point budget: <see cref="RemovedCells"/> holds the trimmed nodes in
/// removal order (least valuable first) — reversed, it is the buy-back order while leveling.
/// </summary>
public sealed record TrimToBudgetResult(
    BuildSnapshot Build,
    int PointsBefore,
    int PointsAfter,
    IReadOnlyList<CellRef> RemovedCells,
    IReadOnlyList<string> Notes);

/// <summary>
/// Equalizes two builds' point spend so a comparison measures build QUALITY, not budget. The
/// smaller build is grown first — its own emphasis (what its allocation demonstrably stacks,
/// via <see cref="BuildReference.EmphasisOf"/>) drives a maximizer spend of the missing
/// points, which adds to the real allocation without touching it — and only when its boards
/// can't absorb the whole deficit is the larger build trimmed by its least-valued expendable
/// leaves (never rares/legendaries/sockets, never one half of a purchased gate pair, always
/// connectivity-safe). Whatever difference neither side can move is reported, not hidden.
/// The same trim engine powers <see cref="TrimToBudget"/> — fitting an imported build to the
/// player's current point pool.
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
        var trimmedLarge = large;
        if (granted < deficit)
        {
            var state = TrimState.For(largeGraph, large);
            removed = RemoveLeastValued(state, deficit - granted, forbidden: null, [], droppedNames);
            trimmedLarge = state.Apply(large, removed);
        }

        var parts = new List<string>();
        if (granted > 0)
            parts.Add($"{small.Name} was granted {granted} point(s), spent by its own stat priorities");
        if (removed > 0)
        {
            parts.Add($"{large.Name} lost its {removed} least-valued node(s) " +
                      $"({string.Join(", ", droppedNames.Distinct().Take(3))}" +
                      $"{(droppedNames.Distinct().Count() > 3 ? ", …" : "")})");
        }

        // Lead with the ORIGINAL spends — the describe lines below show the matched numbers,
        // and without the before/after the adjustment reads as a counting error.
        string header = $"Point parity: {a.Name} spends {pointsA} point(s), {b.Name} {pointsB}";
        var notes = new List<string>();
        if (parts.Count > 0)
            notes.Add($"{header} — {string.Join("; ", parts)}.");
        int residual = deficit - granted - removed;
        if (residual > 0)
        {
            notes.Add((parts.Count > 0 ? "Point parity: " : header + " — ") +
                      $"{residual} point(s) of difference remain — {small.Name}'s boards " +
                      $"are exhausted and {large.Name} has no more safely removable nodes.");
        }
        return aIsSmall
            ? new ParityResult(grownSmall, trimmedLarge, notes)
            : new ParityResult(trimmedLarge, grownSmall, notes);
    }

    /// <summary>
    /// Fits a build to <paramref name="budget"/> points by trimming its least-valued expendable
    /// nodes — the leveling view of a bigger build. Cells feeding a still-active glyph goal in
    /// <paramref name="protectGlyphs"/> are spared as long as the budget allows; only when the
    /// guarded trim can't reach the budget does a second pass cut into glyph radii (noted, since
    /// glyphs may deactivate). Rares, legendaries and sockets are never removed, so what remains
    /// is the build's core plus a reported buy-back list for the way up.
    /// </summary>
    public static TrimToBudgetResult TrimToBudget(
        BuildSnapshot build, int budget, ParagonData data, IReadOnlyList<GlyphGoal>? protectGlyphs = null)
    {
        var nodes = data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase);
        var graph = ComposedGraph.Build(build.Layout, nodes);
        int before = CountPoints(graph, build.AllocatedCells);
        if (before <= budget)
            return new TrimToBudgetResult(build, before, before, [], []);

        int excess = before - budget;
        var state = TrimState.For(graph, build);
        var removedCells = new List<CellRef>();
        var droppedNames = new List<string>();

        // Pass 1: keep every still-active glyph goal active.
        bool GoalForbids(int v)
        {
            var cell = graph.Vertices[v].Cell;
            foreach (var goal in protectGlyphs ?? [])
            {
                if (cell.BoardSlot != goal.Socket.BoardSlot
                    || Math.Abs(cell.X - goal.Socket.X) + Math.Abs(cell.Y - goal.Socket.Y) > goal.Radius)
                    continue;
                double have = GlyphRadius.AttributeTotalsInRange(
                        graph, goal.Socket, state.Set.ToList(), goal.Radius, GlyphRadius.GameMetric)
                    .GetValueOrDefault(goal.SourceAttribute);
                if (have < goal.RequiredTotal)
                    continue; // already inactive — nothing left to protect
                double grant = graph.Vertices[v].Node.Attributes
                    .Where(a => !a.IsThresholdBonus && a.Value is not null
                        && string.Equals(a.Attribute, goal.SourceAttribute, StringComparison.OrdinalIgnoreCase))
                    .Sum(a => a.Value!.Value);
                if (have - grant < goal.RequiredTotal)
                    return true;
            }
            return false;
        }
        int removed = RemoveLeastValued(state, excess,
            protectGlyphs is { Count: > 0 } ? GoalForbids : null, removedCells, droppedNames);

        // Pass 2: the guarded trim couldn't reach the budget — glyph activation stops being
        // sacred, and the user is told which sacrifice the budget forced.
        var notes = new List<string>();
        if (removed < excess && protectGlyphs is { Count: > 0 })
        {
            int deeper = RemoveLeastValued(state, excess - removed, forbidden: null, removedCells, droppedNames);
            if (deeper > 0)
            {
                notes.Add($"The budget forced {deeper} node(s) out of glyph radii — " +
                          "one or more glyphs may drop below their activation requirement.");
            }
            removed += deeper;
        }
        if (removed < excess)
        {
            notes.Add($"Still {excess - removed} point(s) over budget — the rest of the build is " +
                      "rares, legendaries, sockets and connections that cannot be removed safely.");
        }

        return new TrimToBudgetResult(state.Apply(build, removed), before, before - removed, removedCells, notes);
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

    /// <summary>Mutable trim workspace: the surviving cells, their vertex set, and the value function.</summary>
    private sealed record TrimState(
        ComposedGraph Graph, HashSet<CellRef> Set, HashSet<int> Tree, Func<int, double> ValueOf)
    {
        public static TrimState For(ComposedGraph graph, BuildSnapshot build)
        {
            var set = build.AllocatedCells.ToHashSet();
            var tree = new HashSet<int> { graph.StartVertex };
            foreach (var cell in set)
            {
                if (graph.TryGetVertex(cell, out int v))
                    tree.Add(v);
            }
            return new TrimState(graph, set, tree, EmphasisValue(graph, set));
        }

        public BuildSnapshot Apply(BuildSnapshot build, int removed) => removed == 0
            ? build
            : build with { AllocatedCells = build.AllocatedCells.Where(Set.Contains).Distinct().ToList() };
    }

    /// <summary>
    /// Value by the build's OWN priorities: unit-normalized grant × emphasis weight, so the
    /// first nodes to go are the ones granting stats this build doesn't stack.
    /// </summary>
    private static Func<int, double> EmphasisValue(ComposedGraph graph, IReadOnlyCollection<CellRef> allocated)
    {
        var emphasis = BuildReference.EmphasisOf(graph, allocated);
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
        return v =>
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
        };
    }

    /// <summary>
    /// Removes up to <paramref name="count"/> of the least-valued expendable cells: never the
    /// start, rares, legendaries or sockets, never one half of a purchased gate pair (that
    /// refunds nothing in-game), never a cell whose removal disconnects the rest or that
    /// <paramref name="forbidden"/> vetoes. Returns how many came out.
    /// </summary>
    private static int RemoveLeastValued(
        TrimState state, int count, Func<int, bool>? forbidden,
        List<CellRef> removedCells, List<string> droppedNames)
    {
        var (graph, set, tree, valueOf) = state;
        var gatePair = GateCrossings.PairMap(graph);

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

        int removed = 0;
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
                int pair = gatePair[v];
                if (pair >= 0 && tree.Contains(pair))
                    continue;
                if (forbidden?.Invoke(v) == true)
                    continue;
                if (!StaysConnectedWithout(v))
                    continue;
                double value = valueOf(v);
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
            removedCells.Add(graph.Vertices[worst].Cell);
            droppedNames.Add(graph.Vertices[worst].Node.Name ?? graph.Vertices[worst].Node.InternalName);
            removed++;
        }
        return removed;
    }
}
