using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Phase H: after the greedy spend, low-value additions are traded 1:1 for strictly better
/// tree-adjacent candidates — but never the caller's pre-existing purchases.
/// </summary>
public class HillClimbTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    private static ComposedGraph ChainGraph() => SyntheticBoards.Graph(["cSab"], new()
    {
        ['c'] = SyntheticBoards.Node("c", ParagonNodeKind.Magic, ("Dexterity_Core", 6)),
        ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Normal, ("Dexterity_Core", 1)),
        ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Magic, ("Dexterity_Core", 10)),
    });

    [Fact]
    public void Corrects_a_greedy_misordering()
    {
        // c S a b — greedy takes c (best single ratio), then only a fits the last point:
        // 7 Dex total. Trading c for b afterwards lands the optimal 11.
        var graph = ChainGraph();

        var purchased = new HashSet<CellRef>();
        var outcome = PointMaximizer.Extend(graph, purchased, 2,
            new MaximizeFocus(["Dexterity_Core"], false), EmptyRequest);

        purchased.ShouldBe([new CellRef(0, 2, 0), new CellRef(0, 3, 0)], ignoreOrder: true);
        outcome.Gains["Dexterity_Core"].ShouldBe(11);
        outcome.AddedCells.Count.ShouldBe(2); // swaps keep the spend accounting exact
        outcome.Notes.ShouldContain(n => n.StartsWith("Rebalanced"));
    }

    [Fact]
    public void Never_drops_a_pre_existing_purchase()
    {
        // c was bought before this call. With 1 point the spend adds a; trading the
        // pre-existing c for b would be a value win, but only this call's own additions are
        // up for rebalancing.
        var graph = ChainGraph();
        var cCell = new CellRef(0, 0, 0);

        var purchased = new HashSet<CellRef> { cCell };
        var outcome = PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Dexterity_Core"], false), EmptyRequest);

        purchased.ShouldContain(cCell);
        outcome.AddedCells.ShouldBe([new CellRef(0, 2, 0)]);
    }
}
