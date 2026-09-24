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

    /// <summary>
    /// The build's current additive damage bucket as a fraction (0.85 = +85%), or null for no
    /// bucket damping. D4 sums ALL "+% damage" stats into ONE additive bucket (see
    /// <see cref="DamageModel"/>), so their marginal real value is 1/(1 + bucket) — when set,
    /// additive-damage attributes (and the glyph delivery term, whose destinations live in that
    /// bucket) are valued at that marginal instead of face value, and a saturated bucket stops
    /// outcompeting main stat, crit chance, and utility. Sampled once at spend start — the few
    /// percent the spend itself adds doesn't meaningfully move the factor.
    /// </summary>
    public double? AdditiveDamageFraction { get; init; }

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
/// cap is rejected. The post-spend swap passes live in PointMaximizer.Rebalance.cs.
/// </summary>
public static partial class PointMaximizer
{
    private const int Infinity = TreeDijkstra.Infinity;

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

        // Bucket damping: additive-damage attrs are valued at their marginal real contribution.
        double additiveDamp = focus.AdditiveDamageFraction is double bucket ? 1.0 / (1.0 + bucket) : 1.0;
        double DampOf(string attribute) =>
            additiveDamp < 1.0 && DamageModel.Classify(attribute) == DamageBucket.AdditiveDamage
                ? additiveDamp
                : 1.0;

        // Glyph delivery: a point of source stat inside an active glyph's radius is worth its raw
        // value AND what the glyph converts it into — count it again for each covering glyph,
        // scaled by that glyph's weight (activation math elsewhere stays raw). Deliveries land in
        // the additive damage bucket, so the term damps with it.
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
                        glyphBonus[v] += value / (mean.Sum / mean.Count) * delivery.Weight * additiveDamp;
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
                total += value * factor / (mean.Sum / mean.Count) * WeightOf(a.Attribute) * DampOf(a.Attribute);
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

        var ledger = new SpendLedger(graph, purchased, tree, limits, gainSet);
        int remaining = budget;
        var search = new TreeDijkstra(graph);
        var dist = search.Dist;
        var from = search.From;

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

        // Cost and accumulated value of every reachable candidate's not-yet-owned path down to
        // the tree, for the current Dijkstra — computed once per round (memoized along the
        // predecessor chains) instead of re-walking each candidate's path. Value rides the same
        // guard as cost: a gate pair grants its attributes once, so the free half contributes
        // neither (both halves carry the same node def, so the sum is right).
        var pathCost = new int[n];
        var pathValue = new double[n];
        var totaled = new bool[n];
        var walk = new Stack<int>();
        void ComputePathTotals(double[] value)
        {
            Array.Clear(totaled);
            for (int v = 0; v < n; v++)
            {
                if (totaled[v] || tree.Contains(v) || dist[v] >= Infinity)
                    continue;
                for (int u = v; u != -1 && !tree.Contains(u) && !totaled[u]; u = from[u])
                    walk.Push(u);
                // Unwind nearest-to-tree first: each vertex's totals build on its predecessor's.
                while (walk.Count > 0)
                {
                    int w = walk.Pop();
                    int next = from[w];
                    bool nextOwned = next == -1 || tree.Contains(next);
                    int pair = gatePair[w];
                    bool counts = pair < 0 || !tree.Contains(pair);
                    int cost = counts ? 1 : 0;
                    double total = counts ? value[w] : 0;
                    if (!nextOwned)
                    {
                        cost += pathCost[next];
                        total += pathValue[next];
                        // The predecessor was totaled as a walk's first vertex; entered from its
                        // own gate partner it rides free instead.
                        if (gatePair[next] == w)
                        {
                            cost--;
                            total -= value[next];
                        }
                    }
                    pathCost[w] = cost;
                    pathValue[w] = total;
                    totaled[w] = true;
                }
            }
        }

        // Aggregate supply sums over UNOWNED cells count a gate pair once: the half whose partner
        // is owned (it would ride free) and the higher-indexed half of an unowned pair are skipped.
        bool GrantsOnce(int v)
        {
            int pair = gatePair[v];
            return pair < 0 || (!tree.Contains(pair) && pair > v);
        }

