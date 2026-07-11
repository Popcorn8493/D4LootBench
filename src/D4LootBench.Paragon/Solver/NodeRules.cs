using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

public enum NodeRuleMode
{
    /// <summary>No steering — the default for every group.</summary>
    Allow,

    /// <summary>Soft-penalize: only pass through when it saves several plain nodes.</summary>
    Avoid,

    /// <summary>Never purchase or pass through.</summary>
    Exclude,

    /// <summary>Take at most <see cref="NodeRule.Limit"/> nodes of the group, avoiding the rest.</summary>
    Limit,

    /// <summary>
    /// Like <see cref="Limit"/>, but also prefer the FEWEST group nodes the optimal path allows:
    /// among equally cheap solves, the one crossing 1 group node beats the one crossing 2.
    /// </summary>
    Minimal,
}

/// <summary>A user preference applied to every node of a group (see <see cref="NodeGrouping"/>).</summary>
public sealed record NodeRule(string GroupKey, NodeRuleMode Mode, int Limit = 0);

/// <summary>A kind of node the user can set rules on, e.g. all "Life" magic nodes.</summary>
public sealed record NodeGroup(string Key, string DisplayName, ParagonNodeKind Kind, int CellCount);

/// <summary>
/// Buckets nodes into user-facing rule groups: Normal and Magic nodes by their primary
/// attribute ("all Life nodes"), Rare and Legendary nodes by name. Gates, glyph sockets and
/// the start node are not groupable — they are structural. On top of those, synthetic
/// "Stat:" groups span every node granting an attribute regardless of kind, so a limit can
/// cap a stat (rare nodes grant many stats incidentally) instead of one bucket.
/// </summary>
public static class NodeGrouping
{
    public const string StatGroupPrefix = "Stat:";
    public static string? GroupKey(ParagonNodeDef node)
    {
        switch (node.Kind)
        {
            case ParagonNodeKind.Normal:
            case ParagonNodeKind.Magic:
                var primary = node.Attributes.FirstOrDefault(a => !a.IsThresholdBonus);
                return primary is null
                    ? null
                    : $"{node.Kind}:{primary.Attribute}:{primary.Param?.ToString() ?? ""}";
            case ParagonNodeKind.Rare:
            case ParagonNodeKind.Legendary:
                return $"{node.Kind}:{node.Name ?? node.InternalName}";
            default:
                return null;
        }
    }

    public static string? DisplayName(ParagonNodeDef node)
    {
        switch (node.Kind)
        {
            case ParagonNodeKind.Normal:
            case ParagonNodeKind.Magic:
                var primary = node.Attributes.FirstOrDefault(a => !a.IsThresholdBonus);
                return primary is null
                    ? null
                    : $"{node.Kind}: {ParagonDisplay.FormatAttributeName(primary.Attribute)}";
            case ParagonNodeKind.Rare:
            case ParagonNodeKind.Legendary:
                return $"{node.Kind}: {node.Name ?? node.InternalName}";
            default:
                return null;
        }
    }

    /// <summary>Distinct groups present in a composed layout, ordered by kind then name.</summary>
    public static IReadOnlyList<NodeGroup> GroupsIn(ComposedGraph graph)
    {
        var groups = new Dictionary<string, (string DisplayName, ParagonNodeKind Kind, int Count)>();
        foreach (var vertex in graph.Vertices)
        {
            if (GroupKey(vertex.Node) is not string key)
                continue;
            string display = DisplayName(vertex.Node)!;
            groups[key] = groups.TryGetValue(key, out var existing)
                ? (existing.DisplayName, existing.Kind, existing.Count + 1)
                : (display, vertex.Node.Kind, 1);
        }
        return groups
            .Select(kv => new NodeGroup(kv.Key, kv.Value.DisplayName, kv.Value.Kind, kv.Value.Count))
            .OrderBy(g => g.Kind)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Synthetic cross-kind groups, one per attribute that more than one primary group grants
    /// (an attribute confined to a single group is already coverable by that group's row).
    /// Threshold-gated grants count too — a conditional +Life node is still a Life node.
    /// </summary>
    public static IReadOnlyList<NodeGroup> StatGroupsIn(ComposedGraph graph)
    {
        var byAttribute = new Dictionary<string, (int Cells, HashSet<string> Groups)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            if (GroupKey(vertex.Node) is not string primary)
                continue;
            foreach (var attribute in GrantedAttributes(vertex.Node))
            {
                var entry = byAttribute.TryGetValue(attribute, out var existing) ? existing : (0, []);
                entry.Cells++;
                entry.Groups.Add(primary);
                byAttribute[attribute] = entry;
            }
        }
        return byAttribute
            .Where(kv => kv.Value.Groups.Count > 1)
            .Select(kv => new NodeGroup(
                StatGroupPrefix + kv.Key,
                $"Any: {ParagonDisplay.FormatAttributeName(kv.Key)}",
                ParagonNodeKind.Normal,
                kv.Value.Cells))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Cells of the layout belonging to each group key, including "Stat:" groups.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CellRef>> CellsByGroup(ComposedGraph graph)
    {
        var cells = new Dictionary<string, IReadOnlyList<CellRef>>();
        void Add(string key, CellRef cell)
        {
            if (cells.TryGetValue(key, out var list))
                ((List<CellRef>)list).Add(cell);
            else
                cells[key] = new List<CellRef> { cell };
        }
        foreach (var vertex in graph.Vertices)
        {
            if (GroupKey(vertex.Node) is not string key)
                continue;
            Add(key, vertex.Cell);
            foreach (var attribute in GrantedAttributes(vertex.Node))
                Add(StatGroupPrefix + attribute, vertex.Cell);
        }
        return cells;
    }

    /// <summary>Distinct attribute names a node grants a value for (params folded together).</summary>
    private static IEnumerable<string> GrantedAttributes(ParagonNodeDef node) => node.Attributes
        .Where(a => a.Value is not null)
        .Select(a => a.Attribute)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
