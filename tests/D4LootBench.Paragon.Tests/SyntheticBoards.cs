using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Hand-built miniature boards for precise solver/maximizer tests: a char map where 'S' is the
/// start node, '.' (or space) is an empty cell, and any other char places the node def
/// registered for it. The same char can appear on several cells — they share one def.
/// </summary>
internal static class SyntheticBoards
{
    public static ParagonNodeDef Node(
        string snoId, ParagonNodeKind kind, params (string Attribute, double Value)[] attributes) => new()
    {
        SnoId = snoId,
        InternalName = snoId,
        Name = snoId,
        Kind = kind,
        Attributes = attributes
            .Select(a => new NodeAttribute { Attribute = a.Attribute, Value = a.Value })
            .ToList(),
    };

    /// <summary>A rare whose bonus attributes are gated behind the given threshold defs.</summary>
    public static ParagonNodeDef Rare(
        string snoId,
        (string Attribute, double Value)[] attributes,
        (string Attribute, double Value)[] bonusAttributes,
        params string[] thresholds) => new()
    {
        SnoId = snoId,
        InternalName = snoId,
        Name = snoId,
        Kind = ParagonNodeKind.Rare,
        Attributes = attributes
            .Select(a => new NodeAttribute { Attribute = a.Attribute, Value = a.Value })
            .Concat(bonusAttributes.Select(a => new NodeAttribute
                { Attribute = a.Attribute, Value = a.Value, IsThresholdBonus = true }))
            .ToList(),
        Thresholds = thresholds,
    };

    public static ParagonThresholdDef Threshold(string snoId, string attribute, double requirement) => new()
    {
        SnoId = snoId,
        InternalName = snoId,
        Requirements =
        [
            new ThresholdRequirement { Attribute = attribute, ValuesByBoardIndex = [requirement] },
        ],
    };

    /// <summary>The shared start-node def every synthetic board's 'S' cell references.</summary>
    public static readonly ParagonNodeDef StartNode = Node("start", ParagonNodeKind.Start);

    public static ParagonBoardDef Board(
        string[] rows, Dictionary<char, ParagonNodeDef> defs, string snoId = "synthetic")
    {
        var placements = new List<NodePlacement>();
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                char c = rows[y][x];
                if (c is '.' or ' ')
                    continue;
                placements.Add(new NodePlacement
                    { X = x, Y = y, Node = c == 'S' ? StartNode.SnoId : defs[c].SnoId });
            }
        }
        return new ParagonBoardDef
        {
            SnoId = snoId,
            InternalName = snoId,
            Width = rows.Max(r => r.Length),
            Nodes = placements,
        };
    }

    public static ComposedGraph Graph(string[] rows, Dictionary<char, ParagonNodeDef> defs)
    {
        var nodesBySnoId = defs.Values
            .Concat([StartNode])
            .DistinctBy(d => d.SnoId)
            .ToDictionary(d => d.SnoId);
        return ComposedGraph.Build(ParagonLayout.Single(Board(rows, defs)), nodesBySnoId);
    }
}
