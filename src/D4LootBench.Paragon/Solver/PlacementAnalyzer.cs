using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>A machine-applicable edit behind a suggestion, so the UI can preview it.</summary>
public abstract record PlacementChange;

/// <summary>Set the board in <see cref="Slot"/> to <see cref="RotationSteps"/> quarter-turns.</summary>
public sealed record RotationChange(int Slot, int RotationSteps) : PlacementChange;

public sealed record GlyphMove(CellRef FromSocket, CellRef ToSocket);

/// <summary>Re-socket glyphs as one atomic permutation (moves may swap sockets pairwise).</summary>
public sealed record GlyphReassignment(IReadOnlyList<GlyphMove> Moves) : PlacementChange;

/// <summary>
/// Replace the board in <see cref="Slot"/> with <see cref="NewBoard"/> at the given rotation.
/// Targets on the old board are retargeted to the new board's legendary node(s) on apply.
/// </summary>
public sealed record BoardSwapChange(int Slot, ParagonBoardDef NewBoard, int RotationSteps) : PlacementChange;

/// <summary>Move the board in <see cref="Slot"/> to a different parent gate (and rotation).</summary>
public sealed record ReattachChange(int Slot, int ParentSlot, BoardEdge Edge, int RotationSteps) : PlacementChange;

/// <summary>A glyph move expressed by board slot — stable across rotations and swaps.</summary>
public sealed record GlyphSlotMove(int FromSlot, int ToSlot);

/// <summary>Re-socket glyphs between board slots as one atomic permutation.</summary>
public sealed record GlyphSlotReassignment(IReadOnlyList<GlyphSlotMove> Moves) : PlacementChange;

/// <summary>Several changes applied together (e.g. a rotation plus the glyph moves it enables).</summary>
public sealed record CompositeChange(IReadOnlyList<PlacementChange> Changes) : PlacementChange;

public sealed record PlacementSuggestion(string Description, int PointsSaved, PlacementChange? Change = null);

/// <summary>A socketed glyph carried into pipeline evaluation by board slot (sockets move with
/// rotations and swaps, so the slot is the stable key).</summary>
public sealed record PipelineGlyph(int BoardSlot, ParagonGlyphDef Glyph, int Level);

/// <summary>
/// Everything needed to judge a placement candidate by the FINISHED build instead of the bare
/// solve: the point pool and the maximizer settings (focus stats + weights, rare preference,
/// threshold activation, sheet stats). When supplied, each candidate is solved, fully spent,
/// and compared on final activated glyphs → thresholds met → focused stat value → points.
/// <see cref="SocketedGlyphs"/> lets "+X% to [rarity] nodes in radius" buffs count per
/// candidate — each variant's own purchase set decides which sockets are live.
/// </summary>
public sealed record PlacementPipeline(
    int TotalPoints, MaximizeFocus Focus, ThresholdContext? Thresholds,
    IReadOnlyList<PipelineGlyph>? SocketedGlyphs = null)
{
    /// <summary>Off pins glyphs to their current sockets — used to measure what re-socketing
    /// alone is worth on the current layout.</summary>
    public bool OptimizeGlyphAssignment { get; init; } = true;
}

/// <summary>A candidate's end state after solve + full spend. <see cref="GlyphMoves"/> lists the
/// glyph re-socketing the evaluation assumed (the assignment search may move glyphs to whichever
/// sockets activate the most goals on this candidate's boards).</summary>
public sealed record PipelineResult(int GlyphsActive, int ThresholdsMet, int PointsUsed, double FocusScore)
{
    public IReadOnlyList<GlyphSlotMove> GlyphMoves { get; init; } = [];

    /// <summary>Total nodes over every Limit/Minimal cap in the finished build — a candidate may
    /// never be suggested when it breaks limits harder than the baseline does.</summary>
    public int LimitBreaks { get; init; }

    /// <summary>Effective stat totals of the finished build (<see cref="BuildStats"/>, threshold
    /// bonuses and glyph node buffs included); null when no ThresholdContext was supplied.</summary>
    public IReadOnlyDictionary<string, double>? StatTotals { get; init; }

