using D4LootBench.Core.Models;
using D4LootBench.Core.Validation;
using Shouldly;
using static D4LootBench.Core.Validation.RedundancyAnalyzer;

namespace D4LootBench.Core.Tests.Validation;

/// <summary>
/// Tests for the full redundancy/conflict analysis: shadowed rules (dead under a broader
/// earlier rule), conflicts (shadowed with a different effect), obsolete rules (a later
/// broader rule with the same effect), per-entry coverage, merge suggestions, and
/// within-rule contradictions.
/// </summary>
public class RuleAnalysisTests
{
    private static readonly IFilterValidator Validator = new FilterValidator();

    private static FilterRule Rule(
        string name = "R",
        Visibility v = Visibility.Show,
        uint color = 0,
        bool enabled = true,
        params Condition[] conditions) =>
        new(name, v, color, conditions, enabled);

    private static RedundancyReport Analyze(params FilterRule[] rules) =>
        RedundancyAnalyzer.Analyze(new FilterRuleset("F", rules));

    // ── Condition implication ─────────────────────────────────────────────

    [Fact]
    public void ItemPower_ContainedRange_Implies()
    {
        ConditionImplies(new ItemPowerCondition(700, 800), new ItemPowerCondition(600, 900)).ShouldBeTrue();
        ConditionImplies(new ItemPowerCondition(500, 800), new ItemPowerCondition(600, 900)).ShouldBeFalse();
    }

    [Fact]
    public void Rarity_SubsetMask_Implies()
    {
        ConditionImplies(new RarityCondition(RarityFlags.Legendary), new RarityCondition(RarityFlags.LegendaryPlus)).ShouldBeTrue();
        ConditionImplies(new RarityCondition(RarityFlags.LegendaryPlus), new RarityCondition(RarityFlags.Legendary)).ShouldBeFalse();
    }

    [Fact]
    public void GreaterAffix_HigherMinimum_Implies()
    {
        ConditionImplies(new GreaterAffixCondition(3), new GreaterAffixCondition(2)).ShouldBeTrue();
        ConditionImplies(new GreaterAffixCondition(2), new GreaterAffixCondition(3)).ShouldBeFalse();
    }

    [Fact]
    public void ItemTypes_SubsetList_Implies()
    {
        ConditionImplies(new ItemTypeCondition([1u]), new ItemTypeCondition([1u, 2u])).ShouldBeTrue();
        ConditionImplies(new ItemTypeCondition([1u, 3u]), new ItemTypeCondition([1u, 2u])).ShouldBeFalse();
    }

    [Fact]
    public void Affixes_SubsetWithHigherCount_Implies()
    {
        // Needing 2 of {1,2} is stricter than needing 1 of {1,2,3}.
        ConditionImplies(new AffixCondition([1u, 2u], 2), new AffixCondition([1u, 2u, 3u], 1)).ShouldBeTrue();
        ConditionImplies(new AffixCondition([1u, 2u, 3u], 1), new AffixCondition([1u, 2u], 2)).ShouldBeFalse();
    }

    [Fact]
    public void Affixes_BroaderGreaterRequirementNotImposedByStricter_DoesNotImply()
    {
        var broader  = new AffixCondition([1u, 2u], 1) { GreaterEntries = [new(1u, 1u)] };
        var stricter = new AffixCondition([1u], 1);
        ConditionImplies(stricter, broader).ShouldBeFalse();

        var stricterWithGreater = new AffixCondition([1u], 1) { GreaterEntries = [new(1u, 1u)] };
        ConditionImplies(stricterWithGreater, broader).ShouldBeTrue();
    }

    [Fact]
    public void CrossTypeConditions_NeverImply()
    {
        ConditionImplies(new ItemTypeCondition([1u]), new RarityCondition(RarityFlags.All)).ShouldBeFalse();
    }

    // ── Shadowed rules ────────────────────────────────────────────────────

