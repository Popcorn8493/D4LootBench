using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>The rare phase ranks by value-per-point within an attainability rank, not by distance.</summary>
public class RareRankingTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    [Fact]
    public void A_farther_but_stronger_rare_beats_the_nearest_one()
    {
        // w S x Y — w is a rare granting 2 at cost 1 (ratio 2); Y is a rare granting 30 behind
        // one travel node (ratio 15). Cheapest-first bought w first; two points must instead
        // land the strong rare, leaving w unbought.
        var graph = SyntheticBoards.Graph(["wSxY"], new()
        {
            ['w'] = SyntheticBoards.Node("w", ParagonNodeKind.Rare, ("Dexterity_Core", 2)),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Normal),
            ['Y'] = SyntheticBoards.Node("Y", ParagonNodeKind.Rare, ("Dexterity_Core", 30)),
        });

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 2,
            new MaximizeFocus(["Dexterity_Core"], PreferRare: true), EmptyRequest);

        outcome.RaresAdded.ShouldBe(1);
        purchased.ShouldBe([new CellRef(0, 2, 0), new CellRef(0, 3, 0)], ignoreOrder: true);
    }

    [Fact]
    public void Attainability_rank_still_outranks_raw_value()
    {
        // Met-threshold rares must come first even when an unattainable one is worth more —
        // exercised on real boards where threshold defs exist (see
        // PointMaximizerTests.Realistic_rares_buy_met_threshold_rares_before_unattainable_ones).
        // Here: without RealisticRares both rares rank equally, so value decides.
        var graph = SyntheticBoards.Graph(["wSxY"], new()
        {
            ['w'] = SyntheticBoards.Node("w", ParagonNodeKind.Rare, ("Dexterity_Core", 2)),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Normal),
            ['Y'] = SyntheticBoards.Node("Y", ParagonNodeKind.Rare, ("Dexterity_Core", 30)),
        });

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 3,
            new MaximizeFocus(["Dexterity_Core"], PreferRare: true), EmptyRequest);

        // With a third point, the weak rare is still worth catching afterwards.
        outcome.RaresAdded.ShouldBe(2);
        outcome.Gains["Dexterity_Core"].ShouldBe(32);
    }
}
