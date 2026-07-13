using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class SkillReferenceTests
{
    private static MobalyticsSkill Skill(string slug, string name, string description) =>
        new(slug, name, description, "basic", "Core");

    private static double ScoreOf(SkillReferenceResult result, string attribute) =>
        result.Emphasis.FirstOrDefault(e => e.Attribute == attribute)?.Score ?? 0;

    [Fact]
    public void Main_skills_damage_type_is_read_from_its_description()
    {
        var variant = new MobalyticsSkillVariant("t",
            [Skill("chain-lightning", "Chain Lightning", "Deals {x} Lightning damage that bounces.")],
            new Dictionary<string, int> { ["chain-lightning"] = 5 });

        var result = SkillReference.EmphasisOf(variant);

        result.PrimaryDamageType.ShouldBe("Lightning");
        ScoreOf(result, "Damage_Type_Percent_Bonus").ShouldBeGreaterThan(0);
        ScoreOf(result, "Damage_Percent_Bonus_Per_Skill_Tag").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Untyped_damage_defaults_to_physical()
    {
        var variant = new MobalyticsSkillVariant("t",
            [Skill("whirlwind", "Whirlwind", "Rapidly attack surrounding enemies for {71} damage.")],
            new Dictionary<string, int>());

        SkillReference.EmphasisOf(variant).PrimaryDamageType.ShouldBe("Physical");
    }

    [Fact]
    public void The_bar_skill_with_the_deepest_tree_investment_decides_the_damage_type()
    {
        var variant = new MobalyticsSkillVariant("t",
            [
                Skill("flame-shield", "Flame Shield", "Deals {x} Fire damage and grants Immunity."),
                Skill("ball-lightning", "Ball Lightning", "Deals {x} Lightning damage repeatedly."),
            ],
            new Dictionary<string, int> { ["flame-shield"] = 1, ["ball-lightning"] = 5 });

        SkillReference.EmphasisOf(variant).PrimaryDamageType.ShouldBe("Lightning");
    }

    [Fact]
    public void Tree_slugs_vote_mechanics_weighted_by_invested_ranks()
    {
        var variant = new MobalyticsSkillVariant("t",
            [Skill("whirlwind", "Whirlwind", "d")],
            new Dictionary<string, int>
            {
                ["exploit-vulnerable-damage"] = 3,
                ["heavy-handed-critical-strike"] = 2,
            });

        var result = SkillReference.EmphasisOf(variant);

        ScoreOf(result, "Vulnerable_Health_Damage_Bonus").ShouldBe(3);
        ScoreOf(result, "Crit_Percent_Bonus").ShouldBe(2);
        ScoreOf(result, "Crit_Damage_Percent").ShouldBe(2);
    }

    [Fact]
    public void Resource_text_votes_maximum_and_generation_attributes()
    {
        var variant = new MobalyticsSkillVariant("t",
            [Skill("rallying-cry", "Rallying Cry", "You gain increased Fury generation and Maximum Fury.")],
            new Dictionary<string, int>());

        var result = SkillReference.EmphasisOf(variant);

        ScoreOf(result, "Resource_Max_Bonus").ShouldBeGreaterThan(0);
        ScoreOf(result, "Resource_Gain_And_Regen_Bonus_Percent_All_Primary").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Skills_never_vote_core_stats()
    {
        var variant = new MobalyticsSkillVariant("t",
            [Skill("whirlwind", "Whirlwind", "Strength of the bear, dexterity of the cat.")],
            new Dictionary<string, int> { ["strength-training"] = 3 });

        var result = SkillReference.EmphasisOf(variant);

        foreach (var core in MaximizeFocus.CoreStats)
            ScoreOf(result, core).ShouldBe(0);
        result.ActiveSkillNames.ShouldBe(["Whirlwind"]);
    }
}
