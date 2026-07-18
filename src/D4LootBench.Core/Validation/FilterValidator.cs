using D4LootBench.Core.Models;
using D4LootBench.Core.Serialization;

namespace D4LootBench.Core.Validation;

public sealed class FilterValidator : IFilterValidator
{
    public const int MaxRuleNameLength = 24;
    public const int ItemPowerCap      = 900;
    public const int GreaterAffixMinCountFloor = 1;
    public const int GreaterAffixMinCountCeiling = 4;

    public ValidationResult Validate(FilterRuleset ruleset)
    {
        var issues = new List<ValidationIssue>();

        if (ruleset.Rules.Count > FilterRuleset.MaxRuleCount)
            issues.Add(new(ValidationSeverity.Error,
                $"Filter has {ruleset.Rules.Count} rules — maximum is {FilterRuleset.MaxRuleCount}."));

        for (var i = 0; i < ruleset.Rules.Count; i++)
            ValidateRule(ruleset.Rules[i], i, issues);

        AddRedundancyWarnings(ruleset, issues);

        return new ValidationResult(issues);
    }

    private static void AddRedundancyWarnings(FilterRuleset ruleset, List<ValidationIssue> issues)
    {
        var report = RedundancyAnalyzer.Analyze(ruleset);
        string Label(int index) => $"rule {index + 1} (\"{ruleset.Rules[index].Name}\")";
        string Verb(int index) => ruleset.Rules[index].Visibility switch
        {
            Visibility.Show    => "shows",
            Visibility.Recolor => "recolors",
            _                  => "hides",
        };
        string PastVerb(int index) => ruleset.Rules[index].Visibility switch
        {
            Visibility.Show    => "shown",
            Visibility.Recolor => "recolored",
            _                  => "hidden",
        };

        foreach (var dup in report.Duplicates)
            issues.Add(new(ValidationSeverity.Warning,
                $"Rule {dup.Index + 1} (\"{ruleset.Rules[dup.Index].Name}\") duplicates {Label(dup.DuplicateOfIndex)} — it never takes effect and can be removed.",
                dup.Index));

        if (report.ShadowingCatchAll is int catchAll)
        {
            var below = RedundancyAnalyzer.CountEnabledRulesBelow(ruleset, catchAll);
            issues.Add(new(ValidationSeverity.Warning,
                $"Rule {catchAll + 1} (\"{ruleset.Rules[catchAll].Name}\") has no conditions, so it matches every item — the {below} enabled rule(s) below it will never apply.",
                catchAll));
        }

        foreach (var s in report.ShadowedRules)
            issues.Add(new(ValidationSeverity.Warning, s.SameEffect
                ? $"Rule {s.Index + 1} (\"{ruleset.Rules[s.Index].Name}\") never takes effect: broader {Label(s.ByIndex)} above it already {Verb(s.ByIndex)} everything it matches — it can be removed."
                : $"Rule {s.Index + 1} (\"{ruleset.Rules[s.Index].Name}\") conflicts with {Label(s.ByIndex)}: rule {s.ByIndex + 1} matches everything rule {s.Index + 1} matches and {Verb(s.ByIndex)} it first, so rule {s.Index + 1} never {Verb(s.Index)} anything. Move it above rule {s.ByIndex + 1} if it should win.",
                s.Index));

        foreach (var o in report.ObsoleteRules)
            issues.Add(new(ValidationSeverity.Info,
                $"Rule {o.Index + 1} (\"{ruleset.Rules[o.Index].Name}\") is redundant: {Label(o.ByIndex)} below it already {Verb(o.ByIndex)} everything it matches — removing it frees one of the {FilterRuleset.MaxRuleCount} rule slots.",
                o.Index));

        foreach (var c in report.CoveredEntries)
        {
            var names = string.Join(", ", c.Ids.Select(id => EntryName(c.Kind, id)));
            issues.Add(new(c.SameEffect ? ValidationSeverity.Info : ValidationSeverity.Warning, c.SameEffect
                ? $"Rule {c.Index + 1} (\"{ruleset.Rules[c.Index].Name}\"): items matching {names} are already {PastVerb(c.ByIndex)} by {Label(c.ByIndex)} — these entries can be removed here."
                : $"Rule {c.Index + 1} (\"{ruleset.Rules[c.Index].Name}\") never sees items matching {names} — {Label(c.ByIndex)} {Verb(c.ByIndex)} them first. Move rule {c.Index + 1} higher if it should win for them.",
                c.Index));
        }

        foreach (var m in report.MergeCandidates)
        {
            var members = m.Indices.Select(i => $"{i + 1} (\"{ruleset.Rules[i].Name}\")").ToList();
            var list    = string.Join(", ", members.Take(members.Count - 1)) + $" and {members[^1]}";
            var noun = m.Kind switch
            {
                RedundancyAnalyzer.EntryKind.ItemType => "item types",
                RedundancyAnalyzer.EntryKind.Unique   => "unique items",
                _                                     => "affixes",
            };
            var mergedNames = string.Join(", ", m.MergedIds.Select(id => EntryName(m.Kind, id)));
            issues.Add(new(ValidationSeverity.Info,
                $"Rules {list} differ only in their {noun} (everything else is the same) — one combined rule matching {mergedNames} does the same job and frees {m.Indices.Count - 1} of the {FilterRuleset.MaxRuleCount} rule slots. The Combine button above the rule list applies this.",
                m.Indices[0]));
        }

        foreach (var c in report.Contradictions)
            issues.Add(new(ValidationSeverity.Warning,
                $"Rule {c.Index + 1} (\"{ruleset.Rules[c.Index].Name}\"): its {DescribePair(c.First, c.Second)} can never both match one item — this rule never matches anything.",
                c.Index));
    }