    /// <summary>Worth suggesting over the baseline: never at the cost of a Limit rule; then more
    /// glyphs, more thresholds, clearly more focused stat value (>2%, to keep greedy-spend noise
    /// from spamming suggestions), or the same build for fewer points.</summary>
    public bool BeatsForSuggestion(PipelineResult baseline)
    {
        if (LimitBreaks != baseline.LimitBreaks)
            return LimitBreaks < baseline.LimitBreaks;
        return GlyphsActive > baseline.GlyphsActive
            || (GlyphsActive == baseline.GlyphsActive
                && (ThresholdsMet > baseline.ThresholdsMet
                    || (ThresholdsMet == baseline.ThresholdsMet
                        && (FocusScore > baseline.FocusScore * 1.02 + 1e-9
                            || (FocusScore >= baseline.FocusScore - 1e-9 && PointsUsed < baseline.PointsUsed)))));
    }
}

/// <summary>
/// Answers "could this be laid out better?": re-solves the plan under alternate board rotations,
/// evaluates whether the socketed glyphs would see more of their stat at a different socket, and
/// checks whether an unused board would beat one in the layout outright. Rotation analysis is
/// exhaustive per board (one board varied at a time); it does not explore re-attaching boards to
/// different gates.
/// </summary>
public static class PlacementAnalyzer
{
    public static IReadOnlyList<PlacementSuggestion> SuggestRotations(
        ParagonLayout layout, PlanRequest request, PlanResult baseline, PlacementPipeline? pipeline = null)
    {
        var suggestions = new List<PlacementSuggestion>();
        int baselineMet = baseline.GlyphOutcomes.Count(o => o.Met);
        // Full-pipeline mode: candidates are judged by the finished build (solve + full spend
        // with the user's maximizer settings), not by the bare solve.
        var baselineEval = pipeline is null
            ? null
            : EvaluatePipeline(ComposedGraph.Build(layout), request, pipeline, baseline);

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

                string boardName = placed.Board.Name ?? placed.Board.InternalName;

                if (baselineEval is not null)
                {
                    var eval = EvaluatePipeline(variantGraph, variantRequest, pipeline!, variant);
                    if (eval is null || !eval.BeatsForSuggestion(baselineEval))
                        continue;
                    var (text, change) = WithGlyphMoves(
                        $"Rotate slot {slot} ({boardName}) to {rotation * 90}°: " +
                        $"{DescribeGain(eval, baselineEval, request.GlyphGoals.Count)}",
                        new RotationChange(slot, rotation), eval, pipeline!);
                    suggestions.Add(new PlacementSuggestion(
                        text + ".", Math.Max(0, baselineEval.PointsUsed - eval.PointsUsed), change));
                    continue;
                }

                int saved = baseline.PointsSpent - variant.PointsSpent;
                int variantMet = variant.GlyphOutcomes.Count(o => o.Met);
                if (saved <= 0 && variantMet <= baselineMet)
                    continue;

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

    /// <summary>
    /// For each attached board, tests whether replacing it with one of the strongest unused
    /// candidates (ranked by attainable glyph stat around the candidate's socket) activates more
    /// glyph goals or saves points. Each swap is solved for real under every valid rotation, with
    /// the old board's targets retargeted to the candidate's legendary node(s). Only runs when
    /// glyph goals exist — without them, swapping boards also swaps the objective, and point
    /// totals stop being comparable.
    /// </summary>
    public static IReadOnlyList<PlacementSuggestion> SuggestBoardSwaps(
        ParagonLayout layout, PlanRequest request, PlanResult baseline,
        IReadOnlyList<ParagonBoardDef> candidates, int maxCandidatesPerSlot = 3, int maxSuggestions = 3,
        PlacementPipeline? pipeline = null)
    {
        if (request.GlyphGoals.Count == 0)
            return [];

        var inLayout = layout.Boards.Select(b => b.Board.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = candidates
            .DistinctBy(c => c.InternalName)
            .Where(c => !inLayout.Contains(c.InternalName))
            .ToList();
        if (usable.Count == 0)
            return [];

        int baselineMet = baseline.GlyphOutcomes.Count(o => o.Met);
        var baselineEval = pipeline is null
            ? null
            : EvaluatePipeline(ComposedGraph.Build(layout), request, pipeline, baseline);
        var suggestions = new List<(PlacementSuggestion Suggestion, int MetGain)>();

        for (int slot = 1; slot < layout.Boards.Count; slot++)
        {
            var placed = layout.Boards[slot];

            // Rank candidates by what this slot's glyph (or failing that, any goal) could see there.
            var slotGoals = request.GlyphGoals.Where(g => g.Socket.BoardSlot == slot).ToList();
            var rankGoals = slotGoals.Count > 0 ? slotGoals : request.GlyphGoals;
            var ranked = usable
                .Select(c => (Board: c, Fit: rankGoals.Max(g =>
                    AttainableByAttribute(c, g.SourceAttribute, g.Radius))))
                .Where(c => c.Fit > 0)
                .OrderByDescending(c => c.Fit)
                .Take(maxCandidatesPerSlot);

            foreach (var (candidate, _) in ranked)
            {
                (PlanResult Plan, int Rotation, int Met, ComposedGraph Graph, PlanRequest Request)? bestVariant = null;
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    var boards = layout.Boards.ToList();
                    boards[slot] = new PlacedBoard
                    {
                        Board = candidate,
                        ParentSlot = placed.ParentSlot,
                        AttachEdge = placed.AttachEdge,
                        RotationSteps = rotation,
                    };

                    ComposedGraph graph;
                    try
                    {
                        graph = ComposedGraph.Build(new ParagonLayout(boards));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        continue; // this rotation offers no gate toward the parent (or a child)
                    }

                    var newLegendaries = graph.Vertices
                        .Where(v => v.Cell.BoardSlot == slot && v.Node.Kind == ParagonNodeKind.Legendary)
                        .Select(v => v.Cell)
                        .ToList();
                    CellRef? newSocket = graph.Vertices
                        .Where(v => v.Cell.BoardSlot == slot && v.Node.Kind == ParagonNodeKind.GlyphSocket)
                        .Select(v => (CellRef?)v.Cell)
                        .FirstOrDefault();

                    var variantRequest = new PlanRequest
                    {
                        Targets = request.Targets.Where(t => t.BoardSlot != slot)
                            .Concat(newLegendaries).Distinct().ToList(),
                        NodeRules = request.NodeRules,
                        AvoidCells = request.AvoidCells.Where(c => c.BoardSlot != slot).ToList(),
                        ExcludeCells = request.ExcludeCells.Where(c => c.BoardSlot != slot).ToList(),
                        GlyphGoals = request.GlyphGoals
                            .Where(g => g.Socket.BoardSlot != slot || newSocket is not null)
                            .Select(g => g.Socket.BoardSlot == slot ? g with { Socket = newSocket!.Value } : g)
                            .ToList(),
                    };

                    var variant = PlanSolver.Solve(graph, variantRequest);
                    if (!variant.Success)
                        continue;
                    int met = variant.GlyphOutcomes.Count(o => o.Met);
                    if (bestVariant is null || met > bestVariant.Value.Met
                        || (met == bestVariant.Value.Met && variant.PointsSpent < bestVariant.Value.Plan.PointsSpent))
                        bestVariant = (variant, rotation, met, graph, variantRequest);
                }

                if (bestVariant is not var (plan, steps, variantMet, bestGraph, bestRequest))
                    continue;

                string oldName = placed.Board.Name ?? placed.Board.InternalName;
                string newName = candidate.Name ?? candidate.InternalName;

                if (baselineEval is not null)
                {
                    var eval = EvaluatePipeline(bestGraph, bestRequest, pipeline!, plan);
                    if (eval is null || !eval.BeatsForSuggestion(baselineEval))
                        continue;
                    var (text, change) = WithGlyphMoves(
                        $"Swap slot {slot} ({oldName}) for the unused board {newName} at {steps * 90}°: " +
                        $"{DescribeGain(eval, baselineEval, request.GlyphGoals.Count)} " +
                        $"(the slot's targets move to {newName}'s legendary)",
                        new BoardSwapChange(slot, candidate, steps), eval, pipeline!);
                    suggestions.Add((new PlacementSuggestion(
                        text + ".", Math.Max(0, baselineEval.PointsUsed - eval.PointsUsed), change),
                        eval.GlyphsActive - baselineEval.GlyphsActive));
                    continue;
                }

                int saved = baseline.PointsSpent - plan.PointsSpent;
                if (variantMet <= baselineMet && (variantMet < baselineMet || saved <= 0))
                    continue;

                string gain = variantMet > baselineMet
                    ? $"activates {variantMet} glyph goal(s) instead of {baselineMet}, " +
                      $"{baseline.PointsSpent} → {plan.PointsSpent} points"
                    : $"same glyphs, {baseline.PointsSpent} → {plan.PointsSpent} points";
                suggestions.Add((new PlacementSuggestion(
                    $"Swap slot {slot} ({oldName}) for the unused board {newName} at {steps * 90}°: {gain} " +
                    $"(the slot's targets move to {newName}'s legendary).",
                    Math.Max(0, saved),
                    new BoardSwapChange(slot, candidate, steps)), variantMet - baselineMet));
            }
        }

        return suggestions
            .OrderByDescending(s => s.MetGain)
            .ThenByDescending(s => s.Suggestion.PointsSaved)
            .Take(maxSuggestions)
            .Select(s => s.Suggestion)
            .ToList();
    }

    /// <summary>
    /// Tests moving each LEAF board (one no other board hangs off) to a different parent gate —
    /// the dimension rotations and swaps can't reach. Every valid (parent, edge, rotation) is
    /// base-solved; the best few per slot get the full-pipeline comparison when a pipeline is
    /// supplied, otherwise the bare-solve comparison applies.
    /// </summary>
    public static IReadOnlyList<PlacementSuggestion> SuggestReattachments(
        ParagonLayout layout, PlanRequest request, PlanResult baseline,
        PlacementPipeline? pipeline = null, int maxPerSlot = 2, int maxSuggestions = 3)
    {
        if (layout.Boards.Count < 3)
            return []; // with one attached board there is nowhere else to go

        int baselineMet = baseline.GlyphOutcomes.Count(o => o.Met);
        var baselineEval = pipeline is null
            ? null
            : EvaluatePipeline(ComposedGraph.Build(layout), request, pipeline, baseline);
        var parents = layout.Boards.Skip(1).Select(b => b.ParentSlot!.Value).ToHashSet();
        var suggestions = new List<(PlacementSuggestion Suggestion, int MetGain)>();

        for (int slot = 1; slot < layout.Boards.Count; slot++)
        {
            if (parents.Contains(slot))
                continue; // moving a parent would drag its subtree along
            var placed = layout.Boards[slot];

            var candidates = new List<(int Parent, BoardEdge Edge, int Rotation,
                ComposedGraph Graph, PlanRequest Request, PlanResult Plan, int Met)>();
            for (int parentSlot = 0; parentSlot < slot; parentSlot++)
            {
                foreach (var edge in new[] { BoardEdge.Top, BoardEdge.Bottom, BoardEdge.Left, BoardEdge.Right })
                {
                    for (int rotation = 0; rotation < 4; rotation++)
                    {
                        if (parentSlot == placed.ParentSlot && edge == placed.AttachEdge
                            && rotation == placed.RotationSteps)
                            continue;

                        var boards = layout.Boards.ToList();
                        boards[slot] = new PlacedBoard
                        {
                            Board = placed.Board,
                            ParentSlot = parentSlot,
                            AttachEdge = edge,
                            RotationSteps = rotation,
                        };
                        ComposedGraph graph;
                        try
                        {
                            graph = ComposedGraph.Build(new ParagonLayout(boards));
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            continue; // no gate toward the parent, or the position is occupied
                        }

                        // The board keeps its cells; only the rotation delta moves them.
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

                        var variant = PlanSolver.Solve(graph, variantRequest);
                        if (!variant.Success)
                            continue;
                        candidates.Add((parentSlot, edge, rotation, graph, variantRequest, variant,
                            variant.GlyphOutcomes.Count(o => o.Met)));
                    }
                }
            }

            string boardName = placed.Board.Name ?? placed.Board.InternalName;
            foreach (var candidate in candidates
                         .OrderByDescending(c => c.Met)
                         .ThenBy(c => c.Plan.PointsSpent)
                         .Take(maxPerSlot))
            {
                string where = $"slot {candidate.Parent}'s {candidate.Edge} edge at {candidate.Rotation * 90}°";
                if (baselineEval is not null)
                {
                    var eval = EvaluatePipeline(candidate.Graph, candidate.Request, pipeline!, candidate.Plan);
                    if (eval is null || !eval.BeatsForSuggestion(baselineEval))
                        continue;
                    var (text, change) = WithGlyphMoves(
                        $"Re-attach slot {slot} ({boardName}) to {where}: " +
                        $"{DescribeGain(eval, baselineEval, request.GlyphGoals.Count)}",
                        new ReattachChange(slot, candidate.Parent, candidate.Edge, candidate.Rotation),
                        eval, pipeline!);
                    suggestions.Add((new PlacementSuggestion(
                        text + ".", Math.Max(0, baselineEval.PointsUsed - eval.PointsUsed), change),
                        eval.GlyphsActive - baselineEval.GlyphsActive));
                    continue;
                }

                int saved = baseline.PointsSpent - candidate.Plan.PointsSpent;
                if (candidate.Met <= baselineMet && (candidate.Met < baselineMet || saved <= 0))
                    continue;
                string gain = saved > 0
                    ? $"{baseline.PointsSpent} → {candidate.Plan.PointsSpent} points"
                    : $"same points, activates {candidate.Met} glyph(s) instead of {baselineMet}";
                suggestions.Add((new PlacementSuggestion(
                    $"Re-attach slot {slot} ({boardName}) to {where}: {gain}.",
                    Math.Max(0, saved),
                    new ReattachChange(slot, candidate.Parent, candidate.Edge, candidate.Rotation)),
                    candidate.Met - baselineMet));
            }
        }

        return suggestions
            .OrderByDescending(s => s.MetGain)
            .ThenByDescending(s => s.Suggestion.PointsSaved)
            .Take(maxSuggestions)
            .Select(s => s.Suggestion)
            .ToList();
    }

    /// <summary>
    /// Surfaces glyph re-socketing on the CURRENT layout as its own suggestion. Every other
    /// candidate is compared against a glyph-optimized baseline, so a gain that's available by
    /// just moving glyphs would otherwise be absorbed silently and never shown to the user.
    /// </summary>
    public static IReadOnlyList<PlacementSuggestion> SuggestGlyphAssignment(
        ComposedGraph graph, PlanRequest request, PlanResult baseline, PlacementPipeline pipeline)
    {
        if (request.GlyphGoals.Count == 0)
            return [];
        var optimized = EvaluatePipeline(graph, request, pipeline, baseline);
        if (optimized is null || optimized.GlyphMoves.Count == 0)
            return [];
        var pinned = EvaluatePipeline(
            graph, request, pipeline with { OptimizeGlyphAssignment = false }, baseline);
        if (pinned is null || !optimized.BeatsForSuggestion(pinned))
            return [];

        var glyphBySlot = (pipeline.SocketedGlyphs ?? [])
            .GroupBy(g => g.BoardSlot)
            .ToDictionary(g => g.Key, g => g.First().Glyph.Name ?? "glyph");
        string moves = string.Join(", ", optimized.GlyphMoves.Select(m =>
            $"{glyphBySlot.GetValueOrDefault(m.FromSlot, "glyph")} to the board in slot {m.ToSlot}"));
        return
        [
            new PlacementSuggestion(
                $"Re-socket glyphs — move {moves}: {DescribeGain(optimized, pinned, request.GlyphGoals.Count)}.",
                Math.Max(0, pinned.PointsUsed - optimized.PointsUsed),
                new GlyphSlotReassignment(optimized.GlyphMoves)),
        ];
    }

    // ── Full-pipeline evaluation ─────────────────────────────────────────

    /// <summary>
    /// Runs a candidate through the whole planning pipeline — solve, then spend the entire
    /// remaining pool with the user's maximizer settings — and measures the finished build.
    /// Null when the candidate can't be solved.
    /// </summary>
    public static PipelineResult? EvaluatePipeline(
        ComposedGraph graph, PlanRequest request, PlacementPipeline pipeline, PlanResult? solved = null)
    {
        // Glyphs are movable: find the goal→socket assignment that activates the most goals on
        // THIS candidate's boards before solving, so a rotation/swap that only pays off with a
        // re-socketed glyph is judged with that move (and the move rides along in the result).
        var (effectiveRequest, effectiveGlyphs, glyphMoves) = pipeline.OptimizeGlyphAssignment
            ? OptimizeGlyphAssignment(graph, request, pipeline)
            : (request, pipeline.SocketedGlyphs, (IReadOnlyList<GlyphSlotMove>)[]);

        var solve = (glyphMoves.Count == 0 ? solved : null) ?? PlanSolver.Solve(graph, effectiveRequest);
        if (!solve.Success)
            return null;
        var purchased = solve.PurchasedCells.ToHashSet();

        var socketBySlot = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);

        // "+X% to [rarity] nodes in radius" buffs, from THIS variant's own live sockets — a
        // rotation or swap moves the socket, so the buffed area moves with it.
        IReadOnlyDictionary<CellRef, double>? MultipliersFor()
        {
            if (effectiveGlyphs is not { Count: > 0 } glyphs)
                return pipeline.Thresholds?.CellMultipliers;
            var live = glyphs
                .Where(g => socketBySlot.TryGetValue(g.BoardSlot, out var socket) && purchased.Contains(socket))
                .Select(g => new SocketedGlyph(socketBySlot[g.BoardSlot], g.Glyph, g.Level))
                .ToList();
            return GlyphNodeBuffs.MultipliersFor(graph, live);
        }

        var thresholds = pipeline.Thresholds is null
            ? null
            : pipeline.Thresholds with { CellMultipliers = MultipliersFor() };
        // The glyph pull follows THIS candidate's (possibly re-assigned) sockets too — the
        // pipeline's stored focus would point the pull at the pre-move socket cells.
        var focus = effectiveGlyphs is { Count: > 0 } pullGlyphs
            ? pipeline.Focus with
            {
                GlyphDeliveries = GlyphDelivery.For(pullGlyphs
                    .Where(g => socketBySlot.ContainsKey(g.BoardSlot))
                    .Select(g => (socketBySlot[g.BoardSlot], g.Glyph, g.Level)),
                    pipeline.Focus.Weights),
            }
            : pipeline.Focus;
        int budget = Math.Max(0, pipeline.TotalPoints - GateCrossings.PointCost(graph, purchased));
        if (budget > 0 || (focus.ActivateThresholds && thresholds is not null))
            PointMaximizer.Extend(graph, purchased, budget, focus, effectiveRequest, thresholds);

        int glyphsActive = 0;
        foreach (var goal in effectiveRequest.GlyphGoals)
        {
            double have = GlyphRadius.AttributeTotalsInRange(
                    graph, goal.Socket, purchased.ToList(), goal.Radius, GlyphRadius.GameMetric)
                .GetValueOrDefault(goal.SourceAttribute);
            if (have >= goal.RequiredTotal - 1e-9)
                glyphsActive++;
        }

        // The spend may have bought more sockets — refresh the buffed area before measuring.
        var finalMultipliers = MultipliersFor();
        int thresholdsMet = 0;
        IReadOnlyDictionary<string, double>? statTotals = null;
        if (pipeline.Thresholds is ThresholdContext context)
        {
            var report = BuildStats.Compute(graph, purchased, context.Data,
                context.NonParagonStats, context.ClassName, finalMultipliers);
            thresholdsMet = report.ThresholdsMet;
            statTotals = report.Totals;
        }

        // Limit rules are load-bearing: a candidate whose finished build exceeds a cap (the
        // solve proceeds with a note when targets force it) must never look like a clean win.
        int limitBreaks = 0;
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        foreach (var rule in effectiveRequest.NodeRules)
        {
            if (rule.Mode is not (NodeRuleMode.Limit or NodeRuleMode.Minimal)
                || !cellsByGroup.TryGetValue(rule.GroupKey, out var cells))
                continue;
            limitBreaks += Math.Max(0, cells.Count(purchased.Contains) - rule.Limit);
        }

        return new PipelineResult(glyphsActive, thresholdsMet, GateCrossings.PointCost(graph, purchased),
            FocusScoreOf(graph, purchased, pipeline.Focus, finalMultipliers))
        {
            GlyphMoves = glyphMoves,
            LimitBreaks = limitBreaks,
            StatTotals = statTotals,
        };
    }

