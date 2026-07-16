using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>Which part of D4's damage formula a paragon attribute feeds.</summary>
public enum DamageBucket
{
    /// <summary>The class main stat's own multiplier: 1 + stat/coefficient.</summary>
    MainStat,

    /// <summary>The ONE shared additive "+%" bucket — stacking it has diminishing returns.</summary>
    AdditiveDamage,

    /// <summary>Critical strike chance — scales the base ×1.5 crit multiplier's uptime.</summary>
    CritChance,

    /// <summary>Not part of the damage formula (defense, resource, utility, off-class stats).</summary>
    Other,
}

/// <summary>A legendary-rank glyph's global multiplier contribution.</summary>
public sealed record GlyphMultiplier(string GlyphName, string Label, double Percent);

/// <summary>A build's damage-formula breakdown; fractions are native units (0.85 = +85%).
/// <see cref="AdditiveFraction"/> is the WHOLE bucket (paragon + the user-supplied gear
/// offsets, <see cref="AdditiveOffsetFraction"/>); <see cref="SituationalAdditiveFraction"/>
/// is the conditional slice of it (paragon situational attrs + the situational gear offset).
/// <see cref="ExpectedMultiplier"/> assumes full uptime on that slice (the ceiling, like the
/// in-game sheet); <see cref="ExpectedMultiplierUnconditional"/> drops it entirely (the floor
/// — what's guaranteed on any hit).</summary>
public sealed record DamageProfile(
    string MainStatAttribute,
    double MainStatTotal,
    double MainStatCoefficient,
    double AdditiveFraction,
    double SituationalAdditiveFraction,
    double AdditiveOffsetFraction,
    double CritChanceBonusFraction,
    IReadOnlyList<GlyphMultiplier> GlyphMultipliers,
    double ExpectedMultiplier,
    double ExpectedMultiplierUnconditional)
{
    /// <summary>Real damage gained by one more +X% additive, given the current bucket.</summary>
    public double MarginalAdditive(double additiveFraction) =>
        additiveFraction / (1.0 + AdditiveFraction);

    /// <summary>Real damage gained by more main stat, given the current multiplier.</summary>
    public double MarginalMainStat(double points) =>
        points / MainStatCoefficient / (1.0 + MainStatTotal / MainStatCoefficient);
}

/// <summary>
/// D4's damage-bucket model (verified against Maxroll's in-depth damage guide, 2026-07):
/// Damage = WeaponDmg × Skill% × (1 + additive bucket) × (1 + mainstat/coeff) × Π global "×%"
/// multipliers. ALL "+%" damage stats share ONE additive bucket — vulnerable damage, crit
/// damage, close/distant, vs elites, while fortified, skill damage — so +10% on a +400% bucket
/// is only a 2% real gain, while every "×%" bonus multiplies in full. Crit has a base ×1.5 and
/// vulnerable a base ×1.2 global multiplier. Paragon boards only ever grant additive damage,
/// main stat, and crit CHANCE; the boards' only "×%" sources are legendary-rank glyphs
/// (<see cref="GlyphInfo.LegendaryMultiplierPercentAt"/>). Classification is curated over the
/// data's attribute namespace (boards + glyph destinations share it).
/// </summary>
public static class DamageModel
{
    /// <summary>Barbarian needs 9.0991 main stat per 1% skill damage; every other class 8.</summary>
    public static double MainStatCoefficient(string? className) =>
        string.Equals(className, "Barbarian", StringComparison.OrdinalIgnoreCase) ? 909.91 : 800.0;

    /// <summary>
    /// The core stat that grants the class its skill-damage multiplier. Paladin→Strength and
    /// Warlock→Willpower verified via Wowhead/Maxroll class overviews (2026-07).
    /// </summary>
    public static string? MainStatAttribute(string? className) => className?.ToLowerInvariant() switch
    {
        "barbarian" or "paladin" => "Strength_Core",
        "rogue" or "spiritborn" => "Dexterity_Core",
        "sorcerer" or "necromancer" => "Intelligence_Core",
        "druid" or "warlock" => "Willpower_Core",
        _ => null,
    };

    /// <summary>Attributes whose additive bonus only applies while a condition holds (crit,
    /// vulnerable, positional, health-state, CC …) — full uptime is optimistic for these.</summary>
    private static readonly string[] SituationalMarkers =
    [
        "Crit_Damage", "Vulnerable", "Overpower", "_To_Near", "_To_Far", "High_Health",
        "Low_Health", "Vs_CC", "Vs_Elites", "When_Fortified", "While_", "Against_Dot",
        "Affected_By", "Weapon_Swapping", "Having_Shield",
    ];

    private static readonly string[] AdditivePrefixes = ["Damage_Percent_", "Damage_Bonus_", "DOT_DPS_"];

