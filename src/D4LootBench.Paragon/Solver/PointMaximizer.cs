using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// What leftover points should chase: <see cref="Attributes"/> holds raw attribute names
/// (e.g. "Intelligence_Core", "Life_Percent"); empty means the four core stats. When
/// <see cref="PreferRare"/> is set, reachable rare (yellow) nodes are bought first, then the
/// remaining budget goes to the focused stats.
/// </summary>
public sealed record MaximizeFocus(IReadOnlyCollection<string> Attributes, bool PreferRare)
{
    public static readonly IReadOnlyList<string> CoreStats =
        ["Strength_Core", "Intelligence_Core", "Willpower_Core", "Dexterity_Core"];
}

public sealed record MaximizeOutcome(
    IReadOnlyList<CellRef> AddedCells,
    int RaresAdded,
    IReadOnlyDictionary<string, double> Gains);

/// <summary>
/// Spends a point budget extending an already-solved tree. Greedy frontier growth: each round a
/// multi-source Dijkstra finds the reachable candidate with the best value-per-point path and
/// absorbs it, until the budget is gone or nothing valuable is reachable. Values are normalized
/// per attribute (a node's contribution is measured against that attribute's average per-node
/// magnitude) so flat and percent stats compete fairly. Node rules apply: excluded cells are
/// never entered, avoided cells are routed around, and a Limit group stops being bought once it
/// hits its cap.
/// </summary>
public static class PointMaximizer
{
    private const int Infinity = int.MaxValue / 4;

