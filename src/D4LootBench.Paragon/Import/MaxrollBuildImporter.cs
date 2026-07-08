using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace D4LootBench.Paragon.Import;

/// <summary>One paragon setup found in a Maxroll planner profile (deduplicated across profiles).</summary>
public sealed record MaxrollBuildVariant(string Title, IReadOnlyList<MaxrollBoardEntry> Entries);

/// <summary>
/// Imports paragon builds from Maxroll by URL. A build-guide page embeds links to the site's
/// planner (<c>d4/planner/&lt;id&gt;</c>); the planner data lives at
/// <c>https://planners.maxroll.gg/profiles/d4/&lt;id&gt;</c> as <c>{name, class, data}</c> where
/// <c>data</c> is a JSON string holding <c>profiles[]</c>, each with <c>paragon.steps[]</c> whose
/// <c>data</c> is exactly the variant-code array <see cref="MaxrollParagonCodec.Decode"/> parses.
/// Steps usually repeat verbatim across a guide's profiles — identical ones are merged.
/// </summary>
public static partial class MaxrollBuildImporter
{
    /// <summary>The planner profile API endpoint; format with the planner id.</summary>
    public const string ProfileApiFormat = "https://planners.maxroll.gg/profiles/d4/{0}";

    /// <summary>Static planner routes that look like ids but aren't.</summary>
    private static readonly string[] ReservedSlugs = ["builds", "new"];

    /// <summary>True when the URL addresses a planner build directly (maxroll.gg/d4/planner/&lt;id&gt;).</summary>
    public static bool TryParsePlannerUrl(string url, out string plannerId)
    {
        var match = PlannerUrlPattern().Match(url);
        plannerId = match.Success ? match.Groups[1].Value : "";
        return match.Success && !ReservedSlugs.Contains(plannerId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Planner ids referenced by a build-guide page, most-referenced first.</summary>
    public static IReadOnlyList<string> ExtractPlannerIds(string html) =>
        PlannerReferencePattern().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Where(id => !ReservedSlugs.Contains(id, StringComparer.OrdinalIgnoreCase))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

    /// <summary>
    /// Parses a planner profile API response into paragon variants. A variant is one
    /// (profile, step) pair; steps with identical board data are merged, and a step shared by
    /// every profile keeps just its step name as the title.
    /// </summary>
    public static IReadOnlyList<MaxrollBuildVariant> ExtractVariants(string profileJson)
    {
        ProfileEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ProfileEnvelope>(profileJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a Maxroll planner profile: {ex.Message}");
        }
        if (envelope?.Data is not string dataJson)
            throw new FormatException("Not a Maxroll planner profile (no data field).");

        PlannerData? data;
        try
        {
            data = JsonSerializer.Deserialize<PlannerData>(dataJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Unreadable Maxroll planner data: {ex.Message}");
        }
        var profiles = data?.Profiles ?? [];

        // Dedup by the step's raw JSON; remember which (profile, step) pairs produced each.
        var variants = new List<(string RawJson, IReadOnlyList<MaxrollBoardEntry> Entries,
                                 List<string> Profiles, List<string> Steps)>();
        foreach (var profile in profiles)
        {
            foreach (var step in profile.Paragon?.Steps ?? [])
            {
                if (step.Data.ValueKind != JsonValueKind.Array || step.Data.GetArrayLength() == 0)
                    continue;
                string raw = step.Data.GetRawText();
                int existing = variants.FindIndex(v => v.RawJson == raw);
                if (existing < 0)
                {
                    var entries = MaxrollParagonCodec.Decode(raw);
                    variants.Add((raw, entries, [], []));
                    existing = variants.Count - 1;
                }
                variants[existing].Profiles.Add(profile.Name ?? "Profile");
                variants[existing].Steps.Add(step.Name ?? "Step");
            }
        }
        if (variants.Count == 0)
            throw new FormatException(
                $"The Maxroll planner build '{envelope.Name ?? "unnamed"}' contains no paragon boards.");

        int profileCount = profiles.Count;
        return variants.Select(v =>
        {
            var steps = v.Steps.Distinct().ToList();
            string title = steps.Count == 1 && v.Profiles.Distinct().Count() == profileCount
                ? steps[0]
                : string.Join(" / ", v.Profiles.Zip(v.Steps, (p, s) => $"{p}: {s}").Distinct());
            if (envelope.Name is { Length: > 0 } buildName)
                title = $"{buildName} — {title}";
            return new MaxrollBuildVariant(title, v.Entries);
        }).ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ProfileEnvelope
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    private sealed class PlannerData
    {
        [JsonPropertyName("profiles")]
        public List<PlannerProfile> Profiles { get; set; } = [];
    }

    private sealed class PlannerProfile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("paragon")]
        public PlannerParagon? Paragon { get; set; }
    }

    private sealed class PlannerParagon
    {
        [JsonPropertyName("steps")]
        public List<PlannerStep> Steps { get; set; } = [];
    }

    private sealed class PlannerStep
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }
    }

    [GeneratedRegex(@"maxroll\.gg/d4/planner/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlannerUrlPattern();

    [GeneratedRegex(@"[""':/]d4/planner/([a-z0-9]{6,12})(?![a-z0-9])")]
    private static partial Regex PlannerReferencePattern();
}
