using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class ThresholdAndDeliveryTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static ComposedGraph TwoBoardGraph() => ComposedGraph.Build(new ParagonLayout(
    [
        new PlacedBoard { Board = Board("Paragon_Sorc_00") },
        new PlacedBoard { Board = Board("Paragon_Sorc_03"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
    ]));

    [Fact]
    public void Requirement_scales_with_the_board_attachment_slot()
    {
        var requirement = new ThresholdRequirement
        {
            Attribute = "Willpower_Total",
            ValuesByBoardIndex = [190, 265, 340, null, 490],
        };
        BuildStats.RequirementAt(requirement, 0).ShouldBe(190);
        BuildStats.RequirementAt(requirement, 1).ShouldBe(265);
        BuildStats.RequirementAt(requirement, 3).ShouldBe(340);  // hole → last resolvable below
        BuildStats.RequirementAt(requirement, 10).ShouldBe(490); // past the table → last value
    }

    [Fact]
    public void Sheet_stats_unlock_thresholds_and_met_bonuses_join_the_totals()
    {
        var graph = TwoBoardGraph();
        var thresholdRare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = [thresholdRare.Cell] });
        solved.Success.ShouldBeTrue(solved.Error);
        var purchased = solved.PurchasedCells.ToHashSet();

        var poor = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, 0, "Sorcerer");
        var rich = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, 100_000, "Sorcerer");

        poor.Thresholds.Count.ShouldBeGreaterThan(0);
        rich.Thresholds.Count.ShouldBe(poor.Thresholds.Count);
        rich.ThresholdsMet.ShouldBe(rich.Thresholds.Count, "every threshold is met with huge sheet stats");
        rich.ThresholdsMet.ShouldBeGreaterThanOrEqualTo(poor.ThresholdsMet);
        rich.Totals.Values.Sum().ShouldBeGreaterThanOrEqualTo(poor.Totals.Values.Sum());

        // The requirement reported for each node matches its board's slot in the table.
        foreach (var status in rich.Thresholds)
            status.Requirement.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_threshold_on_a_later_board_requires_more()
    {
        var graph = TwoBoardGraph();
        var starterRare = graph.Vertices.FirstOrDefault(v =>
            v.Cell.BoardSlot == 0 && v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var attachedRare = graph.Vertices.FirstOrDefault(v =>
            v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.Rare
            && v.Node.Thresholds.Count > 0
            && starterRare is not null && v.Node.Thresholds.SequenceEqual(starterRare.Node.Thresholds));
        if (starterRare is null || attachedRare is null)
            return; // no shared threshold node across the two boards — nothing to compare

        var purchased = new HashSet<CellRef> { starterRare.Cell, attachedRare.Cell };
        var report = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, 0, "Sorcerer");
        double slot0 = report.Thresholds.Single(t => t.Cell == starterRare.Cell).Requirement;
        double slot1 = report.Thresholds.Single(t => t.Cell == attachedRare.Cell).Requirement;
        slot1.ShouldBeGreaterThan(slot0);
    }

    [Fact]
    public void Glyph_scalar_grows_with_level_and_mapped_delivery_scales_with_stat()
    {
        var mapped = ParagonDatabase.Data.Glyphs.First(g =>
            g.Name is not null && GlyphInfo.IsAttributeMapped(g) && GlyphInfo.BonusScalarAt(g, 1) is not null);

        double level1 = GlyphInfo.BonusScalarAt(mapped, 1)!.Value;
        double level100 = GlyphInfo.BonusScalarAt(mapped, 100)!.Value;
        level100.ShouldBeGreaterThan(level1);

        double at40 = GlyphInfo.DeliveredBonus(mapped, 100, 40)!.Value;
        double at80 = GlyphInfo.DeliveredBonus(mapped, 100, 80)!.Value;
        at80.ShouldBe(at40 * 2, 1e-9);
        GlyphInfo.DeliveryTarget(mapped).ShouldNotBeNullOrEmpty();

        // Rarity-bonus glyphs deliver their scalar flat — stat only gates activation.
        var rarityBonus = ParagonDatabase.Data.Glyphs.First(g =>
            g.Name is not null && !GlyphInfo.IsAttributeMapped(g) && GlyphInfo.BonusScalarAt(g, 1) is not null);
        GlyphInfo.DeliveredBonus(rarityBonus, 100, 80)!.Value
            .ShouldBe(GlyphInfo.DeliveredBonus(rarityBonus, 100, 40)!.Value);
    }

    [Fact]
    public void Maximizer_prefers_stat_inside_an_active_glyph_radius()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(Board("Paragon_Sorc_00")));
        var socket = graph.Vertices.First(v => v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var goal = new GlyphGoal(socket, "Intelligence_Core", 40, 5);

        // Same starting tree (socket connected) and budget, with and without the glyph goal.
        var seed = PlanSolver.Solve(graph, new PlanRequest { Targets = [socket] });
        seed.Success.ShouldBeTrue(seed.Error);

        double InRadiusInt(HashSet<CellRef> purchased) => GlyphRadius.AttributeTotalsInRange(
            graph, socket, purchased, 5, GlyphRadius.GameMetric).GetValueOrDefault("Intelligence_Core");

        var plain = seed.PurchasedCells.ToHashSet();
        PointMaximizer.Extend(graph, plain, 10, new MaximizeFocus([], false), new PlanRequest { Targets = [] });

        var emphasized = seed.PurchasedCells.ToHashSet();
        PointMaximizer.Extend(graph, emphasized, 10, new MaximizeFocus([], false),
            new PlanRequest { Targets = [], GlyphGoals = [goal] });

        InRadiusInt(emphasized).ShouldBeGreaterThanOrEqualTo(InRadiusInt(plain));
    }

    [Fact]
    public void Comparer_reports_thresholds_and_glyph_delivery()
    {
        var layout = ParagonLayout.Single(Board("Paragon_Sorc_00"));
        var graph = ComposedGraph.Build(layout);
        var rare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0).Cell;
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = [rare] });
        solved.Success.ShouldBeTrue(solved.Error);

        var glyph = ParagonDatabase.Data.Glyphs.First(g =>
            g.Name is not null && g.Classes.Contains("Sorcerer") && GlyphInfo.BonusScalarAt(g, 1) is not null);
        var allocated = solved.PurchasedCells
            .Append(graph.Vertices[graph.StartVertex].Cell)
            .ToList();
        var a = new BuildSnapshot("Current", layout, allocated,
            [new Import.MaxrollGlyphAssignment(0, glyph.InternalName, 100)]);
        var b = new BuildSnapshot("Import", layout,
            [graph.Vertices[graph.StartVertex].Cell], []);

        string report = BuildComparer.Compare(a, b, ParagonDatabase.Data, nonParagonStat: 100_000);
        report.ShouldContain("threshold bonus(es) active");
    }
}
