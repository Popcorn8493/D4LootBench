using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class D4BuildsImporterTests
{
    // A real Firestore build document captured from d4builds.gg (July 2026): the "Shred Druid
    // (S14)" build, trimmed to the fields the importer reads. Its default variant carries all
    // four rotations (0/90/90/180/270), which pins the tile-coordinate and rotation conventions.
    private static string DruidJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "d4builds-shred-druid.json"));

    /// <summary>Mimics a Firestore paragon section: boards plus selected tiles.</summary>
    private static string Paragon(string boardsJson, params string[] tiles)
    {
        string tileValues = string.Join(",", tiles.Select(t => "{\"stringValue\":\"" + t + "\"}"));
        return "{\"mapValue\":{\"fields\":{"
            + "\"boards\":{\"arrayValue\":{\"values\":[" + boardsJson + "]}},"
            + "\"selectedTiles\":{\"arrayValue\":{\"values\":[" + tileValues + "]}}}}}";
    }

    /// <summary>A minimal Firestore build document with one paragon variant.</summary>
    private static string Document(string cls, string boardsJson, params string[] tiles) =>
        "{\"name\":\"projects/x/databases/(default)/documents/builds/test\",\"fields\":{"
        + "\"name\":{\"stringValue\":\"Test Build\"},"
        + "\"class\":{\"stringValue\":\"" + cls + "\"},"
        + "\"paragon\":" + Paragon(boardsJson, tiles) + "}}";

    private static string Board(string name, int x, int y, int rotation, string? glyph = null)
    {
        string fields =
            "\"name\":{\"stringValue\":\"" + name + "\"},"
            + "\"x\":{\"integerValue\":\"" + x + "\"},"
            + "\"y\":{\"integerValue\":\"" + y + "\"},"
            + "\"rotation\":{\"integerValue\":\"" + rotation + "\"}";
        if (glyph is not null)
            fields += ",\"glyph\":{\"stringValue\":\"" + glyph + "\"},\"glyphLevel\":{\"integerValue\":\"46\"}";
        return "{\"mapValue\":{\"fields\":{" + fields + "}}}";
    }

    [Fact]
    public void Parses_build_urls()
    {
        D4BuildsImporter.TryParseBuildUrl(
            "https://d4builds.gg/builds/43003e89-bb97-4963-b172-ecc41cf33417/", out string id).ShouldBeTrue();
        id.ShouldBe("43003e89-bb97-4963-b172-ecc41cf33417");

        D4BuildsImporter.TryParseBuildUrl(
            "https://d4builds.gg/builds/43003e89-bb97-4963-b172-ecc41cf33417?var=Push#paragon", out id)
            .ShouldBeTrue();
        id.ShouldBe("43003e89-bb97-4963-b172-ecc41cf33417");

        D4BuildsImporter.TryParseBuildUrl("https://d4builds.gg/rob2628/builds/", out _).ShouldBeFalse();
        D4BuildsImporter.TryParseBuildUrl(
            "https://example.com/builds/43003e89-bb97-4963-b172-ecc41cf33417", out _).ShouldBeFalse();
    }

    [Fact]
    public void Parses_pretty_slug_urls_and_resolves_them_through_page_data()
    {
        // Curated meta builds get pretty slugs; the uuid parser must reject them first.
        const string url = "https://d4builds.gg/builds/whirlwind-barbarian-endgame/?var=0";
        D4BuildsImporter.TryParseBuildUrl(url, out _).ShouldBeFalse();
        D4BuildsImporter.TryParseBuildSlugUrl(url, out string slug).ShouldBeTrue();
        slug.ShouldBe("whirlwind-barbarian-endgame");

        D4BuildsImporter.TryParseBuildSlugUrl("https://d4builds.gg/rob2628/builds/", out _).ShouldBeFalse();

        D4BuildsImporter.ExtractBuildId(
            """{"result":{"pageContext":{"seoId":"fb2a2a80-6907-4be4-a7eb-a98785f128b0"}}}""")
            .ShouldBe("fb2a2a80-6907-4be4-a7eb-a98785f128b0");

        Should.Throw<FormatException>(() => D4BuildsImporter.ExtractBuildId(
            """{"result":{"pageContext":{"seoId":"not-a-uuid"}}}"""));
        Should.Throw<FormatException>(() => D4BuildsImporter.ExtractBuildId("<html>404</html>"));
    }

    [Fact]
    public void Extracts_the_document_variant_and_the_variants_array()
    {
        var variants = D4BuildsImporter.ExtractVariants(DruidJson);

        // The document's own fields are the default variant; three more ride in "variants".
        variants.Count.ShouldBe(4);
        variants[0].Title.ShouldBe("Dont this. Play Push Vers");
        variants[0].ClassName.ShouldBe("Druid");
        variants[0].Boards.Count.ShouldBe(5);
        variants[0].SelectedTiles.Count.ShouldBe(281);
        variants.Select(v => v.Title).ShouldBe(
            ["Dont this. Play Push Vers", "Shred - Standard", "Shred - PTR Version", "Shred - Standard"]);
        variants.ShouldAllBe(v => v.ClassName == "Druid");
    }

    [Fact]
    public void Converts_the_shred_druid_build()
    {
        var variants = D4BuildsImporter.ExtractVariants(DruidJson);
        var build = D4BuildsImporter.ToBuild(variants[0], ParagonDatabase.Data);

        var placed = build.Layout.Boards
            .Select((b, slot) => (b.Board.InternalName, b.RotationSteps, build.Layout.BoardPositions[slot]))
            .ToList();
        placed.ShouldBe(
        [
            ("Paragon_Druid_00", 0, (0, 0)),   // starter, gate up
            ("Paragon_Druid_01", 1, (0, -1)),  // Thunderstruck, 90° CW, above the starter
            ("Paragon_Druid_07", 1, (1, -1)),  // Constricting Tendrils
            ("Paragon_Druid_05", 2, (1, 0)),   // Heightened Malice, 180°
            ("Paragon_Druid_04", 3, (2, 0)),   // Lust for Carnage, 270°
        ], ignoreOrder: true);

        // All 281 selected tiles plus any implicit cells the tile list missed.
        build.AllocatedCells.Count.ShouldBeGreaterThanOrEqualTo(281);

        build.Glyphs.Count.ShouldBe(5);
        var starterGlyph = build.Glyphs.Single(g => g.BoardSlot == 0);
        starterGlyph.GlyphInternalName.ShouldBe("Rare_050_Dexterity_Side"); // Spirit
        starterGlyph.Level.ShouldBe(100);
    }

    [Fact]
    public void Imported_allocation_is_fully_connected_from_the_start_node()
    {
        // The strongest regression test for the calibrated conventions: 1-based unrotated tile
        // coordinates, negated board-grid y, clockwise degrees. If any drifts, the allocation
        // lands on empty cells or falls apart into disconnected islands.
        foreach (var variant in D4BuildsImporter.ExtractVariants(DruidJson))
        {
            var build = D4BuildsImporter.ToBuild(variant, ParagonDatabase.Data);
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
        string json = Document("Barbarian",
            Board("Starting Board", 0, 0, 0) + "," + Board("Carnage", 0, 1, 900),
            "startingboard_r15c11", "carnage_r20c11");
        var variants = D4BuildsImporter.ExtractVariants(json);
        var entries = D4BuildsImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        entries.Single(e => e.Id != "Paragon_Barb_00").Rotation.ShouldBe(2); // 900° → 180° → 2 steps
    }

    [Fact]
    public void Pairs_off_by_a_letter_tile_prefixes_with_their_board()
    {
        // Live d4builds data keys Danse Macabre tiles as "dansmacabre" — not the display name's
        // slug. The importer pairs leftover prefixes to the closest declared board.
        string json = Document("Rogue",
            Board("Starting Board", 0, 0, 0) + "," + Board("Danse Macabre", 0, 1, 0),
            "startingboard_r15c11", "dansmacabre_r20c11", "dansmacabre_r19c11");
        var variants = D4BuildsImporter.ExtractVariants(json);
        var entries = D4BuildsImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        var danse = entries.Single(e => e.Id == "Paragon_Rogue_10");
        danse.Nodes!.Keys.ShouldBe([(19 * 21 + 10).ToString(), (18 * 21 + 10).ToString()], ignoreOrder: true);
    }

    [Fact]
    public void Drops_stale_tiles_of_boards_a_variant_no_longer_attaches()
    {
        // Live documents keep selectedTiles entries for boards a variant has swapped out
        // (seen on a Spiritborn build whose second variant replaced Prodigy's Tempo but kept
        // its tiles). The site ignores them; so must the importer.
        string json = Document("Druid",
            Board("Starting Board", 0, 0, 0),
            "startingboard_r15c11", "lustforcarnage_r20c11");
        var variants = D4BuildsImporter.ExtractVariants(json);
        var entries = D4BuildsImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        var starter = entries.ShouldHaveSingleItem();
        starter.Nodes!.Keys.ShouldBe([(14 * 21 + 10).ToString()]);
    }

    [Fact]
    public void Resolves_glyphs_by_display_name_and_class()
    {
        string json = Document("Rogue",
            Board("Starting Board", 0, 0, 0, glyph: "Ambush"),
            "startingboard_r15c11");
        var variants = D4BuildsImporter.ExtractVariants(json);
        var entries = D4BuildsImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        entries[0].Glyph.ShouldBe("Rare_068_Strength_Side"); // Ambush
        entries[0].GlyphLevel.ShouldBe(46);
    }

    [Fact]
    public void Rejects_unknown_boards_glyphs_and_error_documents()
    {
        Should.Throw<FormatException>(() => D4BuildsImporter.ExtractVariants(
            """{"error":{"code":404,"message":"Document not found."}}"""));

        Should.Throw<FormatException>(() => D4BuildsImporter.ExtractVariants(
            """{"name":"x","fields":{"class":{"stringValue":"Druid"}}}"""));

        var noSuchBoard = D4BuildsImporter.ExtractVariants(Document("Druid",
            Board("No Such Board", 0, 0, 0), "nosuchboard_r15c11"));
        Should.Throw<FormatException>(() =>
            D4BuildsImporter.ToMaxrollEntries(noSuchBoard[0], ParagonDatabase.Data));

        var noSuchGlyph = D4BuildsImporter.ExtractVariants(Document("Druid",
            Board("Starting Board", 0, 0, 0, glyph: "No Such Glyph"), "startingboard_r15c11"));
        Should.Throw<FormatException>(() =>
            D4BuildsImporter.ToMaxrollEntries(noSuchGlyph[0], ParagonDatabase.Data));
    }

    [Fact]
    public void Merges_variants_with_identical_paragon_data()
    {
        string paragon = Paragon(Board("Starting Board", 0, 0, 0), "startingboard_r15c11");
        string json = "{\"name\":\"x\",\"fields\":{"
            + "\"name\":{\"stringValue\":\"Test Build\"},"
            + "\"class\":{\"stringValue\":\"Druid\"},"
            + "\"variantName\":{\"stringValue\":\"Push\"},"
            + "\"paragon\":" + paragon + ","
            + "\"variants\":{\"arrayValue\":{\"values\":[{\"mapValue\":{\"fields\":{"
            + "\"variantName\":{\"stringValue\":\"Speed\"},"
            + "\"paragon\":" + paragon + "}}}]}}}}";
        var variant = D4BuildsImporter.ExtractVariants(json).ShouldHaveSingleItem();
        variant.Title.ShouldBe("Push / Speed");
    }

    [Fact]
    public void Merging_identical_variants_does_not_repeat_the_same_title()
    {
        // Live builds routinely name every variant "Standard Build"; a merged
        // "Standard Build / Standard Build" helps nobody.
        string paragon = Paragon(Board("Starting Board", 0, 0, 0), "startingboard_r15c11");
        string json = "{\"name\":\"x\",\"fields\":{"
            + "\"name\":{\"stringValue\":\"Test Build\"},"
            + "\"class\":{\"stringValue\":\"Druid\"},"
            + "\"variantName\":{\"stringValue\":\"Standard Build\"},"
            + "\"paragon\":" + paragon + ","
            + "\"variants\":{\"arrayValue\":{\"values\":[{\"mapValue\":{\"fields\":{"
            + "\"variantName\":{\"stringValue\":\"Standard Build\"},"
            + "\"paragon\":" + paragon + "}}}]}}}}";

        D4BuildsImporter.ExtractVariants(json).ShouldHaveSingleItem().Title.ShouldBe("Standard Build");
    }

    [Fact]
    public void Normalizes_typographic_apostrophes_in_board_names()
    {
        // The doc's display name uses U+2019 while the tile keys and our database use U+0027.
        string json = Document("Spiritborn",
            Board("Prodigy’s Tempo", 0, 0, 0), "prodigy'stempo_r15c11");
        var variants = D4BuildsImporter.ExtractVariants(json);
        var entries = D4BuildsImporter.ToMaxrollEntries(variants[0], ParagonDatabase.Data);

        var entry = entries.ShouldHaveSingleItem();
        entry.Id.ShouldBe("Paragon_Spirit_08");
        entry.Nodes!.Keys.ShouldBe([(14 * 21 + 10).ToString()]);
    }
}
