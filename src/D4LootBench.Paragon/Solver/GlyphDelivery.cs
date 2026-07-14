using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// An attribute-mapped glyph's pull on the leftover-point spend: source stat purchased within
/// <see cref="Radius"/> of <see cref="Socket"/> is delivered again as glyph output, scaled by
/// <see cref="Weight"/> (the glyph's bonus scalar relative to its peers; 1 = average glyph).
/// </summary>
public sealed record GlyphDelivery(CellRef Socket, string SourceAttribute, int Radius, double Weight)
{
    /// <summary>
    /// Deliveries for the attribute-mapped glyphs among <paramref name="sockets"/>. Rarity-bonus
    /// glyphs deliver a flat scalar — extra in-radius stat buys nothing beyond activation — so
    /// they exert no pull; their node buffs enter valuation as cell multipliers instead.
    /// Weights normalize each glyph's <see cref="GlyphInfo.BonusScalarAt"/> against the group
    /// mean, so a stronger glyph's radius attracts points harder than a weaker one's.
    /// </summary>
    public static IReadOnlyList<GlyphDelivery> For(
        IEnumerable<(CellRef Socket, ParagonGlyphDef Glyph, int Level)> sockets)
    {
        var mapped = sockets
            .Where(s => GlyphInfo.IsAttributeMapped(s.Glyph))
            .Select(s => (s.Socket, s.Level,
                Source: GlyphInfo.PrimarySourceAttribute(s.Glyph),
                Scalar: GlyphInfo.BonusScalarAt(s.Glyph, s.Level)))
            .Where(s => s.Source is not null && s.Scalar is > 0)
            .ToList();
        if (mapped.Count == 0)
            return [];
        double mean = mapped.Average(m => m.Scalar!.Value);
        return mapped
            .Select(m => new GlyphDelivery(
                m.Socket, m.Source!, GlyphRadius.RadiusForLevel(m.Level), m.Scalar!.Value / mean))
            .ToList();
    }
}
