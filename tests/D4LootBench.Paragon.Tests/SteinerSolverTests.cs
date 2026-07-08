using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class SteinerSolverTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static CellRef CellOfKind(ComposedGraph graph, int slot, ParagonNodeKind kind, int skip = 0) =>
        graph.Vertices
            .Where(v => v.Cell.BoardSlot == slot && v.Node.Kind == kind)
            .Skip(skip)
            .First()
            .Cell;

    /// <summary>Independent oracle: cheapest node-count path start→target via plain BFS (unit weights).</summary>
    private static int BfsDistance(ComposedGraph graph, CellRef target)
    {
        graph.TryGetVertex(target, out int goal).ShouldBeTrue();
        var dist = new int[graph.Vertices.Count];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        dist[graph.StartVertex] = 0;
        queue.Enqueue(graph.StartVertex);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int u in graph.Adjacency[v])
            {
                if (dist[u] < 0)
                {
                    dist[u] = dist[v] + 1;
                    queue.Enqueue(u);
                }
            }
        }
        return dist[goal];
    }

    private static void AssertConnectedAndCovering(ComposedGraph graph, SolverResult result, IEnumerable<CellRef> targets)
    {
        var chosen = new HashSet<int> { graph.StartVertex };
        foreach (var cell in result.PurchasedCells)
        {
            graph.TryGetVertex(cell, out int v).ShouldBeTrue($"purchased cell {cell} must exist");
            chosen.Add(v);
        }

        foreach (var target in targets)
        {
            graph.TryGetVertex(target, out int t).ShouldBeTrue();
            chosen.ShouldContain(t, $"target {target} must be purchased");
        }

        // Flood-fill from start within the chosen set must reach every chosen vertex.
        var seen = new HashSet<int> { graph.StartVertex };
        var queue = new Queue<int>();
        queue.Enqueue(graph.StartVertex);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int u in graph.Adjacency[v])
            {
                if (chosen.Contains(u) && seen.Add(u))
                    queue.Enqueue(u);
            }
        }
        seen.Count.ShouldBe(chosen.Count, "every purchased node must connect back to the start node");
    }

    [Fact]
    public void Single_target_path_matches_bfs_shortest_distance()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));
        // Starter boards have no legendary node; use the rare farthest from the start area.
        var target = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Rare)
            .OrderByDescending(v => Math.Abs(v.Cell.X - 10) + Math.Abs(v.Cell.Y - 20))
            .First().Cell;

        var result = SteinerSolver.Solve(graph, [target]);

        result.Success.ShouldBeTrue(result.Error);
        result.IsOptimal.ShouldBeTrue();
        result.PointsSpent.ShouldBe(BfsDistance(graph, target));
        AssertConnectedAndCovering(graph, result, [target]);
    }

    [Fact]
    public void Multi_target_tree_is_connected_covering_and_no_worse_than_separate_paths()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));
        var targets = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Rare)
            .Select(v => v.Cell)
            .Take(4)
            .ToList();
        targets.Count.ShouldBe(4);

        var result = SteinerSolver.Solve(graph, targets);

        result.Success.ShouldBeTrue(result.Error);
        result.IsOptimal.ShouldBeTrue();
        AssertConnectedAndCovering(graph, result, targets);

        int separateSum = targets.Sum(t => BfsDistance(graph, t));
        result.PointsSpent.ShouldBeLessThanOrEqualTo(separateSum);
        result.PointsSpent.ShouldBeGreaterThanOrEqualTo(targets.Max(t => BfsDistance(graph, t)));
    }

    [Fact]
    public void Path_crosses_gates_into_an_attached_board()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_05"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
        ]);
        var graph = ComposedGraph.Build(layout);
        var target = CellOfKind(graph, 1, ParagonNodeKind.Legendary);

        var result = SteinerSolver.Solve(graph, [target]);

        result.Success.ShouldBeTrue(result.Error);
        AssertConnectedAndCovering(graph, result, [target]);
        result.PurchasedCells.ShouldContain(c => c.BoardSlot == 0, "path must traverse the starting board");
        result.PurchasedCells.ShouldContain(c => c.BoardSlot == 1, "path must reach the attached board");
        // Both gate cells sit on the crossing.
        result.PurchasedCells.ShouldContain(new CellRef(0, 10, 0));
        result.PurchasedCells.ShouldContain(new CellRef(1, 10, 20));
        result.PointsSpent.ShouldBe(BfsDistance(graph, target));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotation_preserves_reachability_and_glyph_socket_position(int rotationSteps)
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_05"), ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = rotationSteps },
        ]);
        var graph = ComposedGraph.Build(layout);
        var socket = CellOfKind(graph, 1, ParagonNodeKind.GlyphSocket);

        var result = SteinerSolver.Solve(graph, [socket]);

        result.Success.ShouldBeTrue(result.Error);
        AssertConnectedAndCovering(graph, result, [socket]);
    }

    [Fact]
    public void Unknown_target_cell_fails_with_explanation()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));

        var result = SteinerSolver.Solve(graph, [new CellRef(0, 1, 1)]);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("(1, 1)");
    }

    [Fact]
    public void Many_targets_use_the_heuristic_and_still_produce_a_valid_tree()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_05"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_03"), ParentSlot = 1, AttachEdge = BoardEdge.Left },
        ]);
        var graph = ComposedGraph.Build(layout);
        var targets = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Rare)
            .Select(v => v.Cell)
            .Take(SteinerSolver.MaxExactTargets + 2)
            .ToList();
        targets.Count.ShouldBe(SteinerSolver.MaxExactTargets + 2);

        var result = SteinerSolver.Solve(graph, targets);

        result.Success.ShouldBeTrue(result.Error);
        result.IsOptimal.ShouldBeFalse();
        AssertConnectedAndCovering(graph, result, targets);
    }

    [Fact]
    public void Layout_rejects_double_booked_gates_duplicates_and_oversized_layouts()
    {
        var starter = Board("Paragon_Sorc_00");

        Should.Throw<ArgumentException>(() => new ParagonLayout(
        [
            new PlacedBoard { Board = starter },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_02"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
        ])).Message.ShouldContain("already occupied");

        Should.Throw<ArgumentException>(() => new ParagonLayout(
        [
            new PlacedBoard { Board = starter },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 1, AttachEdge = BoardEdge.Top },
        ])).Message.ShouldContain("each board can be used once");

        var six = new List<PlacedBoard> { new() { Board = starter } };
        var edges = new[] { BoardEdge.Top, BoardEdge.Top, BoardEdge.Top, BoardEdge.Top, BoardEdge.Top };
        for (int i = 0; i < 5; i++)
        {
            six.Add(new PlacedBoard { Board = Board($"Paragon_Sorc_0{i + 1}"), ParentSlot = i, AttachEdge = edges[i] });
        }
        Should.Throw<ArgumentException>(() => new ParagonLayout(six))
            .Message.ShouldContain("at most");
    }

    [Fact]
    public void Board_positions_follow_the_attachment_tree_and_overlaps_are_rejected()
    {
        var starter = Board("Paragon_Sorc_00");

        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = starter },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_02"), ParentSlot = 1, AttachEdge = BoardEdge.Left },
        ]);
        layout.BoardPositions.ShouldBe([(0, 0), (0, -1), (-1, -1)]);

        // Walking Top → Left → Bottom → Right returns to the starter's position.
        var ex = Should.Throw<ArgumentException>(() => new ParagonLayout(
        [
            new PlacedBoard { Board = starter },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_02"), ParentSlot = 1, AttachEdge = BoardEdge.Left },
            new PlacedBoard { Board = Board("Paragon_Sorc_03"), ParentSlot = 2, AttachEdge = BoardEdge.Bottom },
            new PlacedBoard { Board = Board("Paragon_Sorc_04"), ParentSlot = 3, AttachEdge = BoardEdge.Right },
        ]));
        ex.Message.ShouldContain("overlap");
    }

    [Fact]
    public void Physically_adjacent_sibling_boards_connect_through_their_facing_gates()
    {
        // Slots 1 and 2 both attach upward-left of the starter, ending side by side:
        // positions (0,-1) and (-1,-1). Their Left/Right gates must connect even though
        // neither is the other's attachment parent.
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_01"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
            new PlacedBoard { Board = Board("Paragon_Sorc_02"), ParentSlot = 1, AttachEdge = BoardEdge.Left },
        ]);
        var graph = ComposedGraph.Build(layout);

        graph.TryGetVertex(new CellRef(1, 0, 10), out int leftGateOfSlot1).ShouldBeTrue();
        graph.TryGetVertex(new CellRef(2, 20, 10), out int rightGateOfSlot2).ShouldBeTrue();
        graph.Adjacency[leftGateOfSlot1].ShouldContain(rightGateOfSlot2);
    }

    [Fact]
    public void Glyph_radius_level_breakpoints_match_season_14()
    {
        GlyphRadius.RadiusForLevel(1).ShouldBe(3);
        GlyphRadius.RadiusForLevel(24).ShouldBe(3);
        GlyphRadius.RadiusForLevel(25).ShouldBe(4);
        GlyphRadius.RadiusForLevel(49).ShouldBe(4);
        GlyphRadius.RadiusForLevel(50).ShouldBe(5);
        GlyphRadius.RadiusForLevel(150).ShouldBe(5);
    }

    [Fact]
    public void Glyph_radius_totals_respect_metric_board_boundary_and_purchase_set()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));
        var socket = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.Rare).Cell;

        // Purchase every stat-bearing cell on the board, then shrink the radius.
        var all = graph.Vertices.Select(v => v.Cell).ToList();
        var wide = GlyphRadius.AttributeTotalsInRange(graph, socket, all, 40, RadiusMetric.Chebyshev);
        wide.Keys.ShouldContain("Intelligence_Core");

        var manhattan1 = GlyphRadius.AttributeTotalsInRange(graph, socket, all, 1, RadiusMetric.Manhattan);
        var chebyshev1 = GlyphRadius.AttributeTotalsInRange(graph, socket, all, 1, RadiusMetric.Chebyshev);
        // Chebyshev radius 1 covers 8 neighbors, Manhattan only 4 — totals can't exceed.
        manhattan1.Values.Sum().ShouldBeLessThanOrEqualTo(chebyshev1.Values.Sum());
        chebyshev1.Values.Sum().ShouldBeLessThan(wide.Values.Sum());

        // Nothing purchased → nothing counted.
        GlyphRadius.AttributeTotalsInRange(graph, socket, [], 5, RadiusMetric.Chebyshev).ShouldBeEmpty();
    }

    [Fact]
    public void Attaching_to_a_starter_edge_without_a_gate_fails_clearly()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard { Board = Board("Paragon_Sorc_05"), ParentSlot = 0, AttachEdge = BoardEdge.Left },
        ]);

        var ex = Should.Throw<InvalidOperationException>(() => ComposedGraph.Build(layout));
        ex.Message.ShouldContain("no gate");
    }
}
