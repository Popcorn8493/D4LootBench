using System.Runtime.CompilerServices;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// In-game point accounting for board crossings (user-verified against a live build, matching
/// guide consensus): buying YOUR side's Board Attachment Gate costs a point; attaching the
/// next board is free and auto-purchases the far side's gate, which also does NOT grant its
/// +5 attributes. A purchased gate PAIR therefore costs one point and grants one set of
/// stats, so naive cell counting overcounts by one per crossing. The Steiner solve still
/// weighs a crossing as two cells — that only biases path shape mildly against crossings,
/// never the reported cost. Gate pairs are the only adjacent same-kind cells on different
/// boards, and a path can only cross a boundary through both halves consecutively.
/// </summary>
public static class GateCrossings
{
    private static readonly ConditionalWeakTable<ComposedGraph, int[]> PairMaps = new();

    /// <summary>Per-vertex partner map: the adjacent gate cell on ANOTHER board, or -1.
    /// Cached per graph (graphs are immutable) — treat the array as read-only.</summary>
    public static int[] PairMap(ComposedGraph graph) => PairMaps.GetValue(graph, BuildPairMap);

    private static int[] BuildPairMap(ComposedGraph graph)
    {
        var map = new int[graph.Vertices.Count];
        Array.Fill(map, -1);
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            if (graph.Vertices[v].Node.Kind != ParagonNodeKind.Gate)
                continue;
            foreach (int n in graph.Adjacency[v])
            {
                if (graph.Vertices[n].Node.Kind == ParagonNodeKind.Gate
                    && graph.Vertices[n].Cell.BoardSlot != graph.Vertices[v].Cell.BoardSlot)
                {
                    map[v] = n;
                    break;
                }
            }
        }
        return map;
    }

    /// <summary>Points the game refunds versus naive cell counting: one per purchased gate pair.</summary>
    public static int FreeCredits(ComposedGraph graph, IReadOnlyCollection<CellRef> purchased)
    {
        var set = purchased as ISet<CellRef> ?? purchased.ToHashSet();
        var pairs = PairMap(graph);
        int credits = 0;
        for (int v = 0; v < pairs.Length; v++)
        {
            int n = pairs[v];
            if (n > v // count each pair once
                && set.Contains(graph.Vertices[v].Cell)
                && set.Contains(graph.Vertices[n].Cell))
                credits++;
        }
        return credits;
    }

    /// <summary>What the purchased set actually costs in-game.</summary>
    public static int PointCost(ComposedGraph graph, IReadOnlyCollection<CellRef> purchased)
        => purchased.Count - FreeCredits(graph, purchased);
}
