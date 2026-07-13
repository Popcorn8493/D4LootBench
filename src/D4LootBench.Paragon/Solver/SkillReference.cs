using D4LootBench.Paragon.Import;

namespace D4LootBench.Paragon.Solver;

public sealed record SkillReferenceResult(
    IReadOnlyList<ReferenceEmphasis> Emphasis,
    IReadOnlyList<string> ActiveSkillNames,
    string PrimaryDamageType);

/// <summary>
/// Translates a build's SKILL setup — the action bar plus skill-tree investment a guide import
/// carries — into the per-attribute emphasis <see cref="BuildReference"/> derives from a paragon
/// allocation, so a guide's skills vote in the reference consensus alongside its boards and the
/// loot filter. Two signals: the main skill's damage type (read from the bar-skill descriptions;
/// untyped skills deal physical) votes the damage-type and skill-damage nodes, and a curated
/// keyword table over bar-skill text and tree slugs (weighted by invested ranks) votes mechanics
/// the boards can serve — crit, vulnerable, overpower, fortify, resource, and so on. Skills are
/// deliberately never mapped to core stats: the class decides those, not the bar.
/// </summary>
public static class SkillReference
{
    /// <summary>The main skill's damage type carries the most signal — it IS the build.</summary>
    private const double DamageTypeWeight = 10;

    /// <summary>Skill-category damage ("Core skill damage" nodes) from the same main skill.</summary>
    private const double SkillTagWeight = 6;

    /// <summary>A bar slot signals commitment even when the tree ranks are unknown.</summary>
    private const double ActiveSkillWeight = 2;

    private sealed record MapRule(string Token, string[] Attributes);

    /// <summary>Matched against lowercase bar-skill name+description and de-kebabed tree slugs.</summary>
    private static readonly MapRule[] Rules =
    [
        new("vulnerable", ["Vulnerable_Health_Damage_Bonus"]),
        new("critical strike", ["Crit_Percent_Bonus", "Crit_Damage_Percent"]),
        new("overpower", ["Overpower_Damage_Bonus_Per_Stack"]),
        new("fortif", ["Damage_Percent_Bonus_When_Fortified", "Fortified_Health_Application_Bonus"]),
        new("barrier", ["Barrier_Bonus_Percent"]),
        new("thorns", ["Thorns_Flat"]),
        new("attack speed", ["Attack_Speed_Percent_Bonus"]),
        new("cooldown", ["Power_Cooldown_Reduction_Percent_All"]),
        new("lucky hit", ["Combat_Effect_Chance_Bonus"]),
        new("movement speed", ["Movement_Bonus_Run_Speed"]),
        new("damage over time", ["DOT_DPS_Bonus_Percent"]),
        new("bleed", ["DOT_DPS_Bonus_Percent"]),
        new("maximum life", ["Hitpoints_Max_Percent_Bonus", "Flat_Hitpoints_Max_Bonus"]),
        new("unstoppable", []),
        new("berserk", []),
    ];

    private static readonly string[] DamageTypes =
        ["Fire", "Cold", "Lightning", "Poison", "Shadow", "Physical"];

    private static readonly string[] ResourceNames =
        ["fury", "essence", "mana", "spirit", "vigor", "resolve", "faith"];

    public static SkillReferenceResult EmphasisOf(MobalyticsSkillVariant variant)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string attribute, double weight) =>
            scores[attribute] = scores.GetValueOrDefault(attribute) + weight;

        // The main skill: the bar skill with the deepest tree investment (first slot on ties).
        var main = variant.ActiveSkills
            .OrderByDescending(s => variant.TreeRanks.GetValueOrDefault(s.Slug))
            .FirstOrDefault();
        string damageType = "Physical";
        if (main is not null)
        {
            damageType = DamageTypeOf(main.Description)
                ?? variant.ActiveSkills.Select(s => DamageTypeOf(s.Description))
                    .FirstOrDefault(t => t is not null)
                ?? "Physical";
            Add("Damage_Type_Percent_Bonus", DamageTypeWeight);
            Add("Damage_Percent_Bonus_Per_Skill_Tag", SkillTagWeight);
        }

        foreach (var skill in variant.ActiveSkills)
            Vote($"{skill.Name} {skill.Description}", ActiveSkillWeight, Add);
        foreach (var (slug, ranks) in variant.TreeRanks)
            Vote(slug.Replace('-', ' '), ranks, Add);

        var emphasis = scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ReferenceEmphasis(kv.Key, kv.Value))
            .ToList();
        return new SkillReferenceResult(
            emphasis,
            variant.ActiveSkills.Select(s => s.Name).ToList(),
            damageType);
    }

    private static void Vote(string text, double weight, Action<string, double> add)
    {
        if (weight <= 0)
            return;
        string lower = text.ToLowerInvariant();
        foreach (var rule in Rules)
        {
            if (!lower.Contains(rule.Token, StringComparison.Ordinal))
                continue;
            foreach (string attribute in rule.Attributes)
                add(attribute, weight);
            return; // first match wins, mirroring GearReference
        }
        foreach (string resource in ResourceNames)
        {
            if (lower.Contains($"maximum {resource}", StringComparison.Ordinal))
                add("Resource_Max_Bonus", weight);
            if (lower.Contains($"{resource} generation", StringComparison.Ordinal)
                || lower.Contains($"generate {resource}", StringComparison.Ordinal))
                add("Resource_Gain_And_Regen_Bonus_Percent_All_Primary", weight);
        }
    }

    /// <summary>"deals {x} Lightning damage" → Lightning; untyped damage text → null.</summary>
    private static string? DamageTypeOf(string description) =>
        DamageTypes.FirstOrDefault(t =>
            description.Contains($"{t} damage", StringComparison.OrdinalIgnoreCase));
}
