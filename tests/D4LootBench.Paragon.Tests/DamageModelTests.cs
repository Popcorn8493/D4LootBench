using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The damage-bucket model: every "+% damage" stat shares ONE additive bucket (diminishing
/// returns), main stat is its own multiplier (1 + stat/coeff), legendary-rank glyphs are the
/// boards' only "×%" global multipliers. Verified against Maxroll's in-depth damage guide.
/// </summary>
public class DamageModelTests
{
    [Theory]
    [InlineData("Vulnerable_Health_Damage_Bonus", DamageBucket.AdditiveDamage)]
    [InlineData("Crit_Damage_Percent", DamageBucket.AdditiveDamage)]
    [InlineData("Damage_Percent_All_From_Skills", DamageBucket.AdditiveDamage)]
    [InlineData("Damage_Bonus_To_Near", DamageBucket.AdditiveDamage)]
    [InlineData("DOT_DPS_Bonus_Percent", DamageBucket.AdditiveDamage)]
    [InlineData("Strength_Core", DamageBucket.MainStat)]
    [InlineData("Willpower_Core", DamageBucket.MainStat)]
    [InlineData("Crit_Percent_Bonus", DamageBucket.CritChance)]
    [InlineData("Armor_Percent", DamageBucket.Other)]
    [InlineData("Hitpoints_Max_Percent_Bonus", DamageBucket.Other)]
    [InlineData("Resource_Max_Bonus", DamageBucket.Other)]
    public void Attributes_classify_into_their_bucket(string attribute, DamageBucket expected) =>
        DamageModel.Classify(attribute).ShouldBe(expected);

    [Theory]
    [InlineData("Vulnerable_Health_Damage_Bonus", true)]
    [InlineData("Crit_Damage_Percent", true)]
    [InlineData("Damage_Bonus_To_Near", true)]
    [InlineData("Damage_Percent_Bonus_Vs_Elites", true)]
    [InlineData("Damage_Percent_Bonus_When_Fortified", true)]
    [InlineData("Damage_Percent_All_From_Skills", false)]
    [InlineData("Damage_Type_Percent_Bonus", false)]
    [InlineData("Damage_Percent_Bonus_Per_Skill_Tag", false)]
    public void Situational_additives_are_flagged(string attribute, bool situational) =>
        DamageModel.IsSituational(attribute).ShouldBe(situational);

    [Fact]
    public void Every_glyph_destination_in_the_data_is_additive_damage()
    {
        // The one exception amplifies a power's own bonus, not the shared bucket.
        foreach (var glyph in ParagonDatabase.Data.Glyphs)
        {
            if (GlyphInfo.DestinationAttribute(glyph) is not string destination
                || destination == "Bonus_Percent_Per_Power")
                continue;
            DamageModel.Classify(destination).ShouldBe(
                DamageBucket.AdditiveDamage, $"{glyph.InternalName} → {destination}");
        }
    }

    [Theory]
    // Patch 3.2.1 "Core stat" values: % skill damage per 10 main stat.
    [InlineData("Barbarian", "Strength_Core", 0.8)]
    [InlineData("Paladin", "Strength_Core", 1.625)]
    [InlineData("Rogue", "Dexterity_Core", 1.25)]
    [InlineData("Spiritborn", "Dexterity_Core", 1.625)]
    [InlineData("Sorcerer", "Intelligence_Core", 1.625)]
    [InlineData("Necromancer", "Intelligence_Core", 1.625)]
    [InlineData("Warlock", "Willpower_Core", 1.625)]
    [InlineData("Druid", "Willpower_Core", 1.625)]
    public void Class_main_stats_and_coefficients(string className, string mainStat, double percentPer10)
    {
        DamageModel.MainStatAttribute(className).ShouldBe(mainStat);
        // 10 points of main stat must be worth exactly percentPer10 % skill damage.
        (10 / DamageModel.MainStatCoefficient(className) * 100).ShouldBe(percentPer10, 1e-9);
    }

    [Fact]
    public void Profile_composes_the_buckets_and_marginals()
    {
        var totals = new Dictionary<string, double>
        {
            ["Damage_Percent_All_From_Skills"] = 0.30,
            ["Vulnerable_Health_Damage_Bonus"] = 0.20,
            ["Dexterity_Core"] = 800,
            ["Crit_Percent_Bonus"] = 0.05,
            ["Armor_Percent"] = 0.50, // defense never enters the damage formula
        };

        // Rogue: the one class still at 1.25% per 10 main stat (coefficient 800).
        var profile = DamageModel.Profile(totals, "Rogue", mainStatOffset: 800);

        profile.AdditiveFraction.ShouldBe(0.50, 1e-9);
        profile.SituationalAdditiveFraction.ShouldBe(0.20, 1e-9);
        profile.MainStatTotal.ShouldBe(1600);
        // (1 + 0.5) × (1 + 1600/800) × (1 + 0.5 × (5% base + 5% paragon crit))
        profile.ExpectedMultiplier.ShouldBe(1.5 * 3.0 * 1.05, 1e-9);
        // The always-on floor drops the situational slice from the bucket.
        profile.ExpectedMultiplierUnconditional.ShouldBe(1.3 * 3.0 * 1.05, 1e-9);
        // One more +10% additive on a +50% bucket is a 6.7% real gain …
        profile.MarginalAdditive(0.10).ShouldBe(0.10 / 1.5, 1e-9);
        // … and +80 main stat on a ×3 multiplier is a 3.3% real gain.
        profile.MarginalMainStat(80).ShouldBe(80.0 / 800 / 3.0, 1e-9);
    }

    [Fact]
    public void Gear_additive_offsets_saturate_the_bucket_and_split_situational()
    {
        var totals = new Dictionary<string, double> { ["Damage_Percent_All_From_Skills"] = 0.50 };

        var paragonOnly = DamageModel.Profile(totals, "Rogue", 0);
        var withGear = DamageModel.Profile(totals, "Rogue", 0,
            additiveOffset: 2.0, situationalAdditiveOffset: 1.5);

        withGear.AdditiveFraction.ShouldBe(4.0, 1e-9);
        withGear.AdditiveOffsetFraction.ShouldBe(3.5, 1e-9);
        withGear.SituationalAdditiveFraction.ShouldBe(1.5, 1e-9);
        // +10% additive is worth 6.7% on the paragon-only bucket but only 2% on the real one.
        paragonOnly.MarginalAdditive(0.10).ShouldBe(0.10 / 1.5, 1e-9);
        withGear.MarginalAdditive(0.10).ShouldBe(0.10 / 5.0, 1e-9);
        (withGear.ExpectedMultiplier / paragonOnly.ExpectedMultiplier).ShouldBe(5.0 / 1.5, 1e-9);
        // The floor keeps the always-on parts (paragon 0.5 + gear 2.0) and drops the rest.
        (withGear.ExpectedMultiplierUnconditional / withGear.ExpectedMultiplier)
            .ShouldBe(3.5 / 5.0, 1e-9);
    }

    [Fact]
    public void Additive_slices_report_the_paragon_only_bucket()
    {
        var totals = new Dictionary<string, double>
        {
            ["Damage_Percent_All_From_Skills"] = 0.30,
            ["Vulnerable_Health_Damage_Bonus"] = 0.20,
            ["Crit_Damage_Percent"] = 0.10,
            ["Strength_Core"] = 500,
            ["Crit_Percent_Bonus"] = 0.05,
        };

        var (additive, situational) = DamageModel.AdditiveSlices(totals);

        additive.ShouldBe(0.60, 1e-9);
        situational.ShouldBe(0.30, 1e-9);
    }

    [Fact]
    public void Legendary_rank_glyphs_join_the_profile_as_global_multipliers()
    {
        var exploit = ParagonDatabase.Data.Glyphs.First(g => g.Name == "Exploit");
        var totals = new Dictionary<string, double>();

        var below = DamageModel.Profile(totals, "Rogue", 0, [(exploit, 45)]);
        var legendary = DamageModel.Profile(totals, "Rogue", 0, [(exploit, 100)]);

        below.GlyphMultipliers.ShouldBeEmpty();
        legendary.GlyphMultipliers.Count.ShouldBe(1);
        legendary.GlyphMultipliers[0].Percent.ShouldBe(10.4, 0.05);
        // ×10.4% multiplies the whole formula.
        (legendary.ExpectedMultiplier / below.ExpectedMultiplier).ShouldBe(1.104, 0.001);
    }
}