    /// <summary>
    /// Injective goal→socket assignment maximizing (activatable goals, total attainable stat)
    /// over each socket's buyable ceiling (every node of its board within radius). Candidate
    /// sockets are the goals' own plus glyph-free ones, so buff-only glyphs are never displaced;
    /// the current assignment wins ties, so moves only appear when strictly better.
    /// </summary>
    private static (PlanRequest Request, IReadOnlyList<PipelineGlyph>? Glyphs, IReadOnlyList<GlyphSlotMove> Moves)
        OptimizeGlyphAssignment(ComposedGraph graph, PlanRequest request, PlacementPipeline pipeline)
    {
        var goals = request.GlyphGoals;
        if (goals.Count == 0)
            return (request, pipeline.SocketedGlyphs, []);

        var socketBySlot = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);
        var goalSlots = goals.Select(g => g.Socket.BoardSlot).ToHashSet();
        var occupiedSlots = pipeline.SocketedGlyphs?.Select(g => g.BoardSlot).ToHashSet() ?? goalSlots;
        var candidateSockets = socketBySlot.Values
            .Where(s => goalSlots.Contains(s.BoardSlot) || !occupiedSlots.Contains(s.BoardSlot))
            .ToList();
        if (candidateSockets.Count <= 1 || goals.Count > candidateSockets.Count)
            return (request, pipeline.SocketedGlyphs, []);

