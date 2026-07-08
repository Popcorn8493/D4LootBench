using System.Text.Json;
using System.Text.Json.Serialization;
using D4LootBench.Core.Models;
using D4LootBench.Core.Validation;

namespace D4LootBench.Ai;

/// <summary>
/// Orchestrates the full rule-generation loop:
/// build system prompt → call provider → parse intermediate JSON →
/// resolve names to hashes → validate → return result.
/// </summary>
public sealed class RuleAssistant(
    ILlmProvider       provider,
    SystemPromptBuilder promptBuilder,
    NameResolver        resolver,
    IFilterValidator    validator)
{
    private static readonly JsonSerializerOptions _parseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RuleGenerationResult> GenerateAsync(
        string userPrompt, CancellationToken ct = default)
    {
        var completion = await provider.GetCompletionAsync(
            promptBuilder.Prompt, userPrompt, ct);

        if (!completion.IsSuccess)
            return RuleGenerationResult.Failed(completion.Error!);

        var raw = completion.Content!;

        IntermediateRule? intermediate;
        try
        {
            intermediate = JsonSerializer.Deserialize<IntermediateRule>(raw, _parseOptions);
        }
        catch (JsonException ex)
        {
            return RuleGenerationResult.Failed($"LLM returned invalid JSON: {ex.Message}", raw);
        }

        if (intermediate is null)
            return RuleGenerationResult.Failed("LLM returned null or empty response.", raw);

        var (conditions, errors, suggestions) = ResolveConditions(intermediate.Conditions);
        if (errors.Count > 0)
            return RuleGenerationResult.Failed(
                string.Join("; ", errors), raw, suggestions);

        if (!Enum.TryParse<Visibility>(intermediate.Visibility, ignoreCase: true, out var visibility))
            visibility = Visibility.Show;

        var warnings  = new List<string>();
        var ruleName  = TruncateAtWordBoundary(intermediate.Name, maxLength: 24, warnings);

        var rule = new FilterRule(
            ruleName,
            visibility,
            color:      0xFF808080,  // neutral grey default; UI can adjust
            conditions: conditions);

        var ruleset    = new Core.Models.FilterRuleset { Rules = [rule] };
        var validation = validator.Validate(ruleset);
        if (!validation.IsValid)
            return RuleGenerationResult.Failed(
                string.Join("; ", validation.Errors.Select(e => e.Message)), raw);

        return RuleGenerationResult.Succeeded(rule, raw, warnings);
    }

    private (List<Condition> conditions, List<string> errors, IReadOnlyList<string> suggestions)
        ResolveConditions(List<IntermediateCondition> intermediates)
    {
        var conditions  = new List<Condition>();
        var errors      = new List<string>();
        var suggestions = new List<string>();

        foreach (var ic in intermediates)
        {
            switch (ic.Type.ToLowerInvariant())
            {
                case "itemtype":
                {
                    var ids = ResolveNames(ic.Items ?? [],
                        (n, out h, out s) => resolver.TryResolveItemType(n, out h, out s),
                        errors, suggestions);
                    if (ids is not null)
                        conditions.Add(new ItemTypeCondition(ids));
                    break;
                }

                case "itempower":
                    conditions.Add(new ItemPowerCondition(ic.Minimum, ic.Maximum));
                    break;

                case "itemproperties":
                {
                    var mask = 0;
                    foreach (var p in ic.Properties ?? [])
                    {
                        if (p.Equals("Ancestral", StringComparison.OrdinalIgnoreCase))
                            mask |= 4;
                        else if (p.Equals("Mythic", StringComparison.OrdinalIgnoreCase))
                            mask |= 32;
                        else if (p.Equals("None", StringComparison.OrdinalIgnoreCase))
                            mask |= 1;
                        else
                            errors.Add($"Unknown item property '{p}'. Supported: 'Ancestral', 'Mythic', 'None'.");
                    }
                    if (errors.Count == 0)
                        conditions.Add(new ItemPropertiesCondition(mask == 0 ? 4 : mask));
                    break;
                }

                case "codex":
                    conditions.Add(new CodexCondition());
                    break;

                case "requiredaffixes":
                {
                    var ids = ResolveNames(ic.Affixes ?? [],
                        (n, out h, out s) => resolver.TryResolveAffix(n, out h, out s),
                        errors, suggestions);
                    var gaIds = ResolveNames(ic.GreaterAffixes ?? [],
                        (n, out h, out s) => resolver.TryResolveAffix(n, out h, out s),
                        errors, suggestions);
                    if (ids is not null && gaIds is not null)
                    {
                        var greaterEntries = gaIds.Select(h => new GreaterAffixEntry(h, h)).ToList();
                        conditions.Add(new AffixCondition(ids, ic.MinimumCount) { GreaterEntries = greaterEntries });
                    }
                    break;
                }

                case "optionalaffixes":
                {
                    var ids = ResolveNames(ic.Affixes ?? [],
                        (n, out h, out s) => resolver.TryResolveAffix(n, out h, out s),
                        errors, suggestions);
                    var gaIds = ResolveNames(ic.GreaterAffixes ?? [],
                        (n, out h, out s) => resolver.TryResolveAffix(n, out h, out s),
                        errors, suggestions);
                    if (ids is not null && gaIds is not null)
                    {
                        var greaterEntries = gaIds.Select(h => new GreaterAffixEntry(h, h)).ToList();
                        conditions.Add(new OptionalAffixCondition(ids, ic.MinimumCount) { GreaterEntries = greaterEntries });
                    }
                    break;
                }

                case "rarity":
                {
                    var mask = ParseRarityFlags(ic.Rarities ?? [], errors);
                    conditions.Add(new RarityCondition(mask));
                    break;
                }

                case "greateraffix":
                    conditions.Add(new GreaterAffixCondition(Math.Max(1, ic.MinimumCount)));
                    break;

                case "specificunique":
                {
                    var ids = ResolveNames(ic.Items ?? [],
                        (n, out h, out s) => resolver.TryResolveUnique(n, out h, out s),
                        errors, suggestions);
                    if (ids is not null)
                        conditions.Add(new SpecificUniqueCondition(ids));
                    break;
                }

                case "talismanset":
                {
                    var ids = ResolveNames(ic.Sets ?? [],
                        (n, out h, out s) => resolver.TryResolveTalismanSet(n, out h, out s),
                        errors, suggestions);
                    if (ids is not null)
                        conditions.Add(new TalismanSetCondition { SetIds = ids });
                    break;
                }

                default:
                    errors.Add($"Unknown condition type '{ic.Type}'.");
                    break;
            }
        }

        return (conditions, errors, suggestions);
    }

    private delegate bool Resolver(string name, out uint hash, out IReadOnlyList<string> suggestions);

    private static IReadOnlyList<uint>? ResolveNames(
        List<string> names, Resolver resolve,
        List<string> errors, List<string> allSuggestions)
    {
        var hashes = new List<uint>(names.Count);
        foreach (var name in names)
        {
            if (resolve(name, out var hash, out var suggestions))
            {
                hashes.Add(hash);
            }
            else
            {
                errors.Add($"Unknown name '{name}'.");
                allSuggestions.AddRange(suggestions);
            }
        }
        return errors.Count == 0 ? hashes : null;
    }

    private static RarityFlags ParseRarityFlags(List<string> rarities, List<string> errors)
    {
        var mask = RarityFlags.None;
        foreach (var r in rarities)
        {
            if (Enum.TryParse<RarityFlags>(r, ignoreCase: true, out var flag))
                mask |= flag;
            else
                errors.Add($"Unknown rarity '{r}'.");
        }
        return mask == RarityFlags.None ? RarityFlags.Legendary : mask;
    }

    private static string TruncateAtWordBoundary(string name, int maxLength, List<string> warnings)
    {
        if (name.Length <= maxLength) return name;

        var cut = name.LastIndexOf(' ', maxLength - 1);
        var truncated = cut > 0 ? name[..cut] : name[..maxLength];
        warnings.Add($"Name truncated from \"{name}\" to \"{truncated}\" to fit the 24-character limit.");
        return truncated;
    }

    // ── Intermediate deserialization model (internal) ────────────────────────

    private sealed class IntermediateRule
    {
        public string Name       { get; set; } = "";
        public string Visibility { get; set; } = "Show";

        [JsonPropertyName("conditions")]
        public List<IntermediateCondition> Conditions { get; set; } = [];
    }

    private sealed class IntermediateCondition
    {
        public string       Type         { get; set; } = "";
        public List<string>? Items       { get; set; }
        public List<string>? Properties     { get; set; }
        public List<string>? Affixes        { get; set; }
        public List<string>? GreaterAffixes { get; set; }
        public List<string>? Rarities       { get; set; }
        public List<string>? Sets        { get; set; }
        public int           MinimumCount { get; set; } = 1;
        public int           Minimum      { get; set; }
        public int           Maximum      { get; set; }
    }
}
