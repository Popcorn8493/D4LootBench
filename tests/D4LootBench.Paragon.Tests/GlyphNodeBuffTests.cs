using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// "+X% bonuses to [rarity] nodes within range" glyph buffs: percent = data scalar ÷ 10,
/// calibrated against live values (Challenger 20%→100%, Marshal 25%→272.5%).
/// </summary>
public class GlyphNodeBuffTests
{
    private static ParagonGlyphDef Glyph(string internalName) =>
        ParagonDatabase.Data.Glyphs.First(g =>
            string.Equals(g.InternalName, internalName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Buff_percentages_match_the_live_game_values()
    {
        // Challenger: Nodes_BonusToNormal, 200 + 8.085/lvl → 20% at 1, ~100% at 100.
        var challenger = Glyph("Rare_Str_Generic");
        var at1 = GlyphNodeBuffs.BuffsAt(challenger, 1).Single();
        at1.Kind.ShouldBe(ParagonNodeKind.Normal);
        at1.Percent.ShouldBe(20.0, 0.01);
        GlyphNodeBuffs.BuffsAt(challenger, 100).Single().Percent.ShouldBe(100.0, 0.1);

        // Marshal: Nodes_BonusToMagic_Lesser, 250 + 25/lvl → 25% at 1, 272.5% at 100.
        var marshal = Glyph("Rare_028_Strength_Main");
        var m1 = GlyphNodeBuffs.BuffsAt(marshal, 1).Single();
        m1.Kind.ShouldBe(ParagonNodeKind.Magic);
        m1.Percent.ShouldBe(25.0, 0.01);
        GlyphNodeBuffs.BuffsAt(marshal, 100).Single().Percent.ShouldBe(272.5, 0.01);

        // Attribute-mapped-only glyphs buff no nodes.
        GlyphNodeBuffs.BuffsAt(Glyph("Rare_080_Strength_Main"), 100).ShouldBeEmpty();
    }

    [Fact]
    public void Multipliers_cover_matching_kinds_inside_the_radius_only()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_00")));
        var socket = graph.Vertices.Single(v => v.Node.Kind == ParagonNodeKind.GlyphSocket).Cell;
        var challenger = Glyph("Rare_Str_Generic"); // buffs NORMAL nodes

        int level = 46; // radius 4
        var multipliers = GlyphNodeBuffs.MultipliersFor(graph, [new SocketedGlyph(socket, challenger, level)]);
        double expected = 1 + (200 + 8.085 * (level - 1)) / 1000.0;

        multipliers.Count.ShouldBeGreaterThan(0);
        foreach (var (cell, factor) in multipliers)
        {
            var vertex = graph.Vertices.Single(v => v.Cell == cell);
            vertex.Node.Kind.ShouldBe(ParagonNodeKind.Normal, "Challenger only buffs Normal nodes");
            (Math.Abs(cell.X - socket.X) + Math.Abs(cell.Y - socket.Y)).ShouldBeLessThanOrEqualTo(4);
            factor.ShouldBe(expected, 1e-6);
        }

        // Out-of-radius normal nodes are untouched.
        graph.Vertices.Count(v => v.Node.Kind == ParagonNodeKind.Normal
            && !multipliers.ContainsKey(v.Cell)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Buffed_totals_raise_thresholds_the_raw_values_miss()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_00")));
        var rare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = [rare.Cell] });
        solved.Success.ShouldBeTrue(solved.Error);
        var purchased = solved.PurchasedCells.ToHashSet();

        // A synthetic 2× on every purchased cell: totals double, and any threshold sitting
        // between the raw and doubled paragon totals flips to met.
        var doubled = purchased.ToDictionary(c => c, _ => 2.0);
        var raw = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, NonParagonStats.None, "Barbarian");
        var buffed = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, NonParagonStats.None, "Barbarian", doubled);

        string core = MaximizeFocus.CoreStats.First(s => raw.Totals.GetValueOrDefault(s) > 0);
        buffed.Totals[core].ShouldBe(raw.Totals[core] * 2, 1e-6);
        buffed.ThresholdsMet.ShouldBeGreaterThanOrEqualTo(raw.ThresholdsMet);

        var status = raw.Thresholds.Single(t => t.Cell == rare.Cell);
        var buffedStatus = buffed.Thresholds.Single(t => t.Cell == rare.Cell);
        buffedStatus.Have.ShouldBeGreaterThan(status.Have);
    }
}