        double Attainable(GlyphGoal goal, CellRef socket) => graph.Vertices
            .Where(v => v.Cell.BoardSlot == socket.BoardSlot && v.Cell != socket
                && Math.Abs(v.Cell.X - socket.X) + Math.Abs(v.Cell.Y - socket.Y) <= goal.Radius)
            .Sum(v => v.Node.Attributes
                .Where(a => !a.IsThresholdBonus && a.Value is not null
                    && string.Equals(a.Attribute, goal.SourceAttribute, StringComparison.OrdinalIgnoreCase))
                .Sum(a => a.Value!.Value));

        var attainable = new double[goals.Count, candidateSockets.Count];
        for (int g = 0; g < goals.Count; g++)
        {
            for (int s = 0; s < candidateSockets.Count; s++)
                attainable[g, s] = Attainable(goals[g], candidateSockets[s]);
        }

        (int Active, double Total) ScoreOf(int[] assignment)
        {
            int active = 0;
            double total = 0;
            for (int g = 0; g < goals.Count; g++)
            {
                total += attainable[g, assignment[g]];
                if (attainable[g, assignment[g]] >= goals[g].RequiredTotal - 1e-9)
                    active++;
            }
            return (active, total);
        }

        var current = goals.Select(g => candidateSockets.FindIndex(s => s == g.Socket)).ToArray();
        if (current.Any(i => i < 0))
            return (request, pipeline.SocketedGlyphs, []);
        var currentScore = ScoreOf(current);

