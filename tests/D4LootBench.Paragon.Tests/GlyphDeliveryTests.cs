using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The scalar-weighted glyph pull: stronger attribute-mapped glyphs attract in-radius source
/// stat harder, rarity-bonus glyphs exert no pull, and goals still pull at weight 1 when no
/// deliveries are given.
/// </summary>
public class GlyphDeliveryTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    private static ParagonGlyphDef MappedGlyph(
        string snoId, double scalar, string destination = "Damage_Percent") => new()
    {
        SnoId = snoId,
        InternalName = snoId,
        Name = snoId,
        Affixes =
        [
            new GlyphAffixDef
            {
                AttributeMaps =
                [
                    new GlyphAttributeMap
                        { SourceAttribute = "Dexterity_Core", DestinationAttribute = destination },
                ],
                StartingBonusScalar = scalar,
            },
        ],
    };

    private static ParagonGlyphDef RarityGlyph(string snoId, double scalar) => new()
    {
        SnoId = snoId,
        InternalName = snoId,
        Name = snoId,
        Affixes = [new GlyphAffixDef { AffectedNodeRarity = 3, StartingBonusScalar = scalar }],
    };

    [Fact]
    public void For_weights_mapped_glyphs_by_relative_scalar_and_drops_rarity_glyphs()
    {
        var strong = MappedGlyph("strong", 300);
        var weak = MappedGlyph("weak", 100);
        var buffOnly = RarityGlyph("buff", 500);

        var deliveries = GlyphDelivery.For(
        [
            (new CellRef(0, 1, 1), strong, 50),
            (new CellRef(1, 1, 1), weak, 50),
            (new CellRef(2, 1, 1), buffOnly, 50),
        ]);

        deliveries.Count.ShouldBe(2);
        deliveries.Single(d => d.Socket.BoardSlot == 0).Weight.ShouldBe(1.5, 0.001);
        deliveries.Single(d => d.Socket.BoardSlot == 1).Weight.ShouldBe(0.5, 0.001);
        deliveries.ShouldAllBe(d => d.SourceAttribute == "Dexterity_Core" && d.Radius == 5);
    }

    [Fact]
    public void Destination_weights_scale_the_pull_by_build_relevance()
    {
        // Equal scalars — only what each glyph converts INTO differs. The focused destination
        // pulls at its focus weight; the unfocused one stays neutral (never muted).
        var focused = MappedGlyph("focused", 100, "Vulnerable_Health_Damage_Bonus");
        var offBuild = MappedGlyph("off-build", 100, "Damage_Bonus_To_Near");
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            { ["Vulnerable_Health_Damage_Bonus"] = 3.0 };

        var deliveries = GlyphDelivery.For(
        [
            (new CellRef(0, 1, 1), focused, 50),
            (new CellRef(1, 1, 1), offBuild, 50),
        ], weights);

        deliveries.Single(d => d.Socket.BoardSlot == 0).Weight.ShouldBe(3.0, 0.001);
        deliveries.Single(d => d.Socket.BoardSlot == 1).Weight.ShouldBe(1.0, 0.001);
    }

    [Fact]
    public void The_stronger_glyphs_radius_attracts_the_point()
    {
        // a S b — equal Dexterity nodes; each sits under its own glyph, the right one stronger.
        var graph = SyntheticBoards.Graph(["aSb"], new()
        {
            ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
            ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
        });
        HashSet<CellRef> Run(double leftWeight, double rightWeight)
        {
            var purchased = new HashSet<CellRef>();
            PointMaximizer.Extend(graph, purchased, 1, new MaximizeFocus(["Dexterity_Core"], false)
            {
                GlyphDeliveries =
                [
                    new GlyphDelivery(new CellRef(0, 0, 0), "Dexterity_Core", 0, leftWeight),
                    new GlyphDelivery(new CellRef(0, 2, 0), "Dexterity_Core", 0, rightWeight),
                ],
            }, EmptyRequest);
            return purchased;
        }

        Run(leftWeight: 0.5, rightWeight: 2.0).ShouldBe([new CellRef(0, 2, 0)]);
        Run(leftWeight: 2.0, rightWeight: 0.5).ShouldBe([new CellRef(0, 0, 0)]);
    }

    [Fact]
    public void Without_deliveries_goals_pull_at_weight_one()
    {
        // Only the right node is inside the goal's radius — the goal-derived pull must break
        // the tie exactly as before.
        var graph = SyntheticBoards.Graph(["aSb"], new()
        {
            ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
            ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
        });
        var request = new PlanRequest
        {
            Targets = [],
            GlyphGoals = [new GlyphGoal(new CellRef(0, 2, 0), "Dexterity_Core", 40, Radius: 0)],
        };

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Dexterity_Core"], false), request);

        purchased.ShouldBe([new CellRef(0, 2, 0)]);
    }
}
