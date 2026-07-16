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
    /// <paramref name="destinationWeights"/> (the focus's per-stat weights) then scales each
    /// delivery by what the glyph converts INTO — a glyph feeding a focused stat pulls harder,
    /// on the same High/Normal/Low = 3/1/0.3 scale the spend uses; unfocused destinations stay
    /// at 1, so relevance only ever boosts or damps relative pull, never mutes a glyph outright.
    /// </summary>
    public static IReadOnlyList<GlyphDelivery> For(
        IEnumerable<(CellRef Socket, ParagonGlyphDef Glyph, int Level)> sockets,
        IReadOnlyDictionary<string, double>? destinationWeights = null)
    {
        var mapped = sockets
            .Where(s => GlyphInfo.IsAttributeMapped(s.Glyph))
            .Select(s => (s.Socket, s.Level,
                Source: GlyphInfo.PrimarySourceAttribute(s.Glyph),
                Scalar: GlyphInfo.BonusScalarAt(s.Glyph, s.Level),
                Destination: GlyphInfo.DestinationAttribute(s.Glyph)))
            .Where(s => s.Source is not null && s.Scalar is > 0)
            .ToList();
        if (mapped.Count == 0)
            return [];
        double mean = mapped.Average(m => m.Scalar!.Value);
        return mapped
            .Select(m => new GlyphDelivery(
                m.Socket, m.Source!, GlyphRadius.RadiusForLevel(m.Level),
                m.Scalar!.Value / mean * RelevanceOf(m.Destination, destinationWeights)))
            .ToList();
    }

    private static double RelevanceOf(string? destination, IReadOnlyDictionary<string, double>? weights) =>
        destination is not null && weights?.TryGetValue(destination, out double weight) == true ? weight : 1.0;
}