        int[]? best = null;
        (int Active, double Total) bestScore = currentScore;
        var assignment = new int[goals.Count];
        var used = new bool[candidateSockets.Count];
        void Search(int g)
        {
            if (g == goals.Count)
            {
                var score = ScoreOf(assignment);
                if (score.Active > bestScore.Active
                    || (score.Active == bestScore.Active && score.Total > bestScore.Total + 1e-9))
                {
                    best = (int[])assignment.Clone();
                    bestScore = score;
                }
                return;
            }
            for (int s = 0; s < candidateSockets.Count; s++)
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
        if (best is null)
            return (request, pipeline.SocketedGlyphs, []);

        var moves = new List<GlyphSlotMove>();
        var newGoals = new List<GlyphGoal>(goals);
        var slotRemap = new Dictionary<int, int>(); // old slot → new slot
        for (int g = 0; g < goals.Count; g++)
        {
            if (best[g] == current[g])
                continue;
            int fromSlot = goals[g].Socket.BoardSlot;
            int toSlot = candidateSockets[best[g]].BoardSlot;
            moves.Add(new GlyphSlotMove(fromSlot, toSlot));
            slotRemap[fromSlot] = toSlot;
            newGoals[g] = goals[g] with { Socket = candidateSockets[best[g]] };
        }
        if (moves.Count == 0)
            return (request, pipeline.SocketedGlyphs, []);

        var newGlyphs = pipeline.SocketedGlyphs
            ?.Select(g => slotRemap.TryGetValue(g.BoardSlot, out int to) ? g with { BoardSlot = to } : g)
            .ToList();
        var newRequest = new PlanRequest
        {
            Targets = request.Targets,
            NodeRules = request.NodeRules,
            AvoidCells = request.AvoidCells,
            ExcludeCells = request.ExcludeCells,
            GlyphGoals = newGoals,
        };
        return (newRequest, newGlyphs, moves);
    }

