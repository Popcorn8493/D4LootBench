using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Boards are attached FOR their legendary nodes: <see cref="PlanSolver.LegendaryCells"/> turns
/// them into always-on targets the solve must route through.
/// </summary>
public class LegendaryTargetTests
{
    private static ComposedGraph LegendaryGraph() => SyntheticBoards.Graph(
    [
        "Sab.",
        "..cL",
    ], new()
    {
        ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Normal, ("Dexterity_Core", 1)),
        ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Normal, ("Dexterity_Core", 1)),
        ['c'] = SyntheticBoards.Node("c", ParagonNodeKind.Normal, ("Dexterity_Core", 1)),
        ['L'] = SyntheticBoards.Node("L", ParagonNodeKind.Legendary),
    });

    [Fact]
    public void LegendaryCells_lists_every_legendary_except_excluded_ones()
    {
        var graph = LegendaryGraph();
        var legendary = new CellRef(0, 3, 1);

        PlanSolver.LegendaryCells(graph).ShouldBe([legendary]);
        PlanSolver.LegendaryCells(graph, new HashSet<CellRef> { legendary }).ShouldBeEmpty();
    }

    [Fact]
    public void Solving_with_legendary_targets_routes_the_path_to_them()
    {
        var graph = LegendaryGraph();

        // No user targets at all — exactly the state right after a build import.
        var result = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = PlanSolver.LegendaryCells(graph),
        });

        result.Success.ShouldBeTrue(result.Error);
        result.PurchasedCells.ShouldContain(new CellRef(0, 3, 1));
    }
}
