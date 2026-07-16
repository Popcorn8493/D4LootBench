using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon;

/// <summary>Semantic helpers over glyph definitions.</summary>
public static class GlyphInfo
{
    private static readonly string[] CoreStats = ["Strength", "Intelligence", "Willpower", "Dexterity"];

    /// <summary>
    /// The attribute the glyph's radius bonus scales with (e.g. "Intelligence_Core"). Read from
    /// the scaling affix's attribute map when present; glyphs whose bonus targets node rarities
    /// instead (no attribute maps) fall back to the core stat in the internal name or affix tags,
    /// which is the stat their activation requirement counts. Null when no stat can be inferred.
    /// </summary>
    public static string? PrimarySourceAttribute(ParagonGlyphDef glyph)
    {
        foreach (var affix in glyph.Affixes)
        {
            foreach (var map in affix.AttributeMaps)
            {
                if (!string.IsNullOrEmpty(map.SourceAttribute))
                    return map.SourceAttribute;
            }
        }

        foreach (var stat in CoreStats)
        {
            if (glyph.InternalName.Contains(stat, StringComparison.OrdinalIgnoreCase))
                return stat + "_Core";
        }

        foreach (var stat in CoreStats)
        {
            if (glyph.Affixes.Any(a => a.Tags.Contains(stat, StringComparer.OrdinalIgnoreCase)))
                return stat + "_Core";
        }

        return null;
    }

    /// <summary>The affix whose bonus scales with the in-radius stat (the one carrying a scalar).</summary>
    public static GlyphAffixDef? ScalingAffix(ParagonGlyphDef glyph) =>
        glyph.Affixes.FirstOrDefault(a => a.AttributeMaps.Count > 0 && a.StartingBonusScalar is not null)
        ?? glyph.Affixes.FirstOrDefault(a => a.StartingBonusScalar is not null);

    /// <summary>Bonus magnitude per point of in-radius source stat, at the given glyph level.</summary>
    public static double? BonusScalarAt(ParagonGlyphDef glyph, int level)
    {
        var affix = ScalingAffix(glyph);
        if (affix?.StartingBonusScalar is not double start)
            return null;
        return start + (affix.AddedBonusScalarPerLevel ?? 0) * Math.Max(0, level - 1);
    }

    /// <summary>True when the glyph's bonus scales with the stat in radius (an attribute map).</summary>
    public static bool IsAttributeMapped(ParagonGlyphDef glyph) =>
        ScalingAffix(glyph)?.AttributeMaps.Count > 0;

    /// <summary>
    /// A relative benefit score for the glyph: attribute-mapped glyphs deliver scalar × stat in
    /// radius (more source stat means more converted output); node-rarity-bonus glyphs deliver
    /// their scalar flat (the in-radius stat only gates activation). The game's exact bonus units
    /// aren't in the data — use this to rank, not to promise a sheet number.
    /// </summary>
    public static double? DeliveredBonus(ParagonGlyphDef glyph, int level, double statInRadius) =>
        BonusScalarAt(glyph, level) is not double scalar
            ? null
            : IsAttributeMapped(glyph) ? scalar * statInRadius : scalar;

    /// <summary>
    /// Raw destination attribute of the scaling conversion (e.g. "Vulnerable_Health_Damage_Bonus").
    /// Shares the board-node attribute namespace, so focus weights keyed by node attributes apply
    /// directly. Null for rarity-bonus glyphs.
    /// </summary>
    public static string? DestinationAttribute(ParagonGlyphDef glyph) =>
        ScalingAffix(glyph)?.AttributeMaps.FirstOrDefault() is { DestinationAttribute.Length: > 0 } map
            ? map.DestinationAttribute
            : null;

    /// <summary>
    /// Glyph level at which a glyph can be upgraded to legendary rank, unlocking its
    /// multiplicative bonus. Engine-side, not in paragon-data.json — re-check after game patches.
    /// </summary>
    public const int LegendaryUpgradeLevel = 46;

    /// <summary>The multiplicative bonus affix unlocked at legendary rank (requiredRarity 2).</summary>
    public static GlyphAffixDef? LegendaryAffix(ParagonGlyphDef glyph) =>
        glyph.Affixes.FirstOrDefault(a => a.RequiredRarity == 2 && a.StartingBonusScalar is > 0);

    /// <summary>
    /// The legendary-rank multiplicative bonus as an in-game percent (e.g. 5.0), or null below
    /// <see cref="LegendaryUpgradeLevel"/> or when the glyph has no legendary affix. The scalar is
    /// read as a fraction ×100 (Exploit: 0.005 + 0.001/lvl → 5% at 46) — consistent with live
    /// values, but like the node-buff calibration it should be re-validated after balance patches.
    /// </summary>
    public static double? LegendaryMultiplierPercentAt(ParagonGlyphDef glyph, int level)
    {
        if (level < LegendaryUpgradeLevel || LegendaryAffix(glyph) is not { StartingBonusScalar: double start } affix)
            return null;
        return (start + (affix.AddedBonusScalarPerLevel ?? 0) * Math.Max(0, level - 1)) * 100;
    }

    /// <summary>
    /// The flat "Additional Bonus" power granted while the glyph is activated (a bonusPower affix).
    /// Only its identity is in the data — the magnitude lives in the game's power definition.
    /// </summary>
    public static GlyphAffixDef? AdditionalBonusAffix(ParagonGlyphDef glyph) =>
        glyph.Affixes.FirstOrDefault(a => a.BonusPower is not null);

    /// <summary>Readable hint of what an affix touches, from its tags ("Vulnerable, Damage").</summary>
    public static string TagsLabel(GlyphAffixDef affix) =>
        string.Join(", ", affix.Tags.Select(t =>
            t.StartsWith("Keyword_", StringComparison.OrdinalIgnoreCase) ? t[8..] : t));

    /// <summary>Display name of what the bonus lands on: a mapped attribute, or the boosted node rarity.</summary>
    public static string DeliveryTarget(ParagonGlyphDef glyph)
    {
        var affix = ScalingAffix(glyph);
        var map = affix?.AttributeMaps.FirstOrDefault();
        if (map is not null && !string.IsNullOrEmpty(map.DestinationAttribute))
            return ParagonDisplay.FormatAttributeName(map.DestinationAttribute);
        return affix?.AffectedNodeRarity switch
        {
            1 => "Normal node bonuses in radius",
            2 => "Magic node bonuses in radius",
            3 => "Rare node bonuses in radius",
            _ => "node bonuses in radius",
        };
    }
}
