namespace D4LootBench.Paragon.Solver;

/// <summary>One prioritized gear stat from the loot filter: affix display name + guide weight.</summary>
public sealed record GearStatPriority(string StatName, double Weight);

public sealed record GearReferenceResult(
    IReadOnlyList<ReferenceEmphasis> Emphasis,
    IReadOnlyList<string> UnmappedStats);

/// <summary>
/// Translates a build's GEAR priorities — the loot filter's per-slot affix wish list — into the
/// same per-attribute emphasis scores <see cref="BuildReference"/> derives from a paragon
/// allocation, so the loaded filter can vote in the reference consensus alongside imported
/// builds. The mapping is a curated table from gear affix display names to the paragon node
/// attribute names the boards actually grant; gear stats the boards can't supply at all
/// (e.g. Damage Reduction, skill ranks) are reported as unmapped rather than force-fitted.
/// </summary>
public static class GearReference
{
    private sealed record MapRule(Func<string, bool> Matches, string[] Attributes);

    private static MapRule Exact(string name, params string[] attributes) =>
        new(n => n.Equals(name, StringComparison.Ordinal), attributes);

    private static MapRule Has(string token, params string[] attributes) =>
        new(n => n.Contains(token, StringComparison.Ordinal), attributes);

    /// <summary>
    /// First match wins; names are normalized (lowercase, leading +/% stripped) before matching.
    /// Exact rules guard tokens that also appear in skill-rank affixes ("+Cyclone Armor" must
    /// not read as armor). Elemental damage rules precede the generic damage ones.
    /// </summary>
    private static readonly MapRule[] Rules =
    [
        Exact("strength", "Strength_Core"),
        Exact("intelligence", "Intelligence_Core"),
        Exact("willpower", "Willpower_Core"),
        Exact("dexterity", "Dexterity_Core"),
        Has("maximum life", "Hitpoints_Max_Percent_Bonus", "Flat_Hitpoints_Max_Bonus"),
        Exact("armor", "Armor_Percent"),
        Has("resistance", "Resistance_All_Bonus_Percent"),
        Has("critical strike chance", "Crit_Percent_Bonus"),
        Has("critical strike damage", "Crit_Damage_Percent"),
        Has("vulnerable damage", "Vulnerable_Health_Damage_Bonus"),
        Has("attack speed", "Attack_Speed_Percent_Bonus"),
        Has("cooldown reduction", "Power_Cooldown_Reduction_Percent_All"),
        Has("maximum resource", "Resource_Max_Bonus", "Resource_All_Primary_Max_Bonus"),
        Has("resource cost reduction", "Resource_Cost_Reduction_Percent_All"),
        Has("resource generation", "Resource_Gain_And_Regen_Bonus_Percent_All_Primary"),
        Has("damage over time", "DOT_DPS_Bonus_Percent"),
        Has("overpower damage", "Overpower_Damage_Bonus_Per_Stack"),
        Has("fire damage", "Damage_Type_Percent_Bonus"),
        Has("cold damage", "Damage_Type_Percent_Bonus"),
        Has("lightning damage", "Damage_Type_Percent_Bonus"),
        Has("poison damage", "Damage_Type_Percent_Bonus"),
        Has("shadow damage", "Damage_Type_Percent_Bonus"),
        Has("physical damage", "Damage_Type_Percent_Bonus"),
        Has("all damage", "Damage_Percent_All_From_Skills"),
        Has("skill damage", "Damage_Percent_Bonus_Per_Skill_Tag"),
        Has("damage to close", "Damage_Bonus_To_Near"),
        Has("damage to distant", "Damage_Bonus_To_Far"),
        Has("crowd control", "Damage_Percent_Bonus_Vs_CC_All"),
        Has("elites", "Damage_Percent_Bonus_Vs_Elites"),
        Has("fortif", "Damage_Percent_Bonus_When_Fortified", "Fortified_Health_Application_Bonus"),
        Has("dodge", "Dodge_Chance_Bonus", "Dodge_Chance_Bonus_Additive"),
        Has("healing received", "Bonus_Healing_Received_Percent"),
        Has("thorns", "Thorns_Flat"),
        Has("barrier", "Barrier_Bonus_Percent"),
        Has("movement speed", "Movement_Bonus_Run_Speed"),
        Has("block chance", "Block_Chance"),
        Has("lucky hit", "Combat_Effect_Chance_Bonus"),
    ];

    /// <summary>
    /// A gear stat mapping to several attributes gives each its full weight — they are alternate
    /// paragon representations of one concept (e.g. flat and percent life), not split shares.
    /// Several gear stats mapping to one attribute stack, exactly like stacked allocations do.
    /// </summary>
    public static GearReferenceResult EmphasisOf(IEnumerable<GearStatPriority> gearStats)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var unmapped = new List<string>();
        foreach (var stat in gearStats)
        {
            if (stat.Weight <= 0)
                continue;
            string name = Normalize(stat.StatName);
            var rule = Rules.FirstOrDefault(r => r.Matches(name));
            if (rule is null)
            {
                unmapped.Add(stat.StatName);
                continue;
            }
            foreach (string attribute in rule.Attributes)
                scores[attribute] = scores.GetValueOrDefault(attribute) + stat.Weight;
        }

        var emphasis = scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ReferenceEmphasis(kv.Key, kv.Value))
            .ToList();
        return new GearReferenceResult(emphasis, unmapped);
    }

    private static string Normalize(string statName) =>
        statName.TrimStart('+', '%', ' ').ToLowerInvariant().Trim();
}
