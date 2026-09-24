namespace D4LootBench.Paragon.Solver;

/// <summary>Cell-coordinate remapping for layout edits, shared by the solvers and the planner UI.</summary>
public static class LayoutRemap
{
    /// <summary>
    /// Re-rotating the board in <paramref name="slot"/> from its current rotation to
    /// <paramref name="newRotation"/>: its cells keep their identity but move with the rotation
    /// delta; cells on other boards are untouched. Re-attaching a board to a different parent
    /// edge remaps the same way (only the rotation changes the board's local coordinates).
    /// </summary>
    public static Func<CellRef, CellRef> ForRotation(int slot, PlacedBoard placed, int newRotation)
    {
        int delta = (newRotation - placed.RotationSteps + 4) & 3;
        int width = placed.Board.Width;
        return cell =>
        {
            if (cell.BoardSlot != slot || delta == 0)
                return cell;
            var (x, y) = ParagonLayout.Rotate(cell.X, cell.Y, delta, width);
            return cell with { X = x, Y = y };
        };
    }

    /// <summary>
    /// Rewrites the board list so slot numbers AND parent/edge links match how the purchased
    /// path actually assembles the build (<see cref="BuildStats.EffectiveAttachments"/>) — the
    /// planner's tree is only positional and can claim attachments whose crossing the path never
    /// buys. Positions and rotations are preserved; remap cells through the result. Null when the
    /// numbering already matches. Throws <see cref="ArgumentException"/> or
    /// <see cref="InvalidOperationException"/> when the renumbered layout can't be built.
    /// </summary>
    public static SlotRenumbering? ToPathAttachOrder(
        IReadOnlyList<PlacedBoard> boards, ComposedGraph graph, IEnumerable<CellRef> purchased)
    {
        var entries = BuildStats.EffectiveAttachments(graph, purchased);
        if (Enumerable.Range(0, entries.Count).All(s => entries[s].Tier == s))
            return null;

        int count = boards.Count;
        var oldByNew = Enumerable.Range(0, count).OrderBy(s => entries[s].Tier).ToList();
        var newByOld = new int[count];
        for (int n = 0; n < count; n++)
            newByOld[oldByNew[n]] = n;

        var newBoards = new List<PlacedBoard> { boards[0] };
        for (int n = 1; n < count; n++)
        {
            int old = oldByNew[n];
            var placed = boards[old];
            int parentOld;
            BoardEdge edge;
            if (entries[old] is { EnteredFromSlot: int fromSlot, ParentGate: CellRef gate })
            {
                // Re-parent to the crossing the path actually uses.
                parentOld = fromSlot;
                edge = EdgeOfGate(gate, boards[fromSlot].Board.Width);
            }
            else
            {
                parentOld = placed.ParentSlot!.Value;
                edge = placed.AttachEdge!.Value;
            }
            newBoards.Add(new PlacedBoard
            {
                Board = placed.Board,
                ParentSlot = newByOld[parentOld],
                AttachEdge = edge,
                RotationSteps = placed.RotationSteps,
            });
        }

        ComposedGraph.Build(new ParagonLayout(newBoards)); // validates the new tree
        return new SlotRenumbering(newBoards, newByOld);
    }

    /// <summary>Which edge of a board a gate cell sits on (rotated coordinates).</summary>
    private static BoardEdge EdgeOfGate(CellRef gate, int width) =>
        new[] { BoardEdge.Top, BoardEdge.Bottom, BoardEdge.Left, BoardEdge.Right }
            .First(edge => ParagonLayout.GateCell(edge, width) == (gate.X, gate.Y));
}

/// <summary>A renumbered board list plus the old → new slot map its cells move through.</summary>
public sealed record SlotRenumbering(IReadOnlyList<PlacedBoard> Boards, IReadOnlyList<int> NewSlotByOld)
{
    public CellRef Remap(CellRef cell) => cell with { BoardSlot = NewSlotByOld[cell.BoardSlot] };
}
