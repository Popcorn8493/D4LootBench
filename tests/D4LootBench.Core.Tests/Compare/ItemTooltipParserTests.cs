using D4LootBench.Core.Compare;
using Shouldly;

namespace D4LootBench.Core.Tests.Compare;

public sealed class ItemTooltipParserTests
{
    [Fact]
    public void ParsesALegendaryTooltip()
    {
        var parsed = ItemTooltipParser.Parse(
        [
            "CUSTODIAN'S IRE",
            "Ancestral Legendary Gloves",
            "750 Item Power",
            "336 Armor",
            "* +120 Dexterity [112 - 133]",
            "+7.5% Critical Strike Chance [6.5 - 8.0]%",
            "• +1,076 Maximum Life",
            "Attacks deal 1% to 300% of their normal damage",
            "Empty Socket",
            "Requires Level 60",
            "Sell Value: 35,065 Gold",
        ]);

        parsed.ItemName.ShouldBe("CUSTODIAN'S IRE");
        parsed.SlotText.ShouldBe("Gloves");
        parsed.IsUnique.ShouldBeFalse();
        parsed.Stats.Count.ShouldBe(3);

        parsed.Stats[0].StatText.ShouldBe("Dexterity");
        parsed.Stats[0].Value.ShouldBe(120);
        parsed.Stats[0].IsGreater.ShouldBeTrue();

        parsed.Stats[1].StatText.ShouldBe("Critical Strike Chance");
        parsed.Stats[1].Value.ShouldBe(7.5);
        parsed.Stats[1].IsGreater.ShouldBeFalse();

        parsed.Stats[2].StatText.ShouldBe("Maximum Life");
        parsed.Stats[2].Value.ShouldBe(1076);
    }

    [Fact]
    public void RecognizesUniquesAndWrappedNames()
    {
        var parsed = ItemTooltipParser.Parse(
        [
            "HARLEQUIN",
            "CREST",
            "Ancestral Unique Helm",
            "925 Item Power",
            "+48.0% Damage Reduction",
        ]);

        parsed.ItemName.ShouldBe("HARLEQUIN CREST");
        parsed.SlotText.ShouldBe("Helm");
        parsed.IsUnique.ShouldBeTrue();
        parsed.Stats.ShouldHaveSingleItem().StatText.ShouldBe("Damage Reduction");
    }

    [Fact]
    public void StatsUnderATransfiguredHeaderAreFlagged_AndNeverGreater()
    {
        var parsed = ItemTooltipParser.Parse(
        [
            "SOME CHEST",
            "Ancestral Legendary Chest Armor",
            "+120 Strength",
            "Transfigured",
            "* +50 Willpower",
        ]);

        parsed.SlotText.ShouldBe("Chest Armor");
        parsed.Stats.Count.ShouldBe(2);
        parsed.Stats[0].IsTransfigured.ShouldBeFalse();
        parsed.Stats[1].StatText.ShouldBe("Willpower");
        parsed.Stats[1].IsTransfigured.ShouldBeTrue();
        parsed.Stats[1].IsGreater.ShouldBeFalse();
    }

    [Fact]
    public void BaseLinesAndSentencesAreNotStats()
    {
        var parsed = ItemTooltipParser.Parse(
        [
            "A SWORD",
            "Ancestral Rare Sword",
            "1,391 Damage Per Second",
            "1.10 Attacks per Second",
            "925 Item Power",
            "• Casting a Core Skill leaves behind a trail of flames that burn enemies",
            "+42 Intelligence",
        ]);

        parsed.Stats.ShouldHaveSingleItem().StatText.ShouldBe("Intelligence");
        parsed.Stats[0].Value.ShouldBe(42);
    }

    [Fact]
    public void MissingRarityLineFallsBackToFirstLineAsName()
    {
        var parsed = ItemTooltipParser.Parse(["SOMETHING OCR MANGLED", "+30 Dexterity"]);

        parsed.ItemName.ShouldBe("SOMETHING OCR MANGLED");
        parsed.SlotText.ShouldBeNull();
        parsed.Stats.ShouldHaveSingleItem().StatText.ShouldBe("Dexterity");
    }
}
