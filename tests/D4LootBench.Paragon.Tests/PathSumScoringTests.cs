using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Candidates are scored by the value of their WHOLE path (absorbing buys every intermediate
/// node), and equally cheap routes prefer the stat-bearing one.
/// </summary>
public class PathSumScoringTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    [Fact]
    public void A_rich_path_to_a_far_node_beats_a_nearer_single()
    {
        // m S x y E — m grants 7; the right chain grants 4+4+20=28 over 3 points. Endpoint-only
        // scoring buys m first (7 > 20/3) and never affords E; path-sum spends all 3 on the chain.
        var graph = SyntheticBoards.Graph(["mSxyE"], new()
        {
            ['m'] = SyntheticBoards.Node("m", ParagonNodeKind.Magic, ("Dexterity_Core", 7)),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Normal, ("Dexterity_Core", 4)),
            ['y'] = SyntheticBoards.Node("y", ParagonNodeKind.Normal, ("Dexterity_Core", 4)),
            ['E'] = SyntheticBoards.Node("E", ParagonNodeKind.Rare, ("Dexterity_Core", 20)),
        });

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 3,
            new MaximizeFocus(["Dexterity_Core"], false), EmptyRequest);

        purchased.ShouldBe(
            [new CellRef(0, 2, 0), new CellRef(0, 3, 0), new CellRef(0, 4, 0)], ignoreOrder: true);
        outcome.Gains["Dexterity_Core"].ShouldBe(28);
    }

    [Fact]
    public void Equally_cheap_routes_take_the_stat_bearing_one()
    {
        // Two cost-2 routes from S to the big node T: over a (Dexterity 3) or over b (nothing).
        var graph = SyntheticBoards.Graph(
        [
            "Sa",
            "bT",
        ], new()
        {
            ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Normal, ("Dexterity_Core", 3)),
            ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Normal),
            ['T'] = SyntheticBoards.Node("T", ParagonNodeKind.Rare, ("Dexterity_Core", 10)),
        });

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 2,
            new MaximizeFocus(["Dexterity_Core"], false), EmptyRequest);

        purchased.ShouldContain(new CellRef(0, 1, 1)); // T
        purchased.ShouldContain(new CellRef(0, 1, 0)); // the Dex route
        purchased.ShouldNotContain(new CellRef(0, 0, 1));
    }

    [Fact]
    public void Glyph_activation_credits_in_radius_stat_absorbed_en_route()
    {
        // m S g x Z — all Dexterity cells sit within radius 3 of the socket g. Needing 22,
        // endpoint-only scoring buys m (6) first and needs three stat nodes; crediting the
        // path absorbs x (2) on the way to Z (20) and activates with two.
        var graph = SyntheticBoards.Graph(["mSgxZ"], new()
        {
            ['m'] = SyntheticBoards.Node("m", ParagonNodeKind.Magic, ("Dexterity_Core", 6)),
            ['g'] = SyntheticBoards.Node("g", ParagonNodeKind.GlyphSocket),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Normal, ("Dexterity_Core", 2)),
            ['Z'] = SyntheticBoards.Node("Z", ParagonNodeKind.Rare, ("Dexterity_Core", 20)),
        });
        var socket = new CellRef(0, 2, 0);
        var goal = new GlyphGoal(socket, "Dexterity_Core", 22, Radius: 3);

        var purchased = new HashSet<CellRef>();
        var outcomes = GlyphOptimizer.Extend(graph, purchased, [goal], new SolverConstraints());

        outcomes.Single().Met.ShouldBeTrue();
        outcomes.Single().AchievedTotal.ShouldBe(22);
        purchased.ShouldBe([socket, new CellRef(0, 3, 0), new CellRef(0, 4, 0)], ignoreOrder: true);
    }
}
