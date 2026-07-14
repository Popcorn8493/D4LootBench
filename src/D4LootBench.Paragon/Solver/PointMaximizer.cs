using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// What leftover points should chase: <see cref="Attributes"/> holds raw attribute names
/// (e.g. "Intelligence_Core", "Life_Percent"); empty means the four core stats. When
/// <see cref="PreferRare"/> is set, reachable rare (yellow) nodes are bought first, then the
/// remaining budget goes to the focused stats. <see cref="ActivateThresholds"/> inserts a
/// phase that buys whatever stat unmet rare-node threshold bonuses are short of (requires a
/// <see cref="ThresholdContext"/>).
/// </summary>
public sealed record MaximizeFocus(
    IReadOnlyCollection<string> Attributes, bool PreferRare, bool ActivateThresholds = false)
{
    public static readonly IReadOnlyList<string> CoreStats =
        ["Strength_Core", "Intelligence_Core", "Willpower_Core", "Dexterity_Core"];

    /// <summary>
    /// Per-attribute priority multipliers (attribute → weight, missing = 1). Weights scale the
    /// normalized per-point value, so a weight-3 stat is chased three times as hard as a
    /// weight-1 stat of equal magnitude.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Weights { get; init; }

    /// <summary>
    /// With <see cref="PreferRare"/>: buy rares by threshold attainability first — bonuses
    /// already met, then realistically meetable (the boards can still supply the deficit),
    /// then plain rares, and unattainable-threshold rares last. Needs a
    /// <see cref="ThresholdContext"/>; without one the cheapest-first order is kept.
    /// </summary>
    public bool RealisticRares { get; init; }

    /// <summary>
    /// Fraction (0–1) of the leftover budget reserved for survivability: a dedicated phase buys
    /// the best defensive value available (life, armor, resists, dodge, healing…) BEFORE the
    /// focused-stat spend, defensive rares pass the focus filter, and the reallocation pass
    /// treats defensive purchases as valuable instead of free fodder.
    /// </summary>
    public double DefenseShare { get; init; }

    /// <summary>
    /// The socketed attribute-mapped glyphs' pull on the spend (see <see cref="GlyphDelivery"/>):
    /// source stat bought inside a glyph's radius counts again, scaled by the glyph's relative
    /// strength. Null falls back to the request's glyph goals at weight 1; empty means no pull.
    /// </summary>
    public IReadOnlyList<GlyphDelivery>? GlyphDeliveries { get; init; }

    /// <summary>Attributes that keep the character alive — the defense basket's membership.</summary>
    public static bool IsDefensive(string attribute)
    {
        foreach (var marker in DefensiveMarkers)
        {
            if (attribute.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] DefensiveMarkers =
    [
        "Hitpoints", "Armor", "Resist", "Dodge", "Healing", "Regen",
        "Fortify", "Barrier", "Damage_Reduction", "CC_Duration", "Life",
    ];
}

/// <summary>What threshold checks need beyond the graph: requirements scale with attachment
/// slot and check the character TOTAL, so the non-paragon (level/gear) offsets matter, and
/// glyph node buffs (<see cref="GlyphNodeBuffs"/>) multiply what in-radius nodes grant.</summary>
public sealed record ThresholdContext(
    ParagonData Data,
    string? ClassName,
    NonParagonStats NonParagonStats,
    IReadOnlyDictionary<CellRef, double>? CellMultipliers = null);

public sealed record MaximizeOutcome(
    IReadOnlyList<CellRef> AddedCells,
    int RaresAdded,
    IReadOnlyDictionary<string, double> Gains,
    IReadOnlyList<string> Notes,
    int ThresholdsActivated);

/// <summary>
/// Spends a point budget extending an already-solved tree. Greedy frontier growth: each round a
/// multi-source Dijkstra finds the reachable candidate with the best value-per-point path and
/// absorbs it, until the budget is gone or nothing valuable is reachable. A candidate's score is
/// the value of its WHOLE path (absorbing buys every intermediate node), equally cheap routes
/// prefer the stat-bearing one, and glyph node buffs count toward a cell's effective value.
/// Values are normalized per attribute (a node's contribution is measured against that
/// attribute's average per-node magnitude) so flat and percent stats compete fairly. Node rules
/// apply: excluded cells are never entered, avoided cells are routed around, and Limit groups
/// are enforced hard — a full group is off-limits, and a path that would jump a group past its
/// cap is rejected.
/// </summary>
public static class PointMaximizer
{
    private const int Infinity = int.MaxValue / 4;

    public static MaximizeOutcome Extend(
        ComposedGraph graph, ISet<CellRef> purchased, int budget, MaximizeFocus focus, PlanRequest request,
        ThresholdContext? thresholds = null)
    {
        int n = graph.Vertices.Count;
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        var (weightByCell, blockedCells, limitRules) = PlanSolver.BuildSteering(request, cellsByGroup);

        var weights = new int[n];
        Array.Fill(weights, 1);
        foreach (var (cell, weight) in weightByCell)
        {
            if (graph.TryGetVertex(cell, out int v))
                weights[v] = Math.Max(1, weight);
        }
        var blocked = new bool[n];
        foreach (var cell in blockedCells)
        {
            if (graph.TryGetVertex(cell, out int v))
                blocked[v] = true;
        }
        weights[graph.StartVertex] = 0;
        blocked[graph.StartVertex] = false;

        var tree = new HashSet<int> { graph.StartVertex };
        foreach (var cell in purchased)
        {
            if (graph.TryGetVertex(cell, out int v))
                tree.Add(v);
        }

        // User excludes only — snapshotted before limit-blocking mutates the shared array, so
        // the reallocation pass can tell "never touch" apart from "group currently at cap".
        var userBlocked = (bool[])blocked.Clone();

        // Limit groups: full groups become off-limits, routing detours around group members
        // (the penalty keeps traversal from burning the allowance), and candidate paths that
        // would still jump a group past its cap are rejected.
        var limits = new LimitTracker(graph, limitRules, cellsByGroup, tree);
        limits.PenalizeGroups(weights, tree);
        limits.BlockFullGroups(blocked, tree);

        var attributes = focus.Attributes.Count > 0 ? focus.Attributes : MaximizeFocus.CoreStats;
        var attributeSet = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        // The glyph pull: explicit deliveries when the caller knows the socketed glyphs (their
        // weights carry each glyph's relative strength), else the activation goals at weight 1.
        var deliveries = focus.GlyphDeliveries ?? request.GlyphGoals
            .Select(g => new GlyphDelivery(g.Socket, g.SourceAttribute, g.Radius, 1.0))
            .ToList();
        // Gains are also reported for glyph-source stats, even when they aren't focused.
        var gainSet = new HashSet<string>(attributeSet, StringComparer.OrdinalIgnoreCase);
        foreach (var delivery in deliveries)
            gainSet.Add(delivery.SourceAttribute);
        foreach (var goal in request.GlyphGoals)
            gainSet.Add(goal.SourceAttribute);

        // Glyph node buffs make a cell's grants worth more toward totals and thresholds —
        // selection values the effective grant. Activation math stays raw (the game counts
        // purchased stat), and reported gains stay raw for the same reason.
        double MultiplierOf(int v) =>
            thresholds?.CellMultipliers?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0;

        // Per-attribute average per-node magnitude across the layout, for unit-fair scoring.
        // User-excluded cells are not purchasable, so they don't belong in the pool.
        var attributeMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        for (int v = 0; v < n; v++)
        {
            if (userBlocked[v])
                continue;
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !gainSet.Contains(a.Attribute))
                    continue;
                var (sum, count) = attributeMean.GetValueOrDefault(a.Attribute);
                attributeMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
            }
        }

        // Glyph delivery: a point of source stat inside an active glyph's radius is worth its raw
        // value AND what the glyph converts it into — count it again for each covering glyph,
        // scaled by that glyph's weight (activation math elsewhere stays raw).
        var glyphBonus = new double[n];
        foreach (var delivery in deliveries)
        {
            if (!attributeMean.TryGetValue(delivery.SourceAttribute, out var mean))
                continue;
            for (int v = 0; v < n; v++)
            {
                var cell = graph.Vertices[v].Cell;
                if (cell.BoardSlot != delivery.Socket.BoardSlot
                    || Math.Abs(cell.X - delivery.Socket.X) + Math.Abs(cell.Y - delivery.Socket.Y) > delivery.Radius)
                    continue;
                foreach (var a in graph.Vertices[v].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is double value
                        && string.Equals(a.Attribute, delivery.SourceAttribute, StringComparison.OrdinalIgnoreCase))
                        glyphBonus[v] += value / (mean.Sum / mean.Count) * delivery.Weight;
                }
            }
        }

        double WeightOf(string attribute) =>
            focus.Weights?.GetValueOrDefault(attribute, 1.0) ?? 1.0;

        double NormValue(int v)
        {
            double total = glyphBonus[v];
            double factor = MultiplierOf(v);
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !attributeSet.Contains(a.Attribute))
                    continue;
                if (!attributeMean.TryGetValue(a.Attribute, out var mean))
                    continue; // the attribute exists only on excluded cells
                total += value * factor / (mean.Sum / mean.Count) * WeightOf(a.Attribute);
            }
            return total;
        }

        // The defense basket: its own normalization (per defensive attribute across the layout),
        // independent of which stats are focused — survivability is measured, not opted into.
        var defenseMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (focus.DefenseShare > 0)
        {
            for (int v = 0; v < n; v++)
            {
                if (userBlocked[v])
                    continue;
                foreach (var a in graph.Vertices[v].Node.Attributes)
                {
                    if (a.IsThresholdBonus || a.Value is not double value || !MaximizeFocus.IsDefensive(a.Attribute))
                        continue;
                    var (sum, count) = defenseMean.GetValueOrDefault(a.Attribute);
                    defenseMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
                    gainSet.Add(a.Attribute); // report defensive gains too
                }
            }
        }

        double DefenseValue(int v)
        {
            if (focus.DefenseShare <= 0)
                return 0;
            double total = 0;
            double factor = MultiplierOf(v);
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value
                    || !defenseMean.TryGetValue(a.Attribute, out var mean))
                    continue;
                total += value * factor / (mean.Sum / mean.Count);
            }
            return total;
        }

        var dist = new int[n];
        var from = new int[n];
        var added = new List<CellRef>();
        int raresAdded = 0;
        int remaining = budget;
        var gains = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Per-vertex values, precomputed: candidate scoring counts the WHOLE path (absorbing a
        // candidate buys every intermediate node too, so their value belongs in the ratio), and
        // the Dijkstra tie-break prefers the stat-bearing route among equally cheap ones.
        var normByVertex = new double[n];
        var defenseByVertex = new double[n];
        for (int v = 0; v < n; v++)
        {
            normByVertex[v] = NormValue(v);
            defenseByVertex[v] = DefenseValue(v);
        }

        // A board crossing's gate PAIR costs one point in-game (the attached side's gate is
        // auto-purchased free — see GateCrossings). Pair gates are always consecutive on a
        // path, so "partner is the previous walked vertex" covers the within-path case.
        var gatePair = GateCrossings.PairMap(graph);

        int PathCost(int vertex)
        {
            var (cost, _) = PathCostAndValue(vertex, normByVertex);
            return cost;
        }

        // Cost and accumulated value of the not-yet-owned path down to the tree. Value rides
        // the same guard as cost: a gate pair grants its attributes once, so the free half
        // contributes neither (both halves carry the same node def, so the sum is right).
        (int Cost, double Value) PathCostAndValue(int vertex, double[] value)
        {
            int cost = 0;
            double total = 0;
            int prev = -1;
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                int pair = gatePair[v];
                if (pair < 0 || (pair != prev && !tree.Contains(pair)))
                {
                    cost++;
                    total += value[v];
                }
                prev = v;
            }
            return (cost, total);
        }

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                tree.Add(v);
                var cell = graph.Vertices[v].Cell;
                purchased.Add(cell);
                added.Add(cell);
                // The walk runs candidate→tree, so a crossing's far gate is added (and paid)
                // before the near gate finds its partner already in the tree and rides free.
                int pair = gatePair[v];
                if (pair < 0 || !tree.Contains(pair))
                    remaining--;
                var node = graph.Vertices[v].Node;
                if (node.Kind == ParagonNodeKind.Rare)
                    raresAdded++;
                foreach (var a in node.Attributes)
                {
                    if (a.IsThresholdBonus || a.Value is not double value || !gainSet.Contains(a.Attribute))
                        continue;
                    gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) + value;
                }
                limits.OnAbsorbed(v);
            }
            limits.BlockFullGroups(blocked, tree);
        }

        // Phase 1: rare nodes, by focused value-per-point of the whole path (ties: cheaper).
        // With RealisticRares, threshold attainability leads instead: bonuses already met, then
        // ones the boards can realistically still supply, then plain rares, and rares whose
        // bonus is out of reach last — so points chase "good" rares, not just near ones.
        // When the user picked explicit focus stats, a rare that DIRECTLY grants none of them is
        // skipped outright — no points, and crucially no Limit-group allowance, get spent pathing
        // to a rare the build doesn't want. Threshold bonuses deliberately don't qualify a rare:
        // nearly every rare's bonus touches some common stat (resists, armor), which would let
        // off-build life rares sneak back in. Target such a rare manually if its bonus matters.
        if (focus.PreferRare)
        {
            bool realistic = focus.RealisticRares && thresholds is not null;
            bool explicitFocus = focus.Attributes.Count > 0;
            bool WantedRare(int v) => !explicitFocus || normByVertex[v] > 1e-9
                || defenseByVertex[v] > 1e-9; // with a defense share, survivability rares are wanted

            // Every rare with a threshold is a status candidate — BuildStats only reports
            // PURCHASED cells on its own, and the whole point here is ranking unbought ones.
            var thresholdRareCells = new List<CellRef>();
            // A met (or realistically meetable) bonus is real value the moment the rare is
            // bought — its focused/defensive magnitude joins the score, normalized against
            // the layout's other bonuses so it competes fairly with direct grants.
            var bonusNorm = new double[n];
            if (realistic)
            {
                var bonusMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
                for (int v = 0; v < n; v++)
                {
                    if (graph.Vertices[v].Node.Kind == ParagonNodeKind.Rare
                        && graph.Vertices[v].Node.Thresholds.Count > 0)
                        thresholdRareCells.Add(graph.Vertices[v].Cell);
                    if (userBlocked[v])
                        continue;
                    foreach (var a in graph.Vertices[v].Node.Attributes)
                    {
                        if (!a.IsThresholdBonus || a.Value is not double value)
                            continue;
                        if (!attributeSet.Contains(a.Attribute)
                            && !(focus.DefenseShare > 0 && MaximizeFocus.IsDefensive(a.Attribute)))
                            continue;
                        var (sum, count) = bonusMean.GetValueOrDefault(a.Attribute);
                        bonusMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
                    }
                }
                for (int v = 0; v < n; v++)
                {
                    double factor = MultiplierOf(v);
                    foreach (var a in graph.Vertices[v].Node.Attributes)
                    {
                        if (!a.IsThresholdBonus || a.Value is not double value
                            || !bonusMean.TryGetValue(a.Attribute, out var mean))
                            continue;
                        double weight = attributeSet.Contains(a.Attribute)
                            ? WeightOf(a.Attribute)
                            : focus.DefenseShare;
                        bonusNorm[v] += value * factor / (mean.Sum / mean.Count) * weight;
                    }
                }
            }

            while (remaining > 0)
            {
                Func<int, int> rank = _ => 0;
                Func<int, double> bonusValue = _ => 0;
                if (realistic)
                {
                    // Recomputed each round: an absorbed rare changes both Have and supply.
                    var report = BuildStats.Compute(graph, purchased, thresholds!.Data,
                        thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers,
                        evaluate: thresholdRareCells);
                    var statusByCell = report.Thresholds
                        .GroupBy(t => t.Cell)
                        .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Requirement - t.Have).First());
                    var supply = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    for (int v = 0; v < n; v++)
                    {
                        if (tree.Contains(v))
                            continue;
                        double multiplier = thresholds.CellMultipliers
                            ?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0;
                        foreach (var a in graph.Vertices[v].Node.Attributes)
                        {
                            if (a.IsThresholdBonus || a.Value is not double value)
                                continue;
                            supply[a.Attribute] = supply.GetValueOrDefault(a.Attribute) + value * multiplier;
                        }
                    }
                    rank = v =>
                    {
                        if (!statusByCell.TryGetValue(graph.Vertices[v].Cell, out var status))
                            return 2; // no threshold bonus — plain stat rare
                        if (status.Met)
                            return 0; // bonus turns on the moment it's bought
                        string coreKey = status.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                            ? status.Attribute[..^"_Total".Length] + "_Core"
                            : status.Attribute;
                        double deficit = status.Requirement - status.Have;
                        return supply.GetValueOrDefault(coreKey) >= deficit ? 1 : 3;
                    };
                    // Met bonuses score full; realistically meetable ones at a discount (the
                    // deficit still has to be bought); unattainable ones are worth nothing.
                    bonusValue = v => rank(v) switch { 0 => bonusNorm[v], 1 => 0.5 * bonusNorm[v], _ => 0 };
                }

                RunDijkstra(graph, tree, weights, blocked, dist, from, normByVertex);
                var rejected = new HashSet<int>();
                int best;
                while (true)
                {
                    best = -1;
                    int bestRank = 0;
                    int bestCost = 0;
                    double bestRatio = 0;
                    for (int v = 0; v < n; v++)
                    {
                        if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity || rejected.Contains(v)
                            || graph.Vertices[v].Node.Kind != ParagonNodeKind.Rare || !WantedRare(v))
                            continue;
                        var (cost, pathValue) = PathCostAndValue(v, normByVertex);
                        if (cost > remaining)
                            continue;
                        // Within an attainability rank, value-per-point decides — a slightly
                        // farther but much better rare beats the merely nearest one. An active
                        // (or affordable) threshold bonus is part of the rare's value.
                        int nodeRank = rank(v);
                        double ratio = (pathValue + bonusValue(v)) / cost;
                        if (best < 0 || nodeRank < bestRank
                            || (nodeRank == bestRank && (ratio > bestRatio || (ratio == bestRatio && cost < bestCost))))
                        {
                            best = v;
                            bestRank = nodeRank;
                            bestCost = cost;
                            bestRatio = ratio;
                        }
                    }
                    if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                        break;
                    rejected.Add(best);
                }
                if (best < 0)
                    break;
                Absorb(best);
            }
        }

        // Phase T: buy whatever stat unmet rare-node threshold bonuses are short of, smallest
        // deficit first. Recomputed after every activation — a met bonus can grant the stat
        // that unlocks another (the BuildStats fixpoint), and rares from phase 1 count too.
        var notes = new List<string>();
        int metBefore = 0, metAfter = 0;
        if (focus.ActivateThresholds && thresholds is not null)
        {
            metBefore = BuildStats.Compute(graph, purchased, thresholds.Data,
                thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers).ThresholdsMet;
            var attempted = new HashSet<CellRef>();
            while (remaining > 0)
            {
                var report = BuildStats.Compute(graph, purchased, thresholds.Data,
                    thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
                var next = report.Thresholds
                    .Where(t => !t.Met && !attempted.Contains(t.Cell))
                    .OrderBy(t => t.Requirement - t.Have)
                    .FirstOrDefault();
                if (next is null)
                    break;
                attempted.Add(next.Cell);

                // "Strength_Total" requirements are fed by purchasing "Strength_Core" nodes.
                string targetAttribute = next.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                    ? next.Attribute[..^"_Total".Length] + "_Core"
                    : next.Attribute;
                double needed = next.Requirement - next.Have;

                double ValueOf(int v) => graph.Vertices[v].Node.Attributes
                        .Where(a => !a.IsThresholdBonus && a.Value is not null
                            && string.Equals(a.Attribute, targetAttribute, StringComparison.OrdinalIgnoreCase))
                        .Sum(a => a.Value!.Value)
                    * (thresholds.CellMultipliers?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0);

                var targetValue = new double[n];
                for (int v = 0; v < n; v++)
                    targetValue[v] = ValueOf(v);

                RunDijkstra(graph, tree, weights, blocked, dist, from, targetValue);
                double reachable = 0;
                for (int v = 0; v < n; v++)
                {
                    if (!tree.Contains(v) && !blocked[v] && dist[v] < Infinity)
                        reachable += ValueOf(v);
                }
                if (reachable < needed - 1e-9)
                {
                    notes.Add($"Cannot activate {next.NodeName}: needs {needed:0} more " +
                              $"{ParagonDisplay.FormatAttributeName(next.Attribute)} and only {reachable:0} " +
                              $"is buyable on the boards — add {needed - reachable:0}+ from level/gear (Character Stats).");
                    continue;
                }

                gainSet.Add(targetAttribute);
                double gained = 0;
                while (gained < needed - 1e-9 && remaining > 0)
                {
                    RunDijkstra(graph, tree, weights, blocked, dist, from, targetValue);
                    var rejected = new HashSet<int>();
                    int best;
                    while (true)
                    {
                        best = -1;
                        double bestRatio = 0;
                        for (int v = 0; v < n; v++)
                        {
                            if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity || rejected.Contains(v))
                                continue;
                            if (targetValue[v] <= 0)
                                continue;
                            var (cost, pathValue) = PathCostAndValue(v, targetValue);
                            if (cost > remaining)
                                continue;
                            double ratio = pathValue / cost;
                            if (best < 0 || ratio > bestRatio)
                            {
                                best = v;
                                bestRatio = ratio;
                            }
                        }
                        if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                            break;
                        rejected.Add(best);
                    }
                    if (best < 0)
                        break;
                    int before = added.Count;
                    Absorb(best);
                    for (int i = before; i < added.Count; i++)
                    {
                        if (graph.TryGetVertex(added[i], out int v))
                            gained += ValueOf(v);
                    }
                }
                if (gained < needed - 1e-9 && remaining <= 0)
                {
                    notes.Add($"Ran out of points {needed - gained:0} short of activating {next.NodeName} " +
                              $"({ParagonDisplay.FormatAttributeName(next.Attribute)}).");
                }
            }
        }

        // Phase D: the guaranteed survivability slice. A fraction of what's left buys the best
        // defensive value reachable, regardless of the (usually offense-heavy) focus weights —
        // "balance in toughness where available" as an explicit budget, not a hope.
        if (focus.DefenseShare > 0 && remaining > 0)
        {
            // Proportional, not floored: a 2-point budget at a 0.15 share buys no defense.
            int defenseBudget = (int)Math.Round(remaining * focus.DefenseShare);
            while (defenseBudget > 0 && remaining > 0)
            {
                RunDijkstra(graph, tree, weights, blocked, dist, from, defenseByVertex);
                var rejected = new HashSet<int>();
                int best;
                while (true)
                {
                    best = -1;
                    double bestRatio = 0;
                    for (int v = 0; v < n; v++)
                    {
                        if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity || rejected.Contains(v))
                            continue;
                        if (defenseByVertex[v] <= 0)
                            continue;
                        var (cost, pathValue) = PathCostAndValue(v, defenseByVertex);
                        if (cost > remaining)
                            continue;
                        double ratio = pathValue / cost;
                        if (best < 0 || ratio > bestRatio)
                        {
                            best = v;
                            bestRatio = ratio;
                        }
                    }
                    if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                        break;
                    rejected.Add(best);
                }
                if (best < 0)
                    break;
                int costOfBest = PathCost(best);
                Absorb(best);
                defenseBudget -= costOfBest;
            }
        }

        // Phase 2: focused stats by value-per-point, counting the whole path's value — a
        // farther candidate whose route passes stat-bearing nodes can beat a nearer one.
        while (remaining > 0)
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from, normByVertex);
            var rejected = new HashSet<int>();
            int best;
            while (true)
            {
                best = -1;
                double bestRatio = 0;
                for (int v = 0; v < n; v++)
                {
                    if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity || rejected.Contains(v))
                        continue;
                    if (normByVertex[v] <= 0)
                        continue;
                    var (cost, pathValue) = PathCostAndValue(v, normByVertex);
                    if (cost > remaining)
                        continue;
                    double ratio = pathValue / cost;
                    if (best < 0 || ratio > bestRatio)
                    {
                        best = v;
                        bestRatio = ratio;
                    }
                }
                if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                    break;
                rejected.Add(best);
            }
            if (best < 0)
                break;
            Absorb(best);
        }

        // Phase H: value hill-climb over THIS call's additions. The greedy spend is
        // order-dependent — an early pick can be strictly worse than a candidate that became
        // adjacent later — so trade the least valuable added purchase 1:1 for the best
        // tree-adjacent candidate while that improves total value. Pre-existing purchases are
        // never dropped here (only the threshold pass may do that), and connectivity, met
        // thresholds, active glyph goals and Limit caps all hold.
        HillClimbValue(graph, purchased, tree, request, thresholds, limits, userBlocked,
            added, gains, gainSet, ref raresAdded, notes,
            v => normByVertex[v] + defenseByVertex[v] * focus.DefenseShare * 2);

        // Phase R: reallocation. The greedy spend is one-way, so the budget can die a few stat
        // points short of a threshold that a single node would flip. Trade the least valuable
        // expendable purchases (normal/magic/gate leaves that feed nothing important) 1:1 for
        // the deficit stat until the threshold activates; failed attempts roll back.
        if (focus.ActivateThresholds && thresholds is not null)
        {
            // Defensive purchases count as valuable, or the reallocation would treat the
            // defense slice as free fodder and trade it straight back into thresholds.
            ReallocateForThresholds(
                graph, purchased, tree, request, thresholds, limits, userBlocked,
                added, gains, gainSet, ref raresAdded, notes,
                v => NormValue(v) + DefenseValue(v) * focus.DefenseShare * 2);
            metAfter = BuildStats.Compute(graph, purchased, thresholds.Data,
                thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers).ThresholdsMet;
        }

        return new MaximizeOutcome(added, raresAdded, gains, notes, Math.Max(0, metAfter - metBefore));
    }

    /// <summary>Kinds the reallocation pass may drop: cheap connectors and filler stat nodes.</summary>
    private static bool IsExpendableKind(ParagonNodeKind kind) =>
        kind is ParagonNodeKind.Normal or ParagonNodeKind.Magic or ParagonNodeKind.Gate;

    /// <summary>
    /// Trades the least valuable expendable cell among <paramref name="added"/> for the best
    /// tree-adjacent unpurchased candidate, while each swap strictly improves total value.
    /// Only this call's own additions are ever dropped; every safety the threshold
    /// reallocation applies (connectivity, protected targets, gate pairs, met-threshold and
    /// active-glyph slack, Limit caps) applies here too.
    /// </summary>
    private static void HillClimbValue(
        ComposedGraph graph, ISet<CellRef> purchased, HashSet<int> tree, PlanRequest request,
        ThresholdContext? thresholds, LimitTracker limits, bool[] userBlocked,
        List<CellRef> added, Dictionary<string, double> gains, HashSet<string> gainSet,
        ref int raresAdded, List<string> notes, Func<int, double> valueOf)
    {
        const int MaxSwaps = 40;
        const double Margin = 1e-3; // in normalized units: a thousandth of an average node
        int n = graph.Vertices.Count;
        var protectedTargets = request.Targets.ToHashSet();
        var gatePair = GateCrossings.PairMap(graph);
        var addedSet = added.ToHashSet();

        double MultiplierOf(int v) =>
            thresholds?.CellMultipliers?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0;

        double GrantOf(int v, string attribute) => graph.Vertices[v].Node.Attributes
            .Where(a => !a.IsThresholdBonus && a.Value is not null
                && string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Value!.Value) * MultiplierOf(v);

        bool StaysConnectedWithout(int candidate)
        {
            var seen = new HashSet<int> { graph.StartVertex };
            var queue = new Queue<int>();
            queue.Enqueue(graph.StartVertex);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int u in graph.Adjacency[v])
                {
                    if (u != candidate && tree.Contains(u) && seen.Add(u))
                        queue.Enqueue(u);
                }
            }
            return seen.Count == tree.Count - 1;
        }

        int swaps = 0;
        var droppedNames = new List<string>();
        while (swaps < MaxSwaps)
        {
            // Met bonuses may not lose more of their stat than their slack, or they flip off.
            var slackByAttribute = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (thresholds is not null)
            {
                var report = BuildStats.Compute(graph, purchased, thresholds.Data,
                    thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
                foreach (var met in report.Thresholds.Where(t => t.Met))
                {
                    string coreKey = met.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                        ? met.Attribute[..^"_Total".Length] + "_Core"
                        : met.Attribute;
                    double slack = met.Have - met.Requirement;
                    slackByAttribute[coreKey] = Math.Min(
                        slackByAttribute.GetValueOrDefault(coreKey, double.MaxValue), slack);
                }
            }
            // Active glyph goals may not lose more in-radius source stat than their slack.
            var goalSlack = new List<(GlyphGoal Goal, double Slack)>();
            foreach (var goal in request.GlyphGoals)
            {
                double have = GlyphRadius.AttributeTotalsInRange(
                        graph, goal.Socket, purchased.ToList(), goal.Radius, GlyphRadius.GameMetric)
                    .GetValueOrDefault(goal.SourceAttribute);
                goalSlack.Add((goal, have - goal.RequiredTotal));
            }

            // Buy candidate: the most valuable point available next to the tree.
            int buy = -1;
            double buyValue = 0;
            for (int v = 0; v < n; v++)
            {
                if (tree.Contains(v) || userBlocked[v] || limits.WouldViolate(v))
                    continue;
                double value = valueOf(v);
                if (value <= buyValue)
                    continue;
                if (!graph.Adjacency[v].Any(u => tree.Contains(u)))
                    continue;
                buy = v;
                buyValue = value;
            }
            if (buy < 0)
                break;

            // Free candidate: the least valuable of this call's own expendable purchases.
            int free = -1;
            double freeLoss = double.MaxValue;
            foreach (int v in tree)
            {
                var cell = graph.Vertices[v].Cell;
                if (v == graph.StartVertex || !addedSet.Contains(cell)
                    || !IsExpendableKind(graph.Vertices[v].Node.Kind))
                    continue;
                // Half of a purchased gate pair frees no real point in-game — never trade it.
                int pair = gatePair[v];
                if (pair >= 0 && tree.Contains(pair))
                    continue;
                if (protectedTargets.Contains(cell))
                    continue;
                bool unsafeGrant = graph.Vertices[v].Node.Attributes.Any(a =>
                    !a.IsThresholdBonus && a.Value is not null
                    && slackByAttribute.TryGetValue(a.Attribute, out double slack)
                    && a.Value.Value * MultiplierOf(v) > slack);
                if (unsafeGrant)
                    continue; // would flip a met threshold bonus back off
                bool breaksGoal = goalSlack.Any(g => g.Slack >= 0
                    && cell.BoardSlot == g.Goal.Socket.BoardSlot
                    && Math.Abs(cell.X - g.Goal.Socket.X) + Math.Abs(cell.Y - g.Goal.Socket.Y) <= g.Goal.Radius
                    && GrantOf(v, g.Goal.SourceAttribute) / MultiplierOf(v) > g.Slack);
                if (breaksGoal)
                    continue; // would deactivate a met glyph
                // The buy must still touch the tree once this vertex is gone.
                if (!graph.Adjacency[buy].Any(u => u != v && tree.Contains(u)))
                    continue;
                if (!StaysConnectedWithout(v))
                    continue;
                double loss = valueOf(v);
                if (loss < freeLoss)
                {
                    free = v;
                    freeLoss = loss;
                }
            }
            if (free < 0 || buyValue <= freeLoss + Margin)
                break;

            var freedCell = graph.Vertices[free].Cell;
            tree.Remove(free);
            purchased.Remove(freedCell);
            added.Remove(freedCell);
            addedSet.Remove(freedCell);
            limits.OnRemoved(free);
            foreach (var a in graph.Vertices[free].Node.Attributes)
            {
                if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                    gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) - a.Value.Value;
            }

            var boughtCell = graph.Vertices[buy].Cell;
            tree.Add(buy);
            purchased.Add(boughtCell);
            added.Add(boughtCell);
            addedSet.Add(boughtCell);
            limits.OnAbsorbed(buy);
            foreach (var a in graph.Vertices[buy].Node.Attributes)
            {
                if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                    gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) + a.Value.Value;
            }
            if (graph.Vertices[buy].Node.Kind == ParagonNodeKind.Rare)
                raresAdded++;

            droppedNames.Add(graph.Vertices[free].Node.Name ?? graph.Vertices[free].Node.InternalName);
            swaps++;
        }

        if (swaps > 0)
        {
            notes.Add($"Rebalanced {swaps} point(s) toward higher-value nodes " +
                      $"(dropped {string.Join(", ", droppedNames.Distinct().Take(3))}" +
                      $"{(droppedNames.Distinct().Count() > 3 ? ", …" : "")}).");
        }
    }

    private static void ReallocateForThresholds(
        ComposedGraph graph, ISet<CellRef> purchased, HashSet<int> tree, PlanRequest request,
        ThresholdContext thresholds, LimitTracker limits, bool[] userBlocked,
        List<CellRef> added, Dictionary<string, double> gains, HashSet<string> gainSet,
        ref int raresAdded, List<string> notes, Func<int, double> normValue)
    {
        const int MaxSwaps = 40;
        int n = graph.Vertices.Count;
        int swaps = 0;
        var protectedTargets = request.Targets.ToHashSet();
        var failedThresholds = new HashSet<CellRef>();
        var reallocGatePair = GateCrossings.PairMap(graph);

        double MultiplierOf(int v) =>
            thresholds.CellMultipliers?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0;

        double GrantOf(int v, string attribute) => graph.Vertices[v].Node.Attributes
            .Where(a => !a.IsThresholdBonus && a.Value is not null
                && string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Value!.Value) * MultiplierOf(v);

        // Removal must keep the purchase set connected to the start node.
        bool StaysConnectedWithout(int candidate)
        {
            var seen = new HashSet<int> { graph.StartVertex };
            var queue = new Queue<int>();
            queue.Enqueue(graph.StartVertex);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int u in graph.Adjacency[v])
                {
                    if (u != candidate && tree.Contains(u) && seen.Add(u))
                        queue.Enqueue(u);
                }
            }
            return seen.Count == tree.Count - 1;
        }

        while (swaps < MaxSwaps)
        {
            var report = BuildStats.Compute(graph, purchased, thresholds.Data,
                thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
            var next = report.Thresholds
                .Where(t => !t.Met && !failedThresholds.Contains(t.Cell))
                .OrderBy(t => t.Requirement - t.Have)
                .FirstOrDefault();
            if (next is null)
                break;

            string targetAttribute = next.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                ? next.Attribute[..^"_Total".Length] + "_Core"
                : next.Attribute;
            double needed = next.Requirement - next.Have;

            double gained = 0;
            var swapped = new List<(int Freed, int Bought)>();
            var droppedNames = new List<string>();
            while (gained < needed - 1e-9 && swaps + swapped.Count < MaxSwaps)
            {
                // Met bonuses may not lose more of their stat than their slack, or they flip off.
                var slackByAttribute = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var met in report.Thresholds.Where(t => t.Met))
                {
                    string coreKey = met.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                        ? met.Attribute[..^"_Total".Length] + "_Core"
                        : met.Attribute;
                    double slack = met.Have - met.Requirement;
                    slackByAttribute[coreKey] = Math.Min(
                        slackByAttribute.GetValueOrDefault(coreKey, double.MaxValue), slack);
                }
                // Active glyph goals may not lose more in-radius source stat than their slack.
                var goalSlack = new List<(GlyphGoal Goal, double Slack)>();
                foreach (var goal in request.GlyphGoals)
                {
                    double have = GlyphRadius.AttributeTotalsInRange(
                            graph, goal.Socket, purchased.ToList(), goal.Radius, GlyphRadius.GameMetric)
                        .GetValueOrDefault(goal.SourceAttribute);
                    goalSlack.Add((goal, have - goal.RequiredTotal));
                }

                // Buy candidate: the most deficit stat one point can add next to the tree.
                int buy = -1;
                double buyValue = 0;
                for (int v = 0; v < n; v++)
                {
                    if (tree.Contains(v) || userBlocked[v] || limits.WouldViolate(v))
                        continue;
                    double value = GrantOf(v, targetAttribute);
                    if (value <= buyValue)
                        continue;
                    if (!graph.Adjacency[v].Any(u => tree.Contains(u)))
                        continue;
                    buy = v;
                    buyValue = value;
                }
                if (buy < 0)
                    break;

                // Free candidate: the least valuable expendable purchase that nothing depends on.
                int free = -1;
                double freeLoss = double.MaxValue;
                foreach (int v in tree)
                {
                    if (v == graph.StartVertex || !IsExpendableKind(graph.Vertices[v].Node.Kind))
                        continue;
                    // Half of a purchased gate pair frees no real point in-game (the pair costs
                    // one; the survivor still costs it) — never trade it away.
                    int pair = reallocGatePair[v];
                    if (pair >= 0 && tree.Contains(pair))
                        continue;
                    var cell = graph.Vertices[v].Cell;
                    if (protectedTargets.Contains(cell))
                        continue;
                    if (GrantOf(v, targetAttribute) > 0)
                        continue; // dropping it would eat the very stat being bought
                    bool unsafeGrant = graph.Vertices[v].Node.Attributes.Any(a =>
                        !a.IsThresholdBonus && a.Value is not null
                        && slackByAttribute.TryGetValue(a.Attribute, out double slack)
                        && a.Value.Value * MultiplierOf(v) > slack);
                    if (unsafeGrant)
                        continue; // would flip a met threshold bonus back off
                    bool breaksGoal = goalSlack.Any(g => g.Slack >= 0
                        && cell.BoardSlot == g.Goal.Socket.BoardSlot
                        && Math.Abs(cell.X - g.Goal.Socket.X) + Math.Abs(cell.Y - g.Goal.Socket.Y) <= g.Goal.Radius
                        && GrantOf(v, g.Goal.SourceAttribute) / MultiplierOf(v) > g.Slack);
                    if (breaksGoal)
                        continue; // would deactivate a met glyph
                    // The buy must still touch the tree once this vertex is gone.
                    if (!graph.Adjacency[buy].Any(u => u != v && tree.Contains(u)))
                        continue;
                    if (!StaysConnectedWithout(v))
                        continue;
                    double loss = normValue(v);
                    if (loss < freeLoss)
                    {
                        free = v;
                        freeLoss = loss;
                    }
                }
                if (free < 0)
                    break;

                // Execute the 1:1 swap.
                var freedCell = graph.Vertices[free].Cell;
                tree.Remove(free);
                purchased.Remove(freedCell);
                added.Remove(freedCell);
                limits.OnRemoved(free);
                foreach (var a in graph.Vertices[free].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                        gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) - a.Value.Value;
                }

                var boughtCell = graph.Vertices[buy].Cell;
                tree.Add(buy);
                purchased.Add(boughtCell);
                added.Add(boughtCell);
                limits.OnAbsorbed(buy);
                foreach (var a in graph.Vertices[buy].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                        gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) + a.Value.Value;
                }
                if (graph.Vertices[buy].Node.Kind == ParagonNodeKind.Rare)
                    raresAdded++;

                swapped.Add((free, buy));
                droppedNames.Add(graph.Vertices[free].Node.Name ?? graph.Vertices[free].Node.InternalName);
                gained += buyValue;
                report = BuildStats.Compute(graph, purchased, thresholds.Data,
                    thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
            }

            if (gained >= needed - 1e-9 && swapped.Count > 0)
            {
                swaps += swapped.Count;
                notes.Add($"Reallocated {swapped.Count} point(s) to activate {next.NodeName} " +
                          $"(dropped {string.Join(", ", droppedNames.Distinct().Take(3))}" +
                          $"{(droppedNames.Distinct().Count() > 3 ? ", …" : "")}).");
                continue;
            }

            // Couldn't close this deficit — undo any partial swaps and skip the threshold.
            for (int i = swapped.Count - 1; i >= 0; i--)
            {
                var (freed, bought) = swapped[i];
                var boughtCell = graph.Vertices[bought].Cell;
                tree.Remove(bought);
                purchased.Remove(boughtCell);
                added.Remove(boughtCell);
                limits.OnRemoved(bought);
                if (graph.Vertices[bought].Node.Kind == ParagonNodeKind.Rare)
                    raresAdded--;
                foreach (var a in graph.Vertices[bought].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                        gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) - a.Value.Value;
                }

                var freedCell = graph.Vertices[freed].Cell;
                tree.Add(freed);
                purchased.Add(freedCell);
                added.Add(freedCell);
                limits.OnAbsorbed(freed);
                foreach (var a in graph.Vertices[freed].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is not null && gainSet.Contains(a.Attribute))
                        gains[a.Attribute] = gains.GetValueOrDefault(a.Attribute) + a.Value.Value;
                }
            }
            failedThresholds.Add(next.Cell);
        }
    }

    /// <summary>
    /// Multi-source Dijkstra from the tree. With <paramref name="tieValue"/>, equally cheap
    /// routes prefer the one whose cells carry more value — every weight is ≥1, so the
    /// tie-break can reroute but never change a path's cost.
    /// </summary>
    private static void RunDijkstra(
        ComposedGraph graph, HashSet<int> tree, int[] weights, bool[] blocked, int[] dist, int[] from,
        double[]? tieValue = null)
    {
        Array.Fill(dist, Infinity);
        var pathValue = tieValue is null ? null : new double[dist.Length];
        var queue = new PriorityQueue<int, (int Cost, double NegValue)>();
        foreach (int v in tree)
        {
            dist[v] = 0;
            from[v] = -1;
            queue.Enqueue(v, (0, 0));
        }
        while (queue.TryDequeue(out int v, out var priority))
        {
            if (priority.Cost > dist[v])
                continue;
            foreach (int u in graph.Adjacency[v])
            {
                if (blocked[u])
                    continue;
                int next = priority.Cost + weights[u];
                double value = pathValue is null ? 0 : pathValue[v] + tieValue![u];
                if (next < dist[u] || (pathValue is not null && next == dist[u] && value > pathValue[u]))
                {
                    dist[u] = next;
                    from[u] = v;
                    if (pathValue is not null)
                        pathValue[u] = value;
                    queue.Enqueue(u, (next, -value));
                }
            }
        }
    }
}
