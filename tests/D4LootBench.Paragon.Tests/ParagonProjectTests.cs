using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class ParagonProjectTests
{
    private static ParagonProject FullProject() => new()
    {
        ClassName = "Sorcerer",
        Boards =
        [
            new ParagonProjectBoard("Paragon_Sorc_00", null, null, 0),
            new ParagonProjectBoard("Paragon_Sorc_03", 0, BoardEdge.Top, 3),
        ],
        Targets = [new CellRef(1, 10, 2)],
        AvoidCells = [new CellRef(0, 5, 5)],
        ExcludeCells = [new CellRef(0, 6, 6)],
        PurchasedCells = [new CellRef(0, 10, 20), new CellRef(0, 10, 19)],
        Glyphs = [new ParagonProjectGlyph(1, "S04_Glyph_Sorc_10", 46, 40, EnsureActive: true)],
        NodeRules = [new ParagonProjectRule("Magic:Hitpoints_Max_Percent_Bonus:", NodeRuleMode.Limit, 2)],
        FocusStats = ["Intelligence_Core"],
        PreferRareNodes = true,
        TotalPoints = 342,
        SheetStrength = 10,
        SheetIntelligence = 1200,
        SheetWillpower = 11,
        SheetDexterity = 12,
    };

    [Fact]
    public void Round_trips_every_field_through_json()
    {
        string json = ParagonProjectSerializer.ToJson(FullProject());
        var parsed = ParagonProjectSerializer.FromJson(json);

        // Records with list properties compare by reference — compare the canonical JSON instead.
        ParagonProjectSerializer.ToJson(parsed).ShouldBe(json);

        parsed.ClassName.ShouldBe("Sorcerer");
        parsed.Boards[1].AttachEdge.ShouldBe(BoardEdge.Top);
        parsed.Boards[1].RotationSteps.ShouldBe(3);
        parsed.Targets.ShouldBe([new CellRef(1, 10, 2)]);
        parsed.Glyphs[0].EnsureActive.ShouldBeTrue();
        parsed.NodeRules[0].Mode.ShouldBe(NodeRuleMode.Limit);
        parsed.SheetIntelligence.ShouldBe(1200);
    }

    [Fact]
    public void Enums_serialize_as_readable_names()
    {
        string json = ParagonProjectSerializer.ToJson(FullProject());
        json.ShouldContain("\"Top\"");
        json.ShouldContain("\"Limit\"");
    }

    [Fact]
    public void Invalid_content_throws_FormatException()
    {
        Should.Throw<FormatException>(() => ParagonProjectSerializer.FromJson("not json"));
        Should.Throw<FormatException>(() => ParagonProjectSerializer.FromJson("{}"));
        Should.Throw<FormatException>(() =>
            ParagonProjectSerializer.FromJson("""{"className":"Sorcerer","boards":[]}"""));

        string newer = ParagonProjectSerializer.ToJson(FullProject() with { Version = 2 });
        Should.Throw<FormatException>(() => ParagonProjectSerializer.FromJson(newer))
            .Message.ShouldContain("newer");
    }

    [Fact]
    public void Missing_optional_fields_default_cleanly()
    {
        var parsed = ParagonProjectSerializer.FromJson(
            """{"className":"Sorcerer","boards":[{"boardInternalName":"Paragon_Sorc_00","rotationSteps":0}]}""");
        parsed.Version.ShouldBe(1);
        parsed.Targets.ShouldBeEmpty();
        parsed.Glyphs.ShouldBeEmpty();
        parsed.PreferRareNodes.ShouldBeFalse();
        parsed.Boards[0].ParentSlot.ShouldBeNull();
        parsed.Boards[0].AttachEdge.ShouldBeNull();
    }
}
