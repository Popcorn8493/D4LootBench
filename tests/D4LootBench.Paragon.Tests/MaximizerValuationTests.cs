using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// How the maximizer VALUES candidates: glyph node buffs multiply a cell's effective grant,
/// the normalization pool ignores excluded cells, and the defense slice is proportional.
/// </summary>
public class MaximizerValuationTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    [Fact]
    public void Glyph_buffed_cell_beats_an_equal_raw_grant()
    {
        // A S B — identical Dexterity nodes either side of the start; B sits inside a glyph
        // node-buff area (×2 effective), so one point must go right, not left.
        var graph = SyntheticBoards.Graph(["ASB"], new()
        {
            ['A'] = SyntheticBoards.Node("A", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
            ['B'] = SyntheticBoards.Node("B", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
        });
        var context = new ThresholdContext(new ParagonData(), null, NonParagonStats.None,
            new Dictionary<CellRef, double> { [new CellRef(0, 2, 0)] = 2.0 });

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1, new MaximizeFocus(["Dexterity_Core"], false),
            EmptyRequest, context);

        purchased.ShouldBe([new CellRef(0, 2, 0)]);
    }

    [Fact]
    public void Buffed_defensive_cell_beats_an_equal_raw_grant()
    {
        // Same shape for the defense phase: equal Hitpoints nodes, B buffed.
        var graph = SyntheticBoards.Graph(["ASB"], new()
        {
            ['A'] = SyntheticBoards.Node("A", ParagonNodeKind.Magic, ("Hitpoints_Max", 10)),
            ['B'] = SyntheticBoards.Node("B", ParagonNodeKind.Magic, ("Hitpoints_Max", 10)),
        });
        var context = new ThresholdContext(new ParagonData(), null, NonParagonStats.None,
            new Dictionary<CellRef, double> { [new CellRef(0, 2, 0)] = 2.0 });

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Strength_Core"], false) { DefenseShare = 1.0 }, EmptyRequest, context);

        purchased.ShouldBe([new CellRef(0, 2, 0)]);
    }

    [Fact]
    public void Excluded_cells_do_not_skew_the_normalization_pool()
    {
        // Dex candidates d (12) and c (8) normalize against each other, NOT against the huge
        // excluded outlier x (1000). With the outlier in the pool, d would look nearly
        // worthless next to the Strength node t and the single point would go to t.
        var graph = SyntheticBoards.Graph(["cdStux"], new()
        {
            ['c'] = SyntheticBoards.Node("c", ParagonNodeKind.Magic, ("Dexterity_Core", 8)),
            ['d'] = SyntheticBoards.Node("d", ParagonNodeKind.Magic, ("Dexterity_Core", 12)),
            ['t'] = SyntheticBoards.Node("t", ParagonNodeKind.Magic, ("Strength_Core", 10)),
            ['u'] = SyntheticBoards.Node("u", ParagonNodeKind.Magic, ("Strength_Core", 10)),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Rare, ("Dexterity_Core", 1000)),
        });
        var request = new PlanRequest { Targets = [], ExcludeCells = [new CellRef(0, 5, 0)] };

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Dexterity_Core", "Strength_Core"], false), request);

        purchased.ShouldBe([new CellRef(0, 1, 0)]);
    }

    [Fact]
    public void Tiny_budgets_round_the_defense_share_to_nothing()
    {
        // 2 points at a 0.15 share is 0.3 of a point — proportionally that buys no defense.
        var graph = SyntheticBoards.Graph(["DStt"], new()
        {
            ['D'] = SyntheticBoards.Node("D", ParagonNodeKind.Magic, ("Hitpoints_Max", 10)),
            ['t'] = SyntheticBoards.Node("t", ParagonNodeKind.Magic, ("Strength_Core", 5)),
        });

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 2,
            new MaximizeFocus(["Strength_Core"], false) { DefenseShare = 0.15 }, EmptyRequest);

        outcome.Gains.Keys.ShouldNotContain("Hitpoints_Max");
        purchased.ShouldBe([new CellRef(0, 2, 0), new CellRef(0, 3, 0)], ignoreOrder: true);
    }
}
