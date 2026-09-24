using System.Text.Json;
using System.Text.RegularExpressions;

namespace D4LootBench.Core.Import;

/// <summary>One build variant's gear: per-slot item names and affix priorities.</summary>
public sealed record D4BuildsGearVariant(
    string Title, IReadOnlyList<D4BuildsGearSlot> Slots, bool HasCharms);

/// <summary>A gear slot as d4builds stores it: display names throughout, stats in priority order.</summary>
public sealed record D4BuildsGearSlot(string Slot, string? ItemName, IReadOnlyList<string> Stats);

/// <summary>
/// Extracts gear data from a d4builds.gg build. Build pages are a client-rendered SPA with no
/// build data in the HTML — the data lives in a public Firebase Firestore document
/// (<c>builds/&lt;uuid&gt;</c>, the uuid from the page URL), fetched via the Firestore REST API
/// with the site's public web API key (the same key every visitor's browser uses; the paragon
/// planner's importer reads the same documents). The document is itself one variant (its
/// <c>variantName</c>/<c>gear</c>/<c>newStats</c> fields) and its <c>variants</c> array holds the
/// remaining complete variants. <c>gear</c> maps slot → equipped aspect or unique display name;
/// <c>newStats</c> maps slot → affix display names in the author's priority order (null = empty
/// row; older documents carry the same shape as <c>stats</c>, used as a per-slot fallback);
/// a non-empty <c>charms</c> array marks talisman usage. Tempering and gems are skipped —
/// they don't exist on dropped items.
/// </summary>
public static partial class D4BuildsGearImporter
{
    /// <summary>
    /// Firestore REST endpoint for a build document; {0} is the build's uuid. Project id and API
    /// key are the site's public web-app config, embedded in every page it serves.
    /// </summary>
    public const string BuildDocumentApiFormat = Shared.D4BuildsEndpoints.BuildDocumentApiFormat;

