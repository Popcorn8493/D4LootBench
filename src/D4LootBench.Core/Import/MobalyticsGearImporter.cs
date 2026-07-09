using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace D4LootBench.Core.Import;

/// <summary>One build variant's gear list, titled from the page's content-variants widget.</summary>
public sealed record MobalyticsGearVariant(string Title, IReadOnlyList<MobalyticsGearItem> Items);

/// <summary>
/// Extracts the structured gear data a Mobalytics build-guide page embeds — one
/// <c>"equipmentPriorityList":[{slug, type, iconURL, modifiers:[{slug, type}]}]</c> array per
/// build variant — and converts it to the neutral <see cref="ParsedBuildGuide"/> form. This is
/// the whole build as the author entered it, so it beats copy-pasted text: slot coverage is
/// complete, uniques are marked by their icon path, and affix order is the author's priority.
/// Variant titles pair with the data sections positionally, exactly like the paragon importer.
/// </summary>
public static partial class MobalyticsGearImporter
{
    private const string Marker = "\"equipmentPriorityList\":[";

    /// <summary>
    /// Finds every embedded gear list in the page. Variants with identical gear are merged
    /// (their titles joined), mirroring the paragon importer's behavior.
    /// </summary>
    public static IReadOnlyList<MobalyticsGearVariant> ExtractVariants(string html)
    {
        var sections = new List<(string RawJson, List<MobalyticsGearItem> Items)>();
        int from = 0;
        while (true)
        {
            int at = html.IndexOf(Marker, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            from = at + Marker.Length;

            string raw = ExtractJsonArray(html, at + Marker.Length - 1);
            List<MobalyticsGearItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<MobalyticsGearItem>>(raw, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // an unrelated key elsewhere in the page
            }
            if (items is { Count: > 0 })
                sections.Add((raw, items));
        }
        if (sections.Count == 0)
            throw new BuildGuideImportException(
                "No Mobalytics gear data found in the page — is it a Diablo 4 build guide?");

        var titles = VariantTitlePattern().Matches(html)
            .Select(m => JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"") ?? "")
            .ToList();
        bool paired = titles.Count == sections.Count;

        var variants = new List<MobalyticsGearVariant>();
        var indexByRawJson = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sections.Count; i++)
        {
            string title = paired ? titles[i] : $"Variant {i + 1}";
            if (indexByRawJson.TryGetValue(sections[i].RawJson, out int existing))
                variants[existing] = variants[existing] with { Title = $"{variants[existing].Title} / {title}" };
            else
            {
                indexByRawJson[sections[i].RawJson] = variants.Count;
                variants.Add(new MobalyticsGearVariant(title, sections[i].Items));
            }
        }
        return variants;
    }

    /// <summary>
    /// Converts a variant to the neutral parsed-guide form. Uniques (recognized by their
    /// <c>/uniques/</c> icon path) become unique slots; other items contribute their
    /// <c>gear</c>-type modifiers in priority order (tempering, sockets, and implicits don't
    /// exist on dropped items, so they are skipped). Charm and seal slots fold into the
    /// show-all-talismans rule. When alternates share a slot, the first (highest-priority)
    /// non-unique entry provides the slot's affixes; every unique alternate is kept.
    /// </summary>
    public static ParsedBuildGuide ToParsedGuide(MobalyticsGearVariant variant)
    {
        var guide = new ParsedBuildGuide { DetectedFormat = BuildGuideFormat.Mobalytics };
        var affixSlotsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in variant.Items)
        {
            if (item.Type.Contains("charm", StringComparison.OrdinalIgnoreCase)
                || item.Type.EndsWith("-seal", StringComparison.OrdinalIgnoreCase)
                || item.Type.Equals("seal", StringComparison.OrdinalIgnoreCase))
            {
                guide.Slots.Add(new ParsedSlot { SlotLabel = SlotLabel(item.Type), IsTalismanSlot = true });
                continue;
            }

            bool isUnique = item.IconUrl?.Contains("/uniques/", StringComparison.OrdinalIgnoreCase) == true;
            if (isUnique)
            {
                guide.Slots.Add(new ParsedSlot
                {
                    SlotLabel = SlotLabel(item.Type),
                    ItemName = DeKebab(item.Slug),
                    HasUniqueSentinel = true,
                });
                continue;
            }

            if (!affixSlotsSeen.Add(item.Type))
                continue; // a lower-priority alternate for a slot that already has its rule

            var slot = new ParsedSlot
            {
                SlotLabel = SlotLabel(item.Type),
                ItemName = DeKebab(item.Slug),
            };
            foreach (var modifier in item.Modifiers)
            {
                if (!modifier.Type.Equals("gear", StringComparison.OrdinalIgnoreCase))
                    continue;
                slot.Affixes.Add(new ParsedAffix { RawName = DeKebab(modifier.Slug) });
            }
            if (slot.Affixes.Count > 0)
                guide.Slots.Add(slot);
        }
        return guide;
    }

    /// <summary>"critical-strike-chance" → "critical strike chance" (resolution is fuzzy anyway).</summary>
    private static string DeKebab(string slug) => slug.Replace('-', ' ').Trim();

    /// <summary>"chest-armor" → "Chest Armor" — slot labels double as the generated rule names.</summary>
    private static string SlotLabel(string typeSlug) =>
        string.Join(' ', DeKebab(typeSlug)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>Returns the balanced JSON array starting at <paramref name="start"/> ('[').</summary>
    private static string ExtractJsonArray(string text, int start)
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
            else if (c is '[' or '{')
                depth++;
            else if ((c is ']' or '}') && --depth == 0)
                return text[start..(i + 1)];
        }
        throw new BuildGuideImportException("Unterminated gear JSON in the page.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex(@"\{""id"":""[^""]+"",""title"":""((?:[^""\\]|\\.)*)"",""description""")]
    private static partial Regex VariantTitlePattern();
}

public sealed class MobalyticsGearItem
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    /// <summary>Slot slug: helm, chest-armor, ring-1, ranged-weapon, season-12-charm-1, …</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Uniques point into <c>/uniques/</c>; aspects into <c>/aspects/</c>.</summary>
    [JsonPropertyName("iconURL")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("modifiers")]
    public List<MobalyticsGearModifier> Modifiers { get; set; } = [];
}

public sealed class MobalyticsGearModifier
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    /// <summary>gear | tempering | socket | implicit | charm | seal.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}
