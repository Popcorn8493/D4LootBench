using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>A glyph sitting in a purchased socket, for node-buff computation.</summary>
public sealed record SocketedGlyph(CellRef Socket, ParagonGlyphDef Glyph, int Level);

/// <summary>
/// The "+X% bonuses to [Normal/Magic/Rare] nodes within range" side of glyphs
/// (<c>Nodes_BonusTo*</c> affixes). Rarity codes are 1=Normal, 2=Magic, 3=Rare, and the
/// in-game percent is the data scalar ÷ 10 — calibrated against live values for Challenger
/// (20% at level 1 → 100% at 100 vs scalar 200+8.085/lvl) and Marshal (25% → 272.5% vs
/// 250+25/lvl). Buffs from overlapping glyph radii stack additively.
/// </summary>
public static class GlyphNodeBuffs
{
    public static ParagonNodeKind? KindForRarityCode(int code) => code switch
    {
        1 => ParagonNodeKind.Normal,
        2 => ParagonNodeKind.Magic,
        3 => ParagonNodeKind.Rare,
        _ => null,
    };

    /// <summary>The glyph's node buffs at a level, as in-game percentages (e.g. 137.5).</summary>
    public static IEnumerable<(ParagonNodeKind Kind, double Percent)> BuffsAt(ParagonGlyphDef glyph, int level)
    {
        foreach (var affix in glyph.Affixes)
        {
            if (affix.AffectedNodeRarity is not int code || KindForRarityCode(code) is not ParagonNodeKind kind
                || affix.StartingBonusScalar is not double start)
                continue;
            double scalar = start + (affix.AddedBonusScalarPerLevel ?? 0) * Math.Max(0, level - 1);
            yield return (kind, scalar / 10.0);
        }
    }

    /// <summary>
    /// Effective per-cell stat multipliers (cells not present are ×1): each socketed glyph
    /// multiplies matching-kind nodes within its Manhattan radius on its own board.
    /// </summary>
    public static IReadOnlyDictionary<CellRef, double> MultipliersFor(
        ComposedGraph graph, IEnumerable<SocketedGlyph> sockets)
    {
        var bonusPercent = new Dictionary<CellRef, double>();
        foreach (var socketed in sockets)
        {
            var buffs = BuffsAt(socketed.Glyph, socketed.Level).ToList();
            if (buffs.Count == 0)
                continue;
            int radius = GlyphRadius.RadiusForLevel(socketed.Level);
            foreach (var vertex in graph.Vertices)
            {
                var cell = vertex.Cell;
                if (cell.BoardSlot != socketed.Socket.BoardSlot || cell == socketed.Socket)
                    continue;
                if (Math.Abs(cell.X - socketed.Socket.X) + Math.Abs(cell.Y - socketed.Socket.Y) > radius)
                    continue;
                foreach (var (kind, percent) in buffs)
                {
                    if (vertex.Node.Kind == kind)
                        bonusPercent[cell] = bonusPercent.GetValueOrDefault(cell) + percent;
                }
            }
        }
        return bonusPercent.ToDictionary(kv => kv.Key, kv => 1 + kv.Value / 100.0);
    }
}
