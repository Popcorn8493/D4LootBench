using D4LootBench.Core.Codec;
using D4LootBench.Core.Import;
using D4LootBench.Core.Models;
using Shouldly;

namespace D4LootBench.Core.Tests.Import;

/// <summary>Tests for the URL-based importers: Mobalytics embedded gear + Maxroll planner filters.</summary>
public sealed class GuideUrlImporterTests
{
    // ── Mobalytics embedded gear ─────────────────────────────────────────

    private const string GearSection =
        """
        "equipmentPriorityList":[
          {"slug":"harlequin-crest","type":"helm","iconURL":"https://cdn.mobalytics.gg/assets/diablo-4/images/uniques/harlequin-crest.png","modifiers":[{"iconURL":"","slug":"cooldown-reduction","type":"gear"}],"notes":null},
          {"slug":"vanquishing-aspect","type":"ring-1","iconURL":"https://cdn.mobalytics.gg/assets/diablo-4/images/aspects/offensive.png","modifiers":[
            {"iconURL":"","slug":"critical-strike-chance","type":"gear"},
            {"iconURL":"","slug":"attack-speed","type":"gear"},
            {"iconURL":"","slug":"diamond","type":"socket"},
            {"iconURL":"","slug":"worldly-stability-cooldown-reduction","type":"tempering"}],"notes":null},
          {"slug":"alternate-aspect","type":"ring-1","iconURL":"https://cdn.mobalytics.gg/assets/diablo-4/images/aspects/defensive.png","modifiers":[
            {"iconURL":"","slug":"maximum-life","type":"gear"}],"notes":null},
          {"slug":"lucky-charm","type":"season-12-charm-1","iconURL":"x","modifiers":[{"iconURL":"","slug":"gold-find","type":"charm"}],"notes":null}
        ]
        """;

    private static string Page(params string[] sections) =>
        "<html><script>" + string.Join(",", sections) + "</script></html>";

    private static string TitlesWidget(params string[] titles) =>
        string.Join(",", titles.Select(t => $$"""{"id":"v","title":"{{t}}","description":null}"""));

    [Fact]
    public void Mobalytics_ExtractsVariant_AndPairsTitle()
    {
        string html = Page(TitlesWidget("Endgame"), GearSection);
        var variants = MobalyticsGearImporter.ExtractVariants(html);

        variants.Count.ShouldBe(1);
        variants[0].Title.ShouldBe("Endgame");
        variants[0].Items.Count.ShouldBe(4);
    }

    [Fact]
    public void Mobalytics_MergesIdenticalVariants()
    {
        string html = Page(TitlesWidget("Endgame", "Starter"), GearSection, GearSection);
        var variants = MobalyticsGearImporter.ExtractVariants(html);

        variants.Count.ShouldBe(1);
        variants[0].Title.ShouldBe("Endgame / Starter");
    }

    [Fact]
    public void Mobalytics_ThrowsWhenNoGearData()
    {
        Should.Throw<BuildGuideImportException>(() =>
            MobalyticsGearImporter.ExtractVariants("<html>nothing here</html>"));
    }

    [Fact]
    public void Mobalytics_ToParsedGuide_MapsUniquesAffixesAndCharms()
    {
        var variants = MobalyticsGearImporter.ExtractVariants(Page(GearSection));
        var guide = MobalyticsGearImporter.ToParsedGuide(variants[0]);

        // Unique by icon path, name de-kebabed.
        var unique = guide.Slots.Where(s => s.HasUniqueSentinel).ShouldHaveSingleItem();
        unique.ItemName.ShouldBe("harlequin crest");
        unique.SlotLabel.ShouldBe("Helm");

        // The first ring-1 entry provides the affixes; socket/tempering modifiers are dropped,
        // and the lower-priority alternate for the same slot is ignored.
        var ring = guide.Slots.Single(s => s.SlotLabel == "Ring 1");
        ring.Affixes.Select(a => a.RawName).ShouldBe(["critical strike chance", "attack speed"]);

        // Charm slots fold into the talisman rule.
        guide.Slots.Count(s => s.IsTalismanSlot).ShouldBe(1);
    }

    // ── Maxroll planner loot filters ─────────────────────────────────────

    private static string ProfileJson(string dataJson) =>
        System.Text.Json.JsonSerializer.Serialize(new { name = "Test Build", data = dataJson });

    [Fact]
    public void Maxroll_ParsesPlannerUrl()
    {
        MaxrollPlannerImporter.TryParsePlannerUrl("https://maxroll.gg/d4/planner/48g9o20h#3", out var id)
            .ShouldBeTrue();
        id.ShouldBe("48g9o20h");

        MaxrollPlannerImporter.TryParsePlannerUrl("https://maxroll.gg/d4/planner/builds", out _)
            .ShouldBeFalse();
        MaxrollPlannerImporter.TryParsePlannerUrl("https://maxroll.gg/d4/build-guides/foo", out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Maxroll_ExtractsPlannerIds_MostReferencedFirst()
    {
        const string html = """
            <a href="/d4/planner/aaaa11">one</a>
            <a href="/d4/planner/bbbb22">two</a>
            <a href="/d4/planner/bbbb22">two again</a>
            <a href="/d4/planner/builds">reserved</a>
            """;
        MaxrollPlannerImporter.ExtractPlannerIds(html).ShouldBe(["bbbb22", "aaaa11"]);
    }

    [Fact]
    public void Maxroll_ExtractsLootFilters_AndCodesDecode()
    {
        // A real share code produced by our own codec stands in for the author's saved filter.
        string code = FilterCodec.Encode(new FilterRuleset("Strict", [
            new FilterRule("Hide All", Visibility.HideAll, 0, []),
        ]));
        string dataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            lootFilters = new[]
            {
                new { name = "Strict", code = (string?)code },
                new { name = "", code = (string?)null }, // empty entries are skipped
            },
        });

        var (buildName, filters) = MaxrollPlannerImporter.ExtractLootFilters(ProfileJson(dataJson));

        buildName.ShouldBe("Test Build");
        var filter = filters.ShouldHaveSingleItem();
        filter.Name.ShouldBe("Strict");
        FilterCodec.Decode(filter.Code).Rules.ShouldHaveSingleItem().Name.ShouldBe("Hide All");
    }

    [Fact]
    public void Maxroll_ThrowsWhenPlannerHasNoFilters()
    {
        Should.Throw<BuildGuideImportException>(() =>
            MaxrollPlannerImporter.ExtractLootFilters(ProfileJson("""{"lootFilters":[]}""")));
    }

    [Fact]
    public void Maxroll_SurfacesApiError()
    {
        var ex = Should.Throw<BuildGuideImportException>(() =>
            MaxrollPlannerImporter.ExtractLootFilters("""{"error":"Profile not found"}"""));
        ex.Message.ShouldContain("Profile not found");
    }
}
