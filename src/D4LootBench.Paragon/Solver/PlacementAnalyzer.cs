using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>A machine-applicable edit behind a suggestion, so the UI can preview it.</summary>
public abstract record PlacementChange;

/// <summary>Set the board in <see cref="Slot"/> to <see cref="RotationSteps"/> quarter-turns.</summary>
public sealed record RotationChange(int Slot, int RotationSteps) : PlacementChange;

public sealed record GlyphMove(CellRef FromSocket, CellRef ToSocket);

/// <summary>Re-socket glyphs as one atomic permutation (moves may swap sockets pairwise).</summary>
public sealed record GlyphReassignment(IReadOnlyList<GlyphMove> Moves) : PlacementChange;

public sealed record PlacementSuggestion(string Description, int PointsSaved, PlacementChange? Change = null);

/// <summary>
/// Answers "could this be laid out better?": re-solves the plan under alternate board rotations,
/// and evaluates whether the socketed glyphs would see more of their stat at a different socket.
/// Rotation analysis is exhaustive per board (one board varied at a time); it does not explore
/// re-attaching boards to different gates.
/// </summary>
public static class PlacementAnalyzer
{
    public static IReadOnlyList<PlacementSuggestion> SuggestRotations(
        ParagonLayout layout, PlanRequest request, PlanResult baseline)
    {
        var suggestions = new List<PlacementSuggestion>();
        int baselineMet = baseline.GlyphOutcomes.Count(o => o.Met);

        for (int slot = 1; slot < layout.Boards.Count; slot++)
        {
            var placed = layout.Boards[slot];
            for (int rotation = 0; rotation < 4; rotation++)
            {
                if (rotation == placed.RotationSteps)
                    continue;

                var boards = layout.Boards.ToList();
                boards[slot] = new PlacedBoard
                {
                    Board = placed.Board,
                    ParentSlot = placed.ParentSlot,
                    AttachEdge = placed.AttachEdge,
                    RotationSteps = rotation,
                };

                ComposedGraph variantGraph;
                try
                {
                    variantGraph = ComposedGraph.Build(new ParagonLayout(boards));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    continue; // rotation leaves no gate facing the parent — not placeable
                }

                // Cells on the rotated board keep their identity but move: apply the rotation delta.
                int delta = (rotation - placed.RotationSteps + 4) & 3;
                int width = placed.Board.Width;
                CellRef Remap(CellRef cell)
                {
                    if (cell.BoardSlot != slot)
                        return cell;
                    var (x, y) = ParagonLayout.Rotate(cell.X, cell.Y, delta, width);
                    return cell with { X = x, Y = y };
                }

                var variantRequest = new PlanRequest
                {
                    Targets = request.Targets.Select(Remap).ToList(),
                    NodeRules = request.NodeRules,
                    AvoidCells = request.AvoidCells.Select(Remap).ToList(),
                    ExcludeCells = request.ExcludeCells.Select(Remap).ToList(),
                    GlyphGoals = request.GlyphGoals.Select(g => g with { Socket = Remap(g.Socket) }).ToList(),
                };

                var variant = PlanSolver.Solve(variantGraph, variantRequest);
                if (!variant.Success)
                    continue;

                int saved = baseline.PointsSpent - variant.PointsSpent;
                int variantMet = variant.GlyphOutcomes.Count(o => o.Met);
                if (saved <= 0 && variantMet <= baselineMet)
                    continue;

                string boardName = placed.Board.Name ?? placed.Board.InternalName;
                string gain = saved > 0
                    ? $"{baseline.PointsSpent} → {variant.PointsSpent} points"
                    : $"same points, activates {variantMet} glyph(s) instead of {baselineMet}";
                suggestions.Add(new PlacementSuggestion(
                    $"Rotate slot {slot} ({boardName}) to {rotation * 90}°: {gain}.", Math.Max(0, saved),
                    new RotationChange(slot, rotation)));
            }
        }

        return suggestions.OrderByDescending(s => s.PointsSaved).ToList();
    }

