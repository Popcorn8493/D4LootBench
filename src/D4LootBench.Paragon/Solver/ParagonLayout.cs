using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>Edge of a board where another board can be attached (gates sit at edge midpoints).</summary>
public enum BoardEdge
{
    Top,
    Left,
    Right,
    Bottom,
}

/// <summary>A board placed into a layout: the starting board (slot 0) or an attachment.</summary>
public sealed class PlacedBoard
{
    public required ParagonBoardDef Board { get; init; }

    /// <summary>Clockwise 90° rotation steps (0–3) applied before attaching, as in the game UI.</summary>
    public int RotationSteps { get; init; }

    /// <summary>Slot of the board this one is attached to; null only for slot 0.</summary>
    public int? ParentSlot { get; init; }

    /// <summary>Edge of the parent board whose gate this board attaches to; null only for slot 0.</summary>
    public BoardEdge? AttachEdge { get; init; }
}

/// <summary>
/// An ordered set of placed boards forming a player's paragon layout. Slot 0 is the class
/// starting board; each later slot attaches to an earlier one at a gate edge.
/// </summary>
public sealed class ParagonLayout
{
    /// <summary>Game-enforced cap: starting board plus four attachments.</summary>
    public const int MaxBoards = 5;

    public ParagonLayout(IReadOnlyList<PlacedBoard> boards)
    {
        Validate(boards);
        Boards = boards;
    }

    public IReadOnlyList<PlacedBoard> Boards { get; }

    /// <summary>Convenience: a layout consisting of just one board (typically the class starter).</summary>
    public static ParagonLayout Single(ParagonBoardDef board) =>
        new([new PlacedBoard { Board = board }]);

    private static void Validate(IReadOnlyList<PlacedBoard> boards)
    {
        if (boards.Count == 0)
            throw new ArgumentException("A layout needs at least one board.", nameof(boards));
        if (boards.Count > MaxBoards)
            throw new ArgumentException($"A layout allows at most {MaxBoards} boards.", nameof(boards));

        var usedGates = new HashSet<(int Slot, BoardEdge Edge)>();
        for (int slot = 0; slot < boards.Count; slot++)
        {
            var placed = boards[slot];
            if (placed.RotationSteps is < 0 or > 3)
                throw new ArgumentException($"Slot {slot}: rotation must be 0–3 quarter turns.", nameof(boards));

            if (slot == 0)
            {
                if (placed.ParentSlot is not null || placed.AttachEdge is not null)
                    throw new ArgumentException("Slot 0 is the starting board and cannot be attached.", nameof(boards));
                continue;
            }

            if (placed.ParentSlot is not int parent || placed.AttachEdge is not BoardEdge edge)
                throw new ArgumentException($"Slot {slot}: attached boards need ParentSlot and AttachEdge.", nameof(boards));
            if (parent < 0 || parent >= slot)
                throw new ArgumentException($"Slot {slot}: ParentSlot must reference an earlier slot.", nameof(boards));
            if (!usedGates.Add((parent, edge)))
                throw new ArgumentException($"Slot {slot}: gate {edge} of slot {parent} is already occupied.", nameof(boards));
        }
    }

    /// <summary>Transforms stored board coordinates into display coordinates after rotation.</summary>
    public static (int X, int Y) Rotate(int x, int y, int rotationSteps, int width)
    {
        return (rotationSteps & 3) switch
        {
            1 => (width - 1 - y, x),
            2 => (width - 1 - x, width - 1 - y),
            3 => (y, width - 1 - x),
            _ => (x, y),
        };
    }

    /// <summary>The cell holding the gate at the midpoint of the given edge (display coordinates).</summary>
    public static (int X, int Y) GateCell(BoardEdge edge, int width)
    {
        int mid = width / 2;
        return edge switch
        {
            BoardEdge.Top => (mid, 0),
            BoardEdge.Left => (0, mid),
            BoardEdge.Right => (width - 1, mid),
            _ => (mid, width - 1),
        };
    }

    public static BoardEdge Opposite(BoardEdge edge) => edge switch
    {
        BoardEdge.Top => BoardEdge.Bottom,
        BoardEdge.Bottom => BoardEdge.Top,
        BoardEdge.Left => BoardEdge.Right,
        _ => BoardEdge.Left,
    };
}
