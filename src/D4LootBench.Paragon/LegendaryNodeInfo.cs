using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon;

/// <summary>
/// Legendary node powers as extracted from the game's power definitions (tooltip values rendered
/// from the power's script formulas). Their "[x]" values are MULTIPLICATIVE damage bonuses, but
/// almost always conditional — tied to a skill, a status, or a situation — so the damage model
/// reports them beside the always-on/full-uptime range rather than folding them in.
/// </summary>
public static class LegendaryNodeInfo
{
    /// <summary>The node's headline "×%" (the first [x] value in its tooltip), or null.</summary>
    public static double? HeadlineMultiplierPercent(ParagonNodeDef node) =>
        node.Kind == ParagonNodeKind.Legendary && node.Power?.MultiplierPercents is { Count: > 0 } percents
            ? percents[0]
            : null;

    /// <summary>∏(1 + headline ×%) over the given legendary nodes — the ceiling if every
    /// node's condition held at once.</summary>
    public static double HeadlineProduct(IEnumerable<ParagonNodeDef> nodes) =>
        nodes.Select(HeadlineMultiplierPercent)
            .Where(p => p is not null)
            .Aggregate(1.0, (product, p) => product * (1 + p!.Value / 100));
}
