namespace D4LootBench.Paragon.Solver;

/// <summary>
/// A glyph activation requirement: at least <see cref="RequiredTotal"/> of
/// <see cref="SourceAttribute"/> purchased within <see cref="Radius"/> (Manhattan) of the socket.
/// </summary>
public sealed record GlyphGoal(CellRef Socket, string SourceAttribute, double RequiredTotal, int Radius, string? GlyphName = null);

/// <summary><paramref name="LimitConstrained"/>: a node Limit rule kept nodes off the path.</summary>
public sealed record GlyphGoalOutcome(
    GlyphGoal Goal, double AchievedTotal, bool Met, IReadOnlyList<CellRef> AddedCells, bool LimitConstrained = false);

/// <summary>
/// Extends a solved purchase set with the cheapest additional nodes needed to satisfy glyph
/// activation goals. Greedy: each round runs a multi-source Dijkstra from the current tree and
/// connects the in-radius stat node with the best stat-gained-per-point ratio — counting the
/// source stat of every in-radius node the path absorbs on the way, with equally cheap routes
/// preferring the stat-bearing one — until the requirement is met or no reachable stat node
/// remains. Limit rules are hard: a candidate whose path would push a group past its cap is
/// skipped, even if that leaves the goal unmet.
/// </summary>
public static class GlyphOptimizer
{
    private const int Infinity = int.MaxValue / 4;

    /// <summary>
    /// Mutates <paramref name="purchased"/> (cells only — the free start node is never added).
    /// A goal whose socket is not yet purchased gets the socket connected first.
    /// </summary>
    public static IReadOnlyList<GlyphGoalOutcome> Extend(
        ComposedGraph graph,
        ISet<CellRef> purchased,
        IReadOnlyList<GlyphGoal> goals,
        SolverConstraints constraints,
        IReadOnlyList<NodeRule>? limitRules = null,
        IReadOnlyDictionary<string, IReadOnlyList<CellRef>>? cellsByGroup = null)
    {
        int n = graph.Vertices.Count;
        var weights = new int[n];
        Array.Fill(weights, 1);
        foreach (var (cell, weight) in constraints.CellWeights)
        {
            if (graph.TryGetVertex(cell, out int v))
                weights[v] = Math.Max(1, weight);
        }
        var blocked = new bool[n];
        foreach (var cell in constraints.BlockedCells)
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

        var limits = new LimitTracker(graph, limitRules ?? [], cellsByGroup ?? new Dictionary<string, IReadOnlyList<CellRef>>(), tree);
        limits.PenalizeGroups(weights, tree);
        limits.BlockFullGroups(blocked, tree);

        var outcomes = new List<GlyphGoalOutcome>();
        foreach (var goal in goals)
        {
            outcomes.Add(ExtendForGoal(graph, goal, tree, purchased, weights, blocked, limits));
        }
        return outcomes;
    }

    private static GlyphGoalOutcome ExtendForGoal(
        ComposedGraph graph, GlyphGoal goal, HashSet<int> tree, ISet<CellRef> purchased,
        int[] weights, bool[] blocked, LimitTracker limits)
    {
        var added = new List<CellRef>();
        bool limitConstrained = false;
        if (!graph.TryGetVertex(goal.Socket, out int socketVertex))
            return new GlyphGoalOutcome(goal, 0, Met: false, added);
        if (blocked[socketVertex])
            return new GlyphGoalOutcome(goal, 0, Met: false, added, limits.IsLimitBlocked(socketVertex));

        // Stat-bearing cells the glyph can see: same board, Manhattan distance ≤ radius.
        // Blocked cells stay in the pool — they're re-checked (and attributed) when picking.
        var candidateValue = new Dictionary<int, double>();
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            var (cell, node) = (graph.Vertices[v].Cell, graph.Vertices[v].Node);
            if (cell.BoardSlot != goal.Socket.BoardSlot || v == socketVertex)
                continue;
            if (Math.Abs(cell.X - goal.Socket.X) + Math.Abs(cell.Y - goal.Socket.Y) > goal.Radius)
                continue;
            double value = node.Attributes
                .Where(a => !a.IsThresholdBonus && a.Attribute == goal.SourceAttribute && a.Value is double)
                .Sum(a => a.Value!.Value);
            if (value > 0)
                candidateValue[v] = value;
        }