    private static string DescribePair(Condition a, Condition b) => (a, b) switch
    {
        (ItemPowerCondition, ItemPowerCondition)           => "item power ranges don't overlap, so they",
        (RarityCondition, RarityCondition)                 => "rarity selections don't overlap, so they",
        (ItemPropertiesCondition, ItemPropertiesCondition) => "item property selections don't overlap, so they",
        (ItemTypeCondition, ItemTypeCondition)             => "item type lists share no type, so they",
        (SpecificUniqueCondition, SpecificUniqueCondition) => "unique item lists share no item, so they",
        _                                                  => "conditions contradict each other and",
    };

    /// <summary>Display name for a covered list entry; hex when no data context is available
    /// (the validator must stay usable without one).</summary>
    private static string EntryName(RedundancyAnalyzer.EntryKind kind, uint id)
    {
        try
        {
            var data = FilterDataContext.Current;
            return kind switch
            {
                RedundancyAnalyzer.EntryKind.ItemType => data.ItemTypes.GetDisplayName(id),
                RedundancyAnalyzer.EntryKind.Unique   => data.Uniques.GetDisplayName(id),
                _                                     => data.Affixes.GetDisplayName(id),
            };
        }
        catch (InvalidOperationException)
        {
            return $"0x{id:X8}";
        }
    }

    private static void ValidateRule(FilterRule rule, int index, List<ValidationIssue> issues)
    {
        var prefix = $"Rule {index + 1} (\"{rule.Name}\")";

        if (rule.Name.Length > MaxRuleNameLength)
            issues.Add(new(ValidationSeverity.Error,
                $"{prefix}: name is {rule.Name.Length} characters — maximum is {MaxRuleNameLength}.", index));

        foreach (var cond in rule.Conditions)
        {
            switch (cond)
            {
                case ItemPowerCondition ip when ip.Maximum > ItemPowerCap:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: item power maximum is {ip.Maximum} — game cap is {ItemPowerCap}.", index));
                    break;

                case GreaterAffixCondition ga when ga.MinimumCount < GreaterAffixMinCountFloor
                                                || ga.MinimumCount > GreaterAffixMinCountCeiling:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: greater affix minimum count is {ga.MinimumCount} — game allows {GreaterAffixMinCountFloor}–{GreaterAffixMinCountCeiling}.", index));
                    break;

                case AffixCondition a when a.AffixIds.Count > AffixCondition.MaxSelectionCount:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: required affixes has {a.AffixIds.Count} affixes — maximum is {AffixCondition.MaxSelectionCount}.", index));
                    break;

                case AffixCondition a when a.MinimumCount > a.AffixIds.Count:
                    issues.Add(new(ValidationSeverity.Warning,
                        $"{prefix}: required affixes needs {a.MinimumCount} matches but only {a.AffixIds.Count} affix(es) are selected — this rule can never match.", index));
                    break;

                case OptionalAffixCondition oa when oa.AffixIds.Count > OptionalAffixCondition.MaxSelectionCount:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: optional affixes has {oa.AffixIds.Count} affixes — maximum is {OptionalAffixCondition.MaxSelectionCount}.", index));
                    break;

                case OptionalAffixCondition oa when oa.MinimumCount > oa.AffixIds.Count:
                    issues.Add(new(ValidationSeverity.Warning,
                        $"{prefix}: optional affixes needs {oa.MinimumCount} matches but only {oa.AffixIds.Count} affix(es) are selected — this rule can never match.", index));
                    break;

                case SpecificUniqueCondition su when su.UniqueIds.Count > SpecificUniqueCondition.MaxSelectionCount:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: specific uniques has {su.UniqueIds.Count} items — maximum is {SpecificUniqueCondition.MaxSelectionCount}.", index));
                    break;

                case TalismanSetCondition ts when ts.SetIds.Count > TalismanSetCondition.MaxSelectionCount:
                    issues.Add(new(ValidationSeverity.Error,
                        $"{prefix}: talisman sets has {ts.SetIds.Count} sets — maximum is {TalismanSetCondition.MaxSelectionCount}.", index));
                    break;
            }
        }
    }
}
