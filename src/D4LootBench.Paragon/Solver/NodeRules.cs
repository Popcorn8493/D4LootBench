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
}

/// <summary>A user preference applied to every node of a group (see <see cref="NodeGrouping"/>).</summary>
public sealed record NodeRule(string GroupKey, NodeRuleMode Mode, int Limit = 0);

/// <summary>A kind of node the user can set rules on, e.g. all "Life" magic nodes.</summary>
public sealed record NodeGroup(string Key, string DisplayName, ParagonNodeKind Kind, int CellCount);

/// <summary>
/// Buckets nodes into user-facing rule groups: Normal and Magic nodes by their primary
/// attribute ("all Life nodes"), Rare and Legendary nodes by name. Gates, glyph sockets and
/// the start node are not groupable — they are structural.
/// </summary>
public static class NodeGrouping
{
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

    /// <summary>Cells of the layout belonging to each group key.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CellRef>> CellsByGroup(ComposedGraph graph)
    {
        var cells = new Dictionary<string, IReadOnlyList<CellRef>>();
        foreach (var vertex in graph.Vertices)
        {
            if (GroupKey(vertex.Node) is not string key)
                continue;
            if (cells.TryGetValue(key, out var list))
                ((List<CellRef>)list).Add(vertex.Cell);
            else
                cells[key] = new List<CellRef> { vertex.Cell };
        }
        return cells;
    }
}
