namespace D4LootBench.Paragon.Solver;

public sealed class PlanRequest
{
    public required IReadOnlyCollection<CellRef> Targets { get; init; }
    public IReadOnlyList<NodeRule> NodeRules { get; init; } = [];

    /// <summary>Individual cells to soft-penalize, on top of group rules.</summary>
    public IReadOnlyCollection<CellRef> AvoidCells { get; init; } = [];

    /// <summary>Individual cells to hard-exclude, on top of group rules.</summary>
    public IReadOnlyCollection<CellRef> ExcludeCells { get; init; } = [];

    public IReadOnlyList<GlyphGoal> GlyphGoals { get; init; } = [];
}

public sealed class PlanResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<CellRef> PurchasedCells { get; init; } = [];
    public int PointsSpent => PurchasedCells.Count;

    /// <summary>True when the base tree came from the exact solver (glyph extension is always greedy).</summary>
    public bool IsOptimal { get; init; }

    /// <summary>Human-readable caveats: limits that could not be honored, unmet glyph goals.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<GlyphGoalOutcome> GlyphOutcomes { get; init; } = [];

    public static PlanResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Full planning pass over a composed layout: turns node rules into solver constraints, solves
/// the Steiner tree, re-solves with escalating weights while any Limit rule is violated, then
/// extends the tree until every glyph activation goal is met (or provably can't be).
/// </summary>
public static class PlanSolver
{
    /// <summary>Traversal cost of an avoided node: taken only when it saves this many plain nodes.</summary>
    public const int AvoidWeight = 5;

    /// <summary>Weights tried in turn on groups whose Limit is still exceeded.</summary>
    private static readonly int[] LimitEscalation = [AvoidWeight, 20, 80];

    public static PlanResult Solve(ComposedGraph graph, PlanRequest request)
    {
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        var displayByGroup = NodeGrouping.GroupsIn(graph)
            .Concat(NodeGrouping.StatGroupsIn(graph))
            .ToDictionary(g => g.Key, g => g.DisplayName);
        var (weights, blockedCells, limitRules) = BuildSteering(request, cellsByGroup);

        // Minimal rules: prefer the fewest group nodes WITHOUT disturbing point-optimality.
        // Every cell's weight is scaled up and Minimal-group cells cost +1 extra, so the tiny
        // extras can only ever break ties between equally cheap trees, never buy a longer one.
        var minimalRules = limitRules.Where(r => r.Mode == NodeRuleMode.Minimal).ToList();
        int scale = 1;
        if (minimalRules.Count > 0)
        {
            scale = 256;
            foreach (var vertex in graph.Vertices)
                weights[vertex.Cell] = weights.GetValueOrDefault(vertex.Cell, 1) * scale;
            foreach (var rule in minimalRules)
            {
                if (!cellsByGroup.TryGetValue(rule.GroupKey, out var cells))
                    continue;
                foreach (var cell in cells)
                    weights[cell] += 1;
            }
        }

        var notes = new List<string>();
        SolverResult result;
        SolverConstraints constraints;
        int escalation = 0;
        while (true)
        {
            constraints = new SolverConstraints { CellWeights = weights, BlockedCells = blockedCells };
            result = SteinerSolver.Solve(graph, request.Targets, constraints);
            if (!result.Success)
                return PlanResult.Failed(result.Error ?? "Solve failed.");

            var violated = ViolatedLimits(result.PurchasedCells, limitRules, cellsByGroup);
            if (violated.Count == 0)
                break;

            if (escalation >= LimitEscalation.Length)
            {
                foreach (var (rule, count) in violated)
                {
                    notes.Add($"Limit not satisfiable: {Display(rule.GroupKey)} needs {count} node(s) on any path, " +
                              $"limit is {rule.Limit}.");
                }
                break;
            }

            // Make every node of the violated groups pricier and try again — the solver then
            // uses as few of them as possible; if that minimum still exceeds the limit, we report it.
            foreach (var (rule, _) in violated)
            {
                foreach (var cell in cellsByGroup[rule.GroupKey])
                    weights[cell] = Math.Max(weights.GetValueOrDefault(cell, 1), LimitEscalation[escalation] * scale);
            }
            escalation++;
        }

        var purchased = new HashSet<CellRef>(result.PurchasedCells);
        var outcomes = GlyphOptimizer.Extend(graph, purchased, request.GlyphGoals, constraints, limitRules, cellsByGroup);
        foreach (var outcome in outcomes.Where(o => !o.Met))
        {
            string glyph = outcome.Goal.GlyphName ?? "glyph";
            string limitHint = outcome.LimitConstrained
                ? " A node Limit rule kept nodes off the path — raise or clear it to activate."
                : "";
            notes.Add($"Cannot activate {glyph}: only {outcome.AchievedTotal:0} of the required " +
                      $"{outcome.Goal.RequiredTotal:0} {ParagonDisplay.FormatAttributeName(outcome.Goal.SourceAttribute)} " +
                      $"is reachable within radius {outcome.Goal.Radius}.{limitHint}");
        }

        // Glyph extension enforces limits itself now — this is a safety net that should not fire.
        foreach (var (rule, count) in ViolatedLimits(purchased, limitRules, cellsByGroup))
        {
            notes.Add($"Glyph activation pushed {Display(rule.GroupKey)} to {count} node(s), over the limit of {rule.Limit}.");
        }

        // Base tree first, extension cells after, in a stable order.
        var ordered = result.PurchasedCells
            .Concat(outcomes.SelectMany(o => o.AddedCells))
            .Distinct()
            .ToList();
        return new PlanResult
        {
            Success = true,
            IsOptimal = result.IsOptimal,
            PurchasedCells = ordered,
            Notes = notes,
            GlyphOutcomes = outcomes,
        };

        string Display(string groupKey) => displayByGroup.GetValueOrDefault(groupKey, groupKey);
    }

