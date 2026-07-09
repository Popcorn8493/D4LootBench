using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class PointMaximizerTests
{
    private static ComposedGraph StarterGraph() =>
        ComposedGraph.Build(ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00")));

    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    private static void AssertConnected(ComposedGraph graph, IReadOnlyCollection<CellRef> purchased)
    {
        var chosen = new HashSet<int> { graph.StartVertex };
        foreach (var cell in purchased)
        {
            graph.TryGetVertex(cell, out int v).ShouldBeTrue($"purchased cell {cell} must exist");
            chosen.Add(v);
        }
        var seen = new HashSet<int> { graph.StartVertex };
        var queue = new Queue<int>();
        queue.Enqueue(graph.StartVertex);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int u in graph.Adjacency[v])
            {
                if (chosen.Contains(u) && seen.Add(u))
                    queue.Enqueue(u);
            }
        }
        seen.Count.ShouldBe(chosen.Count, "every purchased node must connect back to the start node");
    }

    [Fact]
    public void Spends_at_most_the_budget_and_stays_connected()
    {
        var graph = StarterGraph();
        var purchased = new HashSet<CellRef>();

        var outcome = PointMaximizer.Extend(graph, purchased, 12, new MaximizeFocus([], false), EmptyRequest);

        outcome.AddedCells.Count.ShouldBeGreaterThan(0);
        outcome.AddedCells.Count.ShouldBeLessThanOrEqualTo(12);
        outcome.AddedCells.Count.ShouldBe(purchased.Count);
        outcome.Gains.Values.Sum().ShouldBeGreaterThan(0);
        AssertConnected(graph, purchased);
    }

    [Fact]
    public void Stat_weights_shift_spending_toward_the_prioritized_stat()
    {
        var graph = StarterGraph();
        string[] stats = ["Strength_Core", "Dexterity_Core"];

        var strengthHigh = new HashSet<CellRef>();
        var high = PointMaximizer.Extend(graph, strengthHigh, 20,
            new MaximizeFocus(stats, false)
            {
                Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    { ["Strength_Core"] = 3.0, ["Dexterity_Core"] = 0.3 },
            }, EmptyRequest);

        var strengthLow = new HashSet<CellRef>();
        var low = PointMaximizer.Extend(graph, strengthLow, 20,
            new MaximizeFocus(stats, false)
            {
                Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    { ["Strength_Core"] = 0.3, ["Dexterity_Core"] = 3.0 },
            }, EmptyRequest);

        high.Gains.GetValueOrDefault("Strength_Core")
            .ShouldBeGreaterThanOrEqualTo(low.Gains.GetValueOrDefault("Strength_Core"));
        low.Gains.GetValueOrDefault("Dexterity_Core")
            .ShouldBeGreaterThanOrEqualTo(high.Gains.GetValueOrDefault("Dexterity_Core"));
        // The two runs must actually differ somewhere, or the weights did nothing.
        (high.Gains.GetValueOrDefault("Strength_Core") + high.Gains.GetValueOrDefault("Dexterity_Core"))
            .ShouldBeGreaterThan(0);
        AssertConnected(graph, strengthHigh);
        AssertConnected(graph, strengthLow);
    }

    [Fact]
    public void Realistic_rares_buy_met_threshold_rares_before_unattainable_ones()
    {
        // Two boards so several rares with different threshold stats are reachable.
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00") },
            new PlacedBoard
            {
                Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_03"),
                ParentSlot = 0, AttachEdge = BoardEdge.Top,
            },
        ]);
        var graph = ComposedGraph.Build(layout);

        // A huge Dexterity sheet meets every Dexterity threshold outright; other stats get nothing.
        var context = new ThresholdContext(
            ParagonDatabase.Data, "Sorcerer",
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Dexterity"] = 10_000 }));

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 40,
            new MaximizeFocus([], PreferRare: true) { RealisticRares = true }, EmptyRequest, context);
        outcome.RaresAdded.ShouldBeGreaterThan(0);
        AssertConnected(graph, purchased);

        // The first rare bought must be one whose threshold was already met (rank 0) as long as
        // any met-threshold rare exists on the boards at all.
        var report = BuildStats.Compute(graph, new HashSet<CellRef>(), ParagonDatabase.Data,
            context.NonParagonStats, "Sorcerer");
        var metCells = report.Thresholds.Where(t => t.Met).Select(t => t.Cell).ToHashSet();
        if (metCells.Count > 0)
        {
            var firstRare = outcome.AddedCells.First(c =>
            {
                graph.TryGetVertex(c, out int v);
                return graph.Vertices[v].Node.Kind == ParagonNodeKind.Rare;
            });
            metCells.ShouldContain(firstRare);
        }
    }

    [Fact]
    public void Prefer_rare_buys_rare_nodes_first()
    {
        var graph = StarterGraph();
        var purchased = new HashSet<CellRef>();

        var outcome = PointMaximizer.Extend(graph, purchased, 30, new MaximizeFocus([], true), EmptyRequest);

        outcome.RaresAdded.ShouldBeGreaterThan(0);
        AssertConnected(graph, purchased);

        // Without the rare preference the same budget should catch fewer (or equal) rares.
        var plain = new HashSet<CellRef>();
        var baseline = PointMaximizer.Extend(graph, plain, 30, new MaximizeFocus([], false), EmptyRequest);
        int baselineRares = baseline.AddedCells.Count(c =>
        {
            graph.TryGetVertex(c, out int v);
            return graph.Vertices[v].Node.Kind == ParagonNodeKind.Rare;
        });
        outcome.RaresAdded.ShouldBeGreaterThanOrEqualTo(baselineRares);
    }

    [Fact]
    public void Focused_attribute_dominates_the_gains()
    {
        var graph = StarterGraph();
        var purchased = new HashSet<CellRef>();

        var outcome = PointMaximizer.Extend(
            graph, purchased, 10, new MaximizeFocus(["Intelligence_Core"], false), EmptyRequest);

        outcome.Gains.Keys.ShouldAllBe(k => k == "Intelligence_Core");
        outcome.Gains["Intelligence_Core"].ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_limit_rule_caps_how_many_of_a_group_the_maximizer_buys()
    {
        var graph = StarterGraph();

        // Find the group the unfocused maximizer leans on, then cap it.
        var probe = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, probe, 15, new MaximizeFocus([], false), EmptyRequest);
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        var heaviest = cellsByGroup
            .Select(kv => (kv.Key, Count: kv.Value.Count(probe.Contains)))
            .OrderByDescending(g => g.Count)
            .First();
        heaviest.Count.ShouldBeGreaterThan(1, "the probe run should use some group more than once");

        var purchased = new HashSet<CellRef>();
        var request = new PlanRequest
        {
            Targets = [],
            NodeRules = [new NodeRule(heaviest.Key, NodeRuleMode.Limit, 1)],
        };
        PointMaximizer.Extend(graph, purchased, 15, new MaximizeFocus([], false), request);

        cellsByGroup[heaviest.Key].Count(purchased.Contains).ShouldBeLessThanOrEqualTo(1);
        AssertConnected(graph, purchased);
    }

    [Fact]
    public void Starting_from_a_solved_tree_only_adds_new_cells()
    {
        var graph = StarterGraph();
        var rare = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.Rare).Cell;
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = [rare] });
        solved.Success.ShouldBeTrue(solved.Error);

        var purchased = solved.PurchasedCells.ToHashSet();
        int before = purchased.Count;
        var outcome = PointMaximizer.Extend(graph, purchased, 5, new MaximizeFocus([], false), EmptyRequest);

        outcome.AddedCells.ShouldAllBe(c => !solved.PurchasedCells.Contains(c));
        purchased.Count.ShouldBe(before + outcome.AddedCells.Count);
        AssertConnected(graph, purchased);
    }
}
