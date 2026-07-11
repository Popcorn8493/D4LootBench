using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Limit rules must hold across the whole pipeline — not just the base Steiner solve, but also
/// the greedy glyph-activation extension and the leftover-point maximizer (both used to walk
/// straight through capped groups; the max-life cluster on Searing Heat reproduces it).
/// </summary>
public class LimitEnforcementTests
{
    private const string LifeKey = "Magic:Hitpoints_Max_Percent_Bonus:";
    private const int Limit = 2;

    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    /// <summary>Starter + a board with a 7-cell max-life cluster near targets and the socket.</summary>
    private static ComposedGraph LifeClusterGraph() => ComposedGraph.Build(new ParagonLayout(
    [
        new PlacedBoard { Board = Board("Paragon_Sorc_00") },
        new PlacedBoard
        {
            Board = ParagonDatabase.BoardsForClass("Sorcerer").Single(b => b.Name == "Searing Heat"),
            ParentSlot = 0,
            AttachEdge = BoardEdge.Top,
        },
    ]));

    private static PlanRequest LimitedRequest(ComposedGraph graph) => new()
    {
        Targets = [graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Legendary && v.Cell.BoardSlot == 1).Cell],
        NodeRules = [new NodeRule(LifeKey, NodeRuleMode.Limit, Limit)],
        GlyphGoals = [new GlyphGoal(
            graph.Vertices.First(v =>
                v.Node.Kind == ParagonNodeKind.GlyphSocket && v.Cell.BoardSlot == 1).Cell,
            "Intelligence_Core", 120, 5, "test")],
    };

    private static int LifeNodes(ComposedGraph graph, IReadOnlyCollection<CellRef> purchased) =>
        NodeGrouping.CellsByGroup(graph)[LifeKey].Count(purchased.Contains);

    [Fact]
    public void Minimal_mode_uses_the_fewest_group_nodes_at_equal_points()
    {
        var graph = LifeClusterGraph();
        var request = LimitedRequest(graph);

        var plain = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = request.Targets,
            GlyphGoals = request.GlyphGoals,
        });
        plain.Success.ShouldBeTrue(plain.Error);

        var minimal = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = request.Targets,
            GlyphGoals = request.GlyphGoals,
            // Generous cap: the point of Minimal is the fewest-nodes preference, not the cap.
            NodeRules = [new NodeRule(LifeKey, NodeRuleMode.Minimal, 99)],
        });
        minimal.Success.ShouldBeTrue(minimal.Error);

        // The +1 tie-break may never cost real points, and can only reduce group usage.
        var plainBase = PlanSolver.Solve(graph, new PlanRequest { Targets = request.Targets });
        var minimalBase = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = request.Targets,
            NodeRules = [new NodeRule(LifeKey, NodeRuleMode.Minimal, 99)],
        });
        minimalBase.PointsSpent.ShouldBe(plainBase.PointsSpent);
        LifeNodes(graph, minimalBase.PurchasedCells)
            .ShouldBeLessThanOrEqualTo(LifeNodes(graph, plainBase.PurchasedCells));
        LifeNodes(graph, minimal.PurchasedCells)
            .ShouldBeLessThanOrEqualTo(LifeNodes(graph, plain.PurchasedCells));
    }

    [Fact]
    public void Minimal_mode_still_caps_like_limit()
    {
        var graph = LifeClusterGraph();
        var request = new PlanRequest
        {
            Targets = LimitedRequest(graph).Targets,
            GlyphGoals = LimitedRequest(graph).GlyphGoals,
            NodeRules = [new NodeRule(LifeKey, NodeRuleMode.Minimal, Limit)],
        };
        var result = PlanSolver.Solve(graph, request);
        result.Success.ShouldBeTrue(result.Error);
        LifeNodes(graph, result.PurchasedCells).ShouldBeLessThanOrEqualTo(Limit);

        // The maximizer honors the Minimal cap exactly like a Limit cap.
        var purchased = result.PurchasedCells.ToHashSet();
        PointMaximizer.Extend(graph, purchased, 60, new MaximizeFocus([], PreferRare: true), request);
        LifeNodes(graph, purchased).ShouldBeLessThanOrEqualTo(Limit);
    }

    [Fact]
    public void Suggestions_never_trade_a_limit_break_for_other_gains()
    {
        var clean = new PipelineResult(2, 5, 100, 10.0);
        var breaking = new PipelineResult(3, 8, 90, 20.0) { LimitBreaks = 1 };

        breaking.BeatsForSuggestion(clean).ShouldBeFalse("a limit break may never look like a win");
        clean.BeatsForSuggestion(breaking).ShouldBeTrue("restoring a broken limit is always a win");
    }

    [Fact]
    public void Glyph_activation_extension_respects_a_limit_rule()
    {
        var graph = LifeClusterGraph();
        var result = PlanSolver.Solve(graph, LimitedRequest(graph));
        result.Success.ShouldBeTrue(result.Error);

        LifeNodes(graph, result.PurchasedCells).ShouldBeLessThanOrEqualTo(Limit);
        result.Notes.ShouldNotContain(n => n.Contains("pushed"), "the safety-net note must not fire");
    }

    [Fact]
    public void Maximizer_never_jumps_a_group_past_its_cap()
    {
        var graph = LifeClusterGraph();
        var request = LimitedRequest(graph);
        var result = PlanSolver.Solve(graph, request);
        result.Success.ShouldBeTrue(result.Error);

        // Chase the capped stat directly — the strongest pull toward violating the limit.
        var purchased = result.PurchasedCells.ToHashSet();
        PointMaximizer.Extend(graph, purchased, 60,
            new MaximizeFocus(["Hitpoints_Max_Percent_Bonus"], PreferRare: true), request);

        // The cap is reached exactly: the allowance stays usable for deliberate picks
        // (the routing penalty must not scare the maximizer off the stat it chases).
        LifeNodes(graph, purchased).ShouldBe(Limit);
    }

    [Fact]
    public void Glyph_activation_detours_around_the_limited_cluster_instead_of_giving_up()
    {
        var graph = LifeClusterGraph();
        var limited = LimitedRequest(graph); // requires 120 — unreachable on this board
        var achievable = new PlanRequest
        {
            Targets = limited.Targets,
            NodeRules = limited.NodeRules,
            GlyphGoals = [limited.GlyphGoals[0] with { RequiredTotal = 80 }],
        };

        var result = PlanSolver.Solve(graph, achievable);
        result.Success.ShouldBeTrue(result.Error);

        // 80 Intelligence is reachable without breaking the cap — the extension must route
        // around the max-life cluster, not report the stat as unreachable.
        result.GlyphOutcomes[0].Met.ShouldBeTrue();
        LifeNodes(graph, result.PurchasedCells).ShouldBeLessThanOrEqualTo(Limit);
    }

    [Fact]
    public void Layout_optimizer_applies_node_rules_to_its_solves()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Board("Paragon_Sorc_00"),
            MustUseBoards = [ParagonDatabase.BoardsForClass("Sorcerer").Single(b => b.Name == "Searing Heat")],
            MaxBoards = 2,
            Glyphs = [ParagonDatabase.Data.Glyphs.Single(g =>
                g.Name == "Enchanter" && g.Classes.Contains("Sorcerer"))],
            NodeRules = [new NodeRule(LifeKey, NodeRuleMode.Limit, Limit)],
        }, ParagonDatabase.Data);

        result.Success.ShouldBeTrue(result.Error);
        LifeNodes(ComposedGraph.Build(result.Layout!), result.Plan!.PurchasedCells)
            .ShouldBeLessThanOrEqualTo(Limit);
    }

    [Fact]
    public void Without_a_limit_rule_the_extension_still_buys_freely()
    {
        var graph = LifeClusterGraph();
        var limited = LimitedRequest(graph);
        var unlimited = new PlanRequest { Targets = limited.Targets, GlyphGoals = limited.GlyphGoals };

        var result = PlanSolver.Solve(graph, unlimited);
        result.Success.ShouldBeTrue(result.Error);

        // Sanity: this scenario actually exercises the limit — the unrestricted
        // solve wants more life nodes than the cap allows.
        LifeNodes(graph, result.PurchasedCells).ShouldBeGreaterThan(Limit);
    }
}
