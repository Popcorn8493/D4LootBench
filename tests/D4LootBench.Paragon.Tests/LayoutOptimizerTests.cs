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
    public void Competing_pool_subsets_are_compared_under_the_full_solve()
    {
        var result = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            PoolBoards =
            [
                Board("Paragon_Sorc_01"), Board("Paragon_Sorc_02"), Board("Paragon_Sorc_03"),
                Board("Paragon_Sorc_04"), Board("Paragon_Sorc_05"),
            ],
            MaxBoards = 3,
            Glyphs = [Glyph("Enchanter"), Glyph("Unleash")],
        }, ParagonDatabase.Data);

        result.Success.ShouldBeTrue(result.Error);
        result.Layout!.Boards.Count.ShouldBe(3);
        result.Notes.ShouldContain(n => n.StartsWith("Compared"));
        result.Notes.Count(n => n.StartsWith("Added")).ShouldBe(2);
        result.Plan!.Success.ShouldBeTrue();
    }

    /// <summary>What the legacy pre-selection saw: the board's best in-radius stat for any glyph.</summary>
    private static double GreedyFit(ParagonBoardDef board, IEnumerable<ParagonGlyphDef> glyphs, int radius)
    {
        var nodes = ParagonDatabase.NodesBySnoId;
        var socket = board.Nodes.FirstOrDefault(p => nodes[p.Node].Kind == ParagonNodeKind.GlyphSocket);
        if (socket is null)
            return 0;
        return glyphs.Max(glyph =>
        {
            if (GlyphInfo.PrimarySourceAttribute(glyph) is not string attribute)
                return 0;
            return board.Nodes
                .Where(p => (p.X, p.Y) != (socket.X, socket.Y)
                    && Math.Abs(p.X - socket.X) + Math.Abs(p.Y - socket.Y) <= radius)
                .Sum(p => nodes[p.Node].Attributes
                    .Where(a => !a.IsThresholdBonus && a.Attribute == attribute && a.Value is double)
                    .Sum(a => a.Value!.Value));
        });
    }

    [Fact]
    public void Subset_exploration_never_does_worse_than_the_greedy_pick()
    {
        var pool = new[]
        {
            Board("Paragon_Sorc_01"), Board("Paragon_Sorc_02"), Board("Paragon_Sorc_03"),
            Board("Paragon_Sorc_04"), Board("Paragon_Sorc_05"),
        };
        var glyphs = new[] { Glyph("Enchanter"), Glyph("Unleash") };
        int radius = GlyphRadius.RadiusForLevel(100);

        var explored = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            PoolBoards = pool,
            MaxBoards = 3,
            Glyphs = glyphs,
        }, ParagonDatabase.Data);
        explored.Success.ShouldBeTrue(explored.Error);

        // Replay what the legacy one-shot pre-selection would have chosen: the two boards with
        // the best single-glyph fit, forced as must-use so no subset exploration happens.
        var greedy = pool.OrderByDescending(b => GreedyFit(b, glyphs, radius)).Take(2).ToArray();
        var forced = LayoutOptimizer.Optimize(new LayoutOptimizerRequest
        {
            StarterBoard = Starter,
            MustUseBoards = greedy,
            MaxBoards = 3,
            Glyphs = glyphs,
        }, ParagonDatabase.Data);
        forced.Success.ShouldBeTrue(forced.Error);

        explored.Plan!.GlyphOutcomes.Count(o => o.Met)
            .ShouldBeGreaterThanOrEqualTo(forced.Plan!.GlyphOutcomes.Count(o => o.Met));
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
