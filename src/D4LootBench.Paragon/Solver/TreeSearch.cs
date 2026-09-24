namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Multi-source Dijkstra from a purchased tree, with reusable buffers so the greedy spenders
/// (<see cref="PointMaximizer"/>, <see cref="GlyphOptimizer"/>) don't reallocate every round.
/// With a tie value, equally cheap routes prefer the one whose cells carry more value — every
/// weight is ≥1, so the tie-break can reroute but never change a path's cost. Not thread-safe:
/// one instance per spend.
/// </summary>
internal sealed class TreeDijkstra
{
    public const int Infinity = int.MaxValue / 4;

    private readonly ComposedGraph _graph;
    private readonly double[] _pathValue;
    private readonly PriorityQueue<int, (int Cost, double NegValue)> _queue = new();

    public TreeDijkstra(ComposedGraph graph)
    {
        _graph = graph;
        int n = graph.Vertices.Count;
        Dist = new int[n];
        From = new int[n];
        _pathValue = new double[n];
    }

    /// <summary>Cheapest entry cost per vertex from the tree (tree vertices 0).</summary>
    public int[] Dist { get; }

    /// <summary>Predecessor toward the tree per vertex (-1 at tree vertices).</summary>
    public int[] From { get; }

    public void Run(IReadOnlySet<int> tree, int[] weights, bool[] blocked, double[]? tieValue = null)
    {
        var dist = Dist;
        var from = From;
        var pathValue = _pathValue;
        Array.Fill(dist, Infinity);
        if (tieValue is not null)
            Array.Clear(pathValue);
        var queue = _queue;
        queue.Clear();
        foreach (int v in tree)
        {
            dist[v] = 0;
            from[v] = -1;
            queue.Enqueue(v, (0, 0));
        }
        var adjacency = _graph.Adjacency;
        while (queue.TryDequeue(out int v, out var priority))
        {
            if (priority.Cost > dist[v])
                continue;
            foreach (int u in adjacency[v])
            {
                if (blocked[u])
                    continue;
                int next = priority.Cost + weights[u];
                double value = tieValue is null ? 0 : pathValue[v] + tieValue[u];
                if (next < dist[u] || (tieValue is not null && next == dist[u] && value > pathValue[u]))
                {
                    dist[u] = next;
                    from[u] = v;
                    if (tieValue is not null)
                        pathValue[u] = value;
                    queue.Enqueue(u, (next, -value));
                }
            }
        }
    }
}

/// <summary>Connectivity queries over a purchased tree.</summary>
internal static class TreeConnectivity
{
    /// <summary>
    /// Per vertex: whether removing it leaves every other tree vertex connected to the start —
    /// i.e. it's in the tree, isn't the start, and isn't a cut vertex of the tree-induced
    /// subgraph. One iterative Tarjan pass replaces a BFS per candidate. When the tree is
    /// already disconnected nothing is removable (matching the per-candidate BFS it replaces).
    /// </summary>
    public static bool[] RemovableMask(ComposedGraph graph, IReadOnlySet<int> tree)
    {
        int n = graph.Vertices.Count;
        var removable = new bool[n];
        int root = graph.StartVertex;
        var adjacency = graph.Adjacency;
        var disc = new int[n];
        var low = new int[n];
        var parent = new int[n];
        var edgeIndex = new int[n];
        var isCut = new bool[n];
        int time = 0, visited = 1, rootChildren = 0;
        var stack = new Stack<int>();
        disc[root] = low[root] = ++time;
        parent[root] = -1;
        stack.Push(root);
        while (stack.Count > 0)
        {
            int v = stack.Peek();
            if (edgeIndex[v] < adjacency[v].Length)
            {
                int u = adjacency[v][edgeIndex[v]++];
                if (!tree.Contains(u))
                    continue;
                if (disc[u] == 0)
                {
                    parent[u] = v;
                    disc[u] = low[u] = ++time;
                    visited++;
                    if (v == root)
                        rootChildren++;
                    stack.Push(u);
                }
                else if (u != parent[v])
                {
                    low[v] = Math.Min(low[v], disc[u]);
                }
            }
            else
            {
                stack.Pop();
                int p = parent[v];
                if (p >= 0)
                {
                    low[p] = Math.Min(low[p], low[v]);
                    if (p != root && low[v] >= disc[p])
                        isCut[p] = true;
                }
            }
        }

        int treeSize = tree.Contains(root) ? tree.Count : tree.Count + 1;
        if (visited != treeSize)
            return removable; // already disconnected — removing anything can't be proven safe
        if (rootChildren > 1)
            isCut[root] = true;
        foreach (int v in tree)
            removable[v] = v != root && !isCut[v];
        return removable;
    }
}
