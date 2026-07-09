using System.Text.Json;
using System.Text.Json.Serialization;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.Paragon.Serialization;

public sealed record ParagonProjectBoard(
    string BoardInternalName, int? ParentSlot, BoardEdge? AttachEdge, int RotationSteps);

public sealed record ParagonProjectGlyph(
    int BoardSlot, string GlyphInternalName, int Level, double RequiredStat, bool EnsureActive);

public sealed record ParagonProjectRule(string GroupKey, NodeRuleMode Mode, int Limit);

/// <summary>
/// A saved paragon planner session: layout, targets, per-cell constraints, the solved purchase
/// set, glyph assignments, node rules, and the planner settings. Boards and glyphs are stored
/// by internal name so a project survives data updates (unknown names fail at load with a
/// clear message, not at parse).
/// </summary>
public sealed record ParagonProject
{
    public int Version { get; init; } = 1;
    public required string ClassName { get; init; }
    public required IReadOnlyList<ParagonProjectBoard> Boards { get; init; }
    public IReadOnlyList<CellRef> Targets { get; init; } = [];
    public IReadOnlyList<CellRef> AvoidCells { get; init; } = [];
    public IReadOnlyList<CellRef> ExcludeCells { get; init; } = [];
    public IReadOnlyList<CellRef> PurchasedCells { get; init; } = [];
    public IReadOnlyList<ParagonProjectGlyph> Glyphs { get; init; } = [];
    public IReadOnlyList<ParagonProjectRule> NodeRules { get; init; } = [];
    public IReadOnlyList<string> FocusStats { get; init; } = [];

    /// <summary>Per-stat priority weights for the maximizer (attribute → weight, missing = 1).</summary>
    public IReadOnlyDictionary<string, double> FocusWeights { get; init; } =
        new Dictionary<string, double>();

    public bool PreferRareNodes { get; init; }

    /// <summary>Buy rares by threshold attainability first (see MaximizeFocus.RealisticRares).</summary>
    public bool RealisticRares { get; init; }

    public bool ActivateThresholds { get; init; }
    public int TotalPoints { get; init; }
    public double SheetStrength { get; init; }
    public double SheetIntelligence { get; init; }
    public double SheetWillpower { get; init; }
    public double SheetDexterity { get; init; }
}

public static class ParagonProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(ParagonProject project) =>
        JsonSerializer.Serialize(project, Options);

    /// <summary>Parses a saved project; throws <see cref="FormatException"/> on anything invalid.</summary>
    public static ParagonProject FromJson(string json)
    {
        ParagonProject? project;
        try
        {
            project = JsonSerializer.Deserialize<ParagonProject>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a valid paragon project file: {ex.Message}", ex);
        }
        if (project is null || string.IsNullOrWhiteSpace(project.ClassName) || project.Boards.Count == 0)
            throw new FormatException("Not a valid paragon project file: class or boards missing.");
        if (project.Version > 1)
            throw new FormatException($"This project was saved by a newer version (format {project.Version}).");
        return project;
    }
}
