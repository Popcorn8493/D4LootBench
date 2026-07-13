using D4LootBench.Paragon.Import;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class MobalyticsSkillImporterTests
{
    private static string TitleWidget(params string[] titles) =>
        string.Concat(titles.Select(t => $"{{\"id\":\"v-{t.ToLowerInvariant()}\",\"title\":\"{t}\",\"description\":null}}"));

    private static string Assigned(string slug, string name, string description, string section) =>
        $"\"assignedSkills\":{{\"summons\":null,\"skills\":[{{\"position\":1," +
        $"\"skill\":{{\"slug\":\"{slug}\",\"name\":\"{name}\",\"description\":\"{description}\"," +
        $"\"type\":{{\"id\":\"basic\"}},\"section\":{{\"id\":\"x\",\"name\":\"{section}\"}}}}}}]}}";

    private static string Tree(bool wrapped, params (string Slug, string Action)[] steps)
    {
        string body = "[" + string.Join(",", steps.Select(s =>
            $"{{\"actionType\":\"{s.Action}\",\"skill\":{{\"slug\":\"{s.Slug}\",\"type\":{{\"id\":\"basic\"}}}}}}")) + "]";
        return wrapped ? $"\"skillTree\":{{\"skills\":{body}}}" : $"\"skillTree\":{body}";
    }

    [Fact]
    public void Extracts_variants_with_paired_titles_bar_skills_and_merged_tree_ranks()
    {
        // Two variants, two tree sections each (class + mercenary), mixing both JSON shapes.
        string html =
            TitleWidget("Current Setup", "Selig")
            + Assigned("whirlwind", "Whirlwind", "Rapidly attack surrounding enemies.", "Core")
            + Tree(wrapped: true, ("whirlwind", "ACTIVATE"), ("whirlwind", "ACTIVATE"), ("war-cry", "ACTIVATE"))
            + Tree(wrapped: false, ("ground-slam", "ACTIVATE"))
            + Assigned("upheaval", "Upheaval", "Deals Physical damage.", "Core")
            + Tree(wrapped: true, ("upheaval", "ACTIVATE"), ("upheaval", "ACTIVATE"), ("upheaval", "DEACTIVATE"))
            + Tree(wrapped: false, ("ground-slam", "ACTIVATE"));

        var variants = MobalyticsSkillImporter.ExtractVariants(html);

        variants.Count.ShouldBe(2);
        variants[0].Title.ShouldBe("Current Setup");
        variants[1].Title.ShouldBe("Selig");

        var first = variants[0];
        first.ActiveSkills.ShouldHaveSingleItem();
        first.ActiveSkills[0].Name.ShouldBe("Whirlwind");
        first.ActiveSkills[0].SectionName.ShouldBe("Core");
        first.TreeRanks["whirlwind"].ShouldBe(2);
        first.TreeRanks["war-cry"].ShouldBe(1);
        first.TreeRanks["ground-slam"].ShouldBe(1); // the second (mercenary) tree merges in

        // DEACTIVATE refunds a rank.
        variants[1].TreeRanks["upheaval"].ShouldBe(1);
    }

    [Fact]
    public void Uneven_tree_section_count_omits_ranks_rather_than_guessing()
    {
        string html =
            TitleWidget("A", "B")
            + Assigned("whirlwind", "Whirlwind", "d", "Core")
            + Assigned("upheaval", "Upheaval", "d", "Core")
            + Tree(wrapped: true, ("whirlwind", "ACTIVATE"), ("upheaval", "ACTIVATE"), ("war-cry", "ACTIVATE"));

        var variants = MobalyticsSkillImporter.ExtractVariants(html);

        variants.Count.ShouldBe(2);
        variants.ShouldAllBe(v => v.TreeRanks.Count == 0);
    }

    [Fact]
    public void Unpaired_title_count_falls_back_to_variant_numbers()
    {
        string html =
            TitleWidget("Only One Title")
            + Assigned("whirlwind", "Whirlwind", "d", "Core")
            + Assigned("upheaval", "Upheaval", "d", "Core");

        var variants = MobalyticsSkillImporter.ExtractVariants(html);

        variants.Select(v => v.Title).ShouldBe(["Variant 1", "Variant 2"]);
    }

    [Fact]
    public void String_valued_skillTree_keys_are_ignored()
    {
        string html =
            "\"skillTree\":\"just a label\","
            + Assigned("whirlwind", "Whirlwind", "d", "Core")
            + Tree(wrapped: true, ("whirlwind", "ACTIVATE"));

        var variants = MobalyticsSkillImporter.ExtractVariants(html);

        variants.ShouldHaveSingleItem();
        variants[0].TreeRanks["whirlwind"].ShouldBe(1);
    }

    [Fact]
    public void A_page_without_skill_data_throws()
    {
        Should.Throw<FormatException>(() => MobalyticsSkillImporter.ExtractVariants("<html>nothing</html>"));
    }
}
