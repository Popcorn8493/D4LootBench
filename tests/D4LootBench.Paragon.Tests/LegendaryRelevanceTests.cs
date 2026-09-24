using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class LegendaryRelevanceTests
{
    private static ParagonNodeDef Legendary(string name) =>
        ParagonDatabase.Data.Nodes.Single(n => n.Kind == ParagonNodeKind.Legendary && n.Name == name);

    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static readonly MaximizeFocus CoreFocus = new([], PreferRare: false);

    [Fact]
    public void Skill_conditions_count_only_when_the_build_uses_the_skill()
    {
        var forceOfNature = Legendary("Force of Nature"); // Keyword_DustDevils / Keyword_Earthquake
        var whirlwind = LegendaryContext.From(CoreFocus,
            ["Whirlwind Rapidly attack surrounding enemies, creating Dust Devils as you spin."]);
        var noSkills = LegendaryContext.From(CoreFocus, null);

        LegendaryRelevance.Of(forceOfNature, whirlwind).ShouldBe(1.0);
        LegendaryRelevance.Of(forceOfNature, noSkills).ShouldBe(0.0);
    }

    [Fact]
    public void Class_resource_and_focused_stats_satisfy_conditions()
    {
        var plain = LegendaryContext.From(CoreFocus, null);
        // Weapons Master: "to Injured and Healthy enemies" holds for most of a fight.
        LegendaryRelevance.Of(Legendary("Weapons Master"), plain).ShouldBe(1.0);
        // Warbringer's ResourceFury tag is incidental — its condition is being Fortified.
        LegendaryRelevance.Of(Legendary("Warbringer"), plain).ShouldBe(0.0);
        var fortify = LegendaryContext.From(
            new MaximizeFocus(["Damage_Percent_Bonus_When_Fortified"], PreferRare: false), null);
        LegendaryRelevance.Of(Legendary("Warbringer"), fortify).ShouldBe(1.0);

        // Hemorrhage (Keyword_Vulnerable) applies once the build focuses vulnerable damage.
        var vulnerableFocus = LegendaryContext.From(
            new MaximizeFocus(["Vulnerable_Health_Damage_Bonus"], PreferRare: false), null);
        LegendaryRelevance.Of(Legendary("Hemorrhage"), vulnerableFocus).ShouldBe(1.0);
    }

    [Fact]
    public void Conditionless_legendaries_get_half_credit()
    {
        var context = LegendaryContext.From(CoreFocus, null);

        LegendaryRelevance.Of(Legendary("Enchantment Master"), context).ShouldBe(0.5);
    }

    [Fact]
    public void Pipeline_credits_only_matched_legendaries()
    {
        var layout = new ParagonLayout([
            new PlacedBoard { Board = Board("Paragon_Barb_00") },
            new PlacedBoard { Board = Board("Paragon_Barb_10"), ParentSlot = 0, AttachEdge = BoardEdge.Top },
        ]);
        var graph = ComposedGraph.Build(layout);
        var request = new PlanRequest { Targets = PlanSolver.LegendaryCells(graph) };
        var basePipeline = new PlacementPipeline(60, CoreFocus, null);

        var without = PlacementAnalyzer.EvaluatePipeline(graph, request, basePipeline)!;
        var unmatched = PlacementAnalyzer.EvaluatePipeline(graph, request,
            basePipeline with { Legendary = LegendaryContext.From(CoreFocus, null) })!;
        var matched = PlacementAnalyzer.EvaluatePipeline(graph, request,
            basePipeline with { Legendary = LegendaryContext.From(CoreFocus, ["Dust Devil"]) })!;

        without.LegendaryFactor.ShouldBe(1.0);
        unmatched.LegendaryFactor.ShouldBe(1.0);
        matched.LegendaryFactor.ShouldBe(1.45, 1e-9); // Force of Nature 45%[x]
        matched.EffectiveScore.ShouldBe(matched.FocusScore * 1.45, 1e-9);
    }

    [Fact]
    public void A_matched_legendary_board_surfaces_as_a_swap_candidate()
    {
        var usable = ParagonDatabase.BoardsForClass("Barbarian").Where(b => b.BoardIndex != 0).ToList();

        // Weapons Master (75%[x], mostly-met enemy-state condition) is the strongest by default …
        PlacementAnalyzer.LegendaryCandidate(usable, LegendaryContext.From(CoreFocus, null))
            .ShouldHaveSingleItem().Board.InternalName.ShouldBe("Paragon_Barb_08");
        // … and among the rest, a Dust Devil build surfaces the board whose skill condition it meets.
        var candidate = PlacementAnalyzer.LegendaryCandidate(usable.Where(b => b.InternalName != "Paragon_Barb_08"),
            LegendaryContext.From(CoreFocus, ["Earthquake Dust Devil"])).ShouldHaveSingleItem();
        candidate.Board.InternalName.ShouldBe("Paragon_Barb_10"); // Force of Nature
        PlacementAnalyzer.LegendaryCandidate(usable, null).ShouldBeEmpty();
    }

    [Fact]
    public void Skill_text_survives_the_project_round_trip()
    {
        var project = new ParagonProject
        {
            ClassName = "Barbarian",
            Boards = [new ParagonProjectBoard("Paragon_Barb_00", null, null, 0)],
            References = [new ParagonProjectReference("Guide skills", []) { SkillText = "Whirlwind Dust Devils" }],
        };

        var restored = ParagonProjectSerializer.FromJson(ParagonProjectSerializer.ToJson(project));

        restored.References.ShouldHaveSingleItem().SkillText.ShouldBe("Whirlwind Dust Devils");
    }
}
