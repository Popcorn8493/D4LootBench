using System.Text.Json;
using System.Text.Json.Serialization;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Data;

/// <summary>
/// Lazy singleton over paragon-data.json (embedded; a copy next to the .exe overrides it,
/// mirroring the d4-data.json convention).
/// </summary>
public static class ParagonDatabase
{
    private static readonly Lazy<ParagonData> _data = new(LoadData);
    private static readonly Lazy<IReadOnlyDictionary<string, ParagonNodeDef>> _nodesBySnoId =
        new(() => Data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyDictionary<string, ParagonThresholdDef>> _thresholdsBySnoId =
        new(() => Data.Thresholds.ToDictionary(t => t.SnoId, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyDictionary<string, ParagonBoardDef>> _boardsByInternalName =
        new(() => Data.Boards.ToDictionary(b => b.InternalName, StringComparer.OrdinalIgnoreCase));

    public static ParagonData Data => _data.Value;

    public static IReadOnlyDictionary<string, ParagonNodeDef> NodesBySnoId => _nodesBySnoId.Value;

    public static IReadOnlyDictionary<string, ParagonThresholdDef> ThresholdsBySnoId => _thresholdsBySnoId.Value;

    public static IReadOnlyDictionary<string, ParagonBoardDef> BoardsByInternalName => _boardsByInternalName.Value;

    public static IEnumerable<ParagonBoardDef> BoardsForClass(string className) =>
        Data.Boards.Where(b => string.Equals(b.ClassName, className, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<ParagonGlyphDef> GlyphsForClass(string className) =>
        Data.Glyphs.Where(g => g.Classes.Contains(className, StringComparer.OrdinalIgnoreCase));

    private static ParagonData LoadData()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<ParagonData>(ParagonDataStore.LoadText(), options)
            ?? throw new InvalidDataException("paragon-data.json deserialized to null.");
    }
}