        // Cells absorbed as a crossing's free half — they grant no stats (see GateCrossings).
        var freeHalves = new HashSet<int>();

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                // The walk runs candidate→tree, so a crossing's far gate is added (and paid)
                // before the near gate finds its partner already in the tree and rides free —
                // costing nothing and granting nothing.
                int pair = gatePair[v];
                bool free = pair >= 0 && tree.Contains(pair);
                ledger.Buy(v, grantsStats: !free);
                if (free)
                    freeHalves.Add(v);
                else
                    remaining--;
            }
            limits.BlockFullGroups(blocked, tree);
        }

        // The shared greedy step: run the Dijkstra for this value, then pick the eligible
        // affordable candidate with the best (rank, value-per-point) whose path keeps every
        // Limit cap, rejecting violators and re-picking. -1 when nothing qualifies.
        int PickBest(double[] value, Func<int, bool> eligible,
            Func<int, int>? rankOf = null, Func<int, double>? bonusOf = null, bool cheaperOnTie = false)
        {
            search.Run(tree, weights, blocked, value);
            ComputePathTotals(value);
            HashSet<int>? rejected = null;
            while (true)
            {
                int best = -1, bestRank = 0, bestCost = 0;
                double bestRatio = 0;
                for (int v = 0; v < n; v++)
                {
                    if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity
                        || rejected?.Contains(v) == true || !eligible(v))
                        continue;
                    int cost = pathCost[v];
                    if (cost <= 0 || cost > remaining)
                        continue;
                    int rank = rankOf?.Invoke(v) ?? 0;
                    double ratio = (pathValue[v] + (bonusOf?.Invoke(v) ?? 0)) / cost;
                    if (best < 0 || rank < bestRank
                        || (rank == bestRank && (ratio > bestRatio
                            || (cheaperOnTie && ratio == bestRatio && cost < bestCost))))
                    {
                        best = v;
                        bestRank = rank;
                        bestCost = cost;
                        bestRatio = ratio;
                    }
                }
                if (best < 0 || !limits.PathWouldViolate(best, from, tree))
                    return best;
                (rejected ??= []).Add(best);
            }
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
            bool WantedRare(int v) => graph.Vertices[v].Node.Kind == ParagonNodeKind.Rare
                && (!explicitFocus || normByVertex[v] > 1e-9
                    || defenseByVertex[v] > 1e-9); // with a defense share, survivability rares are wanted

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
                Func<int, int>? rank = null;
                Func<int, double>? bonusValue = null;
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
                        if (tree.Contains(v) || blocked[v] || !GrantsOnce(v))
                            continue;
                        double multiplier = MultiplierOf(v);
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
                        double deficit = status.Requirement - status.Have;
                        return supply.GetValueOrDefault(BuildStats.ParagonKeyFor(status.Attribute)) >= deficit ? 1 : 3;
                    };
                    // Met bonuses score full; realistically meetable ones at a discount (the
                    // deficit still has to be bought); unattainable ones are worth nothing.
                    bonusValue = v => rank(v) switch { 0 => bonusNorm[v], 1 => 0.5 * bonusNorm[v], _ => 0 };
                }

                // Within an attainability rank, value-per-point decides — a slightly farther but
                // much better rare beats the merely nearest one. An active (or affordable)
                // threshold bonus is part of the rare's value.
                int best = PickBest(normByVertex, WantedRare, rank, bonusValue, cheaperOnTie: true);
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
            var targetValue = new double[n];
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
                string targetAttribute = BuildStats.ParagonKeyFor(next.Attribute);
                double needed = next.Requirement - next.Have;

                for (int v = 0; v < n; v++)
                    targetValue[v] = GrantOf(graph, v, targetAttribute) * MultiplierOf(v);

                search.Run(tree, weights, blocked, targetValue);
                double reachable = 0;
                for (int v = 0; v < n; v++)
                {
                    if (!tree.Contains(v) && !blocked[v] && dist[v] < Infinity && GrantsOnce(v))
                        reachable += targetValue[v];
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
                    int best = PickBest(targetValue, v => targetValue[v] > 0);
                    if (best < 0)
                        break;
                    int before = ledger.Added.Count;
                    Absorb(best);
                    for (int i = before; i < ledger.Added.Count; i++)
                    {
                        if (graph.TryGetVertex(ledger.Added[i], out int v) && !freeHalves.Contains(v))
                            gained += targetValue[v];
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
                int best = PickBest(defenseByVertex, v => defenseByVertex[v] > 0);
                if (best < 0)
                    break;
                int costOfBest = pathCost[best];
                Absorb(best);
                defenseBudget -= costOfBest;
            }
        }

        // Phase 2: focused stats by value-per-point, counting the whole path's value — a
        // farther candidate whose route passes stat-bearing nodes can beat a nearer one.
        while (remaining > 0)
        {
            int best = PickBest(normByVertex, v => normByVertex[v] > 0);
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
        HillClimbValue(graph, request, thresholds, ledger, userBlocked, notes,
            v => normByVertex[v] + defenseByVertex[v] * focus.DefenseShare * 2);

        // Phase R: reallocation. The greedy spend is one-way, so the budget can die a few stat
        // points short of a threshold that a single node would flip. Trade the least valuable
        // expendable purchases (normal/magic/gate leaves that feed nothing important) 1:1 for
        // the deficit stat until the threshold activates; failed attempts roll back.
        if (focus.ActivateThresholds && thresholds is not null)
        {
            // Defensive purchases count as valuable, or the reallocation would treat the
            // defense slice as free fodder and trade it straight back into thresholds.
            ReallocateForThresholds(graph, request, thresholds, ledger, userBlocked, notes,
                v => normByVertex[v] + defenseByVertex[v] * focus.DefenseShare * 2);
            metAfter = BuildStats.Compute(graph, purchased, thresholds.Data,
                thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers).ThresholdsMet;
        }

        return new MaximizeOutcome(ledger.Added, ledger.RaresAdded, ledger.Gains, notes, Math.Max(0, metAfter - metBefore));
    }

    /// <summary>Raw (unbuffed) grant of one attribute by a vertex's node.</summary>
    private static double GrantOf(ComposedGraph graph, int v, string attribute)
    {
        double total = 0;
        foreach (var a in graph.Vertices[v].Node.Attributes)
        {
            if (!a.IsThresholdBonus && a.Value is double value
                && string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
                total += value;
        }
        return total;
    }
}
