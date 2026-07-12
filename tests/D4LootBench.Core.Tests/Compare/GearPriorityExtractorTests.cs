using D4LootBench.Core.Compare;
using D4LootBench.Core.Models;
using Shouldly;

namespace D4LootBench.Core.Tests.Compare;

public sealed class GearPriorityExtractorTests
{
    private const uint Gloves = 100, Helm = 101;
    private const uint CritChance = 1, AttackSpeed = 2, MaxLife = 3, Willpower = 4, Armor = 5;

    private static FilterRule ShowRule(string name, params Condition[] conditions) =>
        new(name, Visibility.Show, 0, conditions);

    [Fact]
    public void RequiredAffixesGetPositionalWeights()
    {
        var ruleset = new FilterRuleset("Test", [
            ShowRule("Gloves",
                new ItemTypeCondition([Gloves]),
                new AffixCondition([CritChance, AttackSpeed, MaxLife, Willpower, Armor], 2)),
        ]);

        var priorities = GearPriorityExtractor.Extract(ruleset).ToDictionary(p => p.AffixId, p => p.Weight);

        priorities[CritChance].ShouldBe(4);
        priorities[AttackSpeed].ShouldBe(3);
        priorities[MaxLife].ShouldBe(2);
        priorities[Willpower].ShouldBe(1);
        priorities[Armor].ShouldBe(1); // beyond position 4 the weight floors at 1
    }

    [Fact]
    public void GreaterAffixWishMultipliesTheWeight()
    {
        var ruleset = new FilterRuleset("Test", [
            ShowRule("Gloves",
                new AffixCondition([CritChance, AttackSpeed], 2)
                {
                    GreaterEntries = [new GreaterAffixEntry(CritChance, CritChance)],
                }),
        ]);

        var priorities = GearPriorityExtractor.Extract(ruleset).ToDictionary(p => p.AffixId, p => p.Weight);

        priorities[CritChance].ShouldBe(4 * ItemChoiceComparer.GreaterAffixFactor);
        priorities[AttackSpeed].ShouldBe(3);
    }

    [Fact]
    public void OptionalAffixesCountAsNiceToHave()
    {
        var ruleset = new FilterRuleset("Test", [
            ShowRule("Gloves",
                new AffixCondition([CritChance], 1),
                new OptionalAffixCondition([MaxLife, Willpower], 1)),
        ]);

        var priorities = GearPriorityExtractor.Extract(ruleset).ToDictionary(p => p.AffixId, p => p.Weight);

        priorities[MaxLife].ShouldBe(1);
        priorities[Willpower].ShouldBe(1);
    }

    [Fact]
    public void TheSameAffixWantedOnSeveralSlotsStacks()
    {
        var ruleset = new FilterRuleset("Test", [
            ShowRule("Gloves", new ItemTypeCondition([Gloves]), new AffixCondition([CritChance], 1)),
            ShowRule("Helm", new ItemTypeCondition([Helm]), new AffixCondition([MaxLife, CritChance], 1)),
        ]);

        var priorities = GearPriorityExtractor.Extract(ruleset).ToDictionary(p => p.AffixId, p => p.Weight);

        priorities[CritChance].ShouldBe(4 + 3);
        priorities[MaxLife].ShouldBe(4);
    }

    [Fact]
    public void DisabledAndNonShowRulesAreIgnored()
    {
        var ruleset = new FilterRuleset("Test", [
            new FilterRule("Off", Visibility.Show, 0, [new AffixCondition([CritChance], 1)], isEnabled: false),
            new FilterRule("Hidden", Visibility.HideAll, 0, [new AffixCondition([MaxLife], 1)]),
        ]);

        GearPriorityExtractor.Extract(ruleset).ShouldBeEmpty();
    }

    [Fact]
    public void ResultIsOrderedByWeightDescending()
    {
        var ruleset = new FilterRuleset("Test", [
            ShowRule("Gloves", new AffixCondition([Willpower, CritChance], 1)),
            ShowRule("Helm", new AffixCondition([CritChance], 1)),
        ]);

        var priorities = GearPriorityExtractor.Extract(ruleset);

        priorities[0].AffixId.ShouldBe(CritChance); // 3 + 4 outranks Willpower's 4
        priorities.Select(p => p.Weight).ShouldBeInOrder(SortDirection.Descending);
    }
}
