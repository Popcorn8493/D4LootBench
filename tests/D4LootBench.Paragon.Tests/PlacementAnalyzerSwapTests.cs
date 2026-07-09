using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class PlacementAnalyzerSwapTests
{
    private static ParagonBoardDef Starter =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00");

    private static ParagonGlyphDef Enchanter =>
        ParagonDatabase.Data.Glyphs.Single(g => g.Name == "Enchanter" && g.Classes.Contains("Sorcerer"));

    /// <summary>The board's total of the attribute within radius of its own socket.</summary>
    private static double Fit(ParagonBoardDef board, string attribute, int radius)
    {
        var nodes = ParagonDatabase.NodesBySnoId;
        var socket = board.Nodes.FirstOrDefault(p => nodes[p.Node].Kind == ParagonNodeKind.GlyphSocket);
        if (socket is null)
            return 0;
        return board.Nodes
            .Where(p => (p.X, p.Y) != (socket.X, socket.Y)
                && Math.Abs(p.X - socket.X) + Math.Abs(p.Y - socket.Y) <= radius)
            .Sum(p => nodes[p.Node].Attributes
                .Where(a => !a.IsThresholdBonus && a.Attribute == attribute && a.Value is double)
                .Sum(a => a.Value!.Value));
    }

    [Fact]
    public void A_stronger_unused_board_is_suggested_as_a_swap()
    {
        string attribute = GlyphInfo.PrimarySourceAttribute(Enchanter)!;
        int radius = GlyphRadius.RadiusForLevel(100);

        // Pick, from real data, the weakest and strongest socketed sorcerer boards for the stat.
        var boards = ParagonDatabase.BoardsForClass("Sorcerer")
            .Where(b => b.BoardIndex != 0)
            .Select(b => (Board: b, Fit: Fit(b, attribute, radius)))
            .Where(b => b.Fit > 0)
            .OrderBy(b => b.Fit)
            .ToList();
        boards.Count.ShouldBeGreaterThanOrEqualTo(2);
        var weak = boards[0];
        var strong = boards[^1];
        strong.Fit.ShouldBeGreaterThan(weak.Fit); // otherwise there is nothing to suggest

        // Lay out starter + the weak board; demand more stat than the weak board can supply.
        ParagonLayout? layout = null;
        for (int rotation = 0; rotation < 4 && layout is null; rotation++)
        {
            try
            {
                layout = new ParagonLayout(
                [
                    new PlacedBoard { Board = Starter },
                    new PlacedBoard
                    {
                        Board = weak.Board, ParentSlot = 0,
                        AttachEdge = BoardEdge.Top, RotationSteps = rotation,
                    },
                ]);
                ComposedGraph.Build(layout);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                layout = null;
            }
        }
        layout.ShouldNotBeNull("the weak board should attach to the starter in some rotation");
        var graph = ComposedGraph.Build(layout);

        var socket = graph.Vertices.Single(v =>
            v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var targets = graph.Vertices
            .Where(v => v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.Legendary)
            .Select(v => v.Cell)
            .ToList();
        double required = (weak.Fit + strong.Fit) / 2;
        var request = new PlanRequest
        {
            Targets = targets,
            GlyphGoals = [new GlyphGoal(socket, attribute, required, radius, "Enchanter")],
        };
        var baseline = PlanSolver.Solve(graph, request);
        baseline.Success.ShouldBeTrue(baseline.Error);
        baseline.GlyphOutcomes.Count(o => o.Met).ShouldBe(0); // the weak board can't activate it

        var suggestions = PlacementAnalyzer.SuggestBoardSwaps(
            layout, request, baseline, ParagonDatabase.BoardsForClass("Sorcerer").ToList());

        suggestions.ShouldNotBeEmpty();
        var swap = suggestions
            .Select(s => s.Change)
            .OfType<BoardSwapChange>()
            .First();
        swap.Slot.ShouldBe(1);
        Fit(swap.NewBoard, attribute, radius).ShouldBeGreaterThanOrEqualTo(required);
        suggestions[0].Description.ShouldContain("Swap slot 1");
    }

    [Fact]
    public void Pipeline_evaluation_measures_the_finished_build()
    {
        var layout = new ParagonLayout([new PlacedBoard { Board = Starter }]);
        var graph = ComposedGraph.Build(layout);
        var target = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.Rare).Cell;
        var request = new PlanRequest { Targets = [target] };

        var pipeline = new PlacementPipeline(
            60,
            new MaximizeFocus([], PreferRare: true, ActivateThresholds: true),
            new ThresholdContext(ParagonDatabase.Data, "Sorcerer", NonParagonStats.Uniform(0)));
        var result = PlacementAnalyzer.EvaluatePipeline(graph, request, pipeline);

        result.ShouldNotBeNull();
        // Full spend: far more points used than the bare solve needs, capped by the pool.
        result.PointsUsed.ShouldBeGreaterThan(PlanSolver.Solve(graph, request).PointsSpent);
        result.PointsUsed.ShouldBeLessThanOrEqualTo(60);
        result.FocusScore.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Pipeline_mode_swap_suggestions_report_full_spend_outcomes()
    {
        // Reuse the weak-board scenario: with the pipeline, the suggestion text must speak in
        // finished-build terms ("at full spend"), not bare solve points.
        string attribute = GlyphInfo.PrimarySourceAttribute(Enchanter)!;
        int radius = GlyphRadius.RadiusForLevel(100);
        var boards = ParagonDatabase.BoardsForClass("Sorcerer")
            .Where(b => b.BoardIndex != 0)
            .Select(b => (Board: b, Fit: Fit(b, attribute, radius)))
            .Where(b => b.Fit > 0)
            .OrderBy(b => b.Fit)
            .ToList();
        var weak = boards[0];
        var strong = boards[^1];

        ParagonLayout? layout = null;
        for (int rotation = 0; rotation < 4 && layout is null; rotation++)
        {
            try
            {
                var candidate = new ParagonLayout(
                [
                    new PlacedBoard { Board = Starter },
                    new PlacedBoard
                    {
                        Board = weak.Board, ParentSlot = 0,
                        AttachEdge = BoardEdge.Top, RotationSteps = rotation,
                    },
                ]);
                ComposedGraph.Build(candidate);
                layout = candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        var graph = ComposedGraph.Build(layout!);
        var socket = graph.Vertices.Single(v =>
            v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var targets = graph.Vertices
            .Where(v => v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.Legendary)
            .Select(v => v.Cell)
            .ToList();
        var request = new PlanRequest
        {
            Targets = targets,
            GlyphGoals = [new GlyphGoal(socket, attribute, (weak.Fit + strong.Fit) / 2, radius, "Enchanter")],
        };
        var baseline = PlanSolver.Solve(graph, request);
        baseline.Success.ShouldBeTrue(baseline.Error);

        var pipeline = new PlacementPipeline(
            150,
            new MaximizeFocus([], PreferRare: true),
            new ThresholdContext(ParagonDatabase.Data, "Sorcerer", NonParagonStats.Uniform(0)));
        var suggestions = PlacementAnalyzer.SuggestBoardSwaps(
            layout!, request, baseline, ParagonDatabase.BoardsForClass("Sorcerer").ToList(),
            pipeline: pipeline);

        suggestions.ShouldNotBeEmpty();
        suggestions[0].Description.ShouldContain("at full spend");
        suggestions[0].Description.ShouldContain("glyph");
    }

    [Fact]
    public void No_swaps_are_suggested_without_glyph_goals()
    {
        var layout = new ParagonLayout([new PlacedBoard { Board = Starter }]);
        var graph = ComposedGraph.Build(layout);
        var target = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.Rare).Cell;
        var request = new PlanRequest { Targets = [target] };
        var baseline = PlanSolver.Solve(graph, request);
        baseline.Success.ShouldBeTrue(baseline.Error);

        PlacementAnalyzer.SuggestBoardSwaps(
                layout, request, baseline, ParagonDatabase.BoardsForClass("Sorcerer").ToList())
            .ShouldBeEmpty();
    }
}