    /// <summary>
    /// With the purchase set fixed, finds the glyph→socket assignment that first maximizes the
    /// number of activated glyphs, then the total stat seen — and describes the moves if that
    /// beats where the glyphs currently sit.
    /// </summary>
    public static IReadOnlyList<PlacementSuggestion> SuggestGlyphPlacements(
        ComposedGraph graph,
        ParagonLayout layout,
        IReadOnlyCollection<CellRef> purchased,
        IReadOnlyList<GlyphGoal> goals)
    {
        if (goals.Count == 0)
            return [];

        var sockets = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .Select(v => v.Cell)
            .ToList();
        if (sockets.Count < 2)
            return [];

        var purchasedSet = purchased as ISet<CellRef> ?? purchased.ToHashSet();
        // totals[g][s]: how much of goal g's stat the purchase set provides around socket s.
        var totals = new double[goals.Count][];
        for (int g = 0; g < goals.Count; g++)
        {
            totals[g] = new double[sockets.Count];
            for (int s = 0; s < sockets.Count; s++)
                totals[g][s] = StatInRadius(graph, sockets[s], purchasedSet, goals[g].SourceAttribute, goals[g].Radius);
        }

        // Score of an assignment: activated glyph count first, then total stat.
        (int Met, double Total) Score(int[] assignment)
        {
            int met = 0;
            double total = 0;
            for (int g = 0; g < goals.Count; g++)
            {
                double seen = totals[g][assignment[g]];
                total += seen;
                if (seen >= goals[g].RequiredTotal - 1e-9)
                    met++;
            }
            return (met, total);
        }

        var current = new int[goals.Count];
        for (int g = 0; g < goals.Count; g++)
        {
            current[g] = sockets.IndexOf(goals[g].Socket);
            if (current[g] < 0)
                return []; // a goal references a socket not in this layout — nothing sane to compare
        }

        int[]? best = null;
        (int Met, double Total) bestScore = default;
        var assignment = new int[goals.Count];
        var used = new bool[sockets.Count];
        void Search(int g)
        {
            if (g == goals.Count)
            {
                var score = Score(assignment);
                if (best is null || score.Met > bestScore.Met || (score.Met == bestScore.Met && score.Total > bestScore.Total))
                {
                    best = (int[])assignment.Clone();
                    bestScore = score;
                }
                return;
            }
            for (int s = 0; s < sockets.Count; s++)
            {
                if (used[s])
                    continue;
                used[s] = true;
                assignment[g] = s;
                Search(g + 1);
                used[s] = false;
            }
        }
        Search(0);

        var currentScore = Score(current);
        if (best is null || (bestScore.Met <= currentScore.Met && bestScore.Total <= currentScore.Total + 1e-9))
            return [];

        // One atomic suggestion — the moves may permute sockets, so they apply together.
        var moves = new List<GlyphMove>();
        var descriptions = new List<string>();
        for (int g = 0; g < goals.Count; g++)
        {
            if (best[g] == current[g])
                continue;
            var goal = goals[g];
            string glyph = goal.GlyphName ?? "glyph";
            string stat = ParagonDisplay.FormatAttributeName(goal.SourceAttribute);
            string from = BoardName(layout, sockets[current[g]].BoardSlot);
            string to = BoardName(layout, sockets[best[g]].BoardSlot);
            moves.Add(new GlyphMove(sockets[current[g]], sockets[best[g]]));
            descriptions.Add($"Move {glyph} from {from} to the socket on {to}: " +
                             $"{totals[g][best[g]]:0} {stat} in radius instead of {totals[g][current[g]]:0}.");
        }
        if (moves.Count == 0)
            return [];
        return [new PlacementSuggestion(string.Join(" ", descriptions), 0, new GlyphReassignment(moves))];
    }

    private static double StatInRadius(
        ComposedGraph graph, CellRef socket, ISet<CellRef> purchased, string attribute, int radius)
    {
        double total = 0;
        foreach (var vertex in graph.Vertices)
        {
            var cell = vertex.Cell;
            if (cell.BoardSlot != socket.BoardSlot || cell == socket || !purchased.Contains(cell))
                continue;
            if (Math.Abs(cell.X - socket.X) + Math.Abs(cell.Y - socket.Y) > radius)
                continue;
            total += vertex.Node.Attributes
                .Where(a => !a.IsThresholdBonus && a.Attribute == attribute && a.Value is double)
                .Sum(a => a.Value!.Value);
        }
        return total;
    }

    private static string BoardName(ParagonLayout layout, int slot)
    {
        var board = layout.Boards[slot].Board;
        return $"slot {slot} ({board.Name ?? board.InternalName})";
    }
}