        var dist = new int[graph.Vertices.Count];
        var from = new int[graph.Vertices.Count];
        var valueByVertex = new double[graph.Vertices.Count];
        foreach (var (v, value) in candidateValue)
            valueByVertex[v] = value;

        // Absorbing a candidate buys its whole path, and intermediates inside the radius count
        // toward the total too — credit them, so a route through source-stat nodes wins.
        double PathValue(int vertex)
        {
            double total = 0;
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
                total += valueByVertex[v];
            return total;
        }

        // Activating a glyph means having its socket purchased — connect it first if needed.
        // The limit penalty makes this path cross as few limited nodes as possible, so a
        // violation here means no route to the socket fits under the caps.
        if (!tree.Contains(socketVertex))
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from, valueByVertex);
            if (dist[socketVertex] >= Infinity)
                return new GlyphGoalOutcome(goal, Total(), Met: false, added);
            if (limits.PathWouldViolate(socketVertex, from, tree))
                return new GlyphGoalOutcome(goal, Total(), Met: false, added, LimitConstrained: true);
            Absorb(socketVertex);
        }

        while (Total() < goal.RequiredTotal - 1e-9)
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from, valueByVertex);

            // Pick the best ratio; a candidate whose minimal-limited-node path would still
            // break a cap is rejected and the next-best tried (paths only change on absorb).
            var rejected = new HashSet<int>();
            int best;
            while (true)
            {
                best = -1;
                double bestRatio = 0;
                foreach (var (v, _) in candidateValue)
                {
                    if (tree.Contains(v) || dist[v] >= Infinity || rejected.Contains(v))
                        continue;
                    if (blocked[v])
                    {
                        limitConstrained |= limits.IsLimitBlocked(v);
                        continue;
                    }
                    double ratio = PathValue(v) / dist[v];
                    if (best < 0 || ratio > bestRatio || (ratio == bestRatio && dist[v] < dist[best]))
                    {
                        best = v;
                        bestRatio = ratio;
                    }
                }
                if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                    break;
                rejected.Add(best);
                limitConstrained = true;
            }
            if (best < 0)
                break; // nothing reachable can raise the total any further

            Absorb(best);
        }

        double achieved = Total();
        bool met = achieved >= goal.RequiredTotal - 1e-9;
        return new GlyphGoalOutcome(goal, achieved, met, added, limitConstrained && !met);

        double Total() => candidateValue.Where(kv => tree.Contains(kv.Key)).Sum(kv => kv.Value);

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                tree.Add(v);
                var cell = graph.Vertices[v].Cell;
                purchased.Add(cell);
                added.Add(cell);
                limits.OnAbsorbed(v);
            }
            limits.BlockFullGroups(blocked, tree);
        }
    }

    /// <summary>Multi-source Dijkstra; equally cheap routes prefer more in-radius source stat
    /// (weights are all ≥1, so the tie-break can reroute but never change a path's cost).</summary>
    private static void RunDijkstra(
        ComposedGraph graph, HashSet<int> tree, int[] weights, bool[] blocked, int[] dist, int[] from,
        double[] tieValue)
    {
        Array.Fill(dist, Infinity);
        var pathValue = new double[dist.Length];
        var queue = new PriorityQueue<int, (int Cost, double NegValue)>();
        foreach (int v in tree)
        {
            dist[v] = 0;
            from[v] = -1;
            queue.Enqueue(v, (0, 0));
        }
        while (queue.TryDequeue(out int v, out var priority))
        {
            if (priority.Cost > dist[v])
                continue;
            foreach (int u in graph.Adjacency[v])
            {
                if (blocked[u])
                    continue;
                int next = priority.Cost + weights[u];
                double value = pathValue[v] + tieValue[u];
                if (next < dist[u] || (next == dist[u] && value > pathValue[u]))
                {
                    dist[u] = next;
                    from[u] = v;
                    pathValue[u] = value;
                    queue.Enqueue(u, (next, -value));
                }
            }
        }
    }
}
