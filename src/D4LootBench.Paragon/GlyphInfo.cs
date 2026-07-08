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

    /// <summary>Display name of what the bonus lands on: a mapped attribute, or the boosted node rarity.</summary>
    public static string DeliveryTarget(ParagonGlyphDef glyph)
    {
        var affix = ScalingAffix(glyph);
        var map = affix?.AttributeMaps.FirstOrDefault();
        if (map is not null && !string.IsNullOrEmpty(map.DestinationAttribute))
            return ParagonDisplay.FormatAttributeName(map.DestinationAttribute);
        return affix?.AffectedNodeRarity switch
        {
            1 => "Magic node bonuses in radius",
            2 => "Rare node bonuses in radius",
            _ => "node bonuses in radius",
        };
    }
}
