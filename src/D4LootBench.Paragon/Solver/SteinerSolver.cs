using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Finds the cheapest connected set of paragon nodes containing the start node and all targets.
/// Every node costs one point (the start node is free), so this is a node-weighted Steiner tree
/// with unit weights: exact Dreyfus–Wagner dynamic programming up to <see cref="MaxExactTargets"/>
/// targets, then a nearest-terminal-insertion heuristic with leaf pruning. Optional
/// <see cref="SolverConstraints"/> raise per-cell weights (avoid) or block cells outright (exclude).
/// </summary>
public static class SteinerSolver
{
    /// <summary>Exact DP is O(3^k·n); beyond this many targets the heuristic takes over.</summary>
    public const int MaxExactTargets = 10;

    private const int Infinity = int.MaxValue / 4;

    public static SolverResult Solve(ComposedGraph graph, IReadOnlyCollection<CellRef> targets) =>
        Solve(graph, targets, SolverConstraints.None);

    public static SolverResult Solve(ComposedGraph graph, IReadOnlyCollection<CellRef> targets, SolverConstraints constraints)
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

        var terminalVertices = new List<int>();
        foreach (var cell in targets.Distinct())
        {
            if (!graph.TryGetVertex(cell, out int vertex))
                return SolverResult.Failed($"No node at board slot {cell.BoardSlot}, cell ({cell.X}, {cell.Y}).");
            if (blocked[vertex])
                return SolverResult.Failed(
                    $"Target at board slot {cell.BoardSlot}, cell ({cell.X}, {cell.Y}) is excluded — remove the exclusion or the target.");
            if (vertex != graph.StartVertex)
                terminalVertices.Add(vertex);
        }

        if (terminalVertices.Count == 0)
            return new SolverResult { Success = true, IsOptimal = true };

        var chosen = terminalVertices.Count <= MaxExactTargets
            ? SolveExact(graph, terminalVertices, weights, blocked)
            : SolveHeuristic(graph, terminalVertices, weights, blocked);
        if (chosen is null)
            return SolverResult.Failed(
                "Not all targets are reachable from the start node with the current board layout and exclusions.");

        chosen.Remove(graph.StartVertex);
        return new SolverResult
        {
            Success = true,
            IsOptimal = terminalVertices.Count <= MaxExactTargets,
            PurchasedCells = chosen.Select(v => graph.Vertices[v].Cell).ToList(),
        };
    }

    /// <summary>
    /// Dreyfus–Wagner over terminal subsets. dp[S][v] = min cost of a tree spanning S ∪ {v},
    /// counting the weight of every node in the tree except the root v's own weight is included
    /// too — we subtract the double-counted root on merges. The answer reads at the start vertex.
    /// </summary>
    private static HashSet<int>? SolveExact(ComposedGraph graph, List<int> terminals, int[] weights, bool[] blocked)
    {
        int n = graph.Vertices.Count;
        int k = terminals.Count;
        int full = (1 << k) - 1;

        var dp = new int[full + 1][];
        // parent[S][v]: how dp[S][v] was achieved — merge of (S1,v)+(S2,v), or grow from (S,u).
        var parent = new (int Kind, int A, int B)[full + 1][]; // Kind 0=base, 1=merge(A=subset), 2=grow(A=vertex)
        for (int s = 1; s <= full; s++)
        {
            dp[s] = new int[n];
            parent[s] = new (int, int, int)[n];
            Array.Fill(dp[s], Infinity);
        }

        for (int t = 0; t < k; t++)
        {
            dp[1 << t][terminals[t]] = weights[terminals[t]];
        }

        var queue = new PriorityQueue<int, int>();
        for (int s = 1; s <= full; s++)
        {
            var dpS = dp[s];
            for (int sub = (s - 1) & s; sub > 0; sub = (sub - 1) & s)
            {
                if (sub < (s ^ sub))
                    break; // each unordered split visited once
                var dpA = dp[sub];
                var dpB = dp[s ^ sub];
                for (int v = 0; v < n; v++)
                {
                    if (dpA[v] >= Infinity || dpB[v] >= Infinity)
                        continue;
                    int cost = dpA[v] + dpB[v] - weights[v];
                    if (cost < dpS[v])
                    {
                        dpS[v] = cost;
                        parent[s][v] = (1, sub, 0);
                    }
                }
            }

            // Grow step: Dijkstra relaxation, paying the weight of each vertex entered.
            queue.Clear();
            for (int v = 0; v < n; v++)
            {
                if (dpS[v] < Infinity)
                    queue.Enqueue(v, dpS[v]);
            }
            while (queue.TryDequeue(out int v, out int cost))
            {
                if (cost > dpS[v])
                    continue;
                foreach (int u in graph.Adjacency[v])
                {
                    if (blocked[u])
                        continue;
                    int next = cost + weights[u];
                    if (next < dpS[u])
                    {
                        dpS[u] = next;
                        parent[s][u] = (2, v, 0);
                        queue.Enqueue(u, next);
                    }
                }
            }
        }

        if (dp[full][graph.StartVertex] >= Infinity)
            return null;

        var chosen = new HashSet<int> { graph.StartVertex };
        var stack = new Stack<(int S, int V)>();
        stack.Push((full, graph.StartVertex));
        while (stack.Count > 0)
        {
            var (s, v) = stack.Pop();
            chosen.Add(v);
            var (kind, a, _) = parent[s][v];
            if (kind == 1)
            {
                stack.Push((a, v));
                stack.Push((s ^ a, v));
            }
            else if (kind == 2)
            {
                stack.Push((s, a));
            }
            // kind 0: single-terminal base case — nothing to expand.
        }
        return chosen;
    }

    /// <summary>
    /// Repeatedly connects the nearest unreached terminal to the growing tree via a cheapest
    /// path (multi-source Dijkstra from every tree vertex), then prunes needless leaves.
    /// </summary>
    private static HashSet<int>? SolveHeuristic(ComposedGraph graph, List<int> terminals, int[] weights, bool[] blocked)
    {
        int n = graph.Vertices.Count;
        var inTree = new HashSet<int> { graph.StartVertex };
        var remaining = new HashSet<int>(terminals);
        remaining.Remove(graph.StartVertex);

        var dist = new int[n];
        var from = new int[n];
        var queue = new PriorityQueue<int, int>();

        while (remaining.Count > 0)
        {
            Array.Fill(dist, Infinity);
            queue.Clear();
            foreach (int v in inTree)
            {
                dist[v] = 0;
                from[v] = -1;
                queue.Enqueue(v, 0);
            }

            int reached = -1;
            while (queue.TryDequeue(out int v, out int cost))
            {
                if (cost > dist[v])
                    continue;
                if (remaining.Contains(v))
                {
                    reached = v;
                    break;
                }
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

            if (reached < 0)
                return null;

            for (int v = reached; v != -1 && !inTree.Contains(v); v = from[v])
            {
                inTree.Add(v);
            }
            remaining.Remove(reached);
        }

        Prune(graph, inTree, terminals);
        return inTree;
    }

    /// <summary>Removes non-terminal leaves (degree ≤ 1 within the tree) until none remain.</summary>
    private static void Prune(ComposedGraph graph, HashSet<int> tree, List<int> terminals)
    {
        var keep = new HashSet<int>(terminals) { graph.StartVertex };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int v in tree.ToList())
            {
                if (keep.Contains(v))
                    continue;
                int degree = graph.Adjacency[v].Count(u => tree.Contains(u));
                if (degree <= 1)
                {
                    tree.Remove(v);
                    changed = true;
                }
            }
        }
    }
}
