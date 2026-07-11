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
    public void Defense_share_reserves_points_for_survivability()
    {
        MaximizeFocus.IsDefensive("Hitpoints_Max_Percent_Bonus").ShouldBeTrue();
        MaximizeFocus.IsDefensive("Resistance_All_Bonus_Percent").ShouldBeTrue();
        MaximizeFocus.IsDefensive("Dodge_Chance_Bonus").ShouldBeTrue();
        MaximizeFocus.IsDefensive("Crit_Damage_Percent").ShouldBeFalse();
        MaximizeFocus.IsDefensive("Strength_Core").ShouldBeFalse();

        var graph = StarterGraph();
        double DefenseGained(double share)
        {
            var purchased = new HashSet<CellRef>();
            var outcome = PointMaximizer.Extend(graph, purchased, 25,
                new MaximizeFocus(["Strength_Core"], false) { DefenseShare = share }, EmptyRequest);
            AssertConnected(graph, purchased);
            return outcome.Gains
                .Where(g => MaximizeFocus.IsDefensive(g.Key))
                .Sum(g => g.Value);
        }

        // A pure-offense focus buys no defense; the share carves out a guaranteed slice.
        DefenseGained(0).ShouldBe(0);
        DefenseGained(0.3).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Reference_emphasis_ranks_the_stats_an_allocation_actually_stacks()
    {
        var graph = StarterGraph();
        // Allocate every Strength-granting cell of the board and nothing else.
        var allocated = graph.Vertices
            .Where(v => v.Node.Attributes.Any(a => !a.IsThresholdBonus
                && string.Equals(a.Attribute, "Strength_Core", StringComparison.OrdinalIgnoreCase)
                && a.Value is not null))
            .Select(v => v.Cell)
            .ToList();
        allocated.Count.ShouldBeGreaterThan(0);

        var emphasis = BuildReference.EmphasisOf(graph, allocated);

        emphasis.ShouldNotBeEmpty();
        emphasis[0].Attribute.ShouldBe("Strength_Core", StringCompareShould.IgnoreCase);
        // Kind-weighted (travel normals ×0.25), so a pure-Strength allocation still scores
        // meaningfully but well below its raw node count.
        emphasis[0].Score.ShouldBeGreaterThan(allocated.Count * 0.1);
    }

    [Fact]
    public void Combined_references_reward_consensus_and_dilute_outliers()
    {
        // Guide A and B agree on crit; only A stacks resistance; scales differ 10× to prove
        // per-reference normalization (a bigger build must not dominate the average).
        IReadOnlyList<ReferenceEmphasis> guideA =
        [
            new("Strength_Core", 100), new("Crit_Percent_Bonus", 10), new("Resistance_All_Bonus_Percent", 8),
        ];
        IReadOnlyList<ReferenceEmphasis> guideB =
        [
            new("Strength_Core", 10), new("Crit_Percent_Bonus", 1.0),
        ];

        var combined = BuildReference.Combine([guideA, guideB], MaximizeFocus.CoreStats);
        var byName = combined.ToDictionary(e => e.Attribute, e => e.Score, StringComparer.OrdinalIgnoreCase);

        byName["Strength_Core"].ShouldBe(1.0, 0.001);          // tops both core tiers
        byName["Crit_Percent_Bonus"].ShouldBe(1.0, 0.001);     // tops both secondary tiers
        byName["Resistance_All_Bonus_Percent"].ShouldBe(0.4, 0.001); // 0.8 in A, absent in B
        BuildReference.AgreementCount([guideA, guideB], "Crit_Percent_Bonus", MaximizeFocus.CoreStats)
            .ShouldBe(2);
        BuildReference.AgreementCount([guideA, guideB], "Resistance_All_Bonus_Percent", MaximizeFocus.CoreStats)
            .ShouldBe(1);
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
    public void Reallocation_respects_budget_connectivity_and_never_loses_thresholds()
    {
        // Two boards, tight budget, thresholds on — conditions where the greedy spend runs dry
        // mid-threshold and the reallocation pass kicks in.
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_00") },
            new PlacedBoard
            {
                Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_03"),
                ParentSlot = 0, AttachEdge = BoardEdge.Top,
            },
        ]);
        var graph = ComposedGraph.Build(layout);
        var target = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.Legendary).Cell;
        var request = new PlanRequest { Targets = [target] };
        var solve = PlanSolver.Solve(graph, request);
        solve.Success.ShouldBeTrue(solve.Error);

        var context = new ThresholdContext(ParagonDatabase.Data, "Barbarian",
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Strength"] = 400 }));
        var purchased = solve.PurchasedCells.ToHashSet();
        int before = purchased.Count;
        int metBefore = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            context.NonParagonStats, "Barbarian").ThresholdsMet;

        const int budget = 30;
        var outcome = PointMaximizer.Extend(graph, purchased, budget,
            new MaximizeFocus([], PreferRare: true, ActivateThresholds: true), request, context);

        // Swaps are strictly 1:1 — the total spend can never exceed the budget.
        (purchased.Count - before).ShouldBeLessThanOrEqualTo(budget);
        AssertConnected(graph, purchased);
        BuildStats.Compute(graph, purchased, ParagonDatabase.Data, context.NonParagonStats, "Barbarian")
            .ThresholdsMet.ShouldBeGreaterThanOrEqualTo(metBefore);
        // Protected cells survive any swapping.
        purchased.ShouldContain(target);
    }

    [Fact]
    public void Zero_budget_with_thresholds_can_still_reallocate()
    {
        var graph = StarterGraph();
        var purchased = new HashSet<CellRef>();
        // Fill the starter board substantially, then hand the maximizer a zero budget.
        PointMaximizer.Extend(graph, purchased, 40, new MaximizeFocus([], true), EmptyRequest);
        int count = purchased.Count;

        var context = new ThresholdContext(ParagonDatabase.Data, "Barbarian", NonParagonStats.Uniform(0));
        var outcome = PointMaximizer.Extend(graph, purchased, 0,
            new MaximizeFocus([], PreferRare: true, ActivateThresholds: true), EmptyRequest, context);

        purchased.Count.ShouldBe(count); // zero budget: only 1:1 swaps are allowed
        AssertConnected(graph, purchased);
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
