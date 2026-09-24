using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class PlanSolverTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static ComposedGraph StarterGraph() =>
        ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));

    private static CellRef FarRare(ComposedGraph graph) =>
        graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Rare)
            .OrderByDescending(v => Math.Abs(v.Cell.X - 10) + Math.Abs(v.Cell.Y - 20))
            .First().Cell;

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
    public void Excluded_cell_is_never_purchased_and_the_path_detours_around_it()
    {
        var graph = StarterGraph();
        var target = FarRare(graph);

        var baseline = PlanSolver.Solve(graph, new PlanRequest { Targets = [target] });
        baseline.Success.ShouldBeTrue(baseline.Error);

        var detourAround = baseline.PurchasedCells.First(c =>
        {
            graph.TryGetVertex(c, out int v);
            return c != target && graph.Vertices[v].Node.Kind == ParagonNodeKind.Normal;
        });

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            ExcludeCells = [detourAround],
        });

        result.Success.ShouldBeTrue(result.Error);
        result.PurchasedCells.ShouldNotContain(detourAround);
        result.PurchasedCells.ShouldContain(target);
        result.PointsSpent.ShouldBeGreaterThanOrEqualTo(baseline.PointsSpent);
        AssertConnected(graph, result.PurchasedCells);
    }

    [Fact]
    public void Excluding_a_target_fails_with_a_clear_error()
    {
        var graph = StarterGraph();
        var target = FarRare(graph);

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            ExcludeCells = [target],
        });

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("excluded");
    }

    [Fact]
    public void Group_exclude_keeps_the_whole_group_out_of_the_purchase()
    {
        var graph = StarterGraph();
        var target = FarRare(graph);
        var group = NodeGrouping.GroupsIn(graph)
            .Where(g => g.Kind == ParagonNodeKind.Magic)
            .OrderByDescending(g => g.CellCount)
            .First();

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            NodeRules = [new NodeRule(group.Key, NodeRuleMode.Exclude)],
        });

        // Either the board still offers a route, or exclusion made the target unreachable — both
        // are legitimate; a success must simply honor the rule.
        if (result.Success)
        {
            var groupCells = NodeGrouping.CellsByGroup(graph)[group.Key];
            result.PurchasedCells.ShouldNotContain(c => groupCells.Contains(c));
            AssertConnected(graph, result.PurchasedCells);
        }
        else
        {
            result.Error.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Limit_rule_is_honored_or_reported_in_notes()
    {
        var graph = StarterGraph();
        var target = FarRare(graph);
        var group = NodeGrouping.GroupsIn(graph)
            .Where(g => g.Kind is ParagonNodeKind.Normal or ParagonNodeKind.Magic)
            .OrderByDescending(g => g.CellCount)
            .First();

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            NodeRules = [new NodeRule(group.Key, NodeRuleMode.Limit, Limit: 1)],
        });

        result.Success.ShouldBeTrue(result.Error);
        AssertConnected(graph, result.PurchasedCells);
        var groupCells = NodeGrouping.CellsByGroup(graph)[group.Key];
        int taken = result.PurchasedCells.Count(groupCells.Contains);
        if (taken > 1)
            result.Notes.ShouldContain(n => n.Contains("Limit"), $"took {taken} of {group.DisplayName} without a note");
    }

    [Fact]
    public void Avoided_group_still_yields_a_valid_covering_tree()
    {
        var graph = StarterGraph();
        var target = FarRare(graph);
        var group = NodeGrouping.GroupsIn(graph).First(g => g.Kind == ParagonNodeKind.Normal);

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            NodeRules = [new NodeRule(group.Key, NodeRuleMode.Avoid)],
        });

        result.Success.ShouldBeTrue(result.Error);
        result.PurchasedCells.ShouldContain(target);
        AssertConnected(graph, result.PurchasedCells);
    }

    [Fact]
    public void Glyph_goal_connects_the_socket_and_buys_stat_until_the_requirement_is_met()
    {
        var graph = StarterGraph();
        var socket = graph.Vertices.Single(v => v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var goal = new GlyphGoal(socket, "Intelligence_Core", RequiredTotal: 60, Radius: 5, GlyphName: "TestGlyph");

        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [FarRare(graph)], // socket is NOT a target — the optimizer must connect it
            GlyphGoals = [goal],
        });

        result.Success.ShouldBeTrue(result.Error);
        result.PurchasedCells.ShouldContain(socket);
        AssertConnected(graph, result.PurchasedCells);

        var outcome = result.GlyphOutcomes.ShouldHaveSingleItem();
        outcome.Met.ShouldBeTrue($"achieved only {outcome.AchievedTotal}");

        var totals = GlyphRadius.AttributeTotalsInRange(
            graph, socket, result.PurchasedCells, radius: 5, GlyphRadius.GameMetric);
        totals["Intelligence_Core"].ShouldBeGreaterThanOrEqualTo(60);
        outcome.AchievedTotal.ShouldBe(totals["Intelligence_Core"], tolerance: 1e-6);
    }

    [Fact]
    public void Impossible_glyph_requirement_is_a_note_not_a_failure()
    {
        var graph = StarterGraph();
        var socket = graph.Vertices.Single(v => v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var goal = new GlyphGoal(socket, "Intelligence_Core", RequiredTotal: 10_000, Radius: 5, GlyphName: "Greedy");

        var result = PlanSolver.Solve(graph, new PlanRequest { Targets = [socket], GlyphGoals = [goal] });

        result.Success.ShouldBeTrue(result.Error);
        result.GlyphOutcomes.Single().Met.ShouldBeFalse();
        result.Notes.ShouldContain(n => n.Contains("Cannot activate Greedy"));
        AssertConnected(graph, result.PurchasedCells);
    }

    [Fact]
    public void GlyphInfo_reads_the_source_stat_from_maps_or_falls_back_to_the_name()
    {
        var enchanter = ParagonDatabase.Data.Glyphs.Single(g => g.InternalName == "Rare_001_Intelligence_Main");
        GlyphInfo.PrimarySourceAttribute(enchanter).ShouldBe("Intelligence_Core");

        // "Unleash" has no attribute maps (its bonus buffs magic nodes) — stat comes from the name.
        var unleash = ParagonDatabase.Data.Glyphs.Single(g => g.InternalName == "Rare_002_Intelligence_Main");
        GlyphInfo.PrimarySourceAttribute(unleash).ShouldBe("Intelligence_Core");
    }

    [Fact]
    public void Rotation_analysis_returns_only_genuine_improvements()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_05"), ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = 2 },
        ]);
        var graph = ComposedGraph.Build(layout);
        var legendary = graph.Vertices.Single(v => v.Node.Kind == ParagonNodeKind.Legendary).Cell;
        var request = new PlanRequest { Targets = [legendary] };
        var baseline = PlanSolver.Solve(graph, request);
        baseline.Success.ShouldBeTrue(baseline.Error);

        var suggestions = PlacementAnalyzer.SuggestRotations(layout, request, baseline);

        foreach (var suggestion in suggestions)
        {
            suggestion.Description.ShouldNotBeNullOrWhiteSpace();
            suggestion.PointsSaved.ShouldBeGreaterThanOrEqualTo(0);
            suggestion.PointsSaved.ShouldBeLessThan(baseline.PointsSpent);
        }
    }
}
