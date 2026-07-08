using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>Distance metric for a glyph's area of effect around its socket.</summary>
public enum RadiusMetric
{
    /// <summary>Square area: max(|dx|, |dy|) ≤ radius.</summary>
    Chebyshev,

    /// <summary>Diamond area: |dx| + |dy| ≤ radius.</summary>
    Manhattan,
}

/// <summary>Aggregates what a glyph socketed at a given cell would "see" in its radius.</summary>
public static class GlyphRadius
{
    /// <summary>The game's radius area is a Manhattan diamond (rendered as a square in-game because boards draw rotated 45°).</summary>
    public const RadiusMetric GameMetric = RadiusMetric.Manhattan;

    /// <summary>Radius by glyph level (Season 14): 3 base, 4 at level 25, 5 at level 50. Max level 150.</summary>
    public static int RadiusForLevel(int glyphLevel) =>
        3 + (glyphLevel >= 25 ? 1 : 0) + (glyphLevel >= 50 ? 1 : 0);

    /// <summary>
    /// Sums attribute values of purchased nodes within the radius around a socket cell
    /// (same board only — glyph radii do not cross board gates). Keys are attribute enum
    /// names (e.g. "Strength_Core"); threshold-gated bonus attributes are excluded.
    /// </summary>
    public static IReadOnlyDictionary<string, double> AttributeTotalsInRange(
        ComposedGraph graph,
        CellRef socket,
        IReadOnlyCollection<CellRef> purchased,
        int radius,
        RadiusMetric metric)
    {
        var totals = new Dictionary<string, double>();
        foreach (var cell in purchased)
        {
            if (cell.BoardSlot != socket.BoardSlot || cell == socket)
                continue;
            int dx = Math.Abs(cell.X - socket.X);
            int dy = Math.Abs(cell.Y - socket.Y);
            int distance = metric == RadiusMetric.Chebyshev ? Math.Max(dx, dy) : dx + dy;
            if (distance > radius)
                continue;
            if (!graph.TryGetVertex(cell, out int vertex))
                continue;

            foreach (var attribute in graph.Vertices[vertex].Node.Attributes)
            {
                if (attribute.IsThresholdBonus || attribute.Value is not double value)
                    continue;
                totals[attribute.Attribute] = totals.GetValueOrDefault(attribute.Attribute) + value;
            }
        }
        return totals;
    }
}
