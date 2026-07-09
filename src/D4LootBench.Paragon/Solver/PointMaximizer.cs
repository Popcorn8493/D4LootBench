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
/// absorbs it, until the budget is gone or nothing valuable is reachable. Values are normalized
/// per attribute (a node's contribution is measured against that attribute's average per-node
/// magnitude) so flat and percent stats compete fairly. Node rules apply: excluded cells are
/// never entered, avoided cells are routed around, and Limit groups are enforced hard — a full
/// group is off-limits, and a path that would jump a group past its cap is rejected.
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

        // Limit groups: full groups become off-limits, routing detours around group members
        // (the penalty keeps traversal from burning the allowance), and candidate paths that
        // would still jump a group past its cap are rejected.
        var limits = new LimitTracker(graph, limitRules, cellsByGroup, tree);
        limits.PenalizeGroups(weights, tree);
        limits.BlockFullGroups(blocked, tree);

        var attributes = focus.Attributes.Count > 0 ? focus.Attributes : MaximizeFocus.CoreStats;
        var attributeSet = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        // Gains are also reported for glyph-source stats, even when they aren't focused.
        var gainSet = new HashSet<string>(attributeSet, StringComparer.OrdinalIgnoreCase);
        foreach (var goal in request.GlyphGoals)
            gainSet.Add(goal.SourceAttribute);

        // Per-attribute average per-node magnitude across the layout, for unit-fair scoring.
        var attributeMean = new Dictionary<string, (double Sum, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in graph.Vertices)
        {
            foreach (var a in vertex.Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !gainSet.Contains(a.Attribute))
                    continue;
                var (sum, count) = attributeMean.GetValueOrDefault(a.Attribute);
                attributeMean[a.Attribute] = (sum + Math.Abs(value), count + 1);
            }
        }

        // Glyph delivery: a point of source stat inside an active glyph's radius is worth its raw
        // value AND what the glyph converts it into — count it again for each covering glyph.
        var glyphBonus = new double[n];
        foreach (var goal in request.GlyphGoals)
        {
            if (!attributeMean.TryGetValue(goal.SourceAttribute, out var mean))
                continue;
            for (int v = 0; v < n; v++)
            {
                var cell = graph.Vertices[v].Cell;
                if (cell.BoardSlot != goal.Socket.BoardSlot
                    || Math.Abs(cell.X - goal.Socket.X) + Math.Abs(cell.Y - goal.Socket.Y) > goal.Radius)
                    continue;
                foreach (var a in graph.Vertices[v].Node.Attributes)
                {
                    if (!a.IsThresholdBonus && a.Value is double value
                        && string.Equals(a.Attribute, goal.SourceAttribute, StringComparison.OrdinalIgnoreCase))
                        glyphBonus[v] += value / (mean.Sum / mean.Count);
                }
            }
        }

        double WeightOf(string attribute) =>
            focus.Weights?.GetValueOrDefault(attribute, 1.0) ?? 1.0;

        double NormValue(int v)
        {
            double total = glyphBonus[v];
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (a.IsThresholdBonus || a.Value is not double value || !attributeSet.Contains(a.Attribute))
                    continue;
                var (sum, count) = attributeMean[a.Attribute];
                total += value / (sum / count) * WeightOf(a.Attribute);
            }
            return total;
        }

        var dist = new int[n];
        var from = new int[n];
        var added = new List<CellRef>();
        int raresAdded = 0;
        int remaining = budget;
        var gains = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        int PathCost(int vertex)
        {
            int cost = 0;
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
                cost++;
            return cost;
        }

        void Absorb(int vertex)
        {
            for (int v = vertex; v != -1 && !tree.Contains(v); v = from[v])
            {
                tree.Add(v);
                var cell = graph.Vertices[v].Cell;
                purchased.Add(cell);
                added.Add(cell);
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

        // Phase 1: rare nodes. Default order is cheapest first (ties: more focused stat value).
        // With RealisticRares, threshold attainability leads instead: bonuses already met, then
        // ones the boards can realistically still supply, then plain rares, and rares whose
        // bonus is out of reach last — so points chase "good" rares, not just near ones.
        if (focus.PreferRare)
        {
            bool realistic = focus.RealisticRares && thresholds is not null;
            while (remaining > 0)
            {
                Func<int, int> rank = _ => 0;
                if (realistic)
                {
                    // Recomputed each round: an absorbed rare changes both Have and supply.
                    var report = BuildStats.Compute(graph, purchased, thresholds!.Data,
                        thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
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
                }

                RunDijkstra(graph, tree, weights, blocked, dist, from);
                var rejected = new HashSet<int>();
                int best;
                while (true)
                {
                    best = -1;
                    int bestRank = 0;
                    int bestCost = 0;
                    double bestValue = 0;
                    for (int v = 0; v < n; v++)
                    {
                        if (tree.Contains(v) || blocked[v] || dist[v] >= Infinity || rejected.Contains(v)
                            || graph.Vertices[v].Node.Kind != ParagonNodeKind.Rare)
                            continue;
                        int cost = PathCost(v);
                        if (cost > remaining)
                            continue;
                        int nodeRank = rank(v);
                        double value = NormValue(v);
                        if (best < 0 || nodeRank < bestRank
                            || (nodeRank == bestRank && (cost < bestCost || (cost == bestCost && value > bestValue))))
                        {
                            best = v;
                            bestRank = nodeRank;
                            bestCost = cost;
                            bestValue = value;
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

                RunDijkstra(graph, tree, weights, blocked, dist, from);
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
                    RunDijkstra(graph, tree, weights, blocked, dist, from);
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
                            double value = ValueOf(v);
                            if (value <= 0)
                                continue;
                            int cost = PathCost(v);
                            if (cost > remaining)
                                continue;
                            double ratio = value / cost;
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

        // Phase 2: focused stats by value-per-point.
        while (remaining > 0)
        {
            RunDijkstra(graph, tree, weights, blocked, dist, from);
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
                    double value = NormValue(v);
                    if (value <= 0)
                        continue;
                    int cost = PathCost(v);
                    if (cost > remaining)
                        continue;
                    double ratio = value / cost;
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

        // Phase R: reallocation. The greedy spend is one-way, so the budget can die a few stat
        // points short of a threshold that a single node would flip. Trade the least valuable
        // expendable purchases (normal/magic/gate leaves that feed nothing important) 1:1 for
        // the deficit stat until the threshold activates; failed attempts roll back.
        if (focus.ActivateThresholds && thresholds is not null)
        {
            ReallocateForThresholds(
                graph, purchased, tree, request, thresholds, limits, blocked,
                added, gains, gainSet, ref raresAdded, notes, NormValue);
            metAfter = BuildStats.Compute(graph, purchased, thresholds.Data,
                thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers).ThresholdsMet;
        }

        return new MaximizeOutcome(added, raresAdded, gains, notes, Math.Max(0, metAfter - metBefore));
    }

    /// <summary>Kinds the reallocation pass may drop: cheap connectors and filler stat nodes.</summary>
    private static bool IsExpendableKind(ParagonNodeKind kind) =>
        kind is ParagonNodeKind.Normal or ParagonNodeKind.Magic or ParagonNodeKind.Gate;

    private static void ReallocateForThresholds(
        ComposedGraph graph, ISet<CellRef> purchased, HashSet<int> tree, PlanRequest request,
        ThresholdContext thresholds, LimitTracker limits, bool[] blocked,
        List<CellRef> added, Dictionary<string, double> gains, HashSet<string> gainSet,
        ref int raresAdded, List<string> notes, Func<int, double> normValue)
    {
        const int MaxSwaps = 40;
        int n = graph.Vertices.Count;
        int swaps = 0;
        var protectedTargets = request.Targets.ToHashSet();
        var failedThresholds = new HashSet<CellRef>();

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
                    if (tree.Contains(v) || (blocked[v] && !limits.IsLimitBlocked(v)) || limits.WouldViolate(v))
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

    private static void RunDijkstra(
        ComposedGraph graph, HashSet<int> tree, int[] weights, bool[] blocked, int[] dist, int[] from)
    {
        Array.Fill(dist, Infinity);
        var queue = new PriorityQueue<int, int>();
        foreach (int v in tree)
        {
            dist[v] = 0;
            from[v] = -1;
            queue.Enqueue(v, 0);
        }
        while (queue.TryDequeue(out int v, out int cost))
        {
            if (cost > dist[v])
                continue;
            foreach (int u in graph.Adjacency[v])
            {
                if (blocked[u])
                    continue;
                int next = cost + weights[u];
                if (next < dist[u])
                {
                    dist[u] = next;
                    from[u] = v;
                    queue.Enqueue(u, next);
                }
            }
        }
    }
}