    private static readonly HashSet<string> AdditiveExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vulnerable_Health_Damage_Bonus", "Crit_Damage_Percent", "Crit_Damage_Percent_Per_Skill_Tag",
        "Overpower_Damage_Bonus_Per_Stack", "Imbued_Skill_Damage_Percent_Bonus",
        "NonPhysical_Damage_Percent_Bonus", "Damage_Type_Percent_Bonus", "Power_Damage_Percent_Bonus",
        "Warlock_Demonform_Damage_Bonus", "Warlock_Shadowform_Damage_Bonus",
        "Damage_Increase_While_Having_Shield", "Block_Damage_Percent_Bonus",
        "Paladin_Arbiter_WingStrike_Damage",
    };

    public static DamageBucket Classify(string attribute)
    {
        if (attribute.EndsWith("_Core", StringComparison.OrdinalIgnoreCase))
            return DamageBucket.MainStat;
        if (string.Equals(attribute, "Crit_Percent_Bonus", StringComparison.OrdinalIgnoreCase))
            return DamageBucket.CritChance;
        if (AdditiveExact.Contains(attribute)
            || AdditivePrefixes.Any(p => attribute.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return DamageBucket.AdditiveDamage;
        return DamageBucket.Other;
    }

    public static bool IsSituational(string attribute) =>
        SituationalMarkers.Any(m => attribute.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The additive bucket held in a build's paragon totals: the whole bucket and its situational
    /// slice, as native fractions. This is the board's OWN contribution — what a sheet-derived
    /// gear offset must subtract, since the in-game sheet aggregates paragon and gear together.
    /// </summary>
    public static (double Additive, double Situational) AdditiveSlices(
        IReadOnlyDictionary<string, double> totals)
    {
        double additive = 0, situational = 0;
        foreach (var (attribute, value) in totals)
        {
            if (Classify(attribute) != DamageBucket.AdditiveDamage)
                continue;
            additive += value;
            if (IsSituational(attribute))
                situational += value;
        }
        return (additive, situational);
    }

    /// <summary>
    /// The damage-formula breakdown of a build's paragon totals. <paramref name="totals"/> is
    /// <see cref="BuildStatsReport.Totals"/> (raw attribute keys, fractions for percents);
    /// <paramref name="mainStatOffset"/> is the non-paragon (level + gear) main stat. The
    /// expected multiplier assumes full uptime on situational additives (like the in-game sheet)
    /// and folds crit in at its expected value: 1 + 0.5 × crit chance (5% base + paragon bonus).
    /// The vulnerable base ×1.2 is left out — its uptime is unknowable here and it is identical
    /// across compared builds. <paramref name="additiveOffset"/> is the gear's ALWAYS-ON
    /// "+% damage" sum and <paramref name="situationalAdditiveOffset"/> its conditional sum
    /// (vulnerable/close/crit damage …), both user-supplied native fractions, so saturation
    /// starts from the character's REAL bucket; at 0 the bucket is a lower bound and "+%
    /// additive" marginals are upper bounds.
    /// </summary>
    public static DamageProfile Profile(
        IReadOnlyDictionary<string, double> totals,
        string? className,
        double mainStatOffset,
        IEnumerable<(ParagonGlyphDef Glyph, int Level)>? socketedGlyphs = null,
        double additiveOffset = 0,
        double situationalAdditiveOffset = 0)
    {
        var (paragonAdditive, paragonSituational) = AdditiveSlices(totals);
        double additive = additiveOffset + situationalAdditiveOffset + paragonAdditive;
        double situational = situationalAdditiveOffset + paragonSituational;
        string mainStat = MainStatAttribute(className) ?? "Strength_Core";
        double critChance = totals
            .Where(t => Classify(t.Key) == DamageBucket.CritChance)
            .Sum(t => t.Value);

        double mainStatTotal = totals.GetValueOrDefault(mainStat) + mainStatOffset;
        double coefficient = MainStatCoefficient(className);

        var multipliers = new List<GlyphMultiplier>();
        foreach (var (glyph, level) in socketedGlyphs ?? [])
        {
            if (GlyphInfo.LegendaryMultiplierPercentAt(glyph, level) is double percent
                && GlyphInfo.LegendaryAffix(glyph) is GlyphAffixDef affix)
                multipliers.Add(new GlyphMultiplier(glyph.Name ?? glyph.InternalName, GlyphInfo.TagsLabel(affix), percent));
        }

        // Everything past the additive bucket is common to both estimates.
        double shared = (1.0 + mainStatTotal / coefficient) * (1.0 + 0.5 * (0.05 + critChance));
        foreach (var m in multipliers)
            shared *= 1.0 + m.Percent / 100.0;

        return new DamageProfile(
            mainStat, mainStatTotal, coefficient, additive, situational,
            additiveOffset + situationalAdditiveOffset, critChance, multipliers,
            ExpectedMultiplier: (1.0 + additive) * shared,
            ExpectedMultiplierUnconditional: (1.0 + additive - situational) * shared);
    }
}
