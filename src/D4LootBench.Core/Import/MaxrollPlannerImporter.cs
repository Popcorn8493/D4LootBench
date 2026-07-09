using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace D4LootBench.Core.Import;

/// <summary>A loot filter the guide author saved in their Maxroll planner: name plus share code.</summary>
public sealed record MaxrollLootFilter(string Name, string Code);

/// <summary>
/// Imports loot filters from Maxroll by URL. A build-guide page embeds links to the site's
/// planner (<c>d4/planner/&lt;id&gt;</c>); the planner data lives at
/// <c>https://planners.maxroll.gg/profiles/d4/&lt;id&gt;</c> as <c>{name, data}</c> where
/// <c>data</c> is a JSON string whose <c>lootFilters[]</c> holds the author's own in-game
/// filter share codes — the exact format this app edits, so they import losslessly with no
/// name resolution at all.
/// </summary>
public static partial class MaxrollPlannerImporter
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
    /// Parses a planner profile API response into its saved loot filters (often strictness
    /// tiers like Light / Medium / Strict). Throws when the planner has none.
    /// </summary>
    public static (string BuildName, IReadOnlyList<MaxrollLootFilter> Filters) ExtractLootFilters(string profileJson)
    {
        ProfileEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ProfileEnvelope>(profileJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BuildGuideImportException($"Not a Maxroll planner profile: {ex.Message}");
        }
        if (envelope?.Data is not string dataJson)
            throw new BuildGuideImportException(
                envelope?.Error ?? "Not a Maxroll planner profile (no data field).");

        PlannerData? data;
        try
        {
            data = JsonSerializer.Deserialize<PlannerData>(dataJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BuildGuideImportException($"Unreadable Maxroll planner data: {ex.Message}");
        }

        string buildName = envelope.Name ?? "Maxroll build";
        var filters = (data?.LootFilters ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Code))
            .Select(f => new MaxrollLootFilter(
                string.IsNullOrWhiteSpace(f.Name) ? "Filter" : f.Name!, f.Code!))
            .ToList();
        if (filters.Count == 0)
            throw new BuildGuideImportException(
                $"The Maxroll planner build '{buildName}' has no saved loot filters. " +
                "Not every guide ships one — try the guide's Mobalytics page, or paste the gear text instead.");
        return (buildName, filters);
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

        /// <summary>The API answers unknown ids with {"error":"Profile not found"}.</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class PlannerData
    {
        [JsonPropertyName("lootFilters")]
        public List<PlannerLootFilter> LootFilters { get; set; } = [];
    }

    private sealed class PlannerLootFilter
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }

    [GeneratedRegex(@"maxroll\.gg/d4/planner/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlannerUrlPattern();

    [GeneratedRegex(@"[""':/]d4/planner/([a-z0-9]{6,12})(?![a-z0-9])")]
    private static partial Regex PlannerReferencePattern();
}