    /// <summary>Gear slots in display order; unknown future slots append after these.</summary>
    private static readonly string[] SlotOrder =
    [
        "Helm", "Chest Armor", "Gloves", "Pants", "Boots",
        "Weapon", "Dual-Wield Weapon 1", "Dual-Wield Weapon 2",
        "Bludgeoning Weapon", "Slashing Weapon", "Ranged Weapon",
        "Offhand", "Shield", "Amulet", "Ring 1", "Ring 2",
    ];

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
        throw new BuildGuideImportException(
            "The d4builds.gg page didn't reveal a build id — is the link a build page?");
    }

    /// <summary>
    /// Reads every build variant with gear data out of a Firestore build document. Variants with
    /// identical gear are merged (their titles joined), mirroring the other importers.
    /// </summary>
    public static IReadOnlyList<D4BuildsGearVariant> ExtractVariants(string documentJson)
    {
        using var doc = ParseDocument(documentJson);
        if (!doc.RootElement.TryGetProperty("fields", out var fields))
            throw new BuildGuideImportException("Not a d4builds.gg build document (no fields).");

        string buildName = GetString(fields, "name") ?? "d4builds build";

        var variants = new List<D4BuildsGearVariant>();
        var indexByShape = new Dictionary<string, int>(StringComparer.Ordinal);
        int counter = 0;

        void AddSection(JsonElement sectionFields, bool isTopLevel)
        {
            counter++;
            var slots = ReadSlots(sectionFields);
            if (slots.Count == 0)
                return;
            bool hasCharms = HasValues(sectionFields, "charms");

            string title = GetString(sectionFields, "variantName") is { Length: > 0 } name
                ? name
                : isTopLevel ? buildName : $"Variant {counter}";

            string shape = string.Join(";", slots.Select(s => $"{s.Slot}|{s.ItemName}|{string.Join(",", s.Stats)}"));
            if (indexByShape.TryGetValue(shape, out int existing))
                variants[existing] = variants[existing] with
                {
                    Title = MergeTitles(variants[existing].Title, title),
                };
            else
            {
                indexByShape[shape] = variants.Count;
                variants.Add(new D4BuildsGearVariant(title, slots, hasCharms));
            }
        }

        AddSection(fields, isTopLevel: true);
        if (fields.TryGetProperty("variants", out var variantsValue)
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
            throw new BuildGuideImportException(
                "No gear data found in the d4builds.gg build — is it a Diablo 4 build?");
        return variants;
    }

    /// <summary>Joins merged-variant titles; d4builds variants often repeat the same name.</summary>
    private static string MergeTitles(string existing, string title) =>
        existing.Split(" / ", StringSplitOptions.TrimEntries).Contains(title, StringComparer.OrdinalIgnoreCase)
            ? existing
            : $"{existing} / {title}";

    /// <summary>
    /// Converts a variant to the neutral parsed-guide form. Every legendary aspect's name
    /// contains "Aspect", so anything else equipped is a unique and becomes a unique slot;
    /// aspect-bearing (or empty) slots contribute their stats as affixes in priority order.
    /// Charms fold into the show-all-talismans rule.
    /// </summary>
    public static ParsedBuildGuide ToParsedGuide(D4BuildsGearVariant variant)
    {
        var guide = new ParsedBuildGuide { DetectedFormat = BuildGuideFormat.D4Builds };
        foreach (var slot in variant.Slots)
        {
            bool isUnique = slot.ItemName is not null
                && !slot.ItemName.Contains("aspect", StringComparison.OrdinalIgnoreCase);
            if (isUnique)
            {
                guide.Slots.Add(new ParsedSlot
                {
                    SlotLabel = slot.Slot,
                    ItemName = slot.ItemName,
                    HasUniqueSentinel = true,
                });
                continue;
            }

            if (slot.Stats.Count == 0)
                continue;
            var parsed = new ParsedSlot { SlotLabel = slot.Slot, ItemName = slot.ItemName };
            foreach (string stat in slot.Stats)
                parsed.Affixes.Add(new ParsedAffix { RawName = stat });
            guide.Slots.Add(parsed);
        }
        if (variant.HasCharms)
            guide.Slots.Add(new ParsedSlot { SlotLabel = "Charms", IsTalismanSlot = true });
        return guide;
    }

    private static List<D4BuildsGearSlot> ReadSlots(JsonElement sectionFields)
    {
        var itemBySlot = ReadStringMap(sectionFields, "gear");
        var statsBySlot = ReadStringListMap(sectionFields, "newStats");
        var legacyStatsBySlot = ReadStringListMap(sectionFields, "stats"); // pre-"newStats" documents

        var slots = new List<D4BuildsGearSlot>();
        foreach (string slot in SlotOrder.Concat(itemBySlot.Keys.Concat(statsBySlot.Keys).Concat(legacyStatsBySlot.Keys)
                     .Where(k => !SlotOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            itemBySlot.TryGetValue(slot, out string? item);
            if (!statsBySlot.TryGetValue(slot, out var stats))
                legacyStatsBySlot.TryGetValue(slot, out stats);
            if (item is null && stats is not { Count: > 0 })
                continue;
            slots.Add(new D4BuildsGearSlot(slot, item, stats ?? []));
        }
        return slots;
    }

    // ── Firestore document reading ────────────────────────────────────────
    // The REST API wraps every value in a type envelope ({"stringValue": …},
    // {"mapValue": {"fields": …}}, …); these helpers unwrap the shapes the gear fields use.

    private static JsonDocument ParseDocument(string documentJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(documentJson);
        }
        catch (JsonException ex)
        {
            throw new BuildGuideImportException(
                $"Not a d4builds.gg build document (invalid JSON): {ex.Message}");
        }
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var error))
        {
            string message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            doc.Dispose();
            throw new BuildGuideImportException($"d4builds.gg build not found ({message}).");
        }
        return doc;
    }

    /// <summary>Unwraps a map field of string values: gear's slot → item name.</summary>
    private static Dictionary<string, string> ReadStringMap(JsonElement fields, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty(name, out var wrapper)
            || !wrapper.TryGetProperty("mapValue", out var map)
            || !map.TryGetProperty("fields", out var mapFields))
            return result;
        foreach (var property in mapFields.EnumerateObject())
        {
            if (property.Value.TryGetProperty("stringValue", out var value)
                && value.GetString() is { Length: > 0 } text)
                result[property.Name] = text;
        }
        return result;
    }

    /// <summary>Unwraps a map field of string arrays: newStats' slot → affix names (nulls dropped).</summary>
    private static Dictionary<string, List<string>> ReadStringListMap(JsonElement fields, string name)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty(name, out var wrapper)
            || !wrapper.TryGetProperty("mapValue", out var map)
            || !map.TryGetProperty("fields", out var mapFields))
            return result;
        foreach (var property in mapFields.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("arrayValue", out var array))
                continue;
            var list = new List<string>();
            if (array.TryGetProperty("values", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (item.TryGetProperty("stringValue", out var value)
                        && value.GetString() is { Length: > 0 } text)
                        list.Add(text);
                }
            }
            if (list.Count > 0)
                result[property.Name] = list;
        }
        return result;
    }

    private static bool HasValues(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object
        && fields.TryGetProperty(name, out var wrapper)
        && wrapper.TryGetProperty("arrayValue", out var array)
        && array.TryGetProperty("values", out var values)
        && values.GetArrayLength() > 0;

    private static string? GetString(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object
        && fields.TryGetProperty(name, out var wrapper)
        && wrapper.TryGetProperty("stringValue", out var value)
            ? value.GetString()
            : null;

    [GeneratedRegex(@"d4builds\.gg/builds/([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase)]
    private static partial Regex BuildUrlPattern();

    [GeneratedRegex(@"d4builds\.gg/builds/([a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildSlugUrlPattern();
}
