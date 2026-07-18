using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.Paragon.Import;

/// <summary>
/// Imports paragon builds from a Mobalytics build-guide page (fetched or saved HTML). The page
/// embeds one plain-JSON paragon object per build variant:
/// <c>{"nodes":[{"slug":…}],"boards":[{x,y,rotation,board:{slug},glyph:{slug},glyphLevel}]}</c>.
/// Conventions, empirically calibrated against live builds for three classes:
/// node slugs are <c>&lt;board-slug&gt;-x&lt;col+1&gt;-y&lt;row+1&gt;</c> in the board's UNROTATED
/// grid (1-based); board-grid axes are swapped relative to Maxroll (our (x, y) = their (y, x));
/// <c>rotation</c> is clockwise degrees and may exceed 360; the node list omits start, gate, and
/// glyph-socket cells, which the game grants implicitly.
/// </summary>
public static partial class MobalyticsParagonImporter
{
    /// <summary>
    /// Finds every embedded paragon section in the page. Variants with identical paragon data are
    /// merged (their titles joined), so a page whose five variants share one board setup yields a
    /// single entry.
    /// </summary>
    public static IReadOnlyList<MobalyticsParagonVariant> ExtractVariants(string html)
    {
        const string marker = "\"paragon\":{\"nodes\":[";
        var sections = new List<(string RawJson, MobalyticsParagonSection Section)>();
        int from = 0;
        while (true)
        {
            int at = html.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            from = at + marker.Length;

            string raw = ExtractJsonObject(html, at + "\"paragon\":".Length);
            MobalyticsParagonSection? section;
            try
            {
                section = JsonSerializer.Deserialize<MobalyticsParagonSection>(raw, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // an unrelated "paragon" key elsewhere in the page
            }
            if (section is { Boards.Count: > 0 })
                sections.Add((raw, section));
        }
        if (sections.Count == 0)
            throw new FormatException("No Mobalytics paragon data found in the page.");

        // Variant titles appear in the page's content-variants widget in the same order as the
        // per-variant data sections; pair them only when the counts agree.
        var titles = VariantTitlePattern().Matches(html)
            .Select(m => JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"") ?? "")
            .ToList();
        bool paired = titles.Count == sections.Count;

        var variants = new List<MobalyticsParagonVariant>();
        var indexByRawJson = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sections.Count; i++)
        {
            string title = paired ? titles[i] : $"Variant {i + 1}";
            if (indexByRawJson.TryGetValue(sections[i].RawJson, out int existing))
                variants[existing] = variants[existing] with { Title = $"{variants[existing].Title} / {title}" };
            else
            {
                indexByRawJson[sections[i].RawJson] = variants.Count;
                variants.Add(new MobalyticsParagonVariant(title, sections[i].Section));
            }
        }
        return variants;
    }

    /// <summary>
    /// Converts a variant into a layout plus allocation, resolving Mobalytics slugs against the
    /// paragon database. Start, gate, and glyph-socket cells the build implicitly traverses are
    /// added to the allocation (Mobalytics omits them; Maxroll-style allocations include them).
    /// </summary>
    public static ConvertedMaxrollBuild ToBuild(MobalyticsParagonVariant variant, ParagonData data)
    {
        var build = MaxrollParagonCodec.ToLayout(
            ToMaxrollEntries(variant, data),
            data.Boards
                .Where(b => !string.IsNullOrEmpty(b.InternalName))
                .ToDictionary(b => b.InternalName, StringComparer.OrdinalIgnoreCase));
        return ImplicitCellAugmenter.Augment(build, data);
    }

    /// <summary>Translates the variant into Maxroll board entries (the codec's neutral form).</summary>
    public static IReadOnlyList<MaxrollBoardEntry> ToMaxrollEntries(MobalyticsParagonVariant variant, ParagonData data)
    {
        var boardsBySlug = BuildBoardSlugMap(data);
        var glyphsBySlug = BuildGlyphSlugMap(data);
        var section = variant.Section;

        // Group picked node coordinates by their board-slug prefix.
        var coordsByInternalName = new Dictionary<string, List<(int Col, int Row)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in section.Nodes)
        {
            var match = NodeSlugPattern().Match(node.Slug);
            if (!match.Success)
                throw new FormatException($"Unrecognized Mobalytics node slug '{node.Slug}'.");
            string prefix = match.Groups[1].Value;
            if (!boardsBySlug.TryGetValue(prefix, out var board))
                throw new FormatException($"Unknown Mobalytics board slug '{prefix}'.");

            int col = int.Parse(match.Groups[2].Value) - 1;
            int row = int.Parse(match.Groups[3].Value) - 1;
            if (col < 0 || col >= board.Width || row < 0 || row >= board.Width)
                throw new FormatException($"Node '{node.Slug}' is outside the {board.Width}×{board.Width} grid.");

            if (!coordsByInternalName.TryGetValue(board.InternalName, out var list))
                coordsByInternalName[board.InternalName] = list = [];
            list.Add((col, row));
        }

        var entries = new List<MaxrollBoardEntry>();
        foreach (var entry in section.Boards)
        {
            if (entry.Board is null || !boardsBySlug.TryGetValue(entry.Board.Slug, out var board))
                throw new FormatException($"Unknown Mobalytics board slug '{entry.Board?.Slug}'.");
            if (entry.Rotation % 90 != 0)
                throw new FormatException($"Board '{entry.Board.Slug}' has a non-quarter rotation of {entry.Rotation}°.");

            string? glyphInternalName = null;
            if (entry.Glyph is { Slug.Length: > 0 })
            {
                if (!glyphsBySlug.TryGetValue(entry.Glyph.Slug, out var glyph))
                    throw new FormatException($"Unknown Mobalytics glyph slug '{entry.Glyph.Slug}'.");
                glyphInternalName = glyph.InternalName;
            }

            var nodes = new Dictionary<string, int>();
            if (coordsByInternalName.TryGetValue(board.InternalName, out var coords))
            {
                foreach (var (col, row) in coords)
                    nodes[(row * board.Width + col).ToString()] = 1;
            }

            entries.Add(new MaxrollBoardEntry
            {
                Id = board.InternalName,
                Nodes = nodes,
                Rotation = ((entry.Rotation / 90) % 4 + 4) % 4,
                Position = new MaxrollPosition { X = entry.Y, Y = entry.X }, // axes swapped vs Maxroll
                Glyph = glyphInternalName,
                GlyphLevel = entry.GlyphLevel,
            });
        }
        return entries;
    }

    /// <summary>
    /// Board lookup by Mobalytics slug: <c>kebab(class)-kebab(name)</c>, plus the starter board's
    /// two site-specific aliases (<c>-starter-board</c> in board entries, <c>-starting-board</c>
    /// in node slugs).
    /// </summary>
    private static Dictionary<string, ParagonBoardDef> BuildBoardSlugMap(ParagonData data)
    {
        var map = new Dictionary<string, ParagonBoardDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in data.Boards)
        {
            if (board.Name is null || board.ClassName is null)
                continue;
            string cls = Kebab(board.ClassName);
            map.TryAdd($"{cls}-{Kebab(board.Name)}", board);
            if (board.BoardIndex == 0)
            {
                map.TryAdd($"{cls}-starter-board", board);
                map.TryAdd($"{cls}-starting-board", board);
            }
        }
        return map;
    }

    private static Dictionary<string, ParagonGlyphDef> BuildGlyphSlugMap(ParagonData data)
    {
        var map = new Dictionary<string, ParagonGlyphDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var glyph in data.Glyphs)
        {
            if (glyph.Name is null)
                continue;
            foreach (var cls in glyph.Classes)
                map.TryAdd($"{Kebab(cls)}-{Kebab(glyph.Name)}", glyph);
        }
        return map;
    }

    private static string Kebab(string text) =>
        KebabPattern().Replace(text.Replace("'", "").Replace("’", "").ToLowerInvariant(), "-").Trim('-');

    /// <summary>Returns the balanced JSON object starting at <paramref name="start"/> ('{').</summary>
    private static string ExtractJsonObject(string text, int start)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)
                escaped = false;
            else if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"')
                inString = true;
            else if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return text[start..(i + 1)];
        }
        throw new FormatException("Unterminated paragon JSON object in the page.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex(@"^(.+)-x(\d+)-y(\d+)$")]
    private static partial Regex NodeSlugPattern();

    [GeneratedRegex(@"\{""id"":""[^""]+"",""title"":""((?:[^""\\]|\\.)*)"",""description""")]
    private static partial Regex VariantTitlePattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex KebabPattern();
}

/// <summary>One build variant's paragon section, titled from the page's variant list.</summary>
public sealed record MobalyticsParagonVariant(string Title, MobalyticsParagonSection Section);

public sealed class MobalyticsParagonSection
{
    [JsonPropertyName("nodes")]
    public List<MobalyticsNodeRef> Nodes { get; set; } = [];

    [JsonPropertyName("boards")]
    public List<MobalyticsBoardRef> Boards { get; set; } = [];
}

public sealed class MobalyticsNodeRef
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";
}

public sealed class MobalyticsBoardRef
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    /// <summary>Clockwise degrees; live data contains values beyond 360 (e.g. 540).</summary>
    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    [JsonPropertyName("board")]
    public MobalyticsSlugRef? Board { get; set; }

    [JsonPropertyName("glyph")]
    public MobalyticsSlugRef? Glyph { get; set; }

    [JsonPropertyName("glyphLevel")]
    public int? GlyphLevel { get; set; }
}

public sealed class MobalyticsSlugRef
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";
}
