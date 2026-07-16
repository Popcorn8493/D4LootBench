using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Fitting an imported build to the player's current point pool: least-valued nodes go first,
/// active glyph radii are spared while the budget allows, and what can't be trimmed is reported.
/// </summary>
public class TrimToBudgetTests
{
    private static (BuildSnapshot Build, ParagonData Data, ComposedGraph Graph) GlyphBuild()
    {
        // j x S g d — j is high-value Dexterity, x pure travel, g a socket, d the only
        // Dexterity inside the glyph's radius-1 diamond.
        var defs = new Dictionary<char, ParagonNodeDef>
        {
            ['j'] = SyntheticBoards.Node("j", ParagonNodeKind.Magic, ("Dexterity_Core", 10)),
            ['x'] = SyntheticBoards.Node("x", ParagonNodeKind.Normal),
            ['g'] = SyntheticBoards.Node("g", ParagonNodeKind.GlyphSocket),
            ['d'] = SyntheticBoards.Node("d", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
        };
        var board = SyntheticBoards.Board(["jxSgd"], defs, "trim");
        var data = new ParagonData
        {
            Boards = [board],
            Nodes = defs.Values.Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var build = new BuildSnapshot("Import", ParagonLayout.Single(board),
            [new CellRef(0, 0, 0), new CellRef(0, 1, 0), new CellRef(0, 3, 0), new CellRef(0, 4, 0)],
            Glyphs: []);
        var graph = ComposedGraph.Build(build.Layout, data.Nodes.ToDictionary(n => n.SnoId));
        return (build, data, graph);
    }

    private static readonly GlyphGoal Goal =
        new(new CellRef(0, 3, 0), "Dexterity_Core", 5, Radius: 1);

    [Fact]
    public void Within_budget_imports_are_untouched()
    {
        var (build, data, _) = GlyphBuild();
        var result = PointParity.TrimToBudget(build, 10, data);

        result.Build.ShouldBe(build);
        result.RemovedCells.ShouldBeEmpty();
        result.Notes.ShouldBeEmpty();
        result.PointsAfter.ShouldBe(result.PointsBefore);
    }

    [Fact]
    public void Trims_the_least_valued_removable_nodes_to_the_budget()
    {
        var (build, data, graph) = GlyphBuild();

        // 4 points → 3. Travel node x is worthless but load-bearing (removing it strands j),
        // so the cheapest safe cut is d (5 Dex) over j (10 Dex). No glyph goal to protect here.
        var result = PointParity.TrimToBudget(build, 3, data);

        result.PointsAfter.ShouldBe(3);
        result.RemovedCells.ShouldBe([new CellRef(0, 4, 0)]);
        PointParity.CountPoints(graph, result.Build.AllocatedCells).ShouldBe(3);
    }

    [Fact]
    public void An_active_glyph_radius_is_spared_while_the_budget_allows()
    {
        var (build, data, _) = GlyphBuild();

        // Same cut as above, but d keeps the glyph active (5 of required 5 in radius) — the
        // trim must sacrifice the more valuable j instead.
        var result = PointParity.TrimToBudget(build, 3, data, [Goal]);

        result.RemovedCells.ShouldBe([new CellRef(0, 0, 0)]);
        result.Build.AllocatedCells.ShouldContain(new CellRef(0, 4, 0));
        result.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void A_hard_budget_cuts_into_glyph_radii_and_says_so()
    {
        var (build, data, _) = GlyphBuild();

        // 4 points → 1: after j and x are gone only d remains removable, and it kills the
        // activation — allowed only in the forced second pass, with a warning.
        var result = PointParity.TrimToBudget(build, 1, data, [Goal]);

        result.PointsAfter.ShouldBe(1);
        result.RemovedCells.ShouldContain(new CellRef(0, 4, 0));
        result.Notes.ShouldContain(n => n.Contains("glyph"));
    }

    [Fact]
    public void Reports_when_the_core_build_cannot_shrink_further()
    {
        var defs = new Dictionary<char, ParagonNodeDef>
        {
            ['r'] = SyntheticBoards.Node("r", ParagonNodeKind.Rare, ("Dexterity_Core", 10)),
        };
        var board = SyntheticBoards.Board(["Sr"], defs, "rares");
        var data = new ParagonData
        {
            Boards = [board],
            Nodes = defs.Values.Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var build = new BuildSnapshot("Import", ParagonLayout.Single(board),
            [new CellRef(0, 1, 0)], Glyphs: []);

        var result = PointParity.TrimToBudget(build, 0, data);

        result.RemovedCells.ShouldBeEmpty();
        result.Notes.Single().ShouldContain("over budget");
    }
}
