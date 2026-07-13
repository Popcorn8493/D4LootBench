using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace D4LootBench.Paragon.Import;

/// <summary>
/// One build variant's skill setup: the action-bar skills (full objects with name, description,
/// and tree section) and the skill-tree investment as ranks per skill slug. Titled from the same
/// content-variants widget the paragon and gear importers pair with positionally.
/// </summary>
public sealed record MobalyticsSkillVariant(
    string Title,
    IReadOnlyList<MobalyticsSkill> ActiveSkills,
    IReadOnlyDictionary<string, int> TreeRanks);

/// <summary>An action-bar skill. <see cref="SectionName"/> is the tree section ("Core", "Basic"…).</summary>
public sealed record MobalyticsSkill(
    string Slug, string Name, string Description, string TypeId, string SectionName);

/// <summary>
/// Extracts the skill data a Mobalytics build-guide page embeds, one set per build variant:
/// an <c>"assignedSkills":{…,"skills":[{"skill":{slug,name,description,type,section}}]}</c>
/// object (the action bar, exactly one per variant) and one or more <c>"skillTree"</c> sections
/// (ACTIVATE/DEACTIVATE steps — the class tree plus, on newer guides, a mercenary tree) whose
/// steps fold into ranks per slug. Tree sections attribute to variants by even division; when
/// the page's section count doesn't divide evenly the ranks are simply omitted rather than
/// guessed. The skill tree's own value is either the step array or an object wrapping it.
/// </summary>
public static partial class MobalyticsSkillImporter
{
    public static IReadOnlyList<MobalyticsSkillVariant> ExtractVariants(string html)
    {
        var assigned = ExtractAssignedSections(html);
        if (assigned.Count == 0)
            throw new FormatException("No Mobalytics skill data found in the page.");

        var trees = ExtractTreeSections(html);
        int treesPerVariant = trees.Count > 0 && trees.Count % assigned.Count == 0
            ? trees.Count / assigned.Count
            : 0;

        var titles = VariantTitlePattern().Matches(html)
            .Select(m => JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"") ?? "")
            .ToList();
        bool paired = titles.Count == assigned.Count;

        var variants = new List<MobalyticsSkillVariant>();
        for (int i = 0; i < assigned.Count; i++)
        {
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int t = i * treesPerVariant; t < (i + 1) * treesPerVariant; t++)
            {
                foreach (var (slug, delta) in trees[t])
                    ranks[slug] = Math.Max(0, ranks.GetValueOrDefault(slug) + delta);
            }
            variants.Add(new MobalyticsSkillVariant(
                paired ? titles[i] : $"Variant {i + 1}",
                assigned[i],
                ranks.Where(kv => kv.Value > 0).ToDictionary(StringComparer.OrdinalIgnoreCase)));
        }
        return variants;
    }

    private static List<IReadOnlyList<MobalyticsSkill>> ExtractAssignedSections(string html)
    {
        const string marker = "\"assignedSkills\":{";
        var sections = new List<IReadOnlyList<MobalyticsSkill>>();
        int from = 0;
        while (true)
        {
            int at = html.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            from = at + marker.Length;

            AssignedSection? section;
            try
            {
                section = JsonSerializer.Deserialize<AssignedSection>(
                    ExtractBalancedJson(html, at + marker.Length - 1), JsonOptions);
            }
            catch (JsonException)
            {
                continue; // an unrelated key elsewhere in the page
            }

            var skills = (section?.Skills ?? [])
                .Select(slot => slot.Skill)
                .Where(s => s is { Slug.Length: > 0 })
                .Select(s => new MobalyticsSkill(
                    s!.Slug, s.Name ?? s.Slug.Replace('-', ' '), s.Description ?? "",
                    s.Type?.Id ?? "", s.Section?.Name ?? ""))
                .ToList();
            if (skills.Count > 0)
                sections.Add(skills);
        }
        return sections;
    }

    /// <summary>Each tree section as (slug, ±1) steps: ACTIVATE buys a rank, DEACTIVATE refunds one.</summary>
    private static List<List<(string Slug, int Delta)>> ExtractTreeSections(string html)
    {
        const string marker = "\"skillTree\":";
        var sections = new List<List<(string, int)>>();
        int from = 0;
        while (true)
        {
            int at = html.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            from = at + marker.Length;

            int start = at + marker.Length;
            if (start >= html.Length || (html[start] != '[' && html[start] != '{'))
                continue; // a string-valued key like "skillTree":"…"

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(ExtractBalancedJson(html, start));
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && !root.TryGetProperty("skills", out root))
                    continue;
                if (root.ValueKind != JsonValueKind.Array)
                    continue;

                var steps = new List<(string, int)>();
                foreach (var step in root.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object
                        || !step.TryGetProperty("actionType", out var action)
                        || !step.TryGetProperty("skill", out var skill)
                        || !skill.TryGetProperty("slug", out var slug)
                        || slug.GetString() is not { Length: > 0 } name)
                        continue;
                    int delta = action.GetString() switch
                    {
                        "ACTIVATE" => 1,
                        "DEACTIVATE" => -1,
                        _ => 0,
                    };
                    if (delta != 0)
                        steps.Add((name, delta));
                }
                if (steps.Count > 0)
                    sections.Add(steps);
            }
        }
        return sections;
    }

    /// <summary>Returns the balanced JSON value ('[' or '{') starting at <paramref name="start"/>.</summary>
    private static string ExtractBalancedJson(string text, int start)
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
        throw new FormatException("Unterminated skill JSON in the page.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex(@"\{""id"":""[^""]+"",""title"":""((?:[^""\\]|\\.)*)"",""description""")]
    private static partial Regex VariantTitlePattern();

    private sealed class AssignedSection
    {
        [JsonPropertyName("skills")]
        public List<AssignedSlot>? Skills { get; set; }
    }

    private sealed class AssignedSlot
    {
        [JsonPropertyName("skill")]
        public SkillDto? Skill { get; set; }
    }

    private sealed class SkillDto
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("type")]
        public IdRef? Type { get; set; }

        [JsonPropertyName("section")]
        public NameRef? Section { get; set; }
    }

    private sealed class IdRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class NameRef
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
