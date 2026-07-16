using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The placement judge's score mirrors the maximizer's valuation: source stat purchased inside
/// an attribute-mapped glyph's radius counts again at the delivery weight, so candidates that
/// feed glyphs beyond bare activation are credited for it.
/// </summary>
public class FocusScoreTests
{
    private static ComposedGraph DexPair() => SyntheticBoards.Graph(["aSb"], new()
    {
        ['a'] = SyntheticBoards.Node("a", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
        ['b'] = SyntheticBoards.Node("b", ParagonNodeKind.Magic, ("Dexterity_Core", 5)),
    });

    [Fact]
    public void Source_stat_inside_a_delivery_radius_scores_higher_than_outside()
    {
        var graph = DexPair();
        // Identical nodes; the delivery only covers 'b'.
        var focus = new MaximizeFocus(["Dexterity_Core"], false)
        {
            GlyphDeliveries = [new GlyphDelivery(new CellRef(0, 2, 0), "Dexterity_Core", 0, 2.0)],
        };

        double outside = FocusScore.Of(graph, new HashSet<CellRef> { new(0, 0, 0) }, focus);
        double inside = FocusScore.Of(graph, new HashSet<CellRef> { new(0, 2, 0) }, focus);

        inside.ShouldBeGreaterThan(outside);
        inside.ShouldBe(outside * 3, 0.001); // base 1 + delivery weight 2
    }

    [Fact]
    public void Unfocused_source_stat_still_earns_the_delivery_term()
    {
        var graph = DexPair();
        // Focus is Strength, but the glyph converts Dexterity — buying its radius must count.
        var delivering = new MaximizeFocus(["Strength_Core"], false)
        {
            GlyphDeliveries = [new GlyphDelivery(new CellRef(0, 0, 0), "Dexterity_Core", 0, 1.0)],
        };
        var bare = new MaximizeFocus(["Strength_Core"], false) { GlyphDeliveries = [] };
        var purchased = new HashSet<CellRef> { new(0, 0, 0) };

        FocusScore.Of(graph, purchased, bare).ShouldBe(0);
        FocusScore.Of(graph, purchased, delivering).ShouldBe(1.0, 0.001);
    }
}
