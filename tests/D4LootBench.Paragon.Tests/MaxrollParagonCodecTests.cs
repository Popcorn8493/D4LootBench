using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class MaxrollParagonCodecTests
{
    // Trimmed from a live Maxroll build (Barrage Rogue guide, profile 9t47g0yf).
    private const string RealSample =
        """
        [{"id":"Paragon_Rogue_00","nodes":{"10":1,"30":1,"31":1,"51":1,"53":1,"72":1,"74":1},
          "rotation":0,"position":{"x":0,"y":0},"glyph":"Rare_020_Intelligence_Side","glyphLevel":15},
         {"id":"Paragon_Rogue_04","nodes":{"10":1},"rotation":1,"position":{"x":0,"y":-1}}]
        """;

    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.BoardsByInternalName[internalName];

    [Fact]
    public void Decodes_a_real_maxroll_variant_code()
    {
        var entries = MaxrollParagonCodec.Decode(RealSample);

        entries.Count.ShouldBe(2);
        entries[0].Id.ShouldBe("Paragon_Rogue_00");
        entries[0].Nodes!.ShouldContainKey("10");
        entries[0].Glyph.ShouldBe("Rare_020_Intelligence_Side");
        entries[0].GlyphLevel.ShouldBe(15);
        entries[1].Rotation.ShouldBe(1);
        entries[1].Position!.Y.ShouldBe(-1);
    }

    [Fact]
    public void Converts_positions_to_an_attachment_tree_and_rotates_node_indices()
    {
        var build = MaxrollParagonCodec.ToLayout(
            MaxrollParagonCodec.Decode(RealSample), ParagonDatabase.BoardsByInternalName);

        build.Layout.Boards.Count.ShouldBe(2);
        build.Layout.Boards[0].Board.InternalName.ShouldBe("Paragon_Rogue_00");
        build.Layout.Boards[1].ParentSlot.ShouldBe(0);
        build.Layout.Boards[1].AttachEdge.ShouldBe(BoardEdge.Top);
        build.Layout.Boards[1].RotationSteps.ShouldBe(1);
        build.Layout.BoardPositions[1].ShouldBe((0, -1));

        // Unrotated top gate (index 10) on a board rotated 90° CW lands on the right edge.
        build.AllocatedCells.ShouldContain(new CellRef(1, 20, 10));
        // Starter is unrotated: index 10 stays the top gate.
        build.AllocatedCells.ShouldContain(new CellRef(0, 10, 0));

        build.Glyphs.ShouldHaveSingleItem().ShouldBe(new MaxrollGlyphAssignment(0, "Rare_020_Intelligence_Side", 15));

        // The composed graph accepts every allocated cell.
        var graph = ComposedGraph.Build(build.Layout);
        foreach (var cell in build.AllocatedCells)
            graph.TryGetVertex(cell, out _).ShouldBeTrue($"allocated cell {cell} must exist on the board");
    }

    [Fact]
    public void Round_trips_a_solved_layout_through_encode_and_decode()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = 1 },
            new PlacedBoard { Board = Board("Paragon_Sorc_02"), ParentSlot = 1, AttachEdge = BoardEdge.Left, RotationSteps = 3 },
        ]);
        var graph = ComposedGraph.Build(layout);
        var target = graph.Vertices.First(v => v.Cell.BoardSlot == 2 && v.Node.Kind == ParagonNodeKind.Rare).Cell;
        var solved = SteinerSolver.Solve(graph, [target]);
        solved.Success.ShouldBeTrue(solved.Error);

        var allocated = solved.PurchasedCells
            .Append(graph.Vertices[graph.StartVertex].Cell)
            .ToList();
        var glyphs = new[] { new MaxrollGlyphAssignment(1, "Rare_Dex_Generic", 50) };

        string code = MaxrollParagonCodec.Encode(MaxrollParagonCodec.FromLayout(layout, allocated, glyphs));
        var restored = MaxrollParagonCodec.ToLayout(
            MaxrollParagonCodec.Decode(code), ParagonDatabase.BoardsByInternalName);

        restored.Layout.Boards.Select(b => b.Board.InternalName)
            .ShouldBe(layout.Boards.Select(b => b.Board.InternalName));
        restored.Layout.Boards.Select(b => b.RotationSteps)
            .ShouldBe(layout.Boards.Select(b => b.RotationSteps));
        restored.Layout.BoardPositions.ShouldBe(layout.BoardPositions);
        restored.AllocatedCells.ToHashSet().SetEquals(allocated).ShouldBeTrue();
        restored.Glyphs.ShouldHaveSingleItem().ShouldBe(glyphs[0]);
    }

    [Fact]
    public void Board_beside_the_starters_gateless_edge_parents_through_its_real_neighbor()
    {
        // Real-world shape (Barrage Rogue endgame): boards wrap around so one sits at (-1, 0),
        // left of the starter. The starter has no Left gate — the board's parent must be (-1, -1).
        var build = MaxrollParagonCodec.ToLayout(
            MaxrollParagonCodec.Decode(
                """
                [{"id":"Paragon_Rogue_00","nodes":{},"rotation":0,"position":{"x":0,"y":0}},
                 {"id":"Paragon_Rogue_07","nodes":{},"rotation":0,"position":{"x":0,"y":-1}},
                 {"id":"Paragon_Rogue_04","nodes":{},"rotation":1,"position":{"x":-1,"y":-1}},
                 {"id":"Paragon_Rogue_01","nodes":{},"rotation":0,"position":{"x":-1,"y":0}}]
                """),
            ParagonDatabase.BoardsByInternalName);

        int wrapped = build.Layout.BoardPositions.ToList().IndexOf((-1, 0));
        wrapped.ShouldBeGreaterThan(0);
        int parent = build.Layout.Boards[wrapped].ParentSlot!.Value;
        build.Layout.BoardPositions[parent].ShouldBe((-1, -1));
        build.Layout.Boards[wrapped].AttachEdge.ShouldBe(BoardEdge.Bottom);

        // And the composed graph builds: gate pairs of all adjacent boards connect.
        ComposedGraph.Build(build.Layout);
    }

    [Fact]
    public void Rejects_garbage_disconnected_and_starterless_codes()
    {
        Should.Throw<FormatException>(() => MaxrollParagonCodec.Decode("not json"));
        Should.Throw<FormatException>(() => MaxrollParagonCodec.Decode("[]"));

        Should.Throw<FormatException>(() => MaxrollParagonCodec.ToLayout(
            MaxrollParagonCodec.Decode(
                """[{"id":"Paragon_Rogue_04","nodes":{},"rotation":0,"position":{"x":1,"y":1}}]"""),
            ParagonDatabase.BoardsByInternalName)).Message.ShouldContain("(0, 0)");

        Should.Throw<FormatException>(() => MaxrollParagonCodec.ToLayout(
            MaxrollParagonCodec.Decode(
                """
                [{"id":"Paragon_Rogue_00","nodes":{},"rotation":0,"position":{"x":0,"y":0}},
                 {"id":"Paragon_Rogue_04","nodes":{},"rotation":0,"position":{"x":5,"y":5}}]
                """),
            ParagonDatabase.BoardsByInternalName)).Message.ShouldContain("connected");
    }
}
