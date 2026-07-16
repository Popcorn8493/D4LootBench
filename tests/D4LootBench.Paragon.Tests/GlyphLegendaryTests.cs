using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The parts of a glyph beyond the scaling bonus: the legendary-rank multiplicative affix
/// (requiredRarity 2, unlocked at level 46 — engine-side constant) and the flat Additional
/// Bonus power (identity only; magnitude isn't in the data). Percent read calibrated against
/// live Exploit (5% at 46).
/// </summary>
public class GlyphLegendaryTests
{
    private static Models.ParagonGlyphDef Exploit =>
        ParagonDatabase.Data.Glyphs.First(g => g.Name == "Exploit");

    [Fact]
    public void Exploit_delivers_vulnerable_damage_and_carries_both_extras()
    {
        GlyphInfo.DestinationAttribute(Exploit).ShouldBe("Vulnerable_Health_Damage_Bonus");
        GlyphInfo.LegendaryAffix(Exploit).ShouldNotBeNull();
        GlyphInfo.AdditionalBonusAffix(Exploit).ShouldNotBeNull();
        GlyphInfo.TagsLabel(GlyphInfo.LegendaryAffix(Exploit)!).ShouldContain("Vulnerable");
    }

    [Fact]
    public void Legendary_multiplier_is_gated_at_the_upgrade_level_and_scales()
    {
        GlyphInfo.LegendaryMultiplierPercentAt(Exploit, 45).ShouldBeNull();
        GlyphInfo.LegendaryMultiplierPercentAt(Exploit, 46)!.Value.ShouldBe(5.0, 0.05);
        GlyphInfo.LegendaryMultiplierPercentAt(Exploit, 100)!.Value.ShouldBe(10.4, 0.05);
    }

    [Fact]
    public void Every_glyph_in_the_data_has_a_legendary_and_an_additional_bonus_affix()
    {
        foreach (var glyph in ParagonDatabase.Data.Glyphs)
        {
            GlyphInfo.LegendaryAffix(glyph).ShouldNotBeNull(glyph.InternalName);
            GlyphInfo.AdditionalBonusAffix(glyph).ShouldNotBeNull(glyph.InternalName);
        }
    }
}
