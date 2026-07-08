using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>A cell in a composed layout: board slot plus display coordinates after rotation.</summary>
public readonly record struct CellRef(int BoardSlot, int X, int Y);

public sealed record GraphVertex(CellRef Cell, ParagonNodeDef Node);

/// <summary>
/// The walkable graph over every occupied cell of a layout: orthogonal adjacency within each
/// board, plus gate-to-gate edges where boards attach. Vertex 0..N-1 indexes are stable.
/// </summary>
public sealed class ComposedGraph
{
    private readonly Dictionary<CellRef, int> _vertexByCell;

    private ComposedGraph(List<GraphVertex> vertices, List<int>[] adjacency, Dictionary<CellRef, int> vertexByCell, int startVertex)
    {
        Vertices = vertices;
        Adjacency = adjacency;
        _vertexByCell = vertexByCell;
        StartVertex = startVertex;
    }

    public IReadOnlyList<GraphVertex> Vertices { get; }

    /// <summary>Neighbor vertex indices per vertex index.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Adjacency { get; }

    /// <summary>The pre-granted start node on slot 0 (costs no point).</summary>
    public int StartVertex { get; }

    public bool TryGetVertex(CellRef cell, out int vertex) => _vertexByCell.TryGetValue(cell, out vertex);

    public static ComposedGraph Build(ParagonLayout layout) =>
        Build(layout, ParagonDatabase.NodesBySnoId);

    public static ComposedGraph Build(ParagonLayout layout, IReadOnlyDictionary<string, ParagonNodeDef> nodesBySnoId)
    {
        var vertices = new List<GraphVertex>();
        var vertexByCell = new Dictionary<CellRef, int>();

        for (int slot = 0; slot < layout.Boards.Count; slot++)
        {
            var placed = layout.Boards[slot];
            foreach (var placement in placed.Board.Nodes)
            {
                var (x, y) = ParagonLayout.Rotate(placement.X, placement.Y, placed.RotationSteps, placed.Board.Width);
                var cell = new CellRef(slot, x, y);
                if (!nodesBySnoId.TryGetValue(placement.Node, out var node))
                    throw new InvalidOperationException($"Board {placed.Board.InternalName} references unknown node {placement.Node}.");
                vertexByCell[cell] = vertices.Count;
                vertices.Add(new GraphVertex(cell, node));
            }
        }

        var adjacency = new List<int>[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            adjacency[i] = [];
        }

        foreach (var (cell, index) in vertexByCell)
        {
            // Right and down only; the reverse direction is added symmetrically.
            Connect(cell with { X = cell.X + 1 }, index);
            Connect(cell with { Y = cell.Y + 1 }, index);
        }

        for (int slot = 1; slot < layout.Boards.Count; slot++)
        {
            var placed = layout.Boards[slot];
            int parentSlot = placed.ParentSlot!.Value;
            var edge = placed.AttachEdge!.Value;

            var parentGate = ParagonLayout.GateCell(edge, layout.Boards[parentSlot].Board.Width);
            var childGate = ParagonLayout.GateCell(ParagonLayout.Opposite(edge), placed.Board.Width);

            if (!vertexByCell.TryGetValue(new CellRef(parentSlot, parentGate.X, parentGate.Y), out int parentVertex)
                || Vertex(parentVertex).Node.Kind != ParagonNodeKind.Gate)
                throw new InvalidOperationException(
                    $"Slot {parentSlot} ({layout.Boards[parentSlot].Board.InternalName}) has no gate on its {edge} edge.");
            if (!vertexByCell.TryGetValue(new CellRef(slot, childGate.X, childGate.Y), out int childVertex)
                || Vertex(childVertex).Node.Kind != ParagonNodeKind.Gate)
                throw new InvalidOperationException(
                    $"Slot {slot} ({placed.Board.InternalName}) has no gate facing the {edge} edge of its parent.");

            adjacency[parentVertex].Add(childVertex);
            adjacency[childVertex].Add(parentVertex);
        }

        int startVertex = -1;
        for (int i = 0; i < vertices.Count; i++)
        {
            if (vertices[i].Cell.BoardSlot == 0 && vertices[i].Node.Kind == ParagonNodeKind.Start)
            {
                startVertex = i;
                break;
            }
        }
        if (startVertex < 0)
            throw new InvalidOperationException(
                $"Slot 0 ({layout.Boards[0].Board.InternalName}) has no start node — slot 0 must be a class starting board.");

        return new ComposedGraph(vertices, adjacency, vertexByCell, startVertex);

        GraphVertex Vertex(int index) => vertices[index];

        void Connect(CellRef neighbor, int index)
        {
            if (vertexByCell.TryGetValue(neighbor, out int other))
            {
                adjacency[index].Add(other);
                adjacency[other].Add(index);
            }
        }
    }
}
