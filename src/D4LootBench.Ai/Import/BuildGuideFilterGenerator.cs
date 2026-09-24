using D4LootBench.Core.Import;
using D4LootBench.Core.Models;

namespace D4LootBench.Ai.Import;

/// <summary>
/// Converts a <see cref="ParsedBuildGuide"/> into a <see cref="FilterRuleset"/> by resolving
/// affix and item names to hash IDs via <see cref="NameResolver"/>.
/// No LLM involvement — purely deterministic name resolution and rule construction.
/// </summary>
public sealed class BuildGuideFilterGenerator(NameResolver nameResolver)
{
    private static readonly uint ColorSlot    = FilterRule.PackColor(255, 180,  0);  // gold
    private static readonly uint ColorUnique  = FilterRule.PackColor(160,  32, 240); // purple
    private static readonly uint ColorCharms  = FilterRule.PackColor( 50, 200,  50); // green
    private static readonly uint ColorHideAll = FilterRule.PackColor(  0,   0,   0); // black

    public BuildGuideImportResult Generate(ParsedBuildGuide guide)
    {
        var slotRules = new List<FilterRule>();
        var warnings  = new List<string>();
        var uniqueIds = new List<uint>();
        var hasTalisman = false;

        foreach (var slot in guide.Slots)
        {
            if (slot.IsTalismanSlot)
            {
                hasTalisman = true;
                continue;
            }

            if (TryResolveUnique(slot, warnings, out var uniqueHash))
            {
                if (uniqueHash.HasValue)
                    uniqueIds.Add(uniqueHash.Value);
                continue;
            }

            var rule = BuildSlotRule(slot, warnings);
            if (rule is not null)
                slotRules.Add(rule);
        }

        var outputRules = new List<FilterRule>();

        if (hasTalisman)
            outputRules.Add(new FilterRule("All Charms", Visibility.Show, ColorCharms,
                [new TalismanSetCondition()]));

        if (uniqueIds.Count > 0)
            outputRules.Add(new FilterRule("Target Uniques", Visibility.Show, ColorUnique,
                [new SpecificUniqueCondition(uniqueIds)]));

        // Game-enforced 25-rule cap: keep the trailing Hide All, drop the lowest-priority slot rules.
        int slotBudget = FilterRuleset.MaxRuleCount - outputRules.Count - 1;
        if (slotRules.Count > slotBudget)
        {
            warnings.Add($"Guide produced {slotRules.Count} slot rules; kept the first {slotBudget} " +
                $"to stay within the game's {FilterRuleset.MaxRuleCount}-rule limit.");
            slotRules = slotRules.Take(slotBudget).ToList();
        }
        outputRules.AddRange(slotRules);

        outputRules.Add(new FilterRule("Hide All", Visibility.HideAll, ColorHideAll, []));

        return new BuildGuideImportResult
        {
            Ruleset  = new FilterRuleset("Build Guide Import", outputRules),
            Warnings = warnings
        };
    }

    private FilterRule? BuildSlotRule(ParsedSlot slot, List<string> warnings)
    {
        var conditions = new List<Condition>();

        var itemTypeName = MapSlotToItemType(slot.SlotLabel);
        if (itemTypeName is not null)
        {
            if (nameResolver.TryResolveItemType(itemTypeName, out var typeHash, out _))
                conditions.Add(new ItemTypeCondition([typeHash]));
            else
                warnings.Add($"Could not resolve item type: \"{itemTypeName}\"");
        }

        // Take up to 4 affixes in priority order (Priority=0 means positional — treat as already ordered)
        var targetAffixes = slot.Affixes
            .OrderBy(a => a.Priority == 0 ? int.MaxValue : a.Priority)
            .Take(4)
            .ToList();

        var affixIds       = new List<uint>();
        var greaterEntries = new List<GreaterAffixEntry>();

        foreach (var affix in targetAffixes)
        {
            if (nameResolver.TryResolveAffix(affix.RawName, out var affixHash, out _))
            {
                affixIds.Add(affixHash);
                if (affix.IsGreaterAffix)
                    greaterEntries.Add(new GreaterAffixEntry(affixHash, affixHash));
            }
            else
            {
                warnings.Add($"Could not resolve affix: \"{affix.RawName}\"");
            }
        }

        if (affixIds.Count > 0)
        {
            conditions.Add(new AffixCondition(affixIds, Math.Min(2, affixIds.Count))
            {
                GreaterEntries = greaterEntries
            });
        }

        if (conditions.Count == 0) return null;

        return new FilterRule(slot.SlotLabel, Visibility.Show, ColorSlot, conditions);
    }

    private bool TryResolveUnique(ParsedSlot slot, List<string> warnings, out uint? uniqueHash)
    {
        // Maxroll: "Unique Effect" sentinel is definitive proof the item is a specific unique
        if (slot.HasUniqueSentinel)
        {
            if (slot.ItemName is not null &&
                nameResolver.TryResolveUnique(slot.ItemName, out var hash, out _))
            {
                uniqueHash = hash;
            }
            else
            {
                if (slot.ItemName is not null)
                    warnings.Add($"Could not resolve unique: \"{slot.ItemName}\"");
                uniqueHash = null;
            }
            return true;
        }

        // Mobalytics / Icy Veins: attempt unique name lookup on the item name
        if (slot.ItemName is not null &&
            nameResolver.TryResolveUnique(slot.ItemName, out var id, out _))
        {
            uniqueHash = id;
            return true;
        }

        uniqueHash = null;
        return false;
    }

    private static string? MapSlotToItemType(string slotLabel) =>
        slotLabel.Trim().ToLowerInvariant() switch
        {
            "helm"                                              => "Helm",
            "chest armor" or "chest"                           => "Chest Armor",
            "gloves"                                           => "Gloves",
            "pants"                                            => "Pants",
            "boots"                                            => "Boots",
            "amulet"                                           => "Amulet",
            "ring 1" or "ring 2" or "rings"
                or "left ring" or "right ring"                 => "Ring",
            _                                                  => null  // ambiguous weapon / offhand slots
        };
}
