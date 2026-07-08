using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

public sealed record LayoutOptimizerRequest
{
    public required ParagonBoardDef StarterBoard { get; init; }

    /// <summary>Boards the layout must contain.</summary>
    public IReadOnlyList<ParagonBoardDef> MustUseBoards { get; init; } = [];

    /// <summary>Boards the optimizer may add to fill remaining slots, best glyph support first.</summary>
    public IReadOnlyList<ParagonBoardDef> PoolBoards { get; init; } = [];

    /// <summary>Total boards including the starter (2–5).</summary>
    public int MaxBoards { get; init; } = ParagonLayout.MaxBoards;

    /// <summary>Glyphs to socket; the optimizer decides which board each goes on.</summary>
    public IReadOnlyList<ParagonGlyphDef> Glyphs { get; init; } = [];

    /// <summary>Activation threshold (engine-side, user-editable).</summary>
    public double RequiredStat { get; init; } = 40;

    /// <summary>Level assumed for every glyph, for its radius.</summary>
    public int GlyphLevel { get; init; } = 100;

    /// <summary>Character stat from level and gear, counted toward rare-node threshold requirements.</summary>
    public double NonParagonStat { get; init; }
}

public sealed record GlyphPlacement(int BoardSlot, ParagonGlyphDef Glyph, double AttainableStat, bool CanActivate);

