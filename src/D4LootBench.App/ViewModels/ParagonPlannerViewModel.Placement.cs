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
    // ── Placement analysis: rotations, swaps, re-attachments, glyph moves ─

    /// <summary>
    /// Re-solves the current plan under every alternate rotation of each attached board, and
    /// checks whether the socketed glyphs would see more of their stat elsewhere.
    /// </summary>
    [RelayCommand]
    private Task AnalyzePlacement() => RunGuardedAsync("Placement analysis", AnalyzePlacementCoreAsync);

    private async Task AnalyzePlacementCoreAsync()
    {
        if (_graph is null || _layout is null)
            return;
        var request = BuildPlanRequest();
        if (request.Targets.Count == 0 && request.GlyphGoals.Count == 0)
        {
            SetStatus("Mark targets (or enable a glyph activation goal) before analyzing placement.", error: true);
            return;
        }

        var layout = _layout;
        var graph = _graph;
        // Swap candidates: every board of the class not already in the layout — a superset of any
        // pool the user planned from, so good alternates surface even after the dialog is gone.
        var placedNames = _placedBoards.Select(b => b.Board.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareBoards = ParagonDatabase.BoardsForClass(SelectedClass)
            .Where(b => b.BoardIndex != 0 && !placedNames.Contains(b.InternalName))
            .ToList();
        // Candidates are judged by the FINISHED build: solve + full spend of the point pool
        // with the current Optimize settings, compared on glyphs → thresholds → focus value.
        // Socketed glyphs ride along by board slot so node buffs count per candidate.
        var socketedGlyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new PipelineGlyph(s.Socket.BoardSlot, s.SelectedGlyph!, s.Level))
            .ToList();
        var pipeline = new PlacementPipeline(
            TotalPoints,
            CurrentMaximizeFocus(),
            new ThresholdContext(ParagonDatabase.Data, SelectedClass, SheetStatOffsets()),
            socketedGlyphs)
        {
            Legendary = CurrentLegendaryContext(),
        };
        var (busy, cancellationToken) = BeginCancellableBusy();
        using var _ = busy;
        SetStatus("Analyzing rotations, re-attachments, glyph placements, and board swaps at full point spend…");
        var (baseline, suggestions) = await Task.Run(() =>
        {
            var solved = PlanSolver.Solve(graph, request);
            if (!solved.Success)
                return (solved, (IReadOnlyList<PlacementSuggestion>)Array.Empty<PlacementSuggestion>());
            // One shared baseline evaluation; the four suggestion families run in parallel.
            return (solved, PlacementAnalyzer.SuggestAll(layout, request, solved, spareBoards, pipeline, cancellationToken));
        }, cancellationToken);

        if (!baseline.Success)
        {
            SetStatus(baseline.Error ?? "Solve failed.", error: true);
            return;
        }

        Suggestions.Clear();
        foreach (var suggestion in suggestions)
            Suggestions.Add(suggestion);

        SolveDetails = suggestions.Count == 0
            ? $"Placement analysis: no better board rotation or glyph socket found (baseline {baseline.PointsSpent} points)."
            : "Placement suggestions are listed in the panel — Apply one to see the change on the board, " +
              "then Revert to flip back and compare.";
        SetStatus($"Placement analysis complete — {suggestions.Count} suggestion(s).");
    }

    /// <summary>
    /// Searches SEQUENCES of placement changes — rotations, board swaps, leaf re-attachments,
    /// glyph moves, and glyph substitutions — and presents the top final setups, so the user
    /// jumps straight to an end state instead of applying one suggestion at a time. Each option
    /// applies all its steps as one composite; Revert restores the current setup.
    /// </summary>
    [RelayCommand]
    private Task DeepAnalyzePlacement() =>
        RunGuardedAsync("Deep placement search", DeepAnalyzePlacementCoreAsync);

    private async Task DeepAnalyzePlacementCoreAsync()
    {
        if (_graph is null || _layout is null)
            return;
        var request = BuildPlanRequest();
        if (request.Targets.Count == 0 && request.GlyphGoals.Count == 0)
        {
            SetStatus("Mark targets (or enable a glyph activation goal) before analyzing placement.", error: true);
            return;
        }

        var layout = _layout;
        var placedNames = _placedBoards.Select(b => b.Board.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareBoards = ParagonDatabase.BoardsForClass(SelectedClass)
            .Where(b => b.BoardIndex != 0 && !placedNames.Contains(b.InternalName))
            .ToList();
        var socketedGlyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new PipelineGlyph(s.Socket.BoardSlot, s.SelectedGlyph!, s.Level))
            .ToList();
        // Substitution candidates: every class glyph not already socketed — recommendations are
        // deliberately independent of what the player has leveled (each step says which level
        // the numbers assume).
        var socketedNames = socketedGlyphs.Select(g => g.Glyph.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareGlyphs = ParagonDatabase.GlyphsForClass(SelectedClass)
            .Where(g => !socketedNames.Contains(g.InternalName))
            .ToList();
        var pipeline = new PlacementPipeline(
            TotalPoints,
            CurrentMaximizeFocus(),
            new ThresholdContext(ParagonDatabase.Data, SelectedClass, SheetStatOffsets()),
            socketedGlyphs)
        {
            Legendary = CurrentLegendaryContext(),
        };

        // Locks from the Glyphs tab: required boards never swap out, required glyphs are never
        // substituted away (both may still rotate/re-attach/re-socket — they stay in the build).
        var lockedBoards = GlyphSockets.Where(s => s.IsBoardLocked)
            .Select(s => s.Socket.BoardSlot).ToHashSet();
        var lockedGlyphs = GlyphSockets.Where(s => s.IsGlyphLocked && s.SelectedGlyph is not null)
            .Select(s => s.Socket.BoardSlot).ToHashSet();

        var (busy, cancellationToken) = BeginCancellableBusy();
        using var _ = busy;
        SetStatus("Deep placement search — four passes (current glyph levels; all glyphs at level 51; " +
                  "all at level 100; keep current glyphs) over sequences of rotations, swaps, " +
                  "re-attachments, glyph moves, and substitutions at full point spend. " +
                  "This can take a minute…");
        var passes = await Task.Run(() =>
        {
            // One cache across the passes: bare solves coincide whenever the pinned level's
            // radius matches, and keep-current-glyphs re-reaches pass 1's states.
            var cache = new PlacementSearchCache();
            IReadOnlyList<PlacementPlan> Run(PlanRequest req, PlacementPipeline pipe,
                IReadOnlyList<ParagonGlyphDef> glyphPool) =>
                PlacementSearch.FindPlans(layout, req, pipe, spareBoards, glyphPool,
                    lockedBoardSlots: lockedBoards, lockedGlyphSlots: lockedGlyphs,
                    cancellationToken: cancellationToken, cache: cache);

            // Level-pinned passes (51 = radius 5 + legendary rank, 100 = maxed) normalize every
            // glyph — including ones leveled beyond the pin — so placements are judged
            // independent of current glyph investment.
            var (request51, pipeline51) = PlacementSearch.AtGlyphLevel(request, pipeline, 51);
            var (request100, pipeline100) = PlacementSearch.AtGlyphLevel(request, pipeline, 100);
            return new (string Label, IReadOnlyList<PlacementPlan> Plans)[]
            {
                ("at current glyph levels", Run(request, pipeline, spareGlyphs)),
                ("all glyphs at level 51", Run(request51, pipeline51, spareGlyphs)),
                ("all glyphs at level 100", Run(request100, pipeline100, spareGlyphs)),
                ("keep current glyphs", Run(request, pipeline, [])),
            };
        }, cancellationToken);

        Suggestions.Clear();
        int total = 0;
        foreach (var (label, plans) in passes)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                Suggestions.Add(new PlacementSuggestion(
                    plan.Describe(i + 1, plans.Count, label),
                    Math.Max(0, plan.Baseline.PointsUsed - plan.Result.PointsUsed),
                    plan.Change));
                total++;
            }
        }

        SolveDetails = total == 0
            ? "Deep placement search: no sequence of changes beats the current setup at full spend " +
              "in any pass (current levels, level 51, level 100, keep-glyphs)."
            : "The top final setups are listed per pass — Apply one to jump straight to it " +
              "(all steps at once), then Revert to restore the current setup and compare. " +
              "Level-pinned options assume that level for every glyph (applying does not change " +
              "glyph levels); locked boards/glyphs from the Glyphs tab were respected.";
        SetStatus($"Deep placement search complete — {total} option(s) across four passes.");
    }

    /// <summary>
    /// Boards-tab quick rotate: turns the board to its next VALID quarter-turn (one that still
    /// leaves gates facing the parent and any children), remapping targets/constraints/goals
    /// through the same machinery placement suggestions use, then re-solves.
    /// </summary>
    [RelayCommand]
    private Task RotateBoard(BoardRowViewModel row) =>
        RunGuardedAsync("Rotate", () => RotateBoardCoreAsync(row));

    private async Task RotateBoardCoreAsync(BoardRowViewModel row)
    {
        if (row.Slot == 0 || _layout is null)
            return;
        var placed = _placedBoards[row.Slot];
        int? nextValid = null;
        for (int delta = 1; delta < 4 && nextValid is null; delta++)
        {
            int rotation = (placed.RotationSteps + delta) & 3;
            var boards = _placedBoards.ToList();
            boards[row.Slot] = new PlacedBoard
            {
                Board = placed.Board,
                ParentSlot = placed.ParentSlot,
                AttachEdge = placed.AttachEdge,
                RotationSteps = rotation,
            };
            try
            {
                ComposedGraph.Build(new ParagonLayout(boards));
                nextValid = rotation;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
        if (nextValid is not int target)
        {
            SetStatus("No other rotation of this board keeps its gates aligned here.", error: true);
            return;
        }

        RecordUndo();
        var purchasesBefore = CurrentPurchases();
        ApplyChange(new RotationChange(row.Slot, target));
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
            await SolveAsync();
        MarkPurchaseDiff(purchasesBefore);
        SetStatus($"Rotated slot {row.Slot} ({row.Name}) to {target * 90}°.");
    }

    // ── Applying and reverting placement suggestions ─────────────────────

    private PlannerState? _revertState;

    private sealed record PlannerState(
        List<PlacedBoard> Boards,
        List<CellRef> Targets,
        List<(CellRef Cell, CellConstraint Constraint)> Constraints,
        List<(int Slot, string? Glyph, int Level, double RequiredStat, bool EnsureActive)> Glyphs);

    private PlannerState CaptureState() => new(
        [.. _placedBoards],
        [.. _targets],
        Cells.Where(c => c.Constraint != CellConstraint.None).Select(c => (c.Cell, c.Constraint)).ToList(),
        GlyphSockets.Select(s =>
            (s.Socket.BoardSlot, s.SelectedGlyph?.InternalName, s.Level, s.RequiredStat, s.EnsureActive)).ToList());

    private void RestoreCells(
        IEnumerable<CellRef> targets, IEnumerable<(CellRef Cell, CellConstraint Constraint)> constraints)
    {
        var cellByRef = Cells.ToDictionary(c => c.Cell);
        _targets.Clear();
        foreach (var target in targets)
        {
            if (cellByRef.TryGetValue(target, out var cell) && !cell.IsStart)
            {
                cell.IsTarget = true;
                _targets.Add(target);
            }
        }
        foreach (var (cellRef, constraint) in constraints)
        {
            if (cellByRef.TryGetValue(cellRef, out var cell))
                cell.Constraint = constraint;
        }
    }

    private void RestoreGlyphs(
        IEnumerable<(int Slot, string? Glyph, int Level, double RequiredStat, bool EnsureActive)> glyphs)
    {
        foreach (var saved in glyphs)
        {
            var socket = GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == saved.Slot);
            if (socket is null)
                continue;
            socket.SelectedGlyph = saved.Glyph is null
                ? null
                : socket.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.InternalName, saved.Glyph, StringComparison.OrdinalIgnoreCase));
            socket.Level = saved.Level;
            socket.RequiredStat = saved.RequiredStat;
            socket.EnsureActive = saved.EnsureActive && socket.SelectedGlyph is not null;
        }
    }

    /// <summary>Applies a suggestion to the live board and re-solves, so the difference is visible.</summary>
    [RelayCommand]
    private Task ApplySuggestion(PlacementSuggestion suggestion) =>
        RunGuardedAsync("Applying the suggestion", () => ApplySuggestionCoreAsync(suggestion));

    private async Task ApplySuggestionCoreAsync(PlacementSuggestion suggestion)
    {
        if (_layout is null || suggestion.Change is null)
            return;
        RecordUndo();
        var before = CaptureState();
        var purchasesBefore = CurrentPurchases();
        int pointsBefore = purchasesBefore.Count;

        ApplyChange(suggestion.Change);

        _revertState = before;
        RevertPlacementCommand.NotifyCanExecuteChanged();
        await SolveAsync();
        MarkPurchaseDiff(purchasesBefore);
        int pointsAfter = Cells.Count(c => c.IsPurchased);
        SetStatus($"Applied — path re-solved at {pointsAfter} points (was {pointsBefore}). " +
                  "Revert flips back to compare.", error: StatusIsError);
    }

    /// <summary>Live per-cell constraints — captured per change step, so a composite of several
    /// board changes remaps each step's result instead of resetting to the pre-composite state.</summary>
    private List<(CellRef Cell, CellConstraint Constraint)> CurrentConstraints() =>
        Cells.Where(c => c.Constraint != CellConstraint.None).Select(c => (c.Cell, c.Constraint)).ToList();

    private void ApplyChange(PlacementChange change)
    {
        switch (change)
        {
            case CompositeChange composite:
            {
                foreach (var child in composite.Changes)
                    ApplyChange(child);
                break;
            }
            case ReattachChange reattach:
            {
                var placed = _placedBoards[reattach.Slot];
                // The board keeps its cells; only the rotation delta moves them.
                var remap = LayoutRemap.ForRotation(reattach.Slot, placed, reattach.RotationSteps);
                var targets = _targets.Select(remap).ToList();
                var constraints = CurrentConstraints().Select(c => (remap(c.Cell), c.Constraint)).ToList();
                _placedBoards[reattach.Slot] = new PlacedBoard
                {
                    Board = placed.Board,
                    ParentSlot = reattach.ParentSlot,
                    AttachEdge = reattach.Edge,
                    RotationSteps = reattach.RotationSteps,
                };
                RebuildLayout(); // keeps glyph picks per board slot
                RestoreCells(targets, constraints);
                break;
            }
            case GlyphSlotReassignment slotMoves:
            {
                var socketBySlot = GlyphSockets.ToDictionary(s => s.Socket.BoardSlot);
                var picks = slotMoves.Moves
                    .Where(m => socketBySlot.ContainsKey(m.FromSlot) && socketBySlot.ContainsKey(m.ToSlot))
                    .Select(m => (Target: socketBySlot[m.ToSlot],
                                  socketBySlot[m.FromSlot].SelectedGlyph,
                                  socketBySlot[m.FromSlot].Level,
                                  socketBySlot[m.FromSlot].RequiredStat,
                                  socketBySlot[m.FromSlot].EnsureActive))
                    .ToList();
                foreach (var move in slotMoves.Moves)
                {
                    if (socketBySlot.TryGetValue(move.FromSlot, out var from))
                    {
                        from.SelectedGlyph = null;
                        from.EnsureActive = false;
                    }
                }
                foreach (var (target, glyph, level, requiredStat, ensureActive) in picks)
                {
                    target.SelectedGlyph = glyph;
                    target.Level = level;
                    target.RequiredStat = requiredStat;
                    target.EnsureActive = ensureActive && glyph is not null;
                }
                break;
            }
            case RotationChange rotation:
            {
                var placed = _placedBoards[rotation.Slot];
                var remap = LayoutRemap.ForRotation(rotation.Slot, placed, rotation.RotationSteps);
                var targets = _targets.Select(remap).ToList();
                var constraints = CurrentConstraints().Select(c => (remap(c.Cell), c.Constraint)).ToList();
                _placedBoards[rotation.Slot] = new PlacedBoard
                {
                    Board = placed.Board,
                    ParentSlot = placed.ParentSlot,
                    AttachEdge = placed.AttachEdge,
                    RotationSteps = rotation.RotationSteps,
                };
                RebuildLayout(); // keeps glyph picks per board slot
                RestoreCells(targets, constraints);
                break;
            }
            case BoardSwapChange swap:
            {
                var placed = _placedBoards[swap.Slot];
                // Targets and per-cell constraints on the outgoing board don't exist on the new
                // one; the new board's legendary node(s) become the slot's targets instead.
                var keepTargets = _targets.Where(t => t.BoardSlot != swap.Slot).ToList();
                var constraints = CurrentConstraints().Where(c => c.Cell.BoardSlot != swap.Slot).ToList();
                _placedBoards[swap.Slot] = new PlacedBoard
                {
                    Board = swap.NewBoard,
                    ParentSlot = placed.ParentSlot,
                    AttachEdge = placed.AttachEdge,
                    RotationSteps = swap.RotationSteps,
                };
                RebuildLayout(); // keeps the slot's glyph pick, so the glyph lands on the new board
                var newTargets = Cells
                    .Where(c => c.Cell.BoardSlot == swap.Slot && c.Node.Kind == ParagonNodeKind.Legendary)
                    .Select(c => c.Cell);
                RestoreCells(keepTargets.Concat(newTargets), constraints);
                break;
            }
            case GlyphSwapChange glyphSwap:
            {
                // The search judged the new glyph at the socket's current level, so level,
                // required stat, and the activation flag stay as they are.
                var socket = GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == glyphSwap.Slot);
                if (socket is not null)
                {
                    socket.SelectedGlyph = socket.Glyphs.FirstOrDefault(g =>
                        string.Equals(g.InternalName, glyphSwap.NewGlyph.InternalName,
                            StringComparison.OrdinalIgnoreCase));
                    socket.EnsureActive = socket.EnsureActive && socket.SelectedGlyph is not null;
                }
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertPlacement))]
    private Task RevertPlacement() => RunGuardedAsync("Revert", RevertPlacementCoreAsync);

    private async Task RevertPlacementCoreAsync()
    {
        if (_revertState is not PlannerState state)
            return;
        RecordUndo();
        var purchasesBefore = CurrentPurchases();
        _placedBoards.Clear();
        _placedBoards.AddRange(state.Boards);
        RebuildLayout(); // clears _revertState — reverting is one-shot
        RestoreCells(state.Targets, state.Constraints);
        RestoreGlyphs(state.Glyphs);
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
            await SolveAsync();
        MarkPurchaseDiff(purchasesBefore);
        SetStatus("Reverted to the layout before the applied suggestion.");
    }

    private bool CanRevertPlacement() => _revertState is not null;
}
