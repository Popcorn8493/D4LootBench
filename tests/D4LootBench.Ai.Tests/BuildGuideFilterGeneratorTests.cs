using D4LootBench.Ai.Import;
using D4LootBench.Core.Import;
using D4LootBench.Core.Models;
using Shouldly;

namespace D4LootBench.Ai.Tests;

public sealed class BuildGuideFilterGeneratorTests
{
    private readonly BuildGuideFilterGenerator _generator = new(new NameResolver(TestSetup.Data));

    private static ParsedSlot Slot(string label, params string[] affixes) => new()
    {
        SlotLabel = label,
        Affixes = affixes.Select((a, i) => new ParsedAffix { RawName = a, Priority = i + 1 }).ToList(),
    };

    private static ParsedBuildGuide Guide(params ParsedSlot[] slots) => new() { Slots = [.. slots] };

    [Fact]
    public void SlotRule_HasItemTypeAndAffixCondition()
    {
        var result = _generator.Generate(Guide(
            Slot("Gloves", "Critical Strike Chance", "Attack Speed")));

        var rule = result.Ruleset.Rules.Single(r => r.Name == "Gloves");
        rule.Visibility.ShouldBe(Visibility.Show);
        rule.Conditions.OfType<ItemTypeCondition>().ShouldHaveSingleItem();
        var affixes = rule.Conditions.OfType<AffixCondition>().ShouldHaveSingleItem();
        affixes.AffixIds.Count.ShouldBe(2);
        affixes.MinimumCount.ShouldBe(2);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void SlotRule_TakesTopFourAffixesByPriority_AndMarksGreater()
    {
        var slot = new ParsedSlot
        {
            SlotLabel = "Helm",
            Affixes =
            [
                new ParsedAffix { RawName = "Maximum Life", Priority = 5 },
                new ParsedAffix { RawName = "Critical Strike Chance", Priority = 1, IsGreaterAffix = true },
                new ParsedAffix { RawName = "Attack Speed", Priority = 2 },
                new ParsedAffix { RawName = "Armor", Priority = 3 },
                new ParsedAffix { RawName = "Critical Strike Damage", Priority = 4 },
            ],
        };
        var rule = _generator.Generate(Guide(slot)).Ruleset.Rules.Single(r => r.Name == "Helm");

        var affixes = rule.Conditions.OfType<AffixCondition>().ShouldHaveSingleItem();
        affixes.AffixIds.Count.ShouldBe(4);
        affixes.GreaterEntries.ShouldHaveSingleItem();
        TestSetup.Data.Affixes.TryGetByName("Maximum Life", out var life);
        affixes.AffixIds.ShouldNotContain(life.Hash);
    }

    [Fact]
    public void WeaponSlot_HasNoItemTypeCondition()
    {
        var rule = _generator.Generate(Guide(Slot("Weapon", "Critical Strike Chance")))
            .Ruleset.Rules.Single(r => r.Name == "Weapon");
        rule.Conditions.OfType<ItemTypeCondition>().ShouldBeEmpty();
    }

    [Fact]
    public void UnresolvableAffix_Warns()
    {
        var result = _generator.Generate(Guide(Slot("Boots", "Zzqx Quantum Flux")));
        result.Warnings.ShouldContain(w => w.Contains("Zzqx Quantum Flux"));
    }

    [Fact]
    public void Uniques_FoldIntoOneTargetUniquesRule()
    {
        var result = _generator.Generate(Guide(
            new ParsedSlot { SlotLabel = "Helm", ItemName = "Harlequin Crest", HasUniqueSentinel = true },
            Slot("Gloves", "Critical Strike Chance")));

        var uniques = result.Ruleset.Rules.Single(r => r.Name == "Target Uniques");
        uniques.Conditions.OfType<SpecificUniqueCondition>().ShouldHaveSingleItem().UniqueIds.Count.ShouldBe(1);
        result.Ruleset.Rules.ShouldNotContain(r => r.Name == "Helm");
    }

    [Fact]
    public void UnresolvableSentinelUnique_WarnsAndAddsNoRule()
    {
        var result = _generator.Generate(Guide(
            new ParsedSlot { SlotLabel = "Helm", ItemName = "Imaginary Crown", HasUniqueSentinel = true }));

        result.Warnings.ShouldContain(w => w.Contains("Imaginary Crown"));
        result.Ruleset.Rules.ShouldNotContain(r => r.Name == "Target Uniques");
    }

    [Fact]
    public void TalismanSlots_ProduceOneCharmsRule()
    {
        var result = _generator.Generate(Guide(
            new ParsedSlot { SlotLabel = "Charm 1", IsTalismanSlot = true },
            new ParsedSlot { SlotLabel = "Charm 2", IsTalismanSlot = true }));

        var charms = result.Ruleset.Rules.Single(r => r.Name == "All Charms");
        charms.Conditions.ShouldHaveSingleItem().ShouldBeOfType<TalismanSetCondition>();
    }

    [Fact]
    public void RuleOrder_CharmsThenUniquesThenSlotsThenHideAll()
    {
        var rules = _generator.Generate(Guide(
            Slot("Gloves", "Critical Strike Chance"),
            new ParsedSlot { SlotLabel = "Helm", ItemName = "Harlequin Crest", HasUniqueSentinel = true },
            new ParsedSlot { SlotLabel = "Charm 1", IsTalismanSlot = true })).Ruleset.Rules;

        rules.Select(r => r.Name).ShouldBe(["All Charms", "Target Uniques", "Gloves", "Hide All"]);
    }

    [Fact]
    public void EmptyGuide_StillEndsWithHideAll()
    {
        var hideAll = _generator.Generate(Guide()).Ruleset.Rules.ShouldHaveSingleItem();
        hideAll.Name.ShouldBe("Hide All");
        hideAll.Visibility.ShouldBe(Visibility.HideAll);
        hideAll.Conditions.ShouldBeEmpty();
    }

    [Fact]
    public void ManySlots_CappedAt25Rules_KeepingHideAllLast()
    {
        var slots = Enumerable.Range(1, 30).Select(i => Slot($"Weapon {i}", "Critical Strike Chance"))
            .Append(new ParsedSlot { SlotLabel = "Charm 1", IsTalismanSlot = true })
            .ToArray();

        var result = _generator.Generate(Guide(slots));

        result.Ruleset.Rules.Count.ShouldBe(FilterRuleset.MaxRuleCount);
        result.Ruleset.Rules[^1].Name.ShouldBe("Hide All");
        result.Ruleset.Rules[0].Name.ShouldBe("All Charms");
        result.Ruleset.Rules[1].Name.ShouldBe("Weapon 1"); // highest-priority slots survive
        result.Warnings.ShouldContain(w => w.Contains("25-rule"));
    }
}
