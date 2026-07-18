using D4LootBench.Core.Import;
using Shouldly;

namespace D4LootBench.Core.Tests.Import;

public class D4BuildsGearImporterTests
{
    // ── Firestore document builders (the REST API's typed-value encoding) ─

    private static string Str(string value) => "{\"stringValue\":\"" + value + "\"}";

    private static string StringMap(params (string Key, string Value)[] entries) =>
        "{\"mapValue\":{\"fields\":{"
        + string.Join(",", entries.Select(e => "\"" + e.Key + "\":" + Str(e.Value)))
        + "}}}";

    private static string StatsMap(params (string Key, string?[] Values)[] entries) =>
        "{\"mapValue\":{\"fields\":{"
        + string.Join(",", entries.Select(e =>
            "\"" + e.Key + "\":{\"arrayValue\":{\"values\":["
            + string.Join(",", e.Values.Select(v => v is null ? "{\"nullValue\":null}" : Str(v)))
            + "]}}"))
        + "}}}";

    private static string Section(string? variantName, string gearJson, string statsJson, bool charms = false)
    {
        string fields = "";
        if (variantName is not null)
            fields += "\"variantName\":" + Str(variantName) + ",";
        fields += "\"gear\":" + gearJson + ",\"newStats\":" + statsJson;
        if (charms)
            fields += ",\"charms\":{\"arrayValue\":{\"values\":[" + StringMap(("name", "Berú")) + "]}}";
        return fields;
    }

    private static string Document(string topSection, params string[] variantSections) =>
        "{\"name\":\"projects/x/databases/(default)/documents/builds/test\",\"fields\":{"
        + "\"name\":" + Str("Test Build") + ","
        + "\"class\":" + Str("Druid") + ","
        + topSection
        + (variantSections.Length == 0
            ? ""
            : ",\"variants\":{\"arrayValue\":{\"values\":["
              + string.Join(",", variantSections.Select(s => "{\"mapValue\":{\"fields\":{" + s + "}}}"))
              + "]}}")
        + "}}";

    private static string DefaultGear => StringMap(
        ("Amulet", "Malefic Crescent"),
        ("Helm", "Aspect of the Crowded Sage"),
        ("Chest Armor", "Aspect of Shielding Storm"),
        ("Boots", ""));

    private static string DefaultStats => StatsMap(
        ("Helm", ["Willpower", null, "Maximum Life"]),
        ("Chest Armor", [null, "Armor"]),
        ("Boots", ["Movement Speed"]),
        ("Amulet", ["Cooldown Reduction"]),
        ("Pants", [null, null]));

    [Fact]
    public void Parses_build_urls()
    {
        D4BuildsGearImporter.TryParseBuildUrl(
            "https://d4builds.gg/builds/43003e89-bb97-4963-b172-ecc41cf33417/", out string id).ShouldBeTrue();
        id.ShouldBe("43003e89-bb97-4963-b172-ecc41cf33417");

        D4BuildsGearImporter.TryParseBuildUrl("https://d4builds.gg/rob2628/builds/", out _).ShouldBeFalse();
        D4BuildsGearImporter.TryParseBuildUrl(
            "https://example.com/builds/43003e89-bb97-4963-b172-ecc41cf33417", out _).ShouldBeFalse();
    }

    [Fact]
    public void Parses_pretty_slug_urls_and_resolves_them_through_page_data()
    {
        const string url = "https://d4builds.gg/builds/whirlwind-barbarian-endgame/?var=0";
        D4BuildsGearImporter.TryParseBuildUrl(url, out _).ShouldBeFalse();
        D4BuildsGearImporter.TryParseBuildSlugUrl(url, out string slug).ShouldBeTrue();
        slug.ShouldBe("whirlwind-barbarian-endgame");

        D4BuildsGearImporter.ExtractBuildId(
            "{\"result\":{\"pageContext\":{\"seoId\":\"fb2a2a80-6907-4be4-a7eb-a98785f128b0\"}}}")
            .ShouldBe("fb2a2a80-6907-4be4-a7eb-a98785f128b0");

        Should.Throw<BuildGuideImportException>(() => D4BuildsGearImporter.ExtractBuildId("<html>404</html>"));
    }