    [Fact]
    public void NarrowerRuleBelowBroaderSameEffect_IsShadowedRedundant()
    {
        var report = Analyze(
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u, 3u])),
            Rule("Rings", conditions: new ItemTypeCondition([1u])));

        var s = report.ShadowedRules.ShouldHaveSingleItem();
        (s.Index, s.ByIndex, s.SameEffect).ShouldBe((1, 0, true));
    }

    [Fact]
    public void NarrowerRuleBelowBroaderDifferentEffect_IsConflict()
    {
        var report = Analyze(
            Rule("Hide rings", Visibility.HideAll, conditions: new ItemTypeCondition([1u, 2u])),
            Rule("Show crit rings", conditions: [new ItemTypeCondition([1u]), new OptionalAffixCondition([9u], 1)]));

        var s = report.ShadowedRules.ShouldHaveSingleItem();
        (s.Index, s.ByIndex, s.SameEffect).ShouldBe((1, 0, false));
    }

    [Fact]
    public void ExtraConditionOnLaterRule_StillShadowed()
    {
        // The later rule is stricter (more conditions) — everything it matches, the earlier matches.
        var report = Analyze(
            Rule("Legendaries", conditions: new RarityCondition(RarityFlags.Legendary)),
            Rule("GA legendaries", conditions: [new RarityCondition(RarityFlags.Legendary), new GreaterAffixCondition(2)]));

        report.ShadowedRules.ShouldHaveSingleItem().Index.ShouldBe(1);
    }

    [Fact]
    public void ExactDuplicates_NotAlsoReportedAsShadowed()
    {
        var report = Analyze(
            Rule("A", conditions: new CodexCondition()),
            Rule("B", conditions: new CodexCondition()));

        report.Duplicates.ShouldHaveSingleItem();
        report.ShadowedRules.ShouldBeEmpty();
    }

    [Fact]
    public void SameConditionsDifferentVisibility_ReportedAsConflictNotDuplicate()
    {
        var report = Analyze(
            Rule("Show", Visibility.Show, conditions: new GreaterAffixCondition(2)),
            Rule("Hide", Visibility.HideAll, conditions: new GreaterAffixCondition(2)));

        report.Duplicates.ShouldBeEmpty();
        var s = report.ShadowedRules.ShouldHaveSingleItem();
        (s.Index, s.SameEffect).ShouldBe((1, false));
    }

    [Fact]
    public void DisabledBroaderRule_DoesNotShadow()
    {
        var report = Analyze(
            Rule("All", enabled: false, conditions: new ItemTypeCondition([1u, 2u])),
            Rule("Rings", conditions: new ItemTypeCondition([1u])));

        report.ShadowedRules.ShouldBeEmpty();
    }

    [Fact]
    public void RulesBelowShadowingCatchAll_NotSeparatelyFlagged()
    {
        var report = Analyze(
            Rule("Catch-all"),
            Rule("Broad", conditions: new ItemTypeCondition([1u, 2u])),
            Rule("Narrow", conditions: new ItemTypeCondition([1u])));

        report.ShadowingCatchAll.ShouldBe(0);
        report.ShadowedRules.ShouldBeEmpty();
        report.ObsoleteRules.ShouldBeEmpty();
    }

    // ── Obsolete rules (later broader rule, same effect) ──────────────────

    [Fact]
    public void NarrowRuleAboveBroaderSameEffect_IsObsolete()
    {
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u, 3u])));

        var o = report.ObsoleteRules.ShouldHaveSingleItem();
        (o.Index, o.ByIndex).ShouldBe((0, 1));
    }

    [Fact]
    public void ObsoleteBlockedByPossiblyOverlappingRuleInBetween_NotFlagged()
    {
        // The hide rule in between could match the same items (same type ids) — removing
        // the top rule would change their outcome, so it must not be called redundant.
        var report = Analyze(
            Rule("Crit rings", conditions: [new ItemTypeCondition([1u]), new OptionalAffixCondition([9u], 1)]),
            Rule("Hide rings", Visibility.HideAll, conditions: new ItemTypeCondition([1u])),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u])));

        report.ObsoleteRules.ShouldBeEmpty();
    }

    [Fact]
    public void ObsoleteWithProvablyDisjointRuleInBetween_Flagged()
    {
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("Hide boots", Visibility.HideAll, conditions: new ItemTypeCondition([5u])),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u])));

        var o = report.ObsoleteRules.ShouldHaveSingleItem();
        (o.Index, o.ByIndex).ShouldBe((0, 2));
    }

    [Fact]
    public void ObsoleteWithSameEffectRuleInBetween_Flagged()
    {
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("GA stuff", conditions: new GreaterAffixCondition(2)),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u])));

        report.ObsoleteRules.ShouldHaveSingleItem().Index.ShouldBe(0);
    }

    [Fact]
    public void RuleAboveSameEffectCatchAll_IsObsolete()
    {
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("Show everything"));

        var o = report.ObsoleteRules.ShouldHaveSingleItem();
        (o.Index, o.ByIndex).ShouldBe((0, 1));
    }

    // ── Covered list entries ──────────────────────────────────────────────

    [Fact]
    public void UniqueAlreadyShownByEarlierRule_ReportedAsCoveredEntry()
    {
        var report = Analyze(
            Rule("Helms", conditions: new SpecificUniqueCondition([100u, 101u])),
            Rule("Weapons", conditions: new SpecificUniqueCondition([101u, 102u])));

        var c = report.CoveredEntries.ShouldHaveSingleItem();
        (c.Index, c.ByIndex, c.Kind, c.SameEffect).ShouldBe((1, 0, EntryKind.Unique, true));
        c.Ids.ShouldBe([101u]);
    }

    [Fact]
    public void UniqueShownEarlierButHiddenLater_ReportedAsCoveredConflict()
    {
        var report = Analyze(
            Rule("Keep", conditions: new SpecificUniqueCondition([100u, 101u])),
            Rule("Trash", Visibility.HideAll, conditions: new SpecificUniqueCondition([101u, 102u])));

        var c = report.CoveredEntries.ShouldHaveSingleItem();
        (c.Index, c.SameEffect).ShouldBe((1, false));
        c.Ids.ShouldBe([101u]);
    }

    [Fact]
    public void CoverageRespectsOtherConditions()
    {
        // Earlier rule only covers high-power items; the later rule has no power floor,
        // so its entries are NOT fully covered.
        var report = Analyze(
            Rule("GG rings", conditions: [new ItemTypeCondition([1u, 2u]), new ItemPowerCondition(800, 900)]),
            Rule("Rings", conditions: new ItemTypeCondition([1u, 3u])));

        report.CoveredEntries.ShouldBeEmpty();
    }

    [Fact]
    public void FullyShadowedRule_EntriesNotItemized()
    {
        var report = Analyze(
            Rule("All", conditions: new ItemTypeCondition([1u, 2u, 3u])),
            Rule("Some", conditions: new ItemTypeCondition([1u, 2u])));

        report.ShadowedRules.ShouldHaveSingleItem();
        report.CoveredEntries.ShouldBeEmpty();
    }

    [Fact]
    public void OptionalAffixEntryCoveredByEarlierAnyOfRule_Reported()
    {
        var report = Analyze(
            Rule("Core stats", conditions: new OptionalAffixCondition([10u, 11u], 1)),
            Rule("More stats", conditions: new OptionalAffixCondition([11u, 12u], 1)));

        var c = report.CoveredEntries.ShouldHaveSingleItem();
        (c.Kind, c.SameEffect).ShouldBe((EntryKind.Affix, true));
        c.Ids.ShouldBe([11u]);
    }

    // ── Merge candidates ──────────────────────────────────────────────────

    [Fact]
    public void RulesIdenticalExceptItemTypes_SuggestedAsMerge()
    {
        var report = Analyze(
            Rule("Rings", conditions: [new ItemTypeCondition([1u]), new GreaterAffixCondition(2)]),
            Rule("Amulets", conditions: [new ItemTypeCondition([2u]), new GreaterAffixCondition(2)]),
            Rule("Boots", conditions: [new ItemTypeCondition([3u]), new GreaterAffixCondition(2)]));

        var m = report.MergeCandidates.ShouldHaveSingleItem();
        m.Indices.ShouldBe([0, 1, 2]);
        m.Kind.ShouldBe(EntryKind.ItemType);
        m.MergedIds.ShouldBe([1u, 2u, 3u]);

        // The ready-to-apply rule: first member's identity, combined list, other conditions intact.
        m.MergedRule.Name.ShouldBe("Rings");
        m.MergedRule.Conditions.OfType<ItemTypeCondition>().Single().TypeIds.ShouldBe([1u, 2u, 3u]);
        m.MergedRule.Conditions.OfType<GreaterAffixCondition>().Single().MinimumCount.ShouldBe(2);
    }

    [Fact]
    public void MergeBlockedByConflictingOverlappingRuleInBetween()
    {
        // The hide rule in between shares type 2 with the second member — merging would pull
        // those items above the hide and change behavior.
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("Hide amulets", Visibility.HideAll, conditions: new ItemTypeCondition([2u])),
            Rule("Amulets+", conditions: new ItemTypeCondition([2u, 3u])));

        report.MergeCandidates.ShouldBeEmpty();
    }

    [Fact]
    public void MergeAllowedWhenRuleInBetweenIsDisjoint()
    {
        var report = Analyze(
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("Hide boots", Visibility.HideAll, conditions: new ItemTypeCondition([5u])),
            Rule("Amulets", conditions: new ItemTypeCondition([2u])));

        report.MergeCandidates.ShouldHaveSingleItem().Indices.ShouldBe([0, 2]);
    }

    [Fact]
    public void MergeNotSuggestedWhenUnionExceedsGameCap()
    {
        var first  = Enumerable.Range(1, 6).Select(i => (uint)i).ToList();
        var second = Enumerable.Range(7, 6).Select(i => (uint)i).ToList();
        var report = Analyze(
            Rule("A", conditions: new SpecificUniqueCondition(first)),
            Rule("B", conditions: new SpecificUniqueCondition(second)));

        // 12 uniques > the game cap of 10 per condition.
        report.MergeCandidates.ShouldBeEmpty();
    }

    [Fact]
    public void RulesDifferingInTwoConditions_NotMergeable()
    {
        var report = Analyze(
            Rule("A", conditions: [new ItemTypeCondition([1u]), new GreaterAffixCondition(2)]),
            Rule("B", conditions: [new ItemTypeCondition([2u]), new GreaterAffixCondition(3)]));

        report.MergeCandidates.ShouldBeEmpty();
    }

    [Fact]
    public void RecolorRulesWithDifferentColors_NotMergeable()
    {
        var report = Analyze(
            Rule("A", Visibility.Recolor, 0xFF0000FF, conditions: new ItemTypeCondition([1u])),
            Rule("B", Visibility.Recolor, 0xFF00FF00, conditions: new ItemTypeCondition([2u])));

        report.MergeCandidates.ShouldBeEmpty();
    }

    // ── Contradictions ────────────────────────────────────────────────────

    [Fact]
    public void DisjointPowerRangesInOneRule_Contradiction()
    {
        var report = Analyze(
            Rule("Impossible", conditions: [new ItemPowerCondition(100, 200), new ItemPowerCondition(700, 900)]));

        report.Contradictions.ShouldHaveSingleItem().Index.ShouldBe(0);
    }

    [Fact]
    public void DisjointRarityMasksInOneRule_Contradiction()
    {
        var report = Analyze(
            Rule("Impossible", conditions: [
                new RarityCondition(RarityFlags.Legendary),
                new RarityCondition(RarityFlags.Common | RarityFlags.Magic)]));

        report.Contradictions.ShouldHaveSingleItem().Index.ShouldBe(0);
    }

    [Fact]
    public void OverlappingConditions_NoContradiction()
    {
        var report = Analyze(
            Rule("Fine", conditions: [new ItemPowerCondition(100, 700), new ItemPowerCondition(600, 900)]));

        report.Contradictions.ShouldBeEmpty();
    }

    // ── Validator integration ─────────────────────────────────────────────

    [Fact]
    public void Validator_ShadowedConflict_IsWarningWithActionableMessage()
    {
        var result = Validator.Validate(new FilterRuleset("F",
        [
            Rule("Hide rings", Visibility.HideAll, conditions: new ItemTypeCondition([1u, 2u])),
            Rule("Show my ring", conditions: new ItemTypeCondition([1u])),
        ]));

        result.IsValid.ShouldBeTrue();
        var warning = result.Warnings.ShouldHaveSingleItem();
        warning.RuleIndex.ShouldBe(1);
        warning.Message.ShouldContain("conflicts with");
        warning.Message.ShouldContain("Move it above rule 1");
    }

    [Fact]
    public void Validator_ObsoleteAndMergeFindings_AreInfoNotWarning()
    {
        var result = Validator.Validate(new FilterRuleset("F",
        [
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u, 3u])),
        ]));

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
        result.Issues.ShouldContain(i =>
            i.Severity == ValidationSeverity.Info && i.Message.Contains("is redundant"));
    }

    [Fact]
    public void Validator_MergeSuggestion_NamesRulesAndCombinedList()
    {
        var result = Validator.Validate(new FilterRuleset("F",
        [
            Rule("Rings", conditions: [new ItemTypeCondition([1u]), new GreaterAffixCondition(2)]),
            Rule("Amulets", conditions: [new ItemTypeCondition([2u]), new GreaterAffixCondition(2)]),
        ]));

        var info = result.Issues.ShouldHaveSingleItem();
        info.Severity.ShouldBe(ValidationSeverity.Info);
        info.Message.ShouldContain("Rules 1 (\"Rings\") and 2 (\"Amulets\") differ only in their item types");
        // The combined list is spelled out so the user can see what the merged rule would match.
        info.Message.ShouldContain("Unknown item type (0x00000001), Unknown item type (0x00000002)");
    }

    [Fact]
    public void Validator_Contradiction_IsWarning()
    {
        var result = Validator.Validate(new FilterRuleset("F",
        [
            Rule("Impossible", conditions: [new ItemPowerCondition(100, 200), new ItemPowerCondition(700, 900)]),
        ]));

        result.Warnings.ShouldContain(w => w.Message.Contains("never matches anything"));
    }

    [Fact]
    public void Validator_InfoIssues_DoNotBlockExport()
    {
        var result = Validator.Validate(new FilterRuleset("F",
        [
            Rule("Rings", conditions: new ItemTypeCondition([1u])),
            Rule("All jewelry", conditions: new ItemTypeCondition([1u, 2u])),
        ]));

        result.IsValid.ShouldBeTrue();
    }
}
