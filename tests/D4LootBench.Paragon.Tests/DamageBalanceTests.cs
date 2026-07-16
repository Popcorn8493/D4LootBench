using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Bucket-aware valuation: with <see cref="MaximizeFocus.AdditiveDamageFraction"/> set, additive
/// "+% damage" attributes are worth their marginal real contribution (1/(1+bucket)), so a
/// saturated bucket stops outcompeting main stat; null keeps the old face-value behavior.
/// </summary>
public class DamageBalanceTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    // d S c  — 'd' is an above-average additive damage node (its attr mean is dragged down by
    // e . .    the off-path 'e'), 'c' an exactly-average main stat node.
    private static ComposedGraph Graph() => SyntheticBoards.Graph(["dSc", "e.."], new()
    {
        ['d'] = SyntheticBoards.Node("d", ParagonNodeKind.Magic, ("Damage_Percent_All_From_Skills", 0.15)),
        ['e'] = SyntheticBoards.Node("e", ParagonNodeKind.Magic, ("Damage_Percent_All_From_Skills", 0.05)),
        ['c'] = SyntheticBoards.Node("c", ParagonNodeKind.Magic, ("Strength_Core", 5)),
    });

    private static MaximizeFocus Focus(double? additiveFraction) =>
        new(["Damage_Percent_All_From_Skills", "Strength_Core"], false)
        {
            AdditiveDamageFraction = additiveFraction,
        };

    [Fact]
    public void A_saturated_additive_bucket_steers_the_spend_toward_main_stat()
    {
        HashSet<CellRef> Run(double? additiveFraction)
        {
            var purchased = new HashSet<CellRef>();
            PointMaximizer.Extend(Graph(), purchased, 1, Focus(additiveFraction), EmptyRequest);
            return purchased;
        }

        // Face value: the damage node is 1.5× its attribute mean, the stat node 1.0× — damage wins.
        Run(additiveFraction: null).ShouldBe([new CellRef(0, 0, 0)]);
        // At +100% additive the marginal halves (0.75 < 1.0) — main stat wins.
        Run(additiveFraction: 1.0).ShouldBe([new CellRef(0, 2, 0)]);
    }

    [Fact]
    public void BuildComparer_judges_legendary_glyphs_and_expected_damage()
    {
        var board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00");
        var layout = ParagonLayout.Single(board);
        var graph = ComposedGraph.Build(layout);
        var start = graph.Vertices[graph.StartVertex].Cell;
        var exploit = ParagonDatabase.Data.Glyphs.First(g =>
            g.Name == "Exploit" && g.Classes.Contains("Sorcerer"));

        BuildSnapshot Build(string name, int level) => new(
            name, layout, [start],
            [new MaxrollGlyphAssignment(0, exploit.InternalName, level)]);

        string report = BuildComparer.Compare(
            Build("Legendary", 100), Build("Rare", 45), ParagonDatabase.Data);

        report.ShouldContain("Damage balance Legendary:");
        report.ShouldContain("Damage balance Rare:");
        report.ShouldContain("Legendary runs more legendary-rank glyphs");
        report.ShouldContain("Legendary has a higher expected damage multiplier");

        // The gear offsets seed both builds' buckets and show in the breakdown, and the
        // expected multiplier reports the full-uptime ceiling with an always-on floor.
        string withGear = BuildComparer.Compare(
            Build("Legendary", 100), Build("Rare", 45), ParagonDatabase.Data,
            NonParagonStats.None, additiveDamageOffset: 2.0, situationalDamageOffset: 1.5);
        withGear.ShouldContain("incl. +350% gear");
        withGear.ShouldContain("always-on only");
    }

    [Fact]
    public void FocusScore_damps_additive_damage_the_same_way()
    {
        var graph = Graph();
        var damageCell = new HashSet<CellRef> { new(0, 0, 0) };
        var statCell = new HashSet<CellRef> { new(0, 2, 0) };

        FocusScore.Of(graph, damageCell, Focus(1.0))
            .ShouldBe(FocusScore.Of(graph, damageCell, Focus(null)) / 2, 1e-9);
        // Non-damage attributes keep their value untouched.
        FocusScore.Of(graph, statCell, Focus(1.0))
            .ShouldBe(FocusScore.Of(graph, statCell, Focus(null)), 1e-9);
    }
}
