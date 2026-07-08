namespace D4LootBench.Paragon.Solver;

/// <summary>
/// A glyph activation requirement: at least <see cref="RequiredTotal"/> of
/// <see cref="SourceAttribute"/> purchased within <see cref="Radius"/> (Manhattan) of the socket.
/// </summary>
public sealed record GlyphGoal(CellRef Socket, string SourceAttribute, double RequiredTotal, int Radius, string? GlyphName = null);

public sealed record GlyphGoalOutcome(GlyphGoal Goal, double AchievedTotal, bool Met, IReadOnlyList<CellRef> AddedCells);

/// <summary>
/// Extends a solved purchase set with the cheapest additional nodes needed to satisfy glyph
/// activation goals. Greedy: each round runs a multi-source Dijkstra from the current tree and
/// connects the in-radius stat node with the best stat-gained-per-point ratio, until the
/// requirement is met or no reachable stat node remains.
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
        SolverConstraints constraints)
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

        var outcomes = new List<GlyphGoalOutcome>();
        foreach (var goal in goals)
        {
            outcomes.Add(ExtendForGoal(graph, goal, tree, purchased, weights, blocked));
        }
        return outcomes;
    }

    private static GlyphGoalOutcome ExtendForGoal(
        ComposedGraph graph, GlyphGoal goal, HashSet<int> tree, ISet<CellRef> purchased, int[] weights, bool[] blocked)
    {
        var added = new List<CellRef>();
        if (!graph.TryGetVertex(goal.Socket, out int socketVertex) || blocked[socketVertex])
            return new GlyphGoalOutcome(goal, 0, Met: false, added);

        // Stat-bearing cells the glyph can see: same board, Manhattan distance ≤ radius.
        var candidateValue = new Dictionary<int, double>();
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            var (cell, node) = (graph.Vertices[v].Cell, graph.Vertices[v].Node);
            if (cell.BoardSlot != goal.Socket.BoardSlot || v == socketVertex || blocked[v])
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

        // Activating a glyph means having its socket purchased — connect it first if needed.
        if (!tree.Contains(socketVertex))
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from);
            if (dist[socketVertex] >= Infinity)
                return new GlyphGoalOutcome(goal, Total(), Met: false, added);
            Absorb(socketVertex);
        }

        while (Total() < goal.RequiredTotal - 1e-9)
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from);

            int best = -1;
            double bestRatio = 0;
            foreach (var (v, value) in candidateValue)
            {
                if (tree.Contains(v) || dist[v] >= Infinity)
                    continue;
                double ratio = value / dist[v];
                if (best < 0 || ratio > bestRatio || (ratio == bestRatio && dist[v] < dist[best]))
                {
                    best = v;
                    bestRatio = ratio;
                }
            }
            if (best < 0)
                break; // nothing reachable can raise the total any further

            Absorb(best);
        }

        double achieved = Total();
        return new GlyphGoalOutcome(goal, achieved, achieved >= goal.RequiredTotal - 1e-9, added);

        double Total() => candidateValue.Where(kv => tree.Contains(kv.Key)).Sum(kv => kv.Value);

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                tree.Add(v);
                var cell = graph.Vertices[v].Cell;
                purchased.Add(cell);
                added.Add(cell);
            }
        }
    }

    private static void RunDijkstra(ComposedGraph graph, HashSet<int> tree, int[] weights, bool[] blocked, int[] dist, int[] from)
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