    /// <summary>See <see cref="FocusScore"/> — shared with tests; includes the glyph delivery term.</summary>
    private static double FocusScoreOf(
        ComposedGraph graph, ISet<CellRef> purchased, MaximizeFocus focus,
        IReadOnlyDictionary<CellRef, double>? multipliers = null) =>
        FocusScore.Of(graph, purchased, focus, multipliers);

    /// <summary>
    /// When the evaluation assumed glyph moves, the suggestion must say so and apply them too —
    /// otherwise the applied board wouldn't match the promised numbers.
    /// </summary>
    private static (string Text, PlacementChange Change) WithGlyphMoves(
        string text, PlacementChange primary, PipelineResult eval, PlacementPipeline pipeline)
    {
        if (eval.GlyphMoves.Count == 0)
            return (text, primary);
        var glyphBySlot = (pipeline.SocketedGlyphs ?? [])
            .GroupBy(g => g.BoardSlot)
            .ToDictionary(g => g.Key, g => g.First().Glyph.Name ?? "glyph");
        string moves = string.Join(", ", eval.GlyphMoves.Select(m =>
            $"{glyphBySlot.GetValueOrDefault(m.FromSlot, "glyph")} to the board in slot {m.ToSlot}"));
        return ($"{text} — includes moving {moves}",
            new CompositeChange([primary, new GlyphSlotReassignment(eval.GlyphMoves)]));
    }