    public static MaximizeOutcome Extend(
        ComposedGraph graph, ISet<CellRef> purchased, int budget, MaximizeFocus focus, PlanRequest request)
    {
        int n = graph.Vertices.Count;
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        var (weightByCell, blockedCells, limitRules) = PlanSolver.BuildSteering(request, cellsByGroup);

        var weights = new int[n];
        Array.Fill(weights, 1);
        foreach (var (cell, weight) in weightByCell)
        {
            if (graph.TryGetVertex(cell, out int v))
                weights[v] = Math.Max(1, weight);
        }
        var blocked = new bool[n];
        foreach (var cell in blockedCells)
        {
            if (graph.TryGetVertex(cell, out int v))
                blocked[v] = true;
        }
        weights[graph.StartVertex] = 0;
        blocked[graph.StartVertex] = false;

        var tree = new HashSet<int> { graph.StartVertex };
        foreach (var cell in purchased)
        {
            if (graph.TryGetVertex(cell, out int v))
                tree.Add(v);
        }

        // Limit groups: count what the tree already holds; a full group becomes off-limits.
        var groupOf = new Dictionary<int, NodeRule>();
        var groupCounts = new Dictionary<string, int>();
        foreach (var rule in limitRules)
        {
            groupCounts[rule.GroupKey] = 0;
            foreach (var cell in cellsByGroup[rule.GroupKey])
            {
                if (!graph.TryGetVertex(cell, out int v))
                    continue;
                groupOf[v] = rule;
                if (tree.Contains(v))
                    groupCounts[rule.GroupKey]++;
            }
        }
        void ApplyLimitBlocks()
        {
            foreach (var (v, rule) in groupOf)
            {
                if (groupCounts[rule.GroupKey] >= rule.Limit && !tree.Contains(v))
                    blocked[v] = true;
            }
        }
        ApplyLimitBlocks();

        var attributes = focus.Attributes.Count > 0 ? focus.Attributes : MaximizeFocus.CoreStats;
        var attributeSet = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        // Gains are also reported for glyph-source stats, even when they aren't focused.
        var gainSet = new HashSet<string>(attributeSet, StringComparer.OrdinalIgnoreCase);
        foreach (var goal in request.GlyphGoals)
            gainSet.Add(goal.SourceAttribute);

        // Per-attribute average per-node magnitude across the layout, for unit-fair scoring.
        var attributeMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !gainSet.Contains(a.Attribute))
                    continue;
                var (sum, count) = attributeMean.GetValueOrDefault(a.Attribute);
                attributeMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
            }
        }

        // Glyph delivery: a point of source stat inside an active glyph's radius is worth its raw
        // value AND what the glyph converts it into — count it again for each covering glyph.
        var glyphBonus = new double[n];
        foreach (var goal in request.GlyphGoals)
        {
            if (!attributeMean.TryGetValue(goal.SourceAttribute, out var mean))
                continue;
            for (int v = 0; v < n; v++)
            {
                var cell = graph.Vertices[v].Cell;
                if (cell.BoardSlot != goal.Socket.BoardSlot
                    || Math.Abs(cell.X - goal.Socket.X) + Math.Abs(cell.Y - goal.Socket.Y) > goal.Radius)
                    continue;
                foreach (var a in graph.Vertices[v].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is double value
                        && string.Equals(a.Attribute, goal.SourceAttribute, StringComparison.OrdinalIgnoreCase))
                        glyphBonus[v] += value / (mean.Sum / mean.Count);
                }
            }
        }

        double NormValue(int v)
        {
            double total = glyphBonus[v];
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !attributeSet.Contains(a.Attribute))
                    continue;
                var (sum, count) = attributeMean[a.Attribute];
                total += value / (sum / count);
            }
            return total;
        }

        var dist = new int[n];
        var from = new int[n];
        var added = new List<CellRef>();
        int raresAdded = 0;
        int remaining = budget;
        var gains = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        int PathCost(int vertex)
        {
            int cost = 0;
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
                cost++;
            return cost;
        }

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                tree.Add(v);
                var cell = graph.Vertices[v].Cell;
                purchased.Add(cell);
                added.Add(cell);
                remaining--;
                var node = graph.Vertices[v].Node;
                if (node.Kind == ParagonNodeKind.Rare)
                    raresAdded++;
                foreach (var a in node.Attributes)
                {
                    if (a.IsThresholdBonus || a.Value is not double value || !gainSet.Contains(a.Attribute))
                        continue;
                    gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) + value;
                }
                if (groupOf.TryGetValue(v, out var rule))
                    groupCounts[rule.GroupKey]++;
            }
            ApplyLimitBlocks();
        }

        // Phase 1: rare nodes, cheapest first (ties: more focused stat value).
        if (focus.PreferRare)
        {
            while (remaining > 0)
            {
                RunDijkstra(graph, tree, weights, blocked, dist, from);
                int best = -1;
                int bestCost = 0;
                double bestValue = 0;
                for (int v = 0; v < n; v++)
                {
                    if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity
                        || graph.Vertices[v].Node.Kind != ParagonNodeKind.Rare)
                        continue;
                    int cost = PathCost(v);
                    if (cost > remaining)
                        continue;
                    double value = NormValue(v);
                    if (best < 0 || cost < bestCost || (cost == bestCost && value > bestValue))
                    {
                        best = v;
                        bestCost = cost;
                        bestValue = value;
                    }
                }
                if (best < 0)
                    break;
                Absorb(best);
            }
        }

        // Phase 2: focused stats by value-per-point.
        while (remaining > 0)
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from);
            int best = -1;
            double bestRatio = 0;
            for (int v = 0; v < n; v++)
            {
                if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity)
                    continue;
                double value = NormValue(v);
                if (value <= 0)
                    continue;
                int cost = PathCost(v);
                if (cost > remaining)
                    continue;
                double ratio = value / cost;
                if (best < 0 || ratio > bestRatio)
                {
                    best = v;
                    bestRatio = ratio;
                }
            }
            if (best < 0)
                break;
            Absorb(best);
        }

        return new MaximizeOutcome(added, raresAdded, gains);
    }

    private static void RunDijkstra(
        ComposedGraph graph, HashSet<int> tree, int[] weights, bool[] blocked, int[] dist, int[] from)
    {
        Array.Fill(dist, Infinity);
        var queue = new PriorityQueue<int, int>();
        foreach (int v in tree)
        {
            dist[v] = 0;
            from[v] = -1;
            queue.Enqueue(v, 0);
        }
        while (queue.TryDequeue(out int v, out int cost))
        {
            if (cost > dist[v])
                continue;
            foreach (int u in graph.Adjacency[v])
            {
                if (blocked[u])
                    continue;
                int next = cost + weights[u];
                if (next < dist[u])
                {
                    dist[u] = next;
                    from[u] = v;
                    queue.Enqueue(u, next);
                }
            }
        }
    }
}
