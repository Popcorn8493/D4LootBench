using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class GearReferenceTests
{
    private static double ScoreOf(GearReferenceResult result, string attribute) =>
        result.Emphasis.FirstOrDefault(e => e.Attribute == attribute)?.Score ?? 0;

    [Fact]
    public void Core_stats_map_to_their_core_attributes_with_the_gear_weight()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("Willpower", 8),
            new GearStatPriority("Strength", 3),
        ]);

        ScoreOf(result, "Willpower_Core").ShouldBe(8);
        ScoreOf(result, "Strength_Core").ShouldBe(3);
        result.UnmappedStats.ShouldBeEmpty();
    }

    [Fact]
    public void Plus_and_percent_prefixes_and_case_are_normalized()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("+Armor", 2),
            new GearStatPriority("%Cooldown Reduction", 3),
            new GearStatPriority("MAXIMUM LIFE", 4),
        ]);

        ScoreOf(result, "Armor_Percent").ShouldBe(2);
        ScoreOf(result, "Power_Cooldown_Reduction_Percent_All").ShouldBe(3);
        ScoreOf(result, "Hitpoints_Max_Percent_Bonus").ShouldBe(4);
    }

    [Fact]
    public void Skill_rank_affixes_do_not_leak_through_token_matches()
    {
        // "+Cyclone Armor" is a skill rank, not armor — the armor rule is exact-match guarded.
        var result = GearReference.EmphasisOf([new GearStatPriority("+Cyclone Armor", 4)]);

        result.Emphasis.ShouldBeEmpty();
        result.UnmappedStats.ShouldBe(["+Cyclone Armor"]);
    }

    [Fact]
    public void One_gear_stat_can_map_to_several_alternate_attributes()
    {
        var result = GearReference.EmphasisOf([new GearStatPriority("Maximum Life", 6)]);

        ScoreOf(result, "Hitpoints_Max_Percent_Bonus").ShouldBe(6);
        ScoreOf(result, "Flat_Hitpoints_Max_Bonus").ShouldBe(6);
    }

    [Fact]
    public void Several_gear_stats_mapping_to_one_attribute_stack()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("Fire Resistance", 2),
            new GearStatPriority("+Resistance to All Elements", 3),
        ]);

        ScoreOf(result, "Resistance_All_Bonus_Percent").ShouldBe(5);
    }

    [Fact]
    public void Offense_stats_map_to_their_board_counterparts()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("+Critical Strike Chance", 4),
            new GearStatPriority("Critical Strike Damage", 3),
            new GearStatPriority("Vulnerable Damage Multiplier", 2),
            new GearStatPriority("Cold Damage Multiplier", 1),
        ]);

        ScoreOf(result, "Crit_Percent_Bonus").ShouldBe(4);
        ScoreOf(result, "Crit_Damage_Percent").ShouldBe(3);
        ScoreOf(result, "Vulnerable_Health_Damage_Bonus").ShouldBe(2);
        ScoreOf(result, "Damage_Type_Percent_Bonus").ShouldBe(1);
    }

    [Fact]
    public void Emphasis_is_ordered_by_score_descending_and_feeds_combine()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("Willpower", 2),
            new GearStatPriority("Maximum Life", 7),
        ]);

        result.Emphasis[0].Attribute.ShouldBe("Hitpoints_Max_Percent_Bonus");
        result.Emphasis.Select(e => e.Score).ShouldBeInOrder(SortDirection.Descending);

        // The gear emphasis participates in the same consensus as build-derived references.
        var combined = BuildReference.Combine([result.Emphasis], MaximizeFocus.CoreStats);
        combined.ShouldContain(e => e.Attribute == "Willpower_Core");
    }

    [Fact]
    public void Stats_the_boards_cannot_supply_are_reported_not_force_fitted()
    {
        var result = GearReference.EmphasisOf([
            new GearStatPriority("%Damage Reduction", 4),
            new GearStatPriority("Willpower", 2),
        ]);

        result.UnmappedStats.ShouldBe(["%Damage Reduction"]);
        ScoreOf(result, "Willpower_Core").ShouldBe(2);
    }
}
