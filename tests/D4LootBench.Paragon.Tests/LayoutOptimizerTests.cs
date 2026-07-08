using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class LayoutOptimizerTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static ParagonGlyphDef Glyph(string name) =>
        ParagonDatabase.Data.Glyphs.Single(g => g.Name == name && g.Classes.Contains("Sorcerer"));

    private static ParagonBoardDef Starter => Board("Paragon_Sorc_00");

    [Fact]
    public void Must_boards_and_glyphs_produce_a_solved_layout_with_assignments()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            MustUseBoards = [Board("Paragon_Sorc_03"), Board("Paragon_Sorc_08")],
            MaxBoards = 3,
            Glyphs = [Glyph("Enchanter"), Glyph("Unleash")],
        }, ParagonDatabase.Data);

        result.Success.ShouldBeTrue(result.Error);
        result.Layout!.Boards.Count.ShouldBe(3);
        result.Layout.Boards.Select(b => b.Board.InternalName)
            .ShouldContain("Paragon_Sorc_03");
        result.Layout.Boards.Select(b => b.Board.InternalName)
            .ShouldContain("Paragon_Sorc_08");

        // Two legendaries targeted (the starter has none), and the plan reaches them.
        result.Targets.Count.ShouldBe(2);
        result.Plan!.Success.ShouldBeTrue(result.Plan.Error);
        foreach (var target in result.Targets)
            result.Plan.PurchasedCells.ShouldContain(target);

        // Both glyphs placed on distinct boards and activated by the plan.
        result.GlyphPlacements.Count.ShouldBe(2);
        result.GlyphPlacements.Select(p => p.BoardSlot).Distinct().Count().ShouldBe(2);
        result.Plan.GlyphOutcomes.Count(o => o.Met).ShouldBe(2);
    }

    [Fact]
    public void Pool_boards_fill_the_open_slots_with_a_note()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            PoolBoards = [Board("Paragon_Sorc_01"), Board("Paragon_Sorc_02"), Board("Paragon_Sorc_03")],
            MaxBoards = 3,
            Glyphs = [Glyph("Enchanter")],
        }, ParagonDatabase.Data);

        result.Success.ShouldBeTrue(result.Error);
        result.Layout!.Boards.Count.ShouldBe(3);
        result.Notes.Count(n => n.StartsWith("Added")).ShouldBe(2);
        result.Plan!.Success.ShouldBeTrue();
    }

    [Fact]
    public void Too_many_must_boards_fail_clearly()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            MustUseBoards =
            [
                Board("Paragon_Sorc_01"), Board("Paragon_Sorc_02"),
                Board("Paragon_Sorc_03"), Board("Paragon_Sorc_04"), Board("Paragon_Sorc_05"),
            ],
            MaxBoards = 5,
        }, ParagonDatabase.Data);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("must-use");
    }

    [Fact]
    public void Excess_glyphs_are_dropped_with_a_note()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            MaxBoards = 1,
            Glyphs = [Glyph("Enchanter"), Glyph("Unleash")],
        }, ParagonDatabase.Data);

        result.Success.ShouldBeTrue(result.Error);
        result.Layout!.Boards.Count.ShouldBe(1);
        result.GlyphPlacements.Count.ShouldBe(1);
        result.Notes.ShouldContain(n => n.Contains("No socket left"));
    }

    [Fact]
    public void The_chosen_arrangement_is_no_worse_than_a_naive_chain()
    {
        var boards = new[] { Board("Paragon_Sorc_03"), Board("Paragon_Sorc_08") };
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            MustUseBoards = boards,
            MaxBoards = 3,
        }, ParagonDatabase.Data);
        result.Success.ShouldBeTrue(result.Error);

        // Naive: chain the boards upward at rotation 0 and solve the same objective.
        var naiveLayout = new ParagonLayout(
        [
            new PlacedBoard { Board = Starter },
            new PlacedBoard { Board = boards[0], ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = boards[1], ParentSlot = 1, AttachEdge = BoardEdge.Top },
        ]);
        var naiveGraph = ComposedGraph.Build(naiveLayout);
        var naiveTargets = naiveGraph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Legendary)
            .Select(v => v.Cell)
            .ToList();
        var naive = PlanSolver.Solve(naiveGraph, new PlanRequest { Targets = naiveTargets });
        naive.Success.ShouldBeTrue(naive.Error);

        result.Plan!.PointsSpent.ShouldBeLessThanOrEqualTo(naive.PointsSpent);
    }
}
