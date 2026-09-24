using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Services;
using D4LootBench.App.Views;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

public partial class ParagonPlannerViewModel
{
    // ── Renumbering slots to the path's attach order ──────────────────────

    /// <summary>
    /// Rewrites the board list so slot numbers AND parent/edge links match how the purchased
    /// path actually assembles the build — the planner's tree is only positional and can claim
    /// attachments (e.g. "2 hangs off 1's top") whose crossing the path never buys. Positions,
    /// rotations, targets, marks, purchases, and glyphs are all preserved.
    /// </summary>
    [RelayCommand]
    private void RenumberToPathOrder()
    {
        if (_graph is null || _layout is null)
            return;
        var purchased = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();
        if (purchased.Count == 0)
        {
            SetStatus("Solve or import a build first — the attach order comes from the purchased path.", error: true);
            return;
        }
        SlotRenumbering? renumbering;
        try
        {
            renumbering = LayoutRemap.ToPathAttachOrder(_placedBoards, _graph, purchased);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus($"Couldn't renumber to the path order: {ex.Message}", error: true);
            return;
        }
        if (renumbering is null)
        {
            SetStatus("Slot numbering already matches the path's attach order.");
            return;
        }

        RecordUndo();
        Func<CellRef, CellRef> remap = renumbering.Remap;
        var newByOld = renumbering.NewSlotByOld;
        var targets = _targets.Select(remap).ToList();
        var constraints = Cells.Where(c => c.Constraint != CellConstraint.None)
            .Select(c => (remap(c.Cell), c.Constraint)).ToList();
        var newPurchased = purchased.Select(remap).ToHashSet();
        var glyphPicks = GlyphSockets.Select(s =>
            (NewSlot: newByOld[s.Socket.BoardSlot], Glyph: s.SelectedGlyph?.InternalName,
             s.Level, s.RequiredStat, s.EnsureActive, s.HighlightRadius)).ToList();

        _placedBoards.Clear();
        _placedBoards.AddRange(renumbering.Boards);
        RebuildLayout(); // per-slot glyph preservation is wrong after renumbering — restore below
        RestoreCells(targets, constraints);
        var socketBySlot = GlyphSockets.ToDictionary(s => s.Socket.BoardSlot);
        foreach (var pick in glyphPicks)
        {
            if (!socketBySlot.TryGetValue(pick.NewSlot, out var socket))
                continue;
            socket.SelectedGlyph = pick.Glyph is null
                ? null
                : socket.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.InternalName, pick.Glyph, StringComparison.OrdinalIgnoreCase));
            socket.Level = pick.Level;
            socket.RequiredStat = pick.RequiredStat;
            socket.EnsureActive = pick.EnsureActive && socket.SelectedGlyph is not null;
            socket.HighlightRadius = pick.HighlightRadius;
        }
        foreach (var cell in Cells)
            cell.IsPurchased = newPurchased.Contains(cell.Cell);
        ClearDiffMarks(); // same nodes, renumbered slots — a diff would be pure noise
        RefreshBuildSummary();
        SetStatus("Boards renumbered to the path's attach order — slot numbers and parent links now " +
                  "reflect the crossings the path actually uses.");
    }
}
