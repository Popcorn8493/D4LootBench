using System.Text;
using D4LootBench.Core.Data;

namespace D4LootBench.Ai;

/// <summary>
/// Builds the LLM system prompt once per session from the live data catalogs.
/// The prompt uses plain categorized name lists — no hash IDs.
/// <see cref="NameResolver"/> handles name → hash resolution after generation.
/// </summary>
public sealed class SystemPromptBuilder
{
    private readonly IFilterDataService _data;
    private readonly Lazy<string> _cached;

    private static readonly string[] Classes =
        ["Barbarian", "Druid", "Necromancer", "Paladin", "Rogue", "Sorcerer", "Spiritborn", "Warlock"];

    public SystemPromptBuilder(IFilterDataService data)
    {
        _data   = data;
        _cached = new Lazy<string>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Prompt => _cached.Value;

    private string Build()
    {
        var sb = new StringBuilder(8192);

        sb.AppendLine("""
            You are a Diablo IV loot filter rule generator.

            Respond ONLY with a single valid JSON object (no prose, no markdown fences) matching this schema:
            {
              "name": "<concise 2-5 word rule name>",
              "visibility": "Show" | "Recolor" | "HideAll",
              "conditions": [ <zero or more condition objects> ]
            }

            CONDITION TYPES — include only what is relevant to the request:
              { "type": "ItemType",        "items": ["exact name", ...] }  // names from ITEM TYPES list only (e.g. "Sword", "Helm")
              { "type": "ItemProperties",  "properties": ["Ancestral"] }  // valid values: "Ancestral", "Mythic", "None" (combinable)
              { "type": "Codex" }                                          // items usable to upgrade a Codex of Power entry; no parameters
              { "type": "ItemPower",       "minimum": 0, "maximum": 0 }    // maximum 0 = no cap; hard cap is 900
              { "type": "RequiredAffixes", "affixes": ["exact name", ...], "greaterAffixes": ["exact name", ...], "minimumCount": 1 }
                // greaterAffixes is optional; every name in greaterAffixes MUST also appear in affixes
              { "type": "OptionalAffixes", "affixes": ["exact name", ...], "greaterAffixes": ["exact name", ...], "minimumCount": 1 }
                // same greaterAffixes rule applies
              { "type": "Rarity",          "rarities": ["Legendary", ...] } // valid values: Common Magic Rare Legendary Unique Mythic Talisman
              { "type": "GreaterAffix",    "minimumCount": 1 }
              { "type": "SpecificUnique",  "items": ["exact unique name", ...] }  // names from UNIQUE ITEMS list only (e.g. "Eaglehorn", "Shako")
              { "type": "TalismanSet",     "sets": ["exact set name", ...] }

            """);

        AppendClassNames(sb);
        AppendAffixes(sb);
        AppendItemTypes(sb);
        AppendSkills(sb);
        AppendUniques(sb);
        AppendTalismanSets(sb);

        sb.AppendLine("""

            EXAMPLE — named Greater Affix (follow this pattern exactly):
            Request: "show items that have critical strike chance as a greater affix"
            Response:
            {
              "name": "GA Crit Chance",
              "visibility": "Show",
              "conditions": [
                {
                  "type": "RequiredAffixes",
                  "affixes": ["Critical Strike Chance"],
                  "greaterAffixes": ["Critical Strike Chance"],
                  "minimumCount": 1
                }
              ]
            }
            The GA affix appears in BOTH "affixes" AND "greaterAffixes". No separate GreaterAffix condition is added.

            RULES:
            - Use ONLY names from the lists above — do not invent or abbreviate names.
            - ItemType uses names from ITEM TYPES only. SpecificUnique uses names from UNIQUE ITEMS only. Never mix the two lists.
            - Use full class names. Never use nicknames (Barb, Sorc, Paly, Hammerdin, Auradin, etc.).
            - "greaterAffixes" is optional and defaults to empty. Only populate it when the user explicitly uses the
              word "greater" or the abbreviation "GA". Never add greaterAffixes speculatively.
            - "GA" means Greater Affix. Two distinct patterns:
              (a) Unqualified GA count ("has 2 GAs", "at least 1 GA") → emit ONLY a GreaterAffix condition with the
                  appropriate minimumCount. Do NOT add RequiredAffixes.
              (b) Named GA ("GA Crit Strike Damage", "Crit Strike Damage as GA") → emit a SINGLE RequiredAffixes condition
                  with the affix in BOTH "affixes" and "greaterAffixes". Do NOT also emit a separate GreaterAffix condition.
                  Multiple named GAs ("GA Crit + GA Attack Speed") → list both in "affixes" and both in "greaterAffixes".
            - Keep "name" concise (2-5 words) and at most 24 characters.
            - Respond with valid JSON only.
            """);

        return sb.ToString();
    }

    private static void AppendClassNames(StringBuilder sb)
    {
        sb.AppendLine("CLASS NAMES — use exactly as shown:");
        sb.AppendLine(string.Join(", ", Classes));
        sb.AppendLine();
    }

    private void AppendAffixes(StringBuilder sb)
    {
        sb.AppendLine("AFFIXES — use exact names only:");

        var allClass = _data.Affixes.ForClass("All");
        sb.Append("  All classes: ");
        sb.AppendLine(string.Join(", ", allClass.Select(a => a.Name)));

        foreach (var cls in Classes)
        {
            var classAffixes = _data.Affixes.ForClass(cls)
                .Where(a => !a.Classes.Contains("All"))
                .ToList();
            if (classAffixes.Count == 0) continue;
            sb.Append($"  {cls}: ");
            sb.AppendLine(string.Join(", ", classAffixes.Select(a => a.Name)));
        }
        sb.AppendLine();
    }

    private void AppendItemTypes(StringBuilder sb)
    {
        sb.AppendLine("ITEM TYPES — use exact names only:");
        var byCategory = _data.ItemTypes.All
            .GroupBy(t => t.Category)
            .OrderBy(g => g.Key);
        foreach (var group in byCategory)
        {
            sb.Append($"  {group.Key}: ");
            sb.AppendLine(string.Join(", ", group.Select(t => t.Name)));
        }
        sb.AppendLine();
    }

    private void AppendSkills(StringBuilder sb)
    {
        sb.AppendLine("SKILLS (for context only — not a filter condition type):");
        foreach (var cls in Classes)
        {
            var skills = _data.Skills.ForClass(cls)
                .Where(s => !s.Classes.Contains("All"))
                .ToList();
            if (skills.Count == 0) continue;
            sb.Append($"  {cls}: ");
            sb.AppendLine(string.Join(", ", skills.Select(s => s.Name)));
        }
        sb.AppendLine();
    }

    private void AppendUniques(StringBuilder sb)
    {
        sb.AppendLine("UNIQUE ITEMS — use exact names only:");
        var byClass = _data.Uniques.Released
            .GroupBy(u => u.Classes.Contains("All") ? "All classes" : string.Join("/", u.Classes))
            .OrderBy(g => g.Key);
        foreach (var group in byClass)
        {
            sb.Append($"  {group.Key}: ");
            sb.AppendLine(string.Join(", ", group.Select(u => u.Name)));
        }
        sb.AppendLine();
    }

    private void AppendTalismanSets(StringBuilder sb)
    {
        sb.AppendLine("TALISMAN SETS — use exact set names only:");
        foreach (var set in _data.TalismanSets.All)
        {
            var items = string.Join(", ", set.Items.Select(i => i.Name));
            sb.AppendLine($"  {set.Name} (items: {items})");
        }
        sb.AppendLine();
    }
}