    [Fact]
    public void Extracts_the_document_variant_and_the_variants_array_in_slot_order()
    {
        string json = Document(
            Section(null, DefaultGear, DefaultStats),
            Section("Speed Farm", StringMap(("Gloves", "Fists of Fate")), StatsMap()));

        var variants = D4BuildsGearImporter.ExtractVariants(json);

        variants.Count.ShouldBe(2);
        variants[0].Title.ShouldBe("Test Build"); // no variantName → the build's name
        variants[1].Title.ShouldBe("Speed Farm");

        // Canonical slot order, nulls dropped, stat-less+item-less slots skipped.
        variants[0].Slots.Select(s => s.Slot).ShouldBe(
            ["Helm", "Chest Armor", "Boots", "Amulet"]);
        variants[0].Slots[0].Stats.ShouldBe(["Willpower", "Maximum Life"]);
        variants[0].Slots[0].ItemName.ShouldBe("Aspect of the Crowded Sage");
        variants[0].Slots[2].ItemName.ShouldBeNull(); // empty string = nothing equipped
    }

    [Fact]
    public void Merges_variants_with_identical_gear_without_repeating_titles()
    {
        string json = Document(
            Section("Push", DefaultGear, DefaultStats),
            Section("Uber", DefaultGear, DefaultStats),
            Section("Push", DefaultGear, DefaultStats));

        var variant = D4BuildsGearImporter.ExtractVariants(json).ShouldHaveSingleItem();
        variant.Title.ShouldBe("Push / Uber");
    }

    [Fact]
    public void Falls_back_to_the_legacy_stats_field_when_newStats_is_absent()
    {
        // Older documents predate "newStats" and carry the same shape under "stats".
        string json = "{\"name\":\"x\",\"fields\":{"
            + "\"name\":" + Str("Old Build") + ","
            + "\"gear\":" + StringMap(("Helm", "Aspect of Might")) + ","
            + "\"stats\":" + StatsMap(("Helm", ["Willpower", null, "Maximum Life"])) + "}}";

        var variant = D4BuildsGearImporter.ExtractVariants(json).ShouldHaveSingleItem();
        var helm = variant.Slots.ShouldHaveSingleItem();
        helm.Stats.ShouldBe(["Willpower", "Maximum Life"]);
    }

    [Fact]
    public void Converts_uniques_aspects_and_charms_to_a_parsed_guide()
    {
        string json = Document(Section(null, DefaultGear, DefaultStats, charms: true));
        var guide = D4BuildsGearImporter.ToParsedGuide(D4BuildsGearImporter.ExtractVariants(json)[0]);

        guide.DetectedFormat.ShouldBe(BuildGuideFormat.D4Builds);

        // "Malefic Crescent" carries no "Aspect" → a specific unique; its stats are not emitted.
        var amulet = guide.Slots.Single(s => s.SlotLabel == "Amulet");
        amulet.HasUniqueSentinel.ShouldBeTrue();
        amulet.ItemName.ShouldBe("Malefic Crescent");
        amulet.Affixes.ShouldBeEmpty();

        var helm = guide.Slots.Single(s => s.SlotLabel == "Helm");
        helm.HasUniqueSentinel.ShouldBeFalse();
        helm.Affixes.Select(a => a.RawName).ShouldBe(["Willpower", "Maximum Life"]);

        // Boots have a stat but no item; pants had only nulls and vanish entirely.
        guide.Slots.Single(s => s.SlotLabel == "Boots").Affixes.Count.ShouldBe(1);
        guide.Slots.ShouldNotContain(s => s.SlotLabel == "Pants");

        guide.Slots.Single(s => s.IsTalismanSlot).SlotLabel.ShouldBe("Charms");
    }

    [Fact]
    public void Rejects_error_documents_and_builds_without_gear()
    {
        Should.Throw<BuildGuideImportException>(() => D4BuildsGearImporter.ExtractVariants(
            "{\"error\":{\"code\":404,\"message\":\"Document not found.\"}}"));

        Should.Throw<BuildGuideImportException>(() => D4BuildsGearImporter.ExtractVariants(
            "{\"name\":\"x\",\"fields\":{\"class\":" + Str("Druid") + "}}"));

        Should.Throw<BuildGuideImportException>(() => D4BuildsGearImporter.ExtractVariants("not json"));
    }
}