    /// <summary>Turns a request's rules and per-cell overrides into weights, blocks and limit rules.</summary>
    internal static (Dictionary<CellRef, int> Weights, HashSet<CellRef> Blocked, List<NodeRule> LimitRules)
        BuildSteering(PlanRequest request, IReadOnlyDictionary<string, IReadOnlyList<CellRef>> cellsByGroup)
    {
        var weights = new Dictionary<CellRef, int>();
        var blockedCells = new HashSet<CellRef>(request.ExcludeCells);
        var limitRules = new List<NodeRule>();
        foreach (var rule in request.NodeRules)
        {
            if (!cellsByGroup.TryGetValue(rule.GroupKey, out var groupCells))
                continue;
            switch (rule.Mode)
            {
                case NodeRuleMode.Avoid:
                    foreach (var cell in groupCells)
                        weights[cell] = Math.Max(weights.GetValueOrDefault(cell, 1), AvoidWeight);
                    break;
                case NodeRuleMode.Exclude:
                    foreach (var cell in groupCells)
                        blockedCells.Add(cell);
                    break;
                case NodeRuleMode.Limit:
                case NodeRuleMode.Minimal: // caps like Limit; the fewest-nodes bias is solver-side
                    limitRules.Add(rule);
                    break;
            }
        }
        foreach (var cell in request.AvoidCells)
            weights[cell] = Math.Max(weights.GetValueOrDefault(cell, 1), AvoidWeight);
        return (weights, blockedCells, limitRules);
    }

    private static List<(NodeRule Rule, int Count)> ViolatedLimits(
        IReadOnlyCollection<CellRef> purchased,
        IReadOnlyList<NodeRule> limitRules,
        IReadOnlyDictionary<string, IReadOnlyList<CellRef>> cellsByGroup)
    {
        var violated = new List<(NodeRule, int)>();
        foreach (var rule in limitRules)
        {
            int count = cellsByGroup[rule.GroupKey].Count(purchased.Contains);
            if (count > rule.Limit)
                violated.Add((rule, count));
        }
        return violated;
    }
}
