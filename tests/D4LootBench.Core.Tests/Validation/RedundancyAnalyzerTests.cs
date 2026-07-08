using D4LootBench.Core.Data;
using D4LootBench.Core.Models;
using D4LootBench.Core.Validation;
using Shouldly;

namespace D4LootBench.Core.Tests.Validation;

public class RedundancyAnalyzerTests
{
    private static readonly IFilterValidator Validator = new FilterValidator();

    private static FilterRule Rule(
        string name = "R",
        Visibility v = Visibility.Show,
        uint color = 0,
        bool enabled = true,
        params Condition[] conditions) =>
        new(name, v, color, conditions, enabled);

    // ── Duplicate rules ───────────────────────────────────────────────────

    [Fact]
    public void IdenticalRules_LaterFlaggedAsDuplicateOfFirst()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new GreaterAffixCondition(2)),
            Rule("B", conditions: new GreaterAffixCondition(2)),
            Rule("C", conditions: new GreaterAffixCondition(2)),
        ]);

        var dups = RedundancyAnalyzer.FindDuplicateRules(ruleset);
        dups.ShouldBe([new(1, 0), new(2, 0)]);
    }

    [Fact]
    public void DifferentNames_StillDuplicates_NamesAreCosmetic()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("First", conditions: new CodexCondition()),
            Rule("Totally different name", conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).Count.ShouldBe(1);
    }

    [Fact]
    public void AffixIdOrder_DoesNotDefeatDuplicateDetection()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new AffixCondition([1u, 2u, 3u], 2)),
            Rule("B", conditions: new AffixCondition([3u, 1u, 2u], 2)),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).Count.ShouldBe(1);
    }

    [Fact]
    public void ConditionOrder_DoesNotDefeatDuplicateDetection()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: [new GreaterAffixCondition(2), new ItemPowerCondition(700, 900)]),
            Rule("B", conditions: [new ItemPowerCondition(700, 900), new GreaterAffixCondition(2)]),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).Count.ShouldBe(1);
    }

    [Fact]
    public void DifferentMinimumCount_NotDuplicates()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new AffixCondition([1u, 2u], 1)),
            Rule("B", conditions: new AffixCondition([1u, 2u], 2)),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void DifferentGreaterEntries_NotDuplicates()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new AffixCondition([1u, 2u], 1) { GreaterEntries = [new(1u, 1u)] }),
            Rule("B", conditions: new AffixCondition([1u, 2u], 1)),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void RequiredVsOptionalAffixes_NotDuplicates()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new AffixCondition([1u], 1)),
            Rule("B", conditions: new OptionalAffixCondition([1u], 1)),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void RecolorRules_DifferentColors_NotDuplicates()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", Visibility.Recolor, FilterColors.Blue, conditions: new CodexCondition()),
            Rule("B", Visibility.Recolor, FilterColors.Gold, conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void ShowRules_DifferentColors_ColorIgnored_AreDuplicates()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", Visibility.Show, FilterColors.Blue, conditions: new CodexCondition()),
            Rule("B", Visibility.Show, FilterColors.Gold, conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).Count.ShouldBe(1);
    }

    [Fact]
    public void EarlierDisabled_LaterEnabled_NotADuplicate()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", enabled: false, conditions: new CodexCondition()),
            Rule("B", enabled: true, conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void EarlierEnabled_LaterDisabled_StillFlagged()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", enabled: true, conditions: new CodexCondition()),
            Rule("B", enabled: false, conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).Count.ShouldBe(1);
    }

    [Fact]
    public void UnknownConditions_CompareByTypeAndBytes()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new UnknownCondition(42, [1, 2, 3])),
            Rule("B", conditions: new UnknownCondition(42, [1, 2, 3])),
            Rule("C", conditions: new UnknownCondition(42, [9, 9, 9])),
        ]);
        RedundancyAnalyzer.FindDuplicateRules(ruleset).ShouldBe([new(1, 0)]);
    }

    // ── Catch-all shadowing ───────────────────────────────────────────────

    [Fact]
    public void CatchAllWithRulesBelow_IsFlagged()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("Top", conditions: new CodexCondition()),
            Rule("Catch-all"),
            Rule("Never reached", conditions: new GreaterAffixCondition(2)),
        ]);
        RedundancyAnalyzer.FindShadowingCatchAll(ruleset).ShouldBe(1);
        RedundancyAnalyzer.CountEnabledRulesBelow(ruleset, 1).ShouldBe(1);
    }

    [Fact]
    public void CatchAllAsLastRule_NotFlagged()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("Top", conditions: new CodexCondition()),
            Rule("Hide the rest", Visibility.HideAll),
        ]);
        RedundancyAnalyzer.FindShadowingCatchAll(ruleset).ShouldBeNull();
    }

    [Fact]
    public void DisabledCatchAll_NotFlagged()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("Catch-all", enabled: false),
            Rule("Reached fine", conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindShadowingCatchAll(ruleset).ShouldBeNull();
    }

    [Fact]
    public void CatchAllWithOnlyDisabledRulesBelow_NotFlagged()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("Catch-all"),
            Rule("Off anyway", enabled: false, conditions: new CodexCondition()),
        ]);
        RedundancyAnalyzer.FindShadowingCatchAll(ruleset).ShouldBeNull();
    }

    // ── Validator integration ─────────────────────────────────────────────

    [Fact]
    public void Validator_ReportsDuplicates_AsWarnings_NotErrors()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("A", conditions: new CodexCondition()),
            Rule("B", conditions: new CodexCondition()),
        ]);
        var result = Validator.Validate(ruleset);
        result.IsValid.ShouldBeTrue();
        var warning = result.Warnings.Single();
        warning.RuleIndex.ShouldBe(1);
        warning.Message.ShouldContain("duplicates rule 1");
    }

    [Fact]
    public void Validator_ReportsShadowingCatchAll_AsWarning()
    {
        var ruleset = new FilterRuleset("F",
        [
            Rule("Catch-all"),
            Rule("Shadowed", conditions: new CodexCondition()),
        ]);
        var result = Validator.Validate(ruleset);
        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Message.Contains("matches every item"));
    }

    [Fact]
    public void Validator_WarnsWhenRequiredCountExceedsSelectedAffixes()
    {
        var rule = Rule("A", conditions: new AffixCondition([1u, 2u], 3));
        var result = Validator.Validate(new FilterRuleset("F", [rule]));
        result.IsValid.ShouldBeTrue();
        result.Warnings.Single().Message.ShouldContain("can never match");
    }

    [Fact]
    public void Validator_WarnsWhenOptionalCountExceedsSelectedAffixes()
    {
        var rule = Rule("A", conditions: new OptionalAffixCondition([1u], 2));
        var result = Validator.Validate(new FilterRuleset("F", [rule]));
        result.Warnings.Single().Message.ShouldContain("can never match");
    }

    [Fact]
    public void Validator_NoWarning_WhenCountsAreSatisfiable()
    {
        var rule = Rule("A", conditions: new AffixCondition([1u, 2u], 2));
        Validator.Validate(new FilterRuleset("F", [rule])).HasIssues.ShouldBeFalse();
    }
}