public sealed class LayoutOptimizerResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public ParagonLayout? Layout { get; init; }
    public IReadOnlyList<GlyphPlacement> GlyphPlacements { get; init; } = [];

    /// <summary>The solved plan on the winning layout (targets + glyph activation).</summary>
    public PlanResult? Plan { get; init; }

    /// <summary>The legendary-node cells used as path targets.</summary>
    public IReadOnlyList<CellRef> Targets { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    public static LayoutOptimizerResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Builds a layout from scratch: given desired boards (plus an optional pool to fill remaining
/// slots), and desired glyphs, finds board positions, rotations, and a glyph→board assignment,
/// then solves the cheapest path reaching every board's legendary node and activating every
/// placed glyph. Key decomposition: a glyph's attainable stat around a socket is
/// rotation-independent (the radius neighborhood rotates with the board), so glyphs are assigned
/// to boards before any arrangement is chosen. Arrangements are ranked by a cheap internal-cost
/// heuristic (entry gate → legendary/socket distance plus depth), and the best few get a full
/// <see cref="PlanSolver"/> evaluation.
/// </summary>
public static class LayoutOptimizer
{
    /// <summary>Arrangements that survive the heuristic ranking and get a full solve.</summary>
    private const int RerankCount = 25;

    public static LayoutOptimizerResult Optimize(LayoutOptimizerRequest request, ParagonData data)
    {
        int maxBoards = Math.Clamp(request.MaxBoards, 1, ParagonLayout.MaxBoards);
        var must = request.MustUseBoards.DistinctBy(b => b.InternalName).ToList();
        if (must.Count > maxBoards - 1)
            return LayoutOptimizerResult.Failed(
                $"{must.Count} must-use boards don't fit — a layout holds {maxBoards - 1} besides the starter.");

        var nodesBySnoId = data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        int radius = GlyphRadius.RadiusForLevel(request.GlyphLevel);

        // 1. Fill open slots from the pool, ranked by how well any desired glyph activates there.
        var chosen = new List<ParagonBoardDef>(must);
        var pool = request.PoolBoards
            .DistinctBy(b => b.InternalName)
            .Where(b => must.All(m => m.InternalName != b.InternalName)
                        && b.InternalName != request.StarterBoard.InternalName)
            .ToList();
        if (chosen.Count < maxBoards - 1 && pool.Count > 0)
        {
            var ranked = pool
                .Select(board => (Board: board, Fit: request.Glyphs.Count == 0
                    ? 0
                    : request.Glyphs.Max(g => AttainableStat(board, g, radius, nodesBySnoId))))
                .OrderByDescending(c => c.Fit)
                .ToList();
            foreach (var candidate in ranked.TakeWhile(_ => chosen.Count < maxBoards - 1))
            {
                chosen.Add(candidate.Board);
                notes.Add($"Added {BoardName(candidate.Board)} from the pool" +
                          (candidate.Fit > 0 ? $" (up to {candidate.Fit:0} glyph stat in radius)." : "."));
            }
        }

        // 2. Assign glyphs to boards (slot 0 = starter) — rotation-independent, so done up front.
        var slotBoards = new List<ParagonBoardDef> { request.StarterBoard };
        slotBoards.AddRange(chosen);
        var placements = AssignGlyphs(slotBoards, request, radius, nodesBySnoId, notes);

        // 3. Enumerate arrangements: position sets grown from the starter's single gate,
        //    boards permuted across positions, rotations picked per board by internal cost.
        var profiles = slotBoards.Select(b => new BoardProfile(b, nodesBySnoId)).ToList();
        var glyphSlots = placements.Select(p => p.BoardSlot).ToHashSet();

        var candidates = new List<(double PreScore, List<PlacedBoard> Boards, List<int> SlotByPosition)>();
        if (chosen.Count == 0)
        {
            candidates.Add((0, [new PlacedBoard { Board = request.StarterBoard }], [0]));
        }
        else
        {
            foreach (var positions in EnumeratePositionSets(chosen.Count))
            {
                foreach (var order in Permutations(Enumerable.Range(1, chosen.Count).ToArray()))
                {
                    var combo = BuildCombo(positions, order, profiles, glyphSlots);
                    if (combo is not null)
                        candidates.Add(combo.Value);
                }
            }
        }
        if (candidates.Count == 0)
            return LayoutOptimizerResult.Failed("No valid arrangement found for the chosen boards.");

        // 4. Full evaluation of the best-ranked arrangements. Ranking: activated glyphs, then
        //    active threshold bonuses (slot order changes their requirements), then fewest
        //    points, then total stat delivered into glyph radii.
        (LayoutOptimizerResult Result, ArrangementScore Score)? best = null;
        foreach (var candidate in candidates.OrderBy(c => c.PreScore).Take(RerankCount))
        {
            var evaluated = Evaluate(candidate.Boards, slotBoards, placements, request, radius, nodesBySnoId, data);
            if (evaluated is null)
                continue;
            var (result, score) = evaluated.Value;
            if (best is null || score.Beats(best.Value.Score))
                best = (result, score);
        }
        if (best is null)
            return LayoutOptimizerResult.Failed("No arrangement of the chosen boards could be solved.");

        var final = best.Value.Result;
        return new LayoutOptimizerResult
        {
            Success = true,
            Layout = final.Layout,
            GlyphPlacements = final.GlyphPlacements,
            Plan = final.Plan,
            Targets = final.Targets,
            Notes = notes.Concat(final.Notes).ToList(),
        };
    }

    // ── Glyph assignment ─────────────────────────────────────────────────

    /// <summary>Total of the glyph's primary stat on the board's nodes within radius of its socket.</summary>
    private static double AttainableStat(
        ParagonBoardDef board, ParagonGlyphDef glyph, int radius,
        IReadOnlyDictionary<string, ParagonNodeDef> nodesBySnoId)
    {
        if (GlyphInfo.PrimarySourceAttribute(glyph) is not string attribute)
            return 0;
        (int X, int Y)? socket = null;
        foreach (var placement in board.Nodes)
        {
            if (nodesBySnoId[placement.Node].Kind == ParagonNodeKind.GlyphSocket)
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
            total += nodesBySnoId[placement.Node].Attributes
                .Where(a => !a.IsThresholdBonus && a.Attribute == attribute && a.Value is double)
                .Sum(a => a.Value!.Value);
        }
        return total;
    }

    /// <summary>
    /// Brute-force injective glyph→slot assignment maximizing (activatable count, assigned count,
    /// total attainable stat). Glyphs that fit no free socket are dropped with a note.
    /// </summary>
    private static List<GlyphPlacement> AssignGlyphs(
        IReadOnlyList<ParagonBoardDef> slotBoards, LayoutOptimizerRequest request, int radius,
        IReadOnlyDictionary<string, ParagonNodeDef> nodesBySnoId, List<string> notes)
    {
        var glyphs = request.Glyphs.DistinctBy(g => g.InternalName).ToList();
        if (glyphs.Count == 0)
            return [];

        var attainable = new double[glyphs.Count, slotBoards.Count];
        for (int g = 0; g < glyphs.Count; g++)
        {
            for (int s = 0; s < slotBoards.Count; s++)
                attainable[g, s] = AttainableStat(slotBoards[s], glyphs[g], radius, nodesBySnoId);
        }

        int[]? best = null;
        (int Active, int Assigned, double Total) bestScore = default;
        var assignment = new int[glyphs.Count]; // slot index, or -1 for unplaced
        var used = new bool[slotBoards.Count];
        void Search(int g)
        {
            if (g == glyphs.Count)
            {
                int active = 0, assigned = 0;
                double total = 0;
                for (int i = 0; i < glyphs.Count; i++)
                {
                    if (assignment[i] < 0)
                        continue;
                    assigned++;
                    total += attainable[i, assignment[i]];
                    if (attainable[i, assignment[i]] >= request.RequiredStat - 1e-9)
                        active++;
                }
                var score = (active, assigned, total);
                if (best is null || score.CompareTo(bestScore) > 0)
                {
                    best = (int[])assignment.Clone();
                    bestScore = score;
                }
                return;
            }
            assignment[g] = -1;
            Search(g + 1);
            for (int s = 0; s < slotBoards.Count; s++)
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

        var placements = new List<GlyphPlacement>();
        for (int g = 0; g < glyphs.Count; g++)
        {
            if (best![g] < 0)
            {
                notes.Add($"No socket left for {glyphs[g].Name} — the layout has {slotBoards.Count} board(s).");
                continue;
            }
            double stat = attainable[g, best[g]];
            bool canActivate = stat >= request.RequiredStat - 1e-9;
            if (!canActivate)
            {
                notes.Add($"{glyphs[g].Name} can see at most {stat:0} of the required {request.RequiredStat:0} " +
                          $"stat on {BoardName(slotBoards[best[g]])} — no chosen board activates it fully.");
            }
            placements.Add(new GlyphPlacement(best[g], glyphs[g], stat, canActivate));
        }
        return placements;
    }

    // ── Arrangement enumeration ──────────────────────────────────────────

    private static readonly (int Dx, int Dy, BoardEdge Edge)[] Deltas =
    [
        (0, -1, BoardEdge.Top), (0, 1, BoardEdge.Bottom), (-1, 0, BoardEdge.Left), (1, 0, BoardEdge.Right),
    ];

    /// <summary>
    /// Connected position sets of the given size containing the starter's gate cell (0, −1) and
    /// excluding the starter cell (0, 0). Positions use board-grid coordinates, +y down.
    /// </summary>
    private static List<List<(int X, int Y)>> EnumeratePositionSets(int size)
    {
        var results = new List<List<(int X, int Y)>>();
        var seen = new HashSet<string>();
        void Grow(List<(int X, int Y)> set)
        {
            if (set.Count == size)
            {
                string key = string.Join(";", set.OrderBy(p => p.X).ThenBy(p => p.Y));
                if (seen.Add(key))
                    results.Add([.. set]);
                return;
            }
            var frontier = new HashSet<(int X, int Y)>();
            foreach (var (x, y) in set)
            {
                foreach (var (dx, dy, _) in Deltas)
                {
                    var next = (X: x + dx, Y: y + dy);
                    if (next != (0, 0) && !set.Contains(next))
                        frontier.Add(next);
                }
            }
            foreach (var next in frontier)
            {
                set.Add(next);
                Grow(set);
                set.RemoveAt(set.Count - 1);
            }
        }
        Grow([(0, -1)]);
        return results;
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        foreach (var rest in Permutations(items[1..]))
        {
            for (int i = 0; i <= rest.Length; i++)
            {
                var result = new int[items.Length];
                Array.Copy(rest, result, i);
                result[i] = items[0];
                Array.Copy(rest, i, result, i + 1, rest.Length - i);
                yield return result;
            }
        }
    }

    /// <summary>
    /// Places the ordered board slots onto the position set, picking each board's rotation by
    /// internal cost, and scores the whole arrangement heuristically. Returns null when a board
    /// has no rotation with a gate facing its parent.
    /// </summary>
    private static (double PreScore, List<PlacedBoard> Boards, List<int> SlotByPosition)? BuildCombo(
        List<(int X, int Y)> positions, int[] slotOrder, List<BoardProfile> profiles, HashSet<int> glyphSlots)
    {
        // BFS order from the starter gate so every board's parent is already placed.
        var orderIndex = new Dictionary<(int X, int Y), int>();
        for (int i = 0; i < positions.Count; i++)
            orderIndex[positions[i]] = i;

        var visitOrder = new List<(int X, int Y)>();
        var parentOf = new Dictionary<(int X, int Y), ((int X, int Y) Pos, BoardEdge Edge)>();
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((0, -1));
        var seen = new HashSet<(int X, int Y)> { (0, -1) };
        while (queue.Count > 0)
        {
            var pos = queue.Dequeue();
            visitOrder.Add(pos);
            foreach (var (dx, dy, edge) in Deltas)
            {
                var next = (X: pos.X + dx, Y: pos.Y + dy);
                if (orderIndex.ContainsKey(next) && seen.Add(next))
                {
                    parentOf[next] = (pos, edge);
                    queue.Enqueue(next);
                }
            }
        }
        if (visitOrder.Count != positions.Count)
            return null; // disconnected from the starter gate

        var placed = new List<PlacedBoard> { new() { Board = profiles[0].Board } };
        var slotAtPosition = new Dictionary<(int X, int Y), int> { [(0, 0)] = 0 };
        double preScore = 0;
        var occupied = new HashSet<(int X, int Y)>(positions) { (0, 0) };

        foreach (var pos in visitOrder)
        {
            int slot = slotOrder[orderIndex[pos]];
            var profile = profiles[slot];

            // Entry edges: sides facing an occupied neighbor (the starter counts only via its gate).
            var entryEdges = new List<BoardEdge>();
            foreach (var (dx, dy, edge) in Deltas)
            {
                var neighbor = (X: pos.X + dx, Y: pos.Y + dy);
                if (!occupied.Contains(neighbor))
                    continue;
                if (neighbor == (0, 0) && pos != (0, -1))
                    continue;
                entryEdges.Add(edge);
            }

            var rotation = profile.BestRotation(entryEdges, wantSocket: glyphSlots.Contains(slot));
            if (rotation is not var (steps, internalCost))
                return null;

            int depth = pos == (0, -1)
                ? 1
                : 1 + BoardDepth(pos, parentOf);
            preScore += depth * profile.Board.Width + internalCost;

            var (parentPos, attachEdge) = pos == (0, -1)
                ? ((0, 0), BoardEdge.Top)
                : (parentOf[pos].Pos, parentOf[pos].Edge);
            placed.Add(new PlacedBoard
            {
                Board = profile.Board,
                ParentSlot = slotAtPosition[parentPos],
                AttachEdge = attachEdge,
                RotationSteps = steps,
            });
            slotAtPosition[pos] = placed.Count - 1;
        }

        // Boards are appended in visit order; remember which original slot sits where.
        var slotByPosition = new List<int> { 0 };
        slotByPosition.AddRange(visitOrder.Select(pos => slotOrder[orderIndex[pos]]));
        return (preScore, placed, slotByPosition);
    }

    private static int BoardDepth((int X, int Y) pos, Dictionary<(int X, int Y), ((int X, int Y) Pos, BoardEdge Edge)> parentOf)
    {
        int depth = 0;
        while (parentOf.TryGetValue(pos, out var parent))
        {
            depth++;
            pos = parent.Pos;
        }
        return depth;
    }

    private readonly record struct ArrangementScore(int GlyphsMet, int ThresholdsMet, int Points, double Delivered)
    {
        public bool Beats(ArrangementScore other) =>
            GlyphsMet != other.GlyphsMet ? GlyphsMet > other.GlyphsMet
            : ThresholdsMet != other.ThresholdsMet ? ThresholdsMet > other.ThresholdsMet
            : Points != other.Points ? Points < other.Points
            : Delivered > other.Delivered;
    }

    /// <summary>Full solve of one arrangement: targets are every board's legendary node(s).</summary>
    private static (LayoutOptimizerResult Result, ArrangementScore Score)? Evaluate(
        List<PlacedBoard> placed, IReadOnlyList<ParagonBoardDef> slotBoards,
        List<GlyphPlacement> placements, LayoutOptimizerRequest request,
        int radius, IReadOnlyDictionary<string, ParagonNodeDef> nodesBySnoId, ParagonData data)
    {
        ParagonLayout layout;
        ComposedGraph graph;
        try
        {
            layout = new ParagonLayout(placed);
            graph = ComposedGraph.Build(layout, nodesBySnoId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        var targets = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Legendary)
            .Select(v => v.Cell)
            .ToList();

        // Glyph placements refer to original slot indices; map them onto this arrangement's slots.
        var socketBySlot = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);
        var slotForBoard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < layout.Boards.Count; slot++)
            slotForBoard[layout.Boards[slot].Board.InternalName] = slot;

        var mapped = new List<GlyphPlacement>();
        var goals = new List<GlyphGoal>();
        foreach (var placement in placements)
        {
            // Placement slots index the pre-arrangement board list; resolve by board identity
            // (boards are unique within a layout).
            int slot = slotForBoard.GetValueOrDefault(slotBoards[placement.BoardSlot].InternalName, -1);
            if (slot < 0 || !socketBySlot.TryGetValue(slot, out var socket))
                continue;
            mapped.Add(placement with { BoardSlot = slot });
            if (GlyphInfo.PrimarySourceAttribute(placement.Glyph) is string attribute)
                goals.Add(new GlyphGoal(socket, attribute, request.RequiredStat, radius, placement.Glyph.Name));
        }

        var plan = PlanSolver.Solve(graph, new PlanRequest { Targets = targets, GlyphGoals = goals });
        if (!plan.Success)
            return null;

        var stats = BuildStats.Compute(graph, plan.PurchasedCells, data,
            request.NonParagonStat, layout.Boards[0].Board.ClassName);
        var score = new ArrangementScore(
            plan.GlyphOutcomes.Count(o => o.Met),
            stats.ThresholdsMet,
            plan.PointsSpent,
            plan.GlyphOutcomes.Sum(o => o.AchievedTotal));
        var result = new LayoutOptimizerResult
        {
            Success = true,
            Layout = layout,
            GlyphPlacements = mapped,
            Plan = plan,
            Targets = targets,
            Notes = [],
        };
        return (result, score);
    }

    private static string BoardName(ParagonBoardDef board) => board.Name ?? board.InternalName;

    // ── Per-board geometry ───────────────────────────────────────────────

    /// <summary>
    /// A board's rotated geometry: per rotation, the gate cell of each edge and the BFS distance
    /// from each gate to the glyph socket and the legendary node.
    /// </summary>
    private sealed class BoardProfile
    {
        public ParagonBoardDef Board { get; }

        // [rotation][edge] → internal cost, or null when that edge has no gate.
        private readonly int?[,] _socketCost = new int?[4, 4];
        private readonly int?[,] _legendaryCost = new int?[4, 4];

        public BoardProfile(ParagonBoardDef board, IReadOnlyDictionary<string, ParagonNodeDef> nodesBySnoId)
        {
            Board = board;
            int width = board.Width;
            for (int rotation = 0; rotation < 4; rotation++)
            {
                var cells = new Dictionary<(int X, int Y), ParagonNodeKind>();
                foreach (var placement in board.Nodes)
                {
                    var (x, y) = ParagonLayout.Rotate(placement.X, placement.Y, rotation, width);
                    cells[(x, y)] = nodesBySnoId[placement.Node].Kind;
                }
                var socket = cells.FirstOrDefault(kv => kv.Value == ParagonNodeKind.GlyphSocket).Key;
                bool hasSocket = cells.ContainsValue(ParagonNodeKind.GlyphSocket);
                var legendary = cells.FirstOrDefault(kv => kv.Value == ParagonNodeKind.Legendary).Key;
                bool hasLegendary = cells.ContainsValue(ParagonNodeKind.Legendary);

                foreach (var (edge, edgeIndex) in EdgeIndices)
                {
                    var gate = ParagonLayout.GateCell(edge, width);
                    if (!cells.TryGetValue(gate, out var kind) || kind != ParagonNodeKind.Gate)
                        continue;
                    var dist = Bfs(cells, gate);
                    _socketCost[rotation, edgeIndex] = hasSocket ? dist.GetValueOrDefault(socket, int.MaxValue / 4) : 0;
                    _legendaryCost[rotation, edgeIndex] = hasLegendary ? dist.GetValueOrDefault(legendary, int.MaxValue / 4) : 0;
                }
            }
        }

        private static readonly (BoardEdge Edge, int Index)[] EdgeIndices =
            [(BoardEdge.Top, 0), (BoardEdge.Bottom, 1), (BoardEdge.Left, 2), (BoardEdge.Right, 3)];

        /// <summary>
        /// The rotation minimizing entry cost to the legendary (and the socket when a glyph sits
        /// here), over the given entry edges — this board's own sides that face a neighbor.
        /// </summary>
        public (int RotationSteps, int Cost)? BestRotation(IReadOnlyList<BoardEdge> entryEdges, bool wantSocket)
        {
            (int Steps, int Cost)? best = null;
            for (int rotation = 0; rotation < 4; rotation++)
            {
                foreach (var entry in entryEdges)
                {
                    int edgeIndex = EdgeIndices.First(e => e.Edge == entry).Index;
                    if (_legendaryCost[rotation, edgeIndex] is not int legendary)
                        continue;
                    int cost = legendary + (wantSocket ? _socketCost[rotation, edgeIndex] ?? 0 : 0);
                    if (best is null || cost < best.Value.Cost)
                        best = (rotation, cost);
                }
            }
            return best;
        }

        private static Dictionary<(int X, int Y), int> Bfs(
            Dictionary<(int X, int Y), ParagonNodeKind> cells, (int X, int Y) from)
        {
            var dist = new Dictionary<(int X, int Y), int> { [from] = 0 };
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                int next = dist[(x, y)] + 1;
                foreach (var (dx, dy, _) in Deltas)
                {
                    var cell = (x + dx, y + dy);
                    if (cells.ContainsKey(cell) && dist.TryAdd(cell, next))
                        queue.Enqueue(cell);
                }
            }
            return dist;
        }
    }
}
