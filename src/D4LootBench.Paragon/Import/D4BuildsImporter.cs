using System.Text.Json;
using System.Text.RegularExpressions;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Import;

/// <summary>
/// Imports paragon builds from d4builds.gg. Build pages are a client-rendered SPA with no build
/// data in the HTML — the data lives in a public Firebase Firestore document (<c>builds/&lt;uuid&gt;</c>,
/// the uuid from the page URL), fetched via the Firestore REST API with the site's public web API
/// key (the same key every visitor's browser uses). Conventions, empirically calibrated against
/// live builds for four classes: the document is itself one variant (its <c>variantName</c> +
/// <c>paragon</c> fields) and its <c>variants</c> array holds the remaining complete variants;
/// <c>selectedTiles</c> entries are <c>&lt;boardKey&gt;_r&lt;row+1&gt;c&lt;col+1&gt;</c> — 1-based
/// coordinates in the board's UNROTATED local grid, start/gate/socket cells included;
/// <c>rotation</c> is clockwise degrees and may exceed 360 (live data has 540 and 900); board-grid
/// positions have +y pointing UP from the starter at (0,0), so our Maxroll-convention Y is the
/// negation; boards and glyphs carry display names ("Starting Board" aliases the starter). A
/// board's tile-key prefix is usually the lowercased, space-stripped display name, but not always
/// ("Danse Macabre" keys as "dansmacabre"), so prefixes pair to the declared boards by closest match.
/// </summary>
public static partial class D4BuildsImporter
{
    /// <summary>
    /// Firestore REST endpoint for a build document; {0} is the build's uuid. Project id and API
    /// key are the site's public web-app config, embedded in every page it serves.
    /// </summary>
    public const string BuildDocumentApiFormat = Shared.D4BuildsEndpoints.BuildDocumentApiFormat;

    /// <summary>
    /// Gatsby page-data endpoint for a curated build's pretty-slug page; {0} is the slug. The
    /// page is prerendered and its <c>result.pageContext.seoId</c> is the build document's uuid.
    /// </summary>
    public const string PageDataApiFormat = Shared.D4BuildsEndpoints.PageDataApiFormat;

    public static bool TryParseBuildUrl(string url, out string buildId)
    {
        var match = BuildUrlPattern().Match(url);
        buildId = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }

    /// <summary>
    /// Matches the curated builds' pretty-slug form (d4builds.gg/builds/whirlwind-barbarian-endgame).
    /// Check <see cref="TryParseBuildUrl"/> first — a uuid also matches the slug shape.
    /// </summary>
    public static bool TryParseBuildSlugUrl(string url, out string slug)
    {
        var match = BuildSlugUrlPattern().Match(url);
        slug = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }

    /// <summary>Reads the build document uuid out of a slug page's page-data JSON.</summary>
    public static string ExtractBuildId(string pageDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(pageDataJson);
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("pageContext", out var context)
                && context.TryGetProperty("seoId", out var seoId)
                && seoId.GetString() is { } id
                && BuildUrlPattern().Match($"d4builds.gg/builds/{id}").Success)
                return id;
        }
        catch (JsonException)
        {
        }
        throw new FormatException(
            "The d4builds.gg page didn't reveal a build id — is the link a build page?");
    }

    /// <summary>
    /// Reads every build variant with paragon data out of a Firestore build document. Variants
    /// with identical paragon data are merged (their titles joined), mirroring the other importers.
    /// </summary>
    public static IReadOnlyList<D4BuildsVariant> ExtractVariants(string documentJson)
    {
        using var doc = ParseDocument(documentJson);
        if (!doc.RootElement.TryGetProperty("fields", out var fields))
            throw new FormatException("Not a d4builds.gg build document (no fields).");

        string buildName = GetString(fields, "name") ?? "d4builds build";
        string topClass = GetString(fields, "class") ?? "";

        var variants = new List<D4BuildsVariant>();
        var indexByShape = new Dictionary<string, int>(StringComparer.Ordinal);
        int counter = 0;

        void AddSection(JsonElement sectionFields, bool isTopLevel)
        {
            counter++;
            if (!TryGetMapFields(sectionFields, "paragon", out var paragon))
                return;
            var boards = ReadBoards(paragon);
            var tiles = ReadTiles(paragon);
            if (boards.Count == 0 || tiles.Count == 0)
                return; // e.g. a leveling variant without a paragon setup

            string title = GetString(sectionFields, "variantName") is { Length: > 0 } name
                ? name
                : isTopLevel ? buildName : $"Variant {counter}";
            string className = GetString(sectionFields, "class") is { Length: > 0 } cls ? cls : topClass;

            string shape = string.Join(";", boards.Select(b => $"{b.Name}|{b.X}|{b.Y}|{b.RotationDegrees}|{b.Glyph}"))
                + "#" + string.Join(",", tiles.Order(StringComparer.Ordinal));
            if (indexByShape.TryGetValue(shape, out int existing))
                variants[existing] = variants[existing] with
                {
                    Title = MergeTitles(variants[existing].Title, title),
                };
            else
            {
                indexByShape[shape] = variants.Count;
                variants.Add(new D4BuildsVariant(title, className, boards, tiles));
            }
        }

        AddSection(fields, isTopLevel: true);
        if (TryGetProperty(fields, "variants", out var variantsValue)
            && variantsValue.TryGetProperty("arrayValue", out var array)
            && array.TryGetProperty("values", out var values))
        {
            foreach (var item in values.EnumerateArray())
            {
                if (item.TryGetProperty("mapValue", out var map) && map.TryGetProperty("fields", out var itemFields))
                    AddSection(itemFields, isTopLevel: false);
            }
        }

        if (variants.Count == 0)
            throw new FormatException("The d4builds.gg build contains no paragon data.");
        return variants;
    }

    /// <summary>Joins merged-variant titles; d4builds variants often repeat the same name.</summary>
    private static string MergeTitles(string existing, string title) =>
        existing.Split(" / ", StringSplitOptions.TrimEntries).Contains(title, StringComparer.OrdinalIgnoreCase)
            ? existing
            : $"{existing} / {title}";

    /// <summary>
    /// Converts a variant into a layout plus allocation, resolving d4builds display names against
    /// the paragon database. Start/gate/socket cells are re-added if the tile list missed any.
    /// </summary>
    public static ConvertedMaxrollBuild ToBuild(D4BuildsVariant variant, ParagonData data)
    {
        var build = MaxrollParagonCodec.ToLayout(
            ToMaxrollEntries(variant, data),
            data.Boards
                .Where(b => !string.IsNullOrEmpty(b.InternalName))
                .ToDictionary(b => b.InternalName, StringComparer.OrdinalIgnoreCase));
        return ImplicitCellAugmenter.Augment(build, data);
    }

    /// <summary>Translates the variant into Maxroll board entries (the codec's neutral form).</summary>
    public static IReadOnlyList<MaxrollBoardEntry> ToMaxrollEntries(D4BuildsVariant variant, ParagonData data)
    {
        if (string.IsNullOrEmpty(variant.ClassName))
            throw new FormatException("The d4builds.gg build does not declare a class.");

        var boardsBySlug = new Dictionary<string, ParagonBoardDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in data.Boards)
        {
            if (board.Name is null
                || !string.Equals(board.ClassName, variant.ClassName, StringComparison.OrdinalIgnoreCase))
                continue;
            boardsBySlug.TryAdd(Slug(board.Name), board);
            if (board.BoardIndex == 0)
                boardsBySlug.TryAdd("startingboard", board);
        }
        if (boardsBySlug.Count == 0)
            throw new FormatException($"Unknown d4builds.gg class '{variant.ClassName}'.");

        // Group picked tile coordinates by their board-key prefix.
        var coordsByPrefix = new Dictionary<string, List<(int Col, int Row)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in variant.SelectedTiles)
        {
            var match = TileKeyPattern().Match(tile);
            if (!match.Success)
                throw new FormatException($"Unrecognized d4builds tile key '{tile}'.");
            string prefix = match.Groups[1].Value;
            if (!coordsByPrefix.TryGetValue(prefix, out var list))
                coordsByPrefix[prefix] = list = [];
            list.Add((int.Parse(match.Groups[3].Value) - 1, int.Parse(match.Groups[2].Value) - 1));
        }

        var prefixByBoard = PairTilePrefixes(variant.Boards, coordsByPrefix.Keys);

        var entries = new List<MaxrollBoardEntry>();
        foreach (var boardRef in variant.Boards)
        {
            if (!boardsBySlug.TryGetValue(Slug(boardRef.Name), out var board))
                throw new FormatException(
                    $"Unknown d4builds.gg board '{boardRef.Name}' for class {variant.ClassName} — "
                    + "the build may use content newer than the bundled game data (PTR or a new season).");
            if (boardRef.RotationDegrees % 90 != 0)
                throw new FormatException(
                    $"Board '{boardRef.Name}' has a non-quarter rotation of {boardRef.RotationDegrees}°.");

            string? glyphInternalName = null;
            if (boardRef.Glyph is { Length: > 0 } glyphName)
            {
                var glyph = data.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.Name, glyphName, StringComparison.OrdinalIgnoreCase)
                    && g.Classes.Contains(variant.ClassName, StringComparer.OrdinalIgnoreCase));
                glyphInternalName = glyph?.InternalName
                    ?? throw new FormatException(
                        $"Unknown d4builds.gg glyph '{glyphName}' for class {variant.ClassName}.");
            }

            var nodes = new Dictionary<string, int>();
            if (prefixByBoard.TryGetValue(boardRef, out string? prefix))
            {
                foreach (var (col, row) in coordsByPrefix[prefix])
                {
                    if (col < 0 || col >= board.Width || row < 0 || row >= board.Width)
                        throw new FormatException(
                            $"Tile '{prefix}_r{row + 1}c{col + 1}' is outside the {board.Width}×{board.Width} grid.");
                    nodes[(row * board.Width + col).ToString()] = 1;
                }
            }

            entries.Add(new MaxrollBoardEntry
            {
                Id = board.InternalName,
                Nodes = nodes,
                Rotation = ((boardRef.RotationDegrees / 90) % 4 + 4) % 4,
                Position = new MaxrollPosition { X = boardRef.X, Y = -boardRef.Y }, // their +y points up
                Glyph = glyphInternalName,
                GlyphLevel = boardRef.GlyphLevel,
            });
        }
        return entries;
    }

    /// <summary>
    /// Pairs each tile-key prefix with the declared board it belongs to: exact slug matches
    /// first, then closest edit distance for the site's off-by-a-letter keys ("dansmacabre").
    /// A prefix matching no attached board is dropped — live documents keep stale tiles for
    /// boards a variant has since swapped out, and the site ignores them too.
    /// </summary>
    private static Dictionary<D4BuildsBoardRef, string> PairTilePrefixes(
        IReadOnlyList<D4BuildsBoardRef> boards, IEnumerable<string> prefixes)
    {
        var result = new Dictionary<D4BuildsBoardRef, string>();
        var unmatched = new List<string>();
        foreach (string prefix in prefixes)
        {
            var exact = boards.FirstOrDefault(b =>
                !result.ContainsKey(b) && string.Equals(Slug(b.Name), prefix, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                result[exact] = prefix;
            else
                unmatched.Add(prefix);
        }

        foreach (string prefix in unmatched)
        {
            D4BuildsBoardRef? best = null;
            int bestDistance = int.MaxValue;
            bool tie = false;
            foreach (var board in boards.Where(b => !result.ContainsKey(b)))
            {
                int distance = EditDistance(prefix.ToLowerInvariant(), Slug(board.Name));
                if (distance < bestDistance)
                    (best, bestDistance, tie) = (board, distance, false);
                else if (distance == bestDistance)
                    tie = true;
            }
            if (best is not null && !tie && bestDistance <= 3)
                result[best] = prefix;
        }
        return result;
    }

    /// <summary>
    /// d4builds board keys: display name lowercased with spaces removed (mostly). Typographic
    /// apostrophes normalize to straight ones so a style mismatch never breaks name pairing.
    /// </summary>
    private static string Slug(string name) =>
        name.ToLowerInvariant().Replace(" ", "").Replace('’', '\'');

    private static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitute = diagonal + (a[i - 1] == b[j - 1] ? 0 : 1);
                diagonal = row[j];
                row[j] = Math.Min(substitute, Math.Min(row[j] + 1, row[j - 1] + 1));
            }
        }
        return row[b.Length];
    }

    // ── Firestore document reading ────────────────────────────────────────
    // The REST API wraps every value in a type envelope ({"stringValue": …},
    // {"integerValue": "42"}, {"mapValue": {"fields": …}}, …); these helpers unwrap the
    // handful of shapes the build documents use.

    private static JsonDocument ParseDocument(string documentJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(documentJson);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a d4builds.gg build document (invalid JSON): {ex.Message}");
        }
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var error))
        {
            string message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            doc.Dispose();
            throw new FormatException($"d4builds.gg build not found ({message}).");
        }
        return doc;
    }

    private static List<D4BuildsBoardRef> ReadBoards(JsonElement paragonFields)
    {
        var boards = new List<D4BuildsBoardRef>();
        if (!TryGetProperty(paragonFields, "boards", out var wrapper)
            || !wrapper.TryGetProperty("arrayValue", out var array)
            || !array.TryGetProperty("values", out var values))
            return boards;
        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("mapValue", out var map) || !map.TryGetProperty("fields", out var fields))
                continue;
            string? name = GetString(fields, "name");
            if (string.IsNullOrEmpty(name))
                continue;
            boards.Add(new D4BuildsBoardRef(
                name,
                GetInt(fields, "x") ?? 0,
                GetInt(fields, "y") ?? 0,
                GetInt(fields, "rotation") ?? 0,
                GetString(fields, "glyph"),
                GetInt(fields, "glyphLevel")));
        }
        return boards;
    }

    private static List<string> ReadTiles(JsonElement paragonFields)
    {
        var tiles = new List<string>();
        if (!TryGetProperty(paragonFields, "selectedTiles", out var wrapper)
            || !wrapper.TryGetProperty("arrayValue", out var array)
            || !array.TryGetProperty("values", out var values))
            return tiles;
        foreach (var item in values.EnumerateArray())
        {
            if (item.TryGetProperty("stringValue", out var value) && value.GetString() is { Length: > 0 } tile)
                tiles.Add(tile);
        }
        return tiles;
    }

    private static bool TryGetProperty(JsonElement fields, string name, out JsonElement value)
    {
        value = default;
        return fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out value);
    }

    private static bool TryGetMapFields(JsonElement fields, string name, out JsonElement mapFields)
    {
        mapFields = default;
        return TryGetProperty(fields, name, out var wrapper)
            && wrapper.TryGetProperty("mapValue", out var map)
            && map.TryGetProperty("fields", out mapFields);
    }

    private static string? GetString(JsonElement fields, string name) =>
        TryGetProperty(fields, name, out var wrapper)
        && wrapper.TryGetProperty("stringValue", out var value)
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement fields, string name)
    {
        if (!TryGetProperty(fields, name, out var wrapper))
            return null;
        if (wrapper.TryGetProperty("integerValue", out var integer)
            && int.TryParse(integer.GetString(), out int parsed))
            return parsed;
        if (wrapper.TryGetProperty("doubleValue", out var dbl) && dbl.TryGetDouble(out double d))
            return (int)Math.Round(d);
        return null;
    }

    [GeneratedRegex(@"d4builds\.gg/builds/([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase)]
    private static partial Regex BuildUrlPattern();

    [GeneratedRegex(@"d4builds\.gg/builds/([a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildSlugUrlPattern();

    [GeneratedRegex(@"^(.+)_r(\d+)c(\d+)$")]
    private static partial Regex TileKeyPattern();
}

/// <summary>One build variant's paragon setup; the document's own fields are the first variant.</summary>
public sealed record D4BuildsVariant(
    string Title,
    string ClassName,
    IReadOnlyList<D4BuildsBoardRef> Boards,
    IReadOnlyList<string> SelectedTiles);

/// <summary>A board as d4builds declares it: display names, board-grid cell, clockwise degrees.</summary>
public sealed record D4BuildsBoardRef(
    string Name, int X, int Y, int RotationDegrees, string? Glyph, int? GlyphLevel);
