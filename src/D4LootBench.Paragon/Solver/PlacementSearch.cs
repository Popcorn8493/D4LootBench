using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>Socket a different glyph in a board slot (at the socket's current level). The level
/// is an evaluation assumption — the player may still need to level the recommended glyph.</summary>
public sealed record GlyphSwapChange(int Slot, ParagonGlyphDef NewGlyph) : PlacementChange;

/// <summary>
/// A complete "final setup" found by <see cref="PlacementSearch"/>: an ordered sequence of
/// placement changes applied as one composite, with the finished build it produces.
/// </summary>
public sealed record PlacementPlan(
    IReadOnlyList<string> Steps,
    string Comparison,
    IReadOnlyList<string> StatChanges,
    PlacementChange Change,
    PipelineResult Result,
    PipelineResult Baseline)
{
    /// <summary>One display string: the comparison headline, the numbered steps, and the
    /// biggest effective stat differences vs the current setup.</summary>
    public string Describe(int rank, int of, string? context = null)
    {
        string header = context is null ? $"Option {rank} of {of}" : $"Option {rank} of {of} ({context})";
        string text = $"{header} — {Comparison}\n"
            + string.Join("\n", Steps.Select((s, i) => $"  {i + 1}. {s}"));
        if (StatChanges.Count > 0)
            text += "\n  Stat changes: " + string.Join(", ", StatChanges);
        return text;
    }
}

/// <summary>
/// Memoized bare solves and pipeline evaluations, shareable across <see cref="PlacementSearch.FindPlans"/>
/// passes over the same layout (e.g. the planner's glyph-level passes, whose bare solves coincide
/// whenever the level radius does). Thread-safe.
/// </summary>
public sealed class PlacementSearchCache
{
    internal ConcurrentDictionary<string, PlanResult?> Solves { get; } = new(StringComparer.Ordinal);

    internal ConcurrentDictionary<(PlacementPipeline Pipeline, string Key), PipelineResult?> Evaluations { get; } = new();
}

/// <summary>
/// Searches SEQUENCES of placement changes — rotations, board swaps, leaf re-attachments, and
/// glyph substitutions — instead of one change at a time, so the user lands on a final setup
/// directly rather than applying and re-analyzing repeatedly. Beam search: every candidate
/// state is judged by the FINISHED build (solve + full spend, <see
/// cref="PlacementAnalyzer.EvaluatePipeline"/>, glyph re-socketing folded in per state), the
/// best few states survive each round, and the top distinct end states that beat the current
/// setup are returned. Move selection is FAMILY-DIVERSE (rotations never crowd out board swaps
/// or re-attachments from the evaluation slots), candidates pre-rank by a bare solve, and
/// generation + evaluation run in parallel — the solver layer is stateless, so candidate
/// pipelines are safe to run concurrently. <paramref name="maxEvaluations"/> caps pipeline runs.
/// </summary>
public static class PlacementSearch
{
    public static IReadOnlyList<PlacementPlan> FindPlans(
        ParagonLayout layout, PlanRequest request, PlacementPipeline pipeline,
        IReadOnlyList<ParagonBoardDef> spareBoards,
        IReadOnlyList<ParagonGlyphDef> spareGlyphs,
        int beamWidth = 5, int depth = 4, int topN = 3,
        int evalsPerState = 9, int maxEvaluations = 220,
        IReadOnlySet<int>? lockedBoardSlots = null, IReadOnlySet<int>? lockedGlyphSlots = null,
        CancellationToken cancellationToken = default, PlacementSearchCache? cache = null)
    {
        cache ??= new PlacementSearchCache();
        var baselineGraph = ComposedGraph.Build(layout);
        var probe = new State(layout, request, pipeline, [], [], null!);
        var baseline = Evaluate(cache, probe, baselineGraph, solved: null);
        if (baseline is null)
            return [];

        int evaluations = 0;
        var root = probe with { Eval = baseline };
        var seen = new HashSet<string>(StringComparer.Ordinal) { KeyOf(root) };
        var frontier = new List<State> { root };
        var reached = new List<State>();

        for (int round = 0; round < depth && evaluations < maxEvaluations; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Generate moves in parallel (generation bare-solves each candidate) — flattened to
            // per-slot work items across every frontier state, so even round 0's single state
            // fans out — then dedup and pick a family-diverse selection per state sequentially.
            var generated = frontier
                .SelectMany((state, index) => GenerationWork(
                        state, spareBoards, spareGlyphs, lockedBoardSlots, lockedGlyphSlots, cache)
                    .Select(work => (Index: index, Work: work)))
                .AsParallel().AsOrdered()
                .WithCancellation(cancellationToken)
                .Select(item => (item.Index, Moves: item.Work().ToList()))
                .ToList();
            // Only moves actually sent for evaluation are marked seen — ones the diversity
            // selection or the evaluation cap passed over stay reachable from later states.
            var toEvaluate = new List<Move>();
            int remaining = maxEvaluations - evaluations;
            foreach (var group in generated.GroupBy(g => g.Index))
            {
                var fresh = group
                    .SelectMany(g => g.Moves)
                    .Where(m => !seen.Contains(m.Key))
                    .DistinctBy(m => m.Key);
                foreach (var move in SelectDiverse(fresh, evalsPerState))
                {
                    if (toEvaluate.Count >= remaining)
                        break;
                    if (seen.Add(move.Key))
                        toEvaluate.Add(move);
                }
            }
            if (toEvaluate.Count == 0)
                break;
            evaluations += toEvaluate.Count;

            // The generation's bare solve is reused — the pipeline solves the same request.
            var children = toEvaluate
                .AsParallel().AsOrdered()
                .WithCancellation(cancellationToken)
                .Select(m => (Move: m, Eval: Evaluate(cache, m.Next, m.Graph, m.Solve)))
                .Where(x => x.Eval is not null)
                .Select(x => x.Move.Next with { Eval = x.Eval! })
                .ToList();
            if (children.Count == 0)
                break;
            reached.AddRange(children);
            // The beam keeps the best states even when they don't beat the baseline yet — a
            // swap can pay off only after the follow-up re-attachment.
            frontier = children.OrderBy(s => s, StateComparer).Take(beamWidth).ToList();
        }

        var winners = new List<PlacementPlan>();
        var winnerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in reached.OrderBy(s => s, StateComparer).ThenBy(s => s.Changes.Count))
        {
            if (winners.Count >= topN)
                break;
            if (!state.Eval.BeatsForSuggestion(baseline) || !winnerKeys.Add(KeyOf(state)))
                continue;

            // The evaluation may have assumed glyph re-socketing — the plan must apply it too,
            // or the applied board won't reproduce the promised numbers.
            var changes = state.Changes.ToList();
            var steps = state.Steps.ToList();
            if (state.Eval.GlyphMoves.Count > 0)
            {
                changes.Add(new GlyphSlotReassignment(state.Eval.GlyphMoves));
                var glyphBySlot = (state.Pipeline.SocketedGlyphs ?? [])
                    .GroupBy(g => g.BoardSlot)
                    .ToDictionary(g => g.Key, g => g.First().Glyph.Name ?? "glyph");
                steps.Add("Re-socket glyphs: " + string.Join(", ", state.Eval.GlyphMoves.Select(m =>
                    $"{glyphBySlot.GetValueOrDefault(m.FromSlot, "glyph")} to the board in slot {m.ToSlot}")));
            }

            winners.Add(new PlacementPlan(
                steps,
                Compare(state.Eval, baseline, state.Request.GlyphGoals.Count),
                StatDiff(state.Eval.StatTotals, baseline.StatTotals, pipeline.Thresholds?.ClassName),
                new CompositeChange(changes),
                state.Eval,
                baseline));
        }
        return winners;
    }

    /// <summary>
    /// The same scenario with every glyph pinned to one level: socketed glyphs (even those
    /// leveled beyond it) and goal radii are normalized, so a second analysis pass can judge
    /// placements independent of the player's current glyph investment.
    /// </summary>
    public static (PlanRequest Request, PlacementPipeline Pipeline) AtGlyphLevel(
        PlanRequest request, PlacementPipeline pipeline, int level)
    {
        var normalizedRequest = new PlanRequest
        {
            Targets = request.Targets,
            NodeRules = request.NodeRules,
            AvoidCells = request.AvoidCells,
            ExcludeCells = request.ExcludeCells,
            GlyphGoals = request.GlyphGoals
                .Select(g => g with { Radius = GlyphRadius.RadiusForLevel(level) })
                .ToList(),
        };
        var normalizedPipeline = pipeline with
        {
            SocketedGlyphs = pipeline.SocketedGlyphs?.Select(g => g with { Level = level }).ToList(),
        };
        return (normalizedRequest, normalizedPipeline);
    }

    // ── Search state ─────────────────────────────────────────────────────

    private sealed record State(
        ParagonLayout Layout,
        PlanRequest Request,
        PlacementPipeline Pipeline,
        IReadOnlyList<PlacementChange> Changes,
        IReadOnlyList<string> Steps,
        PipelineResult Eval);

    private enum MoveFamily { Rotation, Swap, Reattach, GlyphSwap }

    /// <summary>A proposed move: the successor state plus its bare-solve rank inputs.</summary>
    private sealed record Move(State Next, ComposedGraph Graph, MoveFamily Family, string Key, PlanResult Solve)
    {
        public int BareMet => Solve.GlyphOutcomes.Count(o => o.Met);
        public int BarePoints => Solve.PointsSpent;
    }

    /// <summary>Better states first: fewest limit breaks, most glyphs, most thresholds, most
    /// focused value, fewest points — the <see cref="PipelineResult.BeatsForSuggestion"/> axes.</summary>
    private static readonly Comparer<State> StateComparer = Comparer<State>.Create((a, b) =>
    {
        int c = a.Eval.LimitBreaks.CompareTo(b.Eval.LimitBreaks);
        if (c != 0) return c;
        c = b.Eval.GlyphsActive.CompareTo(a.Eval.GlyphsActive);
        if (c != 0) return c;
        c = b.Eval.ThresholdsMet.CompareTo(a.Eval.ThresholdsMet);
        if (c != 0) return c;
        c = b.Eval.EffectiveScore.CompareTo(a.Eval.EffectiveScore);
        if (c != 0) return c;
        return a.Eval.PointsUsed.CompareTo(b.Eval.PointsUsed);
    });

    /// <summary>Canonical state key: two different orders reaching the same setup dedup.</summary>
    private static string KeyOf(State state)
    {
        var glyphBySlot = (state.Pipeline.SocketedGlyphs ?? [])
            .GroupBy(g => g.BoardSlot)
            .ToDictionary(g => g.Key, g => g.First().Glyph.InternalName);
        return string.Join(";", state.Layout.Boards.Select((b, slot) =>
            $"{b.Board.InternalName}|{b.ParentSlot}|{b.AttachEdge}|{b.RotationSteps}|" +
            glyphBySlot.GetValueOrDefault(slot, "")));
    }

    /// <summary>
    /// Family-diverse selection: cycle the families, taking each one's next-best candidate in
    /// turn, so cheap-to-improve rotations can never crowd board swaps, re-attachments, or glyph
    /// substitutions out of the evaluation slots entirely.
    /// </summary>
    private static List<Move> SelectDiverse(IEnumerable<Move> moves, int budget)
    {
        var byFamily = moves
            .GroupBy(m => m.Family)
            .ToDictionary(
                g => g.Key,
                g => new Queue<Move>(g.OrderByDescending(m => m.BareMet).ThenBy(m => m.BarePoints)));
        var selected = new List<Move>();
        var order = new[] { MoveFamily.Swap, MoveFamily.Rotation, MoveFamily.Reattach, MoveFamily.GlyphSwap };
        while (selected.Count < budget && byFamily.Values.Any(q => q.Count > 0))
        {
            foreach (var family in order)
            {
                if (selected.Count >= budget)
                    break;
                if (byFamily.TryGetValue(family, out var queue) && queue.Count > 0)
                    selected.Add(queue.Dequeue());
            }
        }
        return selected;
    }

    /// <summary>
    /// Every move family's candidates for a state, split into independent per-slot work items
    /// so generation parallelizes within a state as well as across the frontier.
    /// </summary>
    private static IEnumerable<Func<IEnumerable<Move>>> GenerationWork(
        State state, IReadOnlyList<ParagonBoardDef> spareBoards, IReadOnlyList<ParagonGlyphDef> spareGlyphs,
        IReadOnlySet<int>? lockedBoardSlots, IReadOnlySet<int>? lockedGlyphSlots, PlacementSearchCache cache)
    {
        int count = state.Layout.Boards.Count;
        for (int slot = 1; slot < count; slot++)
        {
            int s = slot;
            yield return () => RotationMoves(state, s, cache);
        }

        var inLayout = state.Layout.Boards.Select(b => b.Board.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = spareBoards
            .DistinctBy(c => c.InternalName)
            .Where(c => !inLayout.Contains(c.InternalName))
            .ToList();
        for (int slot = 1; slot < count; slot++)
        {
            if (lockedBoardSlots?.Contains(slot) == true)
                continue; // a required board never swaps out (rotations/re-attachments still apply)
            int s = slot;
            yield return () => SwapMoves(state, usable, s, cache);
        }

        if (count >= 3)
        {
            var parents = state.Layout.Boards.Skip(1).Select(b => b.ParentSlot!.Value).ToHashSet();
            for (int slot = 1; slot < count; slot++)
            {
                if (parents.Contains(slot))
                    continue; // moving a parent would drag its subtree along
                int s = slot;
                yield return () => ReattachMoves(state, s, cache);
            }
        }

        if (state.Pipeline.SocketedGlyphs is { Count: > 0 } socketed && spareGlyphs.Count > 0)
        {
            // Substitutions leave the layout alone — one graph serves every one of them.
            var graph = new Lazy<ComposedGraph>(() => ComposedGraph.Build(state.Layout));
            foreach (var incumbent in socketed)
            {
                if (lockedGlyphSlots?.Contains(incumbent.BoardSlot) == true)
                    continue; // a required glyph is never substituted away (re-socketing still applies)
                yield return () => GlyphSwapMoves(state, incumbent, spareGlyphs, graph, cache);
            }
        }
    }

    // ── Move families ────────────────────────────────────────────────────

    private static IEnumerable<Move> RotationMoves(State state, int slot, PlacementSearchCache cache)
    {
        var placed = state.Layout.Boards[slot];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            if (rotation == placed.RotationSteps)
                continue;
            var boards = state.Layout.Boards.ToList();
            boards[slot] = Placed(placed.Board, placed.ParentSlot, placed.AttachEdge, rotation);
            if (TryBuild(boards) is not { } graph)
                continue;

            var next = state with
            {
                Layout = new ParagonLayout(boards),
                Request = PlacementAnalyzer.RemapForRotation(state.Request, slot, placed, rotation),
            };
            if (TrySolve(cache, next.Layout, graph, next.Request) is not { } solve)
                continue;

            string name = placed.Board.Name ?? placed.Board.InternalName;
            next = next with
            {
                Changes = [.. state.Changes, new RotationChange(slot, rotation)],
                Steps = [.. state.Steps, $"Rotate slot {slot} ({name}) to {rotation * 90}°"],
            };
            yield return new Move(next, graph, MoveFamily.Rotation, KeyOf(next), solve);
        }
    }

    private static IEnumerable<Move> SwapMoves(
        State state, IReadOnlyList<ParagonBoardDef> usable, int slot, PlacementSearchCache cache)
    {
        var placed = state.Layout.Boards[slot];
        var slotGoals = state.Request.GlyphGoals.Where(g => g.Socket.BoardSlot == slot).ToList();
        var rankGoals = slotGoals.Count > 0 ? slotGoals : state.Request.GlyphGoals;
        // With goals, candidates rank by attainable glyph stat around their socket; without,
        // by how much focused stat the whole board carries — so swaps still surface for
        // builds planned purely on targets.
        var ranked = (rankGoals.Count > 0
                ? usable.Select(c => (Board: c, Fit: rankGoals.Max(g =>
                    PlacementAnalyzer.AttainableByAttribute(c, g.SourceAttribute, g.Radius))))
                : usable.Select(c => (Board: c, Fit: FocusFit(c, state.Pipeline.Focus))))
            .Where(c => c.Fit > 0)
            .OrderByDescending(c => c.Fit)
            .Take(2)
            .Concat(PlacementAnalyzer.LegendaryCandidate(usable, state.Pipeline.Legendary))
            .DistinctBy(c => c.Board.InternalName);

        foreach (var (candidate, _) in ranked)
        {
            Move? best = null;
            for (int rotation = 0; rotation < 4; rotation++)
            {
                var boards = state.Layout.Boards.ToList();
                boards[slot] = Placed(candidate, placed.ParentSlot, placed.AttachEdge, rotation);
                if (TryBuild(boards) is not { } graph)
                    continue;

                var variantRequest = PlacementAnalyzer.RetargetSwappedSlot(graph, state.Request, slot);
                if (TrySolve(cache, new ParagonLayout(boards), graph, variantRequest) is not { } solve)
                    continue;

                string oldName = placed.Board.Name ?? placed.Board.InternalName;
                string newName = candidate.Name ?? candidate.InternalName;
                var next = state with
                {
                    Layout = new ParagonLayout(boards),
                    Request = variantRequest,
                    Changes = [.. state.Changes, new BoardSwapChange(slot, candidate, rotation)],
                    Steps = [.. state.Steps,
                        $"Swap slot {slot} ({oldName}) for the unused board {newName} at {rotation * 90}° " +
                        $"(the slot's targets move to {newName}'s legendary)"],
                };
                var move = new Move(next, graph, MoveFamily.Swap, KeyOf(next), solve);
                if (best is null || move.BareMet > best.BareMet
                    || (move.BareMet == best.BareMet && move.BarePoints < best.BarePoints))
                    best = move;
            }
            if (best is not null)
                yield return best;
        }
    }

    private static IEnumerable<Move> ReattachMoves(State state, int slot, PlacementSearchCache cache)
    {
        var placed = state.Layout.Boards[slot];
        var candidates = new List<Move>();

        for (int parentSlot = 0; parentSlot < slot; parentSlot++)
        {
            foreach (var edge in new[] { BoardEdge.Top, BoardEdge.Bottom, BoardEdge.Left, BoardEdge.Right })
            {
                // Same parent + edge at another rotation is a rotation move, generated there.
                if (parentSlot == placed.ParentSlot && edge == placed.AttachEdge)
                    continue;
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    var boards = state.Layout.Boards.ToList();
                    boards[slot] = Placed(placed.Board, parentSlot, edge, rotation);
                    if (TryBuild(boards) is not { } graph)
                        continue;

                    var variantRequest = PlacementAnalyzer.RemapForRotation(state.Request, slot, placed, rotation);
                    if (TrySolve(cache, new ParagonLayout(boards), graph, variantRequest) is not { } solve)
                        continue;

                    string name = placed.Board.Name ?? placed.Board.InternalName;
                    var next = state with
                    {
                        Layout = new ParagonLayout(boards),
                        Request = variantRequest,
                        Changes = [.. state.Changes, new ReattachChange(slot, parentSlot, edge, rotation)],
                        Steps = [.. state.Steps,
                            $"Re-attach slot {slot} ({name}) to slot {parentSlot}'s {edge} edge at {rotation * 90}°"],
                    };
                    candidates.Add(new Move(next, graph, MoveFamily.Reattach, KeyOf(next), solve));
                }
            }
        }
        return candidates
            .OrderByDescending(c => c.BareMet)
            .ThenBy(c => c.BarePoints)
            .Take(2);
    }

    /// <summary>
    /// Substituting an unsocketed glyph for a socketed one — the dimension the user can't reach
    /// with any board move. Attribute-mapped and node-buff glyphs rank in SEPARATE families
    /// (their scalars aren't comparable: a node buff's raw scalar dwarfs a mapped glyph's
    /// per-point conversion, and its conditional Additional Bonus power isn't in the data at
    /// all — the pipeline judges what we can measure, the buffed nodes). Candidates are
    /// evaluated at the incumbent's level; the step says so, since the recommendation is
    /// independent of what the player has leveled.
    /// </summary>
    private static IEnumerable<Move> GlyphSwapMoves(
        State state, PipelineGlyph incumbent, IReadOnlyList<ParagonGlyphDef> spareGlyphs, Lazy<ComposedGraph> graph,
        PlacementSearchCache cache)
    {
        var socketed = state.Pipeline.SocketedGlyphs!;
        var socketedNames = socketed.Select(g => g.Glyph.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var weights = state.Pipeline.Focus.Weights;

        var goal = state.Request.GlyphGoals.FirstOrDefault(g => g.Socket.BoardSlot == incumbent.BoardSlot);
        var board = incumbent.BoardSlot < state.Layout.Boards.Count
            ? state.Layout.Boards[incumbent.BoardSlot].Board
            : null;
        if (board is null)
            yield break;

        var feasible = spareGlyphs
            .Where(g => !socketedNames.Contains(g.InternalName))
            .Select(g => (Glyph: g, Source: GlyphInfo.PrimarySourceAttribute(g)))
            .Where(g => g.Source is not null)
            // With an activation goal, the new glyph must be activatable on this board.
            .Where(g => goal is null || PlacementAnalyzer.AttainableByAttribute(
                board, g.Source!, goal.Radius) >= goal.RequiredTotal - 1e-9)
            .Select(g => (g.Glyph, g.Source, Scalar: GlyphInfo.BonusScalarAt(g.Glyph, incumbent.Level) ?? 0))
            .Where(g => g.Scalar > 0)
            .ToList();
        var mapped = feasible
            .Where(g => GlyphInfo.IsAttributeMapped(g.Glyph))
            .OrderByDescending(g => g.Scalar * RelevanceOf(GlyphInfo.DestinationAttribute(g.Glyph), weights))
            .Take(2);
        var nodeBuff = feasible
            .Where(g => !GlyphInfo.IsAttributeMapped(g.Glyph))
            .OrderByDescending(g => g.Scalar)
            .Take(1);

        foreach (var (candidate, source, _) in mapped.Concat(nodeBuff))
        {
            var newGlyphs = socketed
                .Select(g => g.BoardSlot == incumbent.BoardSlot ? g with { Glyph = candidate } : g)
                .ToList();
            var next = state with
            {
                Request = new PlanRequest
                {
                    Targets = state.Request.Targets,
                    NodeRules = state.Request.NodeRules,
                    AvoidCells = state.Request.AvoidCells,
                    ExcludeCells = state.Request.ExcludeCells,
                    GlyphGoals = state.Request.GlyphGoals
                        .Select(g => g.Socket.BoardSlot == incumbent.BoardSlot
                            ? g with { SourceAttribute = source!, GlyphName = candidate.Name }
                            : g)
                        .ToList(),
                },
                Pipeline = state.Pipeline with { SocketedGlyphs = newGlyphs },
            };
            if (TrySolve(cache, next.Layout, graph.Value, next.Request) is not { } solve)
                continue;

            string boardName = board.Name ?? board.InternalName;
            next = next with
            {
                Changes = [.. state.Changes, new GlyphSwapChange(incumbent.BoardSlot, candidate)],
                Steps = [.. state.Steps,
                    $"Socket {candidate.Name ?? candidate.InternalName} instead of " +
                    $"{incumbent.Glyph.Name ?? incumbent.Glyph.InternalName} on slot {incumbent.BoardSlot} " +
                    $"({boardName}) — judged at level {incumbent.Level}; level the glyph accordingly"],
            };
            yield return new Move(next, graph.Value, MoveFamily.GlyphSwap, KeyOf(next), solve);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PlacedBoard Placed(ParagonBoardDef board, int? parentSlot, BoardEdge? edge, int rotation) => new()
    {
        Board = board,
        ParentSlot = parentSlot,
        AttachEdge = edge,
        RotationSteps = rotation,
    };

    private static ComposedGraph? TryBuild(IReadOnlyList<PlacedBoard> boards)
    {
        try
        {
            return ComposedGraph.Build(new ParagonLayout(boards));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // rotation leaves no gate facing the parent, or the position is taken
        }
    }

    /// <summary>A bare solve, memoized by layout + request — solves don't depend on glyphs.</summary>
    private static PlanResult? TrySolve(
        PlacementSearchCache cache, ParagonLayout layout, ComposedGraph graph, PlanRequest request) =>
        cache.Solves.GetOrAdd(LayoutKey(layout) + "#" + RequestKey(request), _ =>
        {
            var solve = PlanSolver.Solve(graph, request);
            return solve.Success ? solve : null;
        });

    /// <summary>A full-pipeline evaluation, memoized by state + request under the state's pipeline.</summary>
    private static PipelineResult? Evaluate(
        PlacementSearchCache cache, State state, ComposedGraph graph, PlanResult? solved) =>
        cache.Evaluations.GetOrAdd((state.Pipeline, KeyOf(state) + "#" + RequestKey(state.Request)),
            _ => PlacementAnalyzer.EvaluatePipeline(graph, state.Request, state.Pipeline, solved));

    private static string LayoutKey(ParagonLayout layout) =>
        string.Join(";", layout.Boards.Select(b =>
            $"{b.Board.InternalName}|{b.ParentSlot}|{b.AttachEdge}|{b.RotationSteps}"));

    /// <summary>Everything in a request that can change a solve's outcome, in order.</summary>
    private static string RequestKey(PlanRequest request)
    {
        var key = new StringBuilder();
        static void Cells(StringBuilder sb, IEnumerable<CellRef> cells)
        {
            foreach (var c in cells)
                sb.Append(c.BoardSlot).Append(',').Append(c.X).Append(',').Append(c.Y).Append(' ');
            sb.Append('|');
        }
        Cells(key, request.Targets);
        Cells(key, request.AvoidCells);
        Cells(key, request.ExcludeCells);
        foreach (var rule in request.NodeRules)
            key.Append(rule.GroupKey).Append(':').Append(rule.Mode).Append(':').Append(rule.Limit).Append(' ');
        key.Append('|');
        foreach (var goal in request.GlyphGoals)
        {
            key.Append(goal.Socket.BoardSlot).Append(',').Append(goal.Socket.X).Append(',').Append(goal.Socket.Y)
                .Append(':').Append(goal.SourceAttribute).Append(':')
                .Append(goal.RequiredTotal.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(goal.Radius).Append(':').Append(goal.GlyphName).Append(' ');
        }
        return key.ToString();
    }

    /// <summary>How much focused stat the whole board carries — the swap ranking when no glyph
    /// goals exist to rank by.</summary>
    private static double FocusFit(ParagonBoardDef board, MaximizeFocus focus)
    {
        var nodes = Data.ParagonDatabase.NodesBySnoId;
        double total = 0;
        foreach (var placement in board.Nodes)
        {
            if (!nodes.TryGetValue(placement.Node, out var node))
                continue;
            foreach (var attribute in node.Attributes)
            {
                if (attribute.IsThresholdBonus || attribute.Value is not double value)
                    continue;
                double weight = focus.Weights?.GetValueOrDefault(attribute.Attribute) ??
                    (focus.Attributes.Contains(attribute.Attribute) ? 1 : 0);
                total += Math.Abs(value) * weight;
            }
        }
        return total;
    }

    private static double RelevanceOf(string? destination, IReadOnlyDictionary<string, double>? weights) =>
        destination is not null && weights?.TryGetValue(destination, out double weight) == true ? weight : 1.0;

    /// <summary>Absolute end-state numbers plus the delta, so options compare at a glance.</summary>
    private static string Compare(PipelineResult result, PipelineResult baseline, int goalCount)
    {
        var parts = new List<string>
        {
            $"glyphs {baseline.GlyphsActive}→{result.GlyphsActive} of {goalCount}",
            $"thresholds {baseline.ThresholdsMet}→{result.ThresholdsMet}",
            $"points {baseline.PointsUsed}→{result.PointsUsed}",
        };
        if (baseline.FocusScore > 1e-9)
            parts.Add($"focused value {(result.FocusScore - baseline.FocusScore) / baseline.FocusScore:+0.#%;-0.#%;+0%}");
        if (Math.Abs(result.LegendaryFactor - baseline.LegendaryFactor) > 1e-6)
            parts.Add($"applicable legendary ×{baseline.LegendaryFactor:0.00}→×{result.LegendaryFactor:0.00}");
        if (result.LimitBreaks != baseline.LimitBreaks)
            parts.Add($"limit breaks {baseline.LimitBreaks}→{result.LimitBreaks}");
        return "at full spend: " + string.Join(", ", parts);
    }

    /// <summary>The biggest effective stat differences between two finished builds, formatted
    /// "+120 Willpower" / "-2.5% Critical Strike Damage", largest relative change first. Each
    /// entry is tagged by how it scales damage (<see cref="DamageModel"/>): "+% damage" stats
    /// share ONE additive bucket with diminishing returns, the class's main stat and crit
    /// chance are multiplier-side, everything else is untagged.</summary>
    private static IReadOnlyList<string> StatDiff(
        IReadOnlyDictionary<string, double>? result, IReadOnlyDictionary<string, double>? baseline,
        string? className, int maxEntries = 8)
    {
        if (result is null || baseline is null)
            return [];
        var diffs = new List<(string Attribute, double Delta, double Magnitude)>();
        foreach (string attribute in result.Keys.Union(baseline.Keys, StringComparer.OrdinalIgnoreCase))
        {
            double have = result.GetValueOrDefault(attribute);
            double had = baseline.GetValueOrDefault(attribute);
            double delta = have - had;
            double reference = Math.Max(Math.Abs(have), Math.Abs(had));
            if (reference < 1e-9 || Math.Abs(delta) / reference < 0.005)
                continue; // unchanged, or noise
            diffs.Add((attribute, delta, Math.Abs(delta) / reference));
        }
        return diffs
            .OrderByDescending(d => d.Magnitude)
            .Take(maxEntries)
            .Select(d => $"{(d.Delta > 0 ? "+" : "-")}{FormatValue(Math.Abs(d.Delta))} " +
                         ParagonDisplay.FormatAttributeName(d.Attribute) + ScalingTag(d.Attribute, className))
            .ToList();
    }

    /// <summary>How a stat scales damage — additive bucket vs multiplier-side.</summary>
    private static string ScalingTag(string attribute, string? className)
    {
        if (DamageModel.Classify(attribute) == DamageBucket.AdditiveDamage)
            return " [additive dmg]";
        if (string.Equals(attribute, DamageModel.MainStatAttribute(className), StringComparison.OrdinalIgnoreCase))
            return " [× main stat]";
        if (DamageModel.Classify(attribute) == DamageBucket.CritChance)
            return " [× via crit]";
        return "";
    }

    /// <summary>Fractional values are percentages (matching <see cref="ParagonDisplay"/>).</summary>
    private static string FormatValue(double value) =>
        Math.Abs(value) < 1 && value != 0
            ? (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
