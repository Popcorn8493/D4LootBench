using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Enforces Limit rules while a greedy extension grows a purchase tree: groups already at
/// their cap get their remaining cells blocked, and a candidate whose connection path would
/// carry a group past its cap (a below-cap path can absorb several group nodes at once) is
/// rejected so the caller falls back to the next-best candidate.
/// </summary>
internal sealed class LimitTracker
{
    /// <summary>
    /// Dijkstra weight of a limited-group cell: dwarfs any plain detour on a board, so paths
    /// use as few limited nodes as possible and only cross one when no way around exists.
    /// </summary>
    public const int PenaltyWeight = 1000;

    // A vertex can sit in several limited groups at once (its kind group plus "Stat:" groups).
    private readonly Dictionary<int, List<string>> _groupsOfVertex = [];
    private readonly Dictionary<string, int> _limits = [];
    private readonly Dictionary<string, int> _counts = [];
    private readonly HashSet<int> _limitBlocked = [];

    public LimitTracker(
        ComposedGraph graph,
        IEnumerable<NodeRule> limitRules,
        IReadOnlyDictionary<string, IReadOnlyList<CellRef>> cellsByGroup,
        HashSet<int> tree)
    {
        foreach (var rule in limitRules)
        {
            if (!cellsByGroup.TryGetValue(rule.GroupKey, out var cells) || _limits.ContainsKey(rule.GroupKey))
                continue;
            _limits[rule.GroupKey] = rule.Limit;
            _counts[rule.GroupKey] = 0;
            foreach (var cell in cells)
            {
                if (!graph.TryGetVertex(cell, out int v))
                    continue;
                if (!_groupsOfVertex.TryGetValue(v, out var groups))
                    _groupsOfVertex[v] = groups = [];
                groups.Add(rule.GroupKey);
                if (tree.Contains(v))
                    _counts[rule.GroupKey]++;
            }
        }
    }

    /// <summary>
    /// Steers pathfinding around limited groups: every non-tree member gets a weight that
    /// outweighs any plain detour, so Dijkstra crosses the minimum number of limited nodes.
    /// </summary>
    public void PenalizeGroups(int[] weights, HashSet<int> tree)
    {
        foreach (var (v, _) in _groupsOfVertex)
        {
            if (!tree.Contains(v))
                weights[v] = Math.Max(weights[v], PenaltyWeight);
        }
    }

    /// <summary>Blocks every non-tree cell of each group that already sits at its cap.</summary>
    public void BlockFullGroups(bool[] blocked, HashSet<int> tree)
    {
        foreach (var (v, keys) in _groupsOfVertex)
        {
            if (!tree.Contains(v) && keys.Any(key => _counts[key] >= _limits[key]))
            {
                blocked[v] = true;
                _limitBlocked.Add(v);
            }
        }
    }

    /// <summary>Whether a cell is blocked because its group hit the cap (vs a user exclusion).</summary>
    public bool IsLimitBlocked(int vertex) => _limitBlocked.Contains(vertex);

    /// <summary>
    /// Whether absorbing the path tree→vertex (walking <paramref name="from"/> parents)
    /// would push any group past its cap.
    /// </summary>
    public bool PathWouldViolate(int vertex, int[] from, HashSet<int> tree)
    {
        if (_limits.Count == 0)
            return false;
        Dictionary<string, int>? pathCounts = null;
        for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
        {
            if (!_groupsOfVertex.TryGetValue(v, out var keys))
                continue;
            pathCounts ??= [];
            foreach (var key in keys)
                pathCounts[key] = pathCounts.GetValueOrDefault(key) + 1;
        }
        if (pathCounts is null)
            return false;
        foreach (var (key, added) in pathCounts)
        {
            if (_counts[key] + added > _limits[key])
                return true;
        }
        return false;
    }

    /// <summary>Counts an absorbed vertex; follow up with <see cref="BlockFullGroups"/>.</summary>
    public void OnAbsorbed(int vertex)
    {
        if (!_groupsOfVertex.TryGetValue(vertex, out var keys))
            return;
        foreach (var key in keys)
            _counts[key]++;
    }

    /// <summary>Un-counts a removed vertex (the reallocation pass swaps purchases out).</summary>
    public void OnRemoved(int vertex)
    {
        if (!_groupsOfVertex.TryGetValue(vertex, out var keys))
            return;
        foreach (var key in keys)
            _counts[key]--;
    }

    /// <summary>Whether adding this single vertex would push any of its groups past its cap.</summary>
    public bool WouldViolate(int vertex) =>
        _groupsOfVertex.TryGetValue(vertex, out var keys)
        && keys.Any(key => _counts[key] + 1 > _limits[key]);
}
