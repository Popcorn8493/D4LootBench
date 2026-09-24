namespace D4LootBench.Paragon.Models;

/// <summary>Root of paragon-data.json — see docs/paragon-data-format.md.</summary>
public sealed class ParagonData
{
    public int FormatVersion { get; init; }
    public string Source { get; init; } = "";
    public string? Notes { get; init; }

    /// <summary>
    /// Empirically calibrated values for the engine's built-in
    /// ParagonPowerBudgetMultiplierNode* functions, as fractions (0.05 = 5%).
    /// </summary>
    public IReadOnlyDictionary<string, double> Multipliers { get; init; } =
        new Dictionary<string, double>();

    public IReadOnlyList<ParagonBoardDef> Boards { get; init; } = [];
    public IReadOnlyList<ParagonNodeDef> Nodes { get; init; } = [];
    public IReadOnlyList<ParagonGlyphDef> Glyphs { get; init; } = [];
    public IReadOnlyList<ParagonThresholdDef> Thresholds { get; init; } = [];
}

/// <summary>A 21×21 paragon board; nodes are sparse placements into the grid.</summary>
public sealed class ParagonBoardDef
{
    public string SnoId { get; init; } = "";
    public string InternalName { get; init; } = "";
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public int? BoardIndex { get; init; }
    public int Width { get; init; }
    public long ContentLicense { get; init; }
    public IReadOnlyList<NodePlacement> Nodes { get; init; } = [];
}

public sealed class NodePlacement
{
    public int X { get; init; }
    public int Y { get; init; }

    /// <summary>SnoId of the <see cref="ParagonNodeDef"/> occupying this cell.</summary>
    public string Node { get; init; } = "";
}

public enum ParagonNodeKind
{
    Normal,
    Magic,
    Rare,
    Legendary,
    GlyphSocket,
    Gate,
    Start,
}

/// <summary>A node archetype; boards reference these by SnoId (the same node can appear on many boards).</summary>
public sealed class ParagonNodeDef
{
    public string SnoId { get; init; } = "";
    public string InternalName { get; init; } = "";

    /// <summary>Display name; null for generic magic nodes, which the game names from their attribute.</summary>
    public string? Name { get; init; }

    public ParagonNodeKind Kind { get; init; }
    public IReadOnlyList<NodeAttribute> Attributes { get; init; } = [];

    /// <summary>SnoIds of <see cref="ParagonThresholdDef"/> entries gating the bonus attributes.</summary>
    public IReadOnlyList<string> Thresholds { get; init; } = [];

    public NodePower? Power { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class NodeAttribute
{
    public string Attribute { get; init; } = "";

    /// <summary>Attribute parameter (damage type, resource type, skill tag…); null when unparameterized.</summary>
    public int? Param { get; init; }

    public string? FormulaName { get; init; }
    public string? Formula { get; init; }

    /// <summary>Resolved magnitude: a fraction for percent attributes (0.05 = 5%), flat for stat attributes.</summary>
    public double? Value { get; init; }

    /// <summary>True when this attribute is only granted once the node's threshold requirement is met.</summary>
    public bool IsThresholdBonus { get; init; }
}

public sealed class NodePower
{
    public string SnoId { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>The in-game tooltip, rendered from the power's script formulas at extraction;
    /// values that depend on the live character (e.g. "Current Bonus") show as "?".</summary>
    public string? Description { get; init; }

    /// <summary>The power's script-formula values SF_0..SF_n (null where a value depends on
    /// attributes or engine functions not in the data).</summary>
    public IReadOnlyList<double?> Values { get; init; } = [];

    /// <summary>Every multiplicative "[x]" percent in the tooltip, in order (45 = 45%[x]).</summary>
    public IReadOnlyList<double> MultiplierPercents { get; init; } = [];
}

public sealed class ParagonGlyphDef
{
    public string SnoId { get; init; } = "";
    public string InternalName { get; init; } = "";
    public string? Name { get; init; }
    public int Rarity { get; init; }
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<GlyphAffixDef> Affixes { get; init; } = [];
}

public sealed class GlyphAffixDef
{
    public string SnoId { get; init; } = "";
    public string? InternalName { get; init; }
    public int? AffectedNodeRarity { get; init; }
    public int? RequiredRarity { get; init; }
    public int? BonusOperation { get; init; }
    public IReadOnlyList<GlyphAttributeMap> AttributeMaps { get; init; } = [];

    /// <summary>Bonus magnitude at glyph level 1.</summary>
    public double? StartingBonusScalar { get; init; }

    /// <summary>Bonus magnitude added per glyph level above 1.</summary>
    public double? AddedBonusScalarPerLevel { get; init; }

    public string? BudgetFormulaName { get; init; }
    public NodePower? BonusPower { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class GlyphAttributeMap
{
    public string DestinationAttribute { get; init; } = "";
    public int? DestinationParam { get; init; }
    public string SourceAttribute { get; init; } = "";
    public int? SourceParam { get; init; }
}

/// <summary>An attribute requirement gating a rare node's bonus; scales with board attachment order.</summary>
public sealed class ParagonThresholdDef
{
    public string SnoId { get; init; } = "";
    public string InternalName { get; init; } = "";
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<ThresholdRequirement> Requirements { get; init; } = [];
}

public sealed class ThresholdRequirement
{
    public string Attribute { get; init; } = "";
    public string Formula { get; init; } = "";

    /// <summary>Requirement value by 0-based board attachment index; null entries were unresolvable.</summary>
    public IReadOnlyList<double?> ValuesByBoardIndex { get; init; } = [];
}