    /// <summary>Human-readable delta between a candidate's end state and the baseline's.</summary>
    private static string DescribeGain(PipelineResult variant, PipelineResult baseline, int goalCount)
    {
        var parts = new List<string>();
        if (variant.LimitBreaks < baseline.LimitBreaks)
            parts.Add($"repairs {baseline.LimitBreaks - variant.LimitBreaks} node-limit break(s)");
        if (variant.GlyphsActive != baseline.GlyphsActive)
            parts.Add($"activates {variant.GlyphsActive} of {goalCount} glyph(s) instead of {baseline.GlyphsActive}");
        if (variant.ThresholdsMet != baseline.ThresholdsMet)
            parts.Add($"{variant.ThresholdsMet} threshold bonus(es) instead of {baseline.ThresholdsMet}");
        if (baseline.FocusScore > 1e-9 && Math.Abs(variant.FocusScore - baseline.FocusScore) > baseline.FocusScore * 0.005)
            parts.Add($"{(variant.FocusScore - baseline.FocusScore) / baseline.FocusScore:+0.#%;-0.#%} focused stat value");
        if (variant.PointsUsed != baseline.PointsUsed)
            parts.Add($"{baseline.PointsUsed} → {variant.PointsUsed} points");
        return parts.Count > 0 ? "at full spend " + string.Join(", ", parts) : "equivalent at full spend";
    }

    /// <summary>The candidate board's total of the attribute within radius of its own socket.
    /// Shared with <see cref="PlacementSearch"/> for its swap and glyph-substitution ranking.</summary>
    internal static double AttainableByAttribute(ParagonBoardDef board, string attribute, int radius)
    {
        var nodes = ParagonDatabase.NodesBySnoId;
        (int X, int Y)? socket = null;
        foreach (var placement in board.Nodes)
        {
            if (nodes[placement.Node].Kind == ParagonNodeKind.GlyphSocket)
            {
                socket = (placement.X, placement.Y);
                break;
            }
        }
        if (socket is not var (sx, sy))
            return 0;

        double total = 0;
        foreach (var placement in board.Nodes)
        {
            if ((placement.X, placement.Y) == (sx, sy)
                || Math.Abs(placement.X - sx) + Math.Abs(placement.Y - sy) > radius)
                continue;
            total += nodes[placement.Node].Attributes
                .Where(a => !a.IsThresholdBonus && a.Attribute == attribute && a.Value is double)
                .Sum(a => a.Value!.Value);
        }
        return total;
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
