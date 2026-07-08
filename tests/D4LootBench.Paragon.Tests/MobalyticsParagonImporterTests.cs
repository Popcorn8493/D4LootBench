using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class MobalyticsParagonImporterTests
{
    // Real paragon sections captured from live Mobalytics build guides (July 2026):
    // the Whirlwind Barbarian "Current Setup" variant and the Heartseeker Rogue first variant
    // (whose eldritch-bounty board carries a 540° rotation).
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string BarbJson => Fixture("mobalytics-whirlwind-barb.paragon.json");
    private static string RogueJson => Fixture("mobalytics-heartseeker-rogue.paragon.json");

    /// <summary>Mimics the page structure: a variant title list plus per-variant data sections.</summary>
    private static string WrapPage(string[] titles, string[] paragonJsons)
    {
        string variantList = string.Join(",", titles.Select((t, i) =>
            "{\"id\":\"" + (i + 10) + "\",\"title\":\"" + t + "\",\"description\":{\"value\":null}}"));
        string sections = string.Join(",", paragonJsons.Select(j =>
            "{\"skillTree\":[],\"paragon\":" + j + ",\"mercenary\":null}"));
        return "<html><script>{\"childrenVariants\":[" + variantList + "]}…{\"variantsData\":[" + sections + "]}</script></html>";
    }

    [Fact]
    public void Extracts_variants_and_pairs_titles_by_order()
    {
        var variants = MobalyticsParagonImporter.ExtractVariants(
            WrapPage(["Endgame", "Leveling"], [BarbJson, RogueJson]));

        variants.Count.ShouldBe(2);
        variants[0].Title.ShouldBe("Endgame");
        variants[1].Title.ShouldBe("Leveling");
        variants[0].Section.Boards.Count.ShouldBe(5);
    }

    [Fact]
    public void Merges_variants_with_identical_paragon_data()
    {
        var variants = MobalyticsParagonImporter.ExtractVariants(
            WrapPage(["Uniques", "Mythic"], [BarbJson, BarbJson]));

        var variant = variants.ShouldHaveSingleItem();
        variant.Title.ShouldBe("Uniques / Mythic");
    }

    [Fact]
    public void Falls_back_to_numbered_titles_when_counts_mismatch()
    {
        var variants = MobalyticsParagonImporter.ExtractVariants(
            WrapPage(["Only One Title"], [BarbJson, RogueJson]));

        variants.Select(v => v.Title).ShouldBe(["Variant 1", "Variant 2"]);
    }

    [Fact]
    public void Converts_the_whirlwind_barbarian_build()
    {
        var variants = MobalyticsParagonImporter.ExtractVariants(WrapPage(["Current Setup"], [BarbJson]));
        var build = MobalyticsParagonImporter.ToBuild(variants[0], ParagonDatabase.Data);

        var placed = build.Layout.Boards
            .Select((b, slot) => (b.Board.InternalName, b.RotationSteps, build.Layout.BoardPositions[slot]))
            .ToList();
        placed.ShouldBe(
        [
            ("Paragon_Barb_00", 0, (0, 0)),   // starter, gate up
            ("Paragon_Barb_07", 3, (0, -1)),  // Warbringer, 270° CW
            ("Paragon_Barb_08", 3, (0, -2)),  // Weapons Master
            ("Paragon_Barb_06", 2, (-1, -1)), // Flawless Technique, 180°
            ("Paragon_Barb_02", 3, (1, -1)),  // Blood Rage
        ], ignoreOrder: true);

        // 325 picked nodes plus the implicit start/gate/socket cells Mobalytics omits.
        build.AllocatedCells.Count.ShouldBeGreaterThan(325);
        build.AllocatedCells.ShouldContain(new CellRef(0, 10, 0)); // starter top gate

        build.Glyphs.Count.ShouldBe(5);
        var starterGlyph = build.Glyphs.Single(g => g.BoardSlot == 0);
        starterGlyph.GlyphInternalName.ShouldBe("Rare_Str_Generic"); // Challenger
        starterGlyph.Level.ShouldBe(150);
    }

    [Fact]
    public void Imported_allocation_is_fully_connected_from_the_start_node()
    {
        // The strongest regression test for the calibrated coordinate conventions: if node
        // coordinates, board axes, or rotation direction drift, the allocation falls apart.
        foreach (string json in new[] { BarbJson, RogueJson })
        {
            var variants = MobalyticsParagonImporter.ExtractVariants(WrapPage(["V"], [json]));
            var build = MobalyticsParagonImporter.ToBuild(variants[0], ParagonDatabase.Data);
            var graph = ComposedGraph.Build(build.Layout);

            var allocated = new HashSet<int>();
            foreach (var cell in build.AllocatedCells)
            {
                graph.TryGetVertex(cell, out int vertex).ShouldBeTrue($"cell {cell} must exist on the board");
                allocated.Add(vertex);
            }

            var seen = new HashSet<int> { graph.StartVertex };
            var queue = new Queue<int>();
            queue.Enqueue(graph.StartVertex);
            while (queue.Count > 0)
            {
                foreach (int next in graph.Adjacency[queue.Dequeue()])
                {
                    if (allocated.Contains(next) && seen.Add(next))
                        queue.Enqueue(next);
                }
            }
            seen.Count.ShouldBe(allocated.Count + (allocated.Contains(graph.StartVertex) ? 0 : 1));
        }
    }

    [Fact]
    public void Normalizes_rotations_beyond_360_degrees()
    {
        var variants = MobalyticsParagonImporter.ExtractVariants(WrapPage(["V"], [RogueJson]));
        var entries = MobalyticsParagonImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        entries.Single(e => e.Id == "Paragon_Rogue_01").Rotation.ShouldBe(2); // 540° → 180° → 2 steps
    }

    [Fact]
    public void Rejects_pages_without_paragon_data_and_unknown_slugs()
    {
        Should.Throw<FormatException>(() =>
            MobalyticsParagonImporter.ExtractVariants("<html>no paragon here</html>"));

        var variant = new MobalyticsParagonVariant("V", new MobalyticsParagonSection
        {
            Boards =
            [
                new MobalyticsBoardRef { Board = new MobalyticsSlugRef { Slug = "barbarian-no-such-board" } },
            ],
        });
        Should.Throw<FormatException>(() =>
            MobalyticsParagonImporter.ToMaxrollEntries(variant, ParagonDatabase.Data));
    }
}
