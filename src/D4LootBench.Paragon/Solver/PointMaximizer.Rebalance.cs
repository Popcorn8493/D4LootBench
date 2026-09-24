using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>The post-spend 1:1 swap passes: the value hill-climb and the threshold reallocation.</summary>
public static partial class PointMaximizer
{
    /// <summary>
    /// The spend's mutable bookkeeping — tree, purchase set, this call's additions, Limit
    /// counters, reported gains and rare count — kept consistent by one Buy/Sell pair that the
    /// greedy phases and both swap passes share.
    /// </summary>
    private sealed class SpendLedger(
        ComposedGraph graph, ISet<CellRef> purchased, HashSet<int> tree, LimitTracker limits, HashSet<string> gainSet)
    {
        public ComposedGraph Graph => graph;
        public ISet<CellRef> Purchased => purchased;
        public HashSet<int> Tree => tree;
        public LimitTracker Limits => limits;

        /// <summary>Cells this call added, in purchase order.</summary>
        public List<CellRef> Added { get; } = [];

        public HashSet<CellRef> AddedSet { get; } = [];

        public Dictionary<string, double> Gains { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int RaresAdded { get; private set; }

        /// <summary>Purchases a vertex. A crossing's free gate half passes
        /// <paramref name="grantsStats"/> false: it costs nothing and grants nothing.</summary>
        public void Buy(int v, bool grantsStats = true, bool recordAsAdded = true)
        {
            var cell = graph.Vertices[v].Cell;
            tree.Add(v);
            purchased.Add(cell);
            if (recordAsAdded && AddedSet.Add(cell))
                Added.Add(cell);
            limits.OnAbsorbed(v);
            if (!grantsStats)
                return;
            var node = graph.Vertices[v].Node;
            if (node.Kind == ParagonNodeKind.Rare)
                RaresAdded++;
            AdjustGains(node, +1);
        }

        /// <summary>Un-purchases a vertex; returns whether it was one of this call's additions.</summary>
        public bool Sell(int v)
        {
            var cell = graph.Vertices[v].Cell;
            tree.Remove(v);
            purchased.Remove(cell);
            bool wasAdded = AddedSet.Remove(cell);
            if (wasAdded)
                Added.Remove(cell);
            limits.OnRemoved(v);
            var node = graph.Vertices[v].Node;
            if (node.Kind == ParagonNodeKind.Rare)
                RaresAdded--;
            AdjustGains(node, -1);
            return wasAdded;
        }

        private void AdjustGains(ParagonNodeDef node, int sign)
        {
            foreach (var a in node.Attributes)
            {
                if (!a.IsThresholdBonus && a.Value is double value && gainSet.Contains(a.Attribute))
                    Gains[a.Attribute] = Gains.GetValueOrDefault(a.Attribute) + sign * value;
            }
        }
    }

    /// <summary>
    /// What may be sold without collateral damage, captured for the current purchase set:
    /// met threshold bonuses keep their slack, active glyph goals keep their in-radius source
    /// stat, the tree stays connected, targets and gate-pair halves are never sold.
    /// </summary>
    private sealed class SellGuard
    {
        private readonly SpendLedger _ledger;
        private readonly ThresholdContext? _thresholds;
        private readonly HashSet<CellRef> _protectedTargets;
        private readonly int[] _gatePair;
        private readonly Dictionary<string, double> _slackByAttribute = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(GlyphGoal Goal, double Slack)> _goalSlack = [];
        private readonly bool[] _removable;

        public SellGuard(SpendLedger ledger, PlanRequest request, ThresholdContext? thresholds,
            BuildStatsReport? report)
        {
            _ledger = ledger;
            _thresholds = thresholds;
            _protectedTargets = request.Targets.ToHashSet();
            _gatePair = GateCrossings.PairMap(ledger.Graph);

            // Met bonuses may not lose more of their stat than their slack, or they flip off.
            if (report is not null)
            {
                foreach (var met in report.Thresholds.Where(t => t.Met))
                {
                    string coreKey = BuildStats.ParagonKeyFor(met.Attribute);
                    double slack = met.Have - met.Requirement;
                    _slackByAttribute[coreKey] = Math.Min(
                        _slackByAttribute.GetValueOrDefault(coreKey, double.MaxValue), slack);
                }
            }
            // Active glyph goals may not lose more in-radius source stat than their slack.
            var purchasedList = ledger.Purchased.ToList();
            foreach (var goal in request.GlyphGoals)
            {
                double have = GlyphRadius.AttributeTotalsInRange(
                        ledger.Graph, goal.Socket, purchasedList, goal.Radius, GlyphRadius.GameMetric)
                    .GetValueOrDefault(goal.SourceAttribute);
                _goalSlack.Add((goal, have - goal.RequiredTotal));
            }
            // Removal must keep the purchase set connected to the start node.
            _removable = TreeConnectivity.RemovableMask(ledger.Graph, ledger.Tree);
        }

        private double MultiplierOf(int v) =>
            _thresholds?.CellMultipliers?.GetValueOrDefault(_ledger.Graph.Vertices[v].Cell, 1.0) ?? 1.0;

        /// <summary>Whether <paramref name="v"/> can be sold to make room for <paramref name="buy"/>.</summary>
        public bool CanSell(int v, int buy)
        {
            var graph = _ledger.Graph;
            var tree = _ledger.Tree;
            if (v == graph.StartVertex || !IsExpendableKind(graph.Vertices[v].Node.Kind))
                return false;
            // Half of a purchased gate pair frees no real point in-game (the pair costs one; the
            // survivor still costs it) — never trade it away.
            int pair = _gatePair[v];
            if (pair >= 0 && tree.Contains(pair))
                return false;
            var cell = graph.Vertices[v].Cell;
            if (_protectedTargets.Contains(cell))
                return false;
            double multiplier = MultiplierOf(v);
            foreach (var a in graph.Vertices[v].Node.Attributes)
            {
                if (!a.IsThresholdBonus && a.Value is double value
                    && _slackByAttribute.TryGetValue(a.Attribute, out double slack)
                    && value * multiplier > slack)
                    return false; // would flip a met threshold bonus back off
            }
            foreach (var (goal, slack) in _goalSlack)
            {
                if (slack >= 0
                    && cell.BoardSlot == goal.Socket.BoardSlot
                    && Math.Abs(cell.X - goal.Socket.X) + Math.Abs(cell.Y - goal.Socket.Y) <= goal.Radius
                    && GrantOf(graph, v, goal.SourceAttribute) > slack)
                    return false; // would deactivate a met glyph
            }
            // The buy must still touch the tree once this vertex is gone.
            bool buyAnchored = false;
            foreach (int u in graph.Adjacency[buy])
            {
                if (u != v && tree.Contains(u))
                {
                    buyAnchored = true;
                    break;
                }
            }
            return buyAnchored && _removable[v];
        }
    }

    /// <summary>Kinds the reallocation pass may drop: cheap connectors and filler stat nodes.</summary>
    private static bool IsExpendableKind(ParagonNodeKind kind) =>
        kind is ParagonNodeKind.Normal or ParagonNodeKind.Magic or ParagonNodeKind.Gate;

    private static bool TouchesTree(ComposedGraph graph, HashSet<int> tree, int v)
    {
        foreach (int u in graph.Adjacency[v])
        {
            if (tree.Contains(u))
                return true;
        }
        return false;
    }

    private static string NameOf(ComposedGraph graph, int v) =>
        graph.Vertices[v].Node.Name ?? graph.Vertices[v].Node.InternalName;

    private static string DroppedList(List<string> droppedNames)
    {
        var distinct = droppedNames.Distinct().ToList();
        return string.Join(", ", distinct.Take(3)) + (distinct.Count > 3 ? ", …" : "");
    }

    /// <summary>
    /// Trades the least valuable expendable cell among this call's additions for the best
    /// tree-adjacent unpurchased candidate, while each swap strictly improves total value.
    /// Only this call's own additions are ever dropped; every safety the threshold
    /// reallocation applies (connectivity, protected targets, gate pairs, met-threshold and
    /// active-glyph slack, Limit caps) applies here too.
    /// </summary>
    private static void HillClimbValue(
        ComposedGraph graph, PlanRequest request, ThresholdContext? thresholds, SpendLedger ledger,
        bool[] userBlocked, List<string> notes, Func<int, double> valueOf)
    {
        const int MaxSwaps = 40;
        const double Margin = 1e-3; // in normalized units: a thousandth of an average node
        int n = graph.Vertices.Count;
        var tree = ledger.Tree;

        int swaps = 0;
        var droppedNames = new List<string>();
        while (swaps < MaxSwaps)
        {
            // Buy candidate: the most valuable point available next to the tree.
            int buy = -1;
            double buyValue = 0;
            for (int v = 0; v < n; v++)
            {
                if (tree.Contains(v) || userBlocked[v] || ledger.Limits.WouldViolate(v))
                    continue;
                double value = valueOf(v);
                if (value <= buyValue || !TouchesTree(graph, tree, v))
                    continue;
                buy = v;
                buyValue = value;
            }
            if (buy < 0)
                break;

            var report = thresholds is null
                ? null
                : BuildStats.Compute(graph, ledger.Purchased, thresholds.Data,
                    thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);
            var guard = new SellGuard(ledger, request, thresholds, report);

            // Sell candidate: the least valuable of this call's own expendable purchases.
            int sell = -1;
            double sellLoss = double.MaxValue;
            foreach (int v in tree)
            {
                if (!ledger.AddedSet.Contains(graph.Vertices[v].Cell) || !guard.CanSell(v, buy))
                    continue;
                double loss = valueOf(v);
                if (loss < sellLoss)
                {
                    sell = v;
                    sellLoss = loss;
                }
            }
            if (sell < 0 || buyValue <= sellLoss + Margin)
                break;

            ledger.Sell(sell);
            ledger.Buy(buy);
            droppedNames.Add(NameOf(graph, sell));
            swaps++;
        }

        if (swaps > 0)
            notes.Add($"Rebalanced {swaps} point(s) toward higher-value nodes (dropped {DroppedList(droppedNames)}).");
    }

    private static void ReallocateForThresholds(
        ComposedGraph graph, PlanRequest request, ThresholdContext thresholds, SpendLedger ledger,
        bool[] userBlocked, List<string> notes, Func<int, double> valueOf)
    {
        const int MaxSwaps = 40;
        int n = graph.Vertices.Count;
        int swaps = 0;
        var tree = ledger.Tree;
        var failedThresholds = new HashSet<CellRef>();

        double MultiplierOf(int v) =>
            thresholds.CellMultipliers?.GetValueOrDefault(graph.Vertices[v].Cell, 1.0) ?? 1.0;

        BuildStatsReport Report() => BuildStats.Compute(graph, ledger.Purchased, thresholds.Data,
            thresholds.NonParagonStats, thresholds.ClassName, thresholds.CellMultipliers);

        while (swaps < MaxSwaps)
        {
            var report = Report();
            var next = report.Thresholds
                .Where(t => !t.Met && !failedThresholds.Contains(t.Cell))
                .OrderBy(t => t.Requirement - t.Have)
                .FirstOrDefault();
            if (next is null)
                break;

            string targetAttribute = BuildStats.ParagonKeyFor(next.Attribute);
            double needed = next.Requirement - next.Have;

            double gained = 0;
            var swapped = new List<(int Sold, bool SoldWasAdded, int Bought)>();
            var droppedNames = new List<string>();
            while (gained < needed - 1e-9 && swaps + swapped.Count < MaxSwaps)
            {
                // Buy candidate: the most deficit stat one point can add next to the tree.
                int buy = -1;
                double buyValue = 0;
                for (int v = 0; v < n; v++)
                {
                    if (tree.Contains(v) || userBlocked[v] || ledger.Limits.WouldViolate(v))
                        continue;
                    double value = GrantOf(graph, v, targetAttribute) * MultiplierOf(v);
                    if (value <= buyValue || !TouchesTree(graph, tree, v))
                        continue;
                    buy = v;
                    buyValue = value;
                }
                if (buy < 0)
                    break;

                // Sell candidate: the least valuable expendable purchase that nothing depends on.
                var guard = new SellGuard(ledger, request, thresholds, report);
                int sell = -1;
                double sellLoss = double.MaxValue;
                foreach (int v in tree)
                {
                    if (GrantOf(graph, v, targetAttribute) > 0)
                        continue; // dropping it would eat the very stat being bought
                    if (!guard.CanSell(v, buy))
                        continue;
                    double loss = valueOf(v);
                    if (loss < sellLoss)
                    {
                        sell = v;
                        sellLoss = loss;
                    }
                }
                if (sell < 0)
                    break;

                // Execute the 1:1 swap.
                bool soldWasAdded = ledger.Sell(sell);
                ledger.Buy(buy);
                swapped.Add((sell, soldWasAdded, buy));
                droppedNames.Add(NameOf(graph, sell));
                gained += buyValue;
                report = Report();
            }

            if (gained >= needed - 1e-9 && swapped.Count > 0)
            {
                swaps += swapped.Count;
                notes.Add($"Reallocated {swapped.Count} point(s) to activate {next.NodeName} " +
                          $"(dropped {DroppedList(droppedNames)}).");
                continue;
            }

            // Couldn't close this deficit — undo any partial swaps and skip the threshold.
            for (int i = swapped.Count - 1; i >= 0; i--)
            {
                var (sold, soldWasAdded, bought) = swapped[i];
                ledger.Sell(bought);
                ledger.Buy(sold, recordAsAdded: soldWasAdded);
            }
            failedThresholds.Add(next.Cell);
        }
    }
}
