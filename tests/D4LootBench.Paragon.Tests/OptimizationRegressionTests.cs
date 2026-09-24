using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>Guards for the solver-layer optimizations: shared helpers must match the simple
/// algorithms they replaced, and gate-pair accounting must agree with BuildStats.</summary>
public class OptimizationRegressionTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static ParagonLayout TwoBoardLayout() => new([
        new PlacedBoard { Board = Board("Paragon_Sorc_00") },
        new PlacedBoard { Board = Board("Paragon_Sorc_03"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
    ]);

    /// <summary>The per-candidate BFS the cut-vertex mask replaced.</summary>
    private static bool StaysConnectedWithout(ComposedGraph graph, HashSet<int> tree, int candidate)
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Removable_mask_matches_per_candidate_bfs_on_random_trees(int seed)
    {
        var graph = ComposedGraph.Build(TwoBoardLayout());
        var random = new Random(seed);
        // Grow a random connected purchase set from the start node, with cycles allowed.
        var tree = new HashSet<int> { graph.StartVertex };
        for (int i = 0; i < 120; i++)
        {
            var frontier = tree.SelectMany(v => graph.Adjacency[v]).Where(u => !tree.Contains(u)).Distinct().ToList();
            if (frontier.Count == 0)
                break;
            tree.Add(frontier[random.Next(frontier.Count)]);
        }

        var mask = TreeConnectivity.RemovableMask(graph, tree);

        foreach (int v in tree.Where(v => v != graph.StartVertex))
            mask[v].ShouldBe(StaysConnectedWithout(graph, tree, v), $"vertex {v}");
        mask[graph.StartVertex].ShouldBeFalse();
    }

    [Fact]
    public void Spend_across_a_crossing_reports_gains_matching_build_stats()
    {
        // Starting from the bare start node, a large spend must cross into the attached board;
        // the gate pair grants its +5 once, so reported gains equal the BuildStats delta.
        var graph = ComposedGraph.Build(TwoBoardLayout());
        var purchased = new HashSet<CellRef>();
        var before = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, NonParagonStats.None, "Sorcerer");

        var outcome = PointMaximizer.Extend(graph, purchased, 90,
            new MaximizeFocus([], PreferRare: false), new PlanRequest { Targets = [] });

        GateCrossings.FreeCredits(graph, purchased).ShouldBeGreaterThan(0, "the spend should cross the gate pair");
        GateCrossings.PointCost(graph, purchased).ShouldBeLessThanOrEqualTo(90);
        var after = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, NonParagonStats.None, "Sorcerer");
        foreach (var stat in MaximizeFocus.CoreStats)
        {
            double delta = after.Totals.GetValueOrDefault(stat) - before.Totals.GetValueOrDefault(stat);
            outcome.Gains.GetValueOrDefault(stat).ShouldBe(delta, 1e-6, stat);
        }
    }

    [Fact]
    public void Suggest_all_equals_the_individual_families_against_one_baseline()
    {
        var layout = TwoBoardLayout();
        var graph = ComposedGraph.Build(layout);
        var targets = PlanSolver.LegendaryCells(graph);
        var request = new PlanRequest { Targets = targets };
        var solved = PlanSolver.Solve(graph, request);
        solved.Success.ShouldBeTrue(solved.Error);
        var pipeline = new PlacementPipeline(
            80,
            new MaximizeFocus([], PreferRare: true),
            new ThresholdContext(ParagonDatabase.Data, "Sorcerer", NonParagonStats.Uniform(0)));
        var spare = ParagonDatabase.BoardsForClass("Sorcerer").ToList();

        var combined = PlacementAnalyzer.SuggestAll(layout, request, solved, spare, pipeline,
            TestContext.Current.CancellationToken);

        var individual = PlacementAnalyzer.SuggestGlyphAssignment(graph, request, solved, pipeline)
            .Concat(PlacementAnalyzer.SuggestRotations(layout, request, solved, pipeline).Take(3))
            .Concat(PlacementAnalyzer.SuggestBoardSwaps(layout, request, solved, spare, pipeline: pipeline))
            .Concat(PlacementAnalyzer.SuggestReattachments(layout, request, solved, pipeline))
            .ToList();
        combined.Select(s => s.Description).ShouldBe(individual.Select(s => s.Description));
    }

    [Fact]
    public void Placement_search_is_deterministic_across_runs()
    {
        var layout = TwoBoardLayout();
        var graph = ComposedGraph.Build(layout);
        var request = new PlanRequest { Targets = PlanSolver.LegendaryCells(graph) };
        var pipeline = new PlacementPipeline(
            80,
            new MaximizeFocus([], PreferRare: true),
            new ThresholdContext(ParagonDatabase.Data, "Sorcerer", NonParagonStats.Uniform(0)));
        var spare = ParagonDatabase.BoardsForClass("Sorcerer").ToList();

        var first = PlacementSearch.FindPlans(layout, request, pipeline, spare, [], depth: 2, maxEvaluations: 40,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = PlacementSearch.FindPlans(layout, request, pipeline, spare, [], depth: 2, maxEvaluations: 40,
            cancellationToken: TestContext.Current.CancellationToken);

        second.Select(p => p.Describe(1, 1)).ShouldBe(first.Select(p => p.Describe(1, 1)));
    }

    [Fact]
    public void Placement_search_honors_cancellation()
    {
        var layout = TwoBoardLayout();
        var request = new PlanRequest { Targets = PlanSolver.LegendaryCells(ComposedGraph.Build(layout)) };
        var pipeline = new PlacementPipeline(80, new MaximizeFocus([], PreferRare: true), null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => PlacementSearch.FindPlans(
            layout, request, pipeline, ParagonDatabase.BoardsForClass("Sorcerer").ToList(), [],
            cancellationToken: cts.Token));
    }
}
