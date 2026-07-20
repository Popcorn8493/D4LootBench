using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>
/// One attached board in the sidebar's Boards tab. The row LINKS to the slot's glyph socket
/// (<see cref="Socket"/>): the glyph name shown here updates live with the Glyphs tab, and the
/// board lock is the socket's <see cref="GlyphSocketViewModel.IsBoardLocked"/> — one source of
/// truth for both views.
/// </summary>
public sealed class BoardRowViewModel
{
    public BoardRowViewModel(int slot, PlacedBoard placed, GlyphSocketViewModel? socket)
    {
        Slot = slot;
        Placed = placed;
        Socket = socket;
    }

    public int Slot { get; }
    public PlacedBoard Placed { get; }

    /// <summary>The slot's glyph socket, when the board has one (bound for name + board lock).</summary>
    public GlyphSocketViewModel? Socket { get; }

    public string Name => Placed.Board.Name ?? Placed.Board.InternalName;
    public string Header => $"Slot {Slot} · {Name}";

    public string Placement => Slot == 0
        ? "Starter board — its single gate faces up"
        : $"Rotation {Placed.RotationSteps * 90}° · attached to slot {Placed.ParentSlot}'s "
          + $"{Placed.AttachEdge} edge";

    /// <summary>The starter can't rotate — its lone gate must keep facing its child.</summary>
    public bool CanRotate => Slot > 0;

    public bool HasSocket => Socket is not null;
}
