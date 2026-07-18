using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.Paragon.Import;

/// <summary>
/// Adds the start node, the gates a build crosses, and reachable glyph sockets on glyph-bearing
/// boards to an imported allocation, by walking from the start node over picked cells with free
/// cells (start/gate/socket) as permitted bridges. Mobalytics omits these cells entirely;
/// d4builds usually includes them — re-adding is idempotent either way.
/// </summary>
internal static class ImplicitCellAugmenter
{
    public static ConvertedMaxrollBuild Augment(ConvertedMaxrollBuild build, ParagonData data)
    {
        var graph = ComposedGraph.Build(
            build.Layout, data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase));

        var picked = new HashSet<int>();
        foreach (var cell in build.AllocatedCells)
        {
            if (graph.TryGetVertex(cell, out int vertex))
                picked.Add(vertex);
        }

        static bool IsFree(GraphVertex v) =>
            v.Node.Kind is ParagonNodeKind.Start or ParagonNodeKind.Gate or ParagonNodeKind.GlyphSocket;

        var parent = new Dictionary<int, int> { [graph.StartVertex] = graph.StartVertex };
        var queue = new Queue<int>();
        queue.Enqueue(graph.StartVertex);
        while (queue.Count > 0)
        {
            int vertex = queue.Dequeue();
            foreach (int next in graph.Adjacency[vertex])
            {
                if (parent.ContainsKey(next) || (!picked.Contains(next) && !IsFree(graph.Vertices[next])))
                    continue;
                parent[next] = vertex;
                queue.Enqueue(next);
            }
        }

        // Free cells on the path from any reached picked cell back to the start are traversed.
        var used = new HashSet<int> { graph.StartVertex };
        foreach (int vertex in picked.Where(parent.ContainsKey))
        {
            for (int v = vertex; !used.Contains(v); v = parent[v])
            {
                if (IsFree(graph.Vertices[v]))
                    used.Add(v);
            }
        }

        // A board with an assigned glyph has its socket bought even when nothing routes through it.
        foreach (var glyph in build.Glyphs)
        {
            for (int v = 0; v < graph.Vertices.Count; v++)
            {
                if (graph.Vertices[v].Cell.BoardSlot == glyph.BoardSlot
                    && graph.Vertices[v].Node.Kind == ParagonNodeKind.GlyphSocket
                    && parent.ContainsKey(v))
                    used.Add(v);
            }
        }

        var allocated = new List<CellRef>(build.AllocatedCells);
        var have = build.AllocatedCells.ToHashSet();
        foreach (int vertex in used)
        {
            if (have.Add(graph.Vertices[vertex].Cell))
                allocated.Add(graph.Vertices[vertex].Cell);
        }
        return build with { AllocatedCells = allocated };
    }
}
