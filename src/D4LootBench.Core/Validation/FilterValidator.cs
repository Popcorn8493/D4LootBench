using D4LootBench.Core.Models;

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
        foreach (var dup in RedundancyAnalyzer.FindDuplicateRules(ruleset))
        {
            var rule     = ruleset.Rules[dup.Index];
            var original = ruleset.Rules[dup.DuplicateOfIndex];
            issues.Add(new(ValidationSeverity.Warning,
                $"Rule {dup.Index + 1} (\"{rule.Name}\") duplicates rule {dup.DuplicateOfIndex + 1} (\"{original.Name}\") — it never takes effect and can be removed.",
                dup.Index));
        }

        if (RedundancyAnalyzer.FindShadowingCatchAll(ruleset) is int catchAll)
        {
            var rule  = ruleset.Rules[catchAll];
            var below = RedundancyAnalyzer.CountEnabledRulesBelow(ruleset, catchAll);
            issues.Add(new(ValidationSeverity.Warning,
                $"Rule {catchAll + 1} (\"{rule.Name}\") has no conditions, so it matches every item — the {below} enabled rule(s) below it will never apply.",
                catchAll));
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
