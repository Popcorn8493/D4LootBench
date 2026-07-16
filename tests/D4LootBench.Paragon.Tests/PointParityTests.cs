using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Point parity for comparisons: the smaller build grows by its own priorities first, the
/// larger build only loses its least-valued expendable leaves when growth saturates, and
/// whatever cannot be equalized is reported.
/// </summary>
public class PointParityTests
{
    private static ParagonNodeDef Magic(string snoId, string attribute, double value) =>
        SyntheticBoards.Node(snoId, ParagonNodeKind.Magic, (attribute, value));

    private static BuildSnapshot Snapshot(
        string name, ParagonBoardDef board, params CellRef[] allocated) =>
        new(name, ParagonLayout.Single(board), allocated, Glyphs: []);

    [Fact]
    public void Grows_the_smaller_build_first_and_trims_only_the_rest()
    {
        // Small: S a b c with only a bought (1 pt, 2 buyable). Large: S d e f g with all four
        // bought (4 pts). Parity 3-deep: grow small by 2 (exhausting its board), trim 1 leaf
        // off large.
        var smallDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['a'] = Magic("sa", "Dexterity_Core", 5),
            ['b'] = Magic("sb", "Dexterity_Core", 5),
            ['c'] = Magic("sc", "Dexterity_Core", 5),
        };
        var largeDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['d'] = Magic("ld", "Dexterity_Core", 5),
            ['e'] = Magic("le", "Dexterity_Core", 5),
            ['f'] = Magic("lf", "Dexterity_Core", 5),
            ['g'] = Magic("lg", "Dexterity_Core", 5),
        };
        var smallBoard = SyntheticBoards.Board(["Sabc"], smallDefs, "small");
        var largeBoard = SyntheticBoards.Board(["Sdefg"], largeDefs, "large");
        var data = new ParagonData
        {
            Boards = [smallBoard, largeBoard],
            Nodes = smallDefs.Values.Concat(largeDefs.Values).Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var small = Snapshot("Current", smallBoard, new CellRef(0, 1, 0));
        var large = Snapshot("Import", largeBoard,
            new CellRef(0, 1, 0), new CellRef(0, 2, 0), new CellRef(0, 3, 0), new CellRef(0, 4, 0));

        var result = PointParity.MatchPoints(small, large, data);

        var graphSmall = ComposedGraph.Build(result.A.Layout, data.Nodes.ToDictionary(n => n.SnoId));
        var graphLarge = ComposedGraph.Build(result.B.Layout, data.Nodes.ToDictionary(n => n.SnoId));
        PointParity.CountPoints(graphSmall, result.A.AllocatedCells).ShouldBe(3);
        PointParity.CountPoints(graphLarge, result.B.AllocatedCells).ShouldBe(3);
        result.A.AllocatedCells.ShouldContain(new CellRef(0, 2, 0)); // b bought
        result.A.AllocatedCells.ShouldContain(new CellRef(0, 3, 0)); // c bought
        result.Notes.Count.ShouldBe(1);
        result.Notes[0].ShouldContain("Current spends 1 point(s), Import 4"); // originals stay visible
        result.Notes[0].ShouldContain("granted 2 point(s)");
        result.Notes[0].ShouldContain("lost its 1 least-valued node(s)");
    }

    [Fact]
    public void Trimming_removes_what_the_build_stacks_least()
    {
        // Large stacks Dexterity (a, b) with one off-emphasis Strength leaf (c) — c must be
        // the first node trimmed, even though a is equally removable.
        var largeDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['a'] = Magic("la", "Dexterity_Core", 5),
            ['b'] = Magic("lb", "Dexterity_Core", 5),
            ['c'] = Magic("lc", "Strength_Core", 5),
        };
        var smallDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['x'] = Magic("sx", "Dexterity_Core", 5),
        };
        var largeBoard = SyntheticBoards.Board(["aSbc"], largeDefs, "large");
        var smallBoard = SyntheticBoards.Board(["Sx"], smallDefs, "small");
        var data = new ParagonData
        {
            Boards = [largeBoard, smallBoard],
            Nodes = largeDefs.Values.Concat(smallDefs.Values).Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var small = Snapshot("Current", smallBoard, new CellRef(0, 1, 0));
        var large = Snapshot("Import", largeBoard,
            new CellRef(0, 0, 0), new CellRef(0, 2, 0), new CellRef(0, 3, 0));

        // Small's board is exhausted, so parity comes entirely from trimming large by 2.
        var result = PointParity.MatchPoints(small, large, data);

        result.B.AllocatedCells.Count.ShouldBe(1);
        result.B.AllocatedCells.ShouldNotContain(new CellRef(0, 3, 0)); // the Strength leaf went first
        result.Notes.Single().ShouldContain("lost its 2 least-valued node(s)");
    }

    [Fact]
    public void Reports_the_residual_when_neither_side_can_move()
    {
        // Small is exhausted; large is all rares (never trimmed) — the difference must be
        // reported, not silently ignored.
        var largeDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['x'] = SyntheticBoards.Node("lx", ParagonNodeKind.Rare, ("Dexterity_Core", 10)),
            ['y'] = SyntheticBoards.Node("ly", ParagonNodeKind.Rare, ("Dexterity_Core", 10)),
        };
        var smallDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['a'] = Magic("sa", "Dexterity_Core", 5),
        };
        var largeBoard = SyntheticBoards.Board(["Sxy"], largeDefs, "large");
        var smallBoard = SyntheticBoards.Board(["Sa"], smallDefs, "small");
        var data = new ParagonData
        {
            Boards = [largeBoard, smallBoard],
            Nodes = largeDefs.Values.Concat(smallDefs.Values).Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var small = Snapshot("Current", smallBoard, new CellRef(0, 1, 0));
        var large = Snapshot("Import", largeBoard, new CellRef(0, 1, 0), new CellRef(0, 2, 0));

        var result = PointParity.MatchPoints(small, large, data);

        result.A.AllocatedCells.ShouldBe(small.AllocatedCells);
        result.B.AllocatedCells.ShouldBe(large.AllocatedCells);
        result.Notes.Single().ShouldContain("1 point(s) of difference remain");
    }

    [Fact]
    public void WithGlyphLevels_relevels_known_glyphs_and_falls_back_for_the_rest()
    {
        var defs = new Dictionary<char, ParagonNodeDef> { ['a'] = Magic("wa", "Dexterity_Core", 5) };
        var build = new BuildSnapshot("x",
            ParagonLayout.Single(SyntheticBoards.Board(["Sa"], defs, "wboard")),
            [new CellRef(0, 1, 0)],
            [new MaxrollGlyphAssignment(0, "glyph_x", 100), new MaxrollGlyphAssignment(1, "glyph_y", null)]);

        var releveled = BuildComparer.WithGlyphLevels(build,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["GLYPH_X"] = 46 },
            fallbackLevel: 21);

        releveled.Glyphs.Single(g => g.GlyphInternalName == "glyph_x").Level.ShouldBe(46);
        releveled.Glyphs.Single(g => g.GlyphInternalName == "glyph_y").Level.ShouldBe(21);
    }

    [Fact]
    public void Compare_with_matchPoints_reports_parity_and_drops_the_points_verdict()
    {
        var smallDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['a'] = Magic("sa", "Dexterity_Core", 5),
            ['b'] = Magic("sb", "Dexterity_Core", 5),
            ['c'] = Magic("sc", "Dexterity_Core", 5),
        };
        var largeDefs = new Dictionary<char, ParagonNodeDef>
        {
            ['d'] = Magic("ld", "Dexterity_Core", 5),
            ['e'] = Magic("le", "Dexterity_Core", 5),
            ['f'] = Magic("lf", "Dexterity_Core", 5),
        };
        var smallBoard = SyntheticBoards.Board(["Sabc"], smallDefs, "small");
        var largeBoard = SyntheticBoards.Board(["Sdef"], largeDefs, "large");
        var data = new ParagonData
        {
            Boards = [smallBoard, largeBoard],
            Nodes = smallDefs.Values.Concat(largeDefs.Values).Concat([SyntheticBoards.StartNode]).ToList(),
        };
        var small = Snapshot("Current", smallBoard, new CellRef(0, 1, 0));
        var large = Snapshot("Import", largeBoard,
            new CellRef(0, 1, 0), new CellRef(0, 2, 0), new CellRef(0, 3, 0));

        string report = BuildComparer.Compare(small, large, data, NonParagonStats.None, matchPoints: true);

        report.ShouldContain("Point parity:");
        report.ShouldNotContain("spends fewer points");

        // Without the flag the budget difference dominates as before.
        BuildComparer.Compare(small, large, data, NonParagonStats.None)
            .ShouldContain("spends fewer points");
    }
}
