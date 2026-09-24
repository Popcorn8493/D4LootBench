using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class ParagonDataTests
{
    private static readonly string[] AllClasses =
    [
        "Barbarian", "Druid", "Necromancer", "Rogue", "Sorcerer", "Spiritborn", "Paladin", "Warlock",
    ];

    [Fact]
    public void Data_loads_with_expected_catalog_counts()
    {
        var data = ParagonDatabase.Data;
        data.FormatVersion.ShouldBe(1);
        data.Boards.Count.ShouldBe(80);
        data.Nodes.Count.ShouldBe(566);
        data.Glyphs.Count.ShouldBe(161);
        data.Thresholds.Count.ShouldBe(52);
        data.Multipliers.Count.ShouldBe(8); // six node multipliers + two power-tuning constants (3.2.1)
    }

    [Fact]
    public void Every_class_has_boards_and_every_board_has_a_display_name()
    {
        foreach (var className in AllClasses)
        {
            var boards = ParagonDatabase.BoardsForClass(className).ToList();
            boards.Count.ShouldBeGreaterThanOrEqualTo(9, className);
            boards.ShouldAllBe(b => !string.IsNullOrWhiteSpace(b.Name));
        }

        ParagonDatabase.Data.Boards.ShouldAllBe(b => b.ClassName != null);
    }

    [Fact]
    public void All_board_placements_resolve_to_nodes_and_fit_the_grid()
    {
        foreach (var board in ParagonDatabase.Data.Boards)
        {
            board.Width.ShouldBe(21);
            foreach (var placement in board.Nodes)
            {
                placement.X.ShouldBeInRange(0, board.Width - 1);
                placement.Y.ShouldBeInRange(0, board.Width - 1);
                ParagonDatabase.NodesBySnoId.ShouldContainKey(placement.Node);
            }
        }
    }

    [Fact]
    public void Node_kind_census_matches_game_data()
    {
        var byKind = ParagonDatabase.Data.Nodes
            .GroupBy(n => n.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        byKind[ParagonNodeKind.Rare].ShouldBe(344);
        byKind[ParagonNodeKind.Magic].ShouldBe(135);
        byKind[ParagonNodeKind.Legendary].ShouldBe(73);
        byKind[ParagonNodeKind.Start].ShouldBe(8);
        byKind[ParagonNodeKind.Normal].ShouldBe(4);
        byKind[ParagonNodeKind.Gate].ShouldBe(1);
        byKind[ParagonNodeKind.GlyphSocket].ShouldBe(1);
    }

    [Fact]
    public void Normal_stat_node_grants_plus_five()
    {
        var node = ParagonDatabase.Data.Nodes.Single(n => n.InternalName == "Generic_Normal_Str");
        node.Kind.ShouldBe(ParagonNodeKind.Normal);
        var attribute = node.Attributes.ShouldHaveSingleItem();
        attribute.Attribute.ShouldBe("Strength_Core");
        attribute.Value.ShouldBe(5);
    }

    [Fact]
    public void Magic_crit_damage_node_resolves_to_calibrated_percent_value()
    {
        var node = ParagonDatabase.Data.Nodes.Single(n => n.InternalName == "Generic_Magic_CriticalDamage");
        var attribute = node.Attributes.ShouldHaveSingleItem();
        attribute.Attribute.ShouldBe("Crit_Damage_Percent");
        attribute.Value.ShouldNotBeNull();
        attribute.Value.Value.ShouldBe(0.075d, 1e-9); // displays as 7.5%
    }

    [Fact]
    public void Every_legendary_node_has_a_passive_power()
    {
        ParagonDatabase.Data.Nodes
            .Where(n => n.Kind == ParagonNodeKind.Legendary)
            .ShouldAllBe(n => n.Power != null && n.Power.SnoId.Length > 0);
    }

    [Fact]
    public void Node_threshold_references_resolve()
    {
        foreach (var node in ParagonDatabase.Data.Nodes)
        {
            foreach (var snoId in node.Thresholds)
            {
                ParagonDatabase.ThresholdsBySnoId.ShouldContainKey(snoId);
            }
        }
    }

    [Fact]
    public void Threshold_requirements_scale_with_board_attachment_index()
    {
        var threshold = ParagonDatabase.Data.Thresholds.Single(t => t.InternalName == "WillPowerSide1");
        var requirement = threshold.Requirements.ShouldHaveSingleItem();
        requirement.Attribute.ShouldBe("Willpower_Total");
        requirement.ValuesByBoardIndex.Take(4).ShouldBe([190d, 265d, 340d, 415d]);
    }

    [Fact]
    public void Known_board_and_glyph_lookups_work()
    {
        var board = ParagonDatabase.BoardsForClass("Sorcerer").Single(b => b.Name == "Static Surge");
        board.InternalName.ShouldBe("Paragon_Sorc_03");
        board.Nodes.Count.ShouldBeGreaterThan(100);

        var glyph = ParagonDatabase.GlyphsForClass("Sorcerer").Single(g => g.Name == "Enchanter");
        glyph.Classes.ShouldBe(["Sorcerer"]);
        glyph.Affixes.Count.ShouldBe(3);
        glyph.Affixes.ShouldAllBe(a => a.InternalName != null);
    }

    [Fact]
    public void Glyph_classes_only_use_known_class_names()
    {
        ParagonDatabase.Data.Glyphs
            .SelectMany(g => g.Classes)
            .Distinct()
            .ShouldAllBe(c => AllClasses.Contains(c));
    }

    [Fact]
    public void Patch_3_2_1_board_attaches_and_solves_to_its_legendary()
    {
        var starter = ParagonDatabase.BoardsForClass("Spiritborn").Single(b => b.BoardIndex == 0);
        var swarm = ParagonDatabase.BoardsForClass("Spiritborn").Single(b => b.Name == "Swarm of Storms");
        var solved = Enumerable.Range(0, 4).Select(rotation =>
        {
            try
            {
                var graph = ComposedGraph.Build(new ParagonLayout([
                    new PlacedBoard { Board = starter },
                    new PlacedBoard { Board = swarm, ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = rotation },
                ]));
                return PlanSolver.Solve(graph, new PlanRequest { Targets = PlanSolver.LegendaryCells(graph) });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }).Where(r => r is { Success: true }).ToList();

        solved.ShouldNotBeEmpty("Swarm of Storms should attach to the starter and reach its legendary");
    }

    // Legendary node / glyph power values are rendered from the game's power script formulas;
    // these anchors are the 3.2.1 patch notes' published numbers.
    [Theory]
    [InlineData("Hemorrhage", 60)]
    [InlineData("Blood Rage", 45)]
    [InlineData("In-Fighter", 70)]
    [InlineData("Swarm of Storms", 80)]
    [InlineData("Icefall", 60)]
    [InlineData("Force of Nature", 45)]
    public void Legendary_node_headline_multipliers_match_patch_notes(string nodeName, double percent)
    {
        var node = ParagonDatabase.Data.Nodes.Single(n => n.Kind == ParagonNodeKind.Legendary && n.Name == nodeName);

        LegendaryNodeInfo.HeadlineMultiplierPercent(node).ShouldBe(percent);
        node.Power!.Description.ShouldNotBeNull().ShouldContain($"{percent:0}%[x]");
        node.Power.Description.ShouldNotContain("{");
        node.Power.Description.ShouldNotContain("[SF_");
    }

    [Theory]
    [InlineData("Superiority", "10% Damage Reduction")]
    [InlineData("Talon", "25%[x] increased Critical Strike damage")]
    [InlineData("Exhumation", "10% Damage Reduction for 8 seconds")]
    public void Glyph_additional_bonus_renders_calibrated_values(string glyphName, string expected)
    {
        var glyph = ParagonDatabase.Data.Glyphs.First(g => g.Name == glyphName);

        GlyphInfo.AdditionalBonusAffix(glyph)!.BonusPower!.Description.ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void Every_legendary_node_has_a_rendered_tooltip()
    {
        var legendaries = ParagonDatabase.Data.Nodes.Where(n => n.Kind == ParagonNodeKind.Legendary).ToList();

        legendaries.ShouldAllBe(n => !string.IsNullOrWhiteSpace(n.Power!.Description));
        // Only live-character values ("Current Bonus", Maximum Life) may stay unresolved.
        legendaries.Count(n => n.Power!.Description!.Contains('?')).ShouldBeLessThanOrEqualTo(8);
    }
}
