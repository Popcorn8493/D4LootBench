using System.Text.Json;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class ImportCompareCombineTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    private static BuildSnapshot Snapshot(string name, ParagonLayout layout, IReadOnlyCollection<CellRef> targets)
    {
        var graph = ComposedGraph.Build(layout);
        if (targets.Count == 0)
            return new BuildSnapshot(name, layout, [graph.Vertices[graph.StartVertex].Cell], []);
        var result = PlanSolver.Solve(graph, new PlanRequest { Targets = targets });
        result.Success.ShouldBeTrue(result.Error);
        var allocated = result.PurchasedCells
            .Append(graph.Vertices[graph.StartVertex].Cell)
            .ToList();
        return new BuildSnapshot(name, layout, allocated, []);
    }

    private static CellRef RareOn(ComposedGraph graph, int slot, int rank = 0) =>
        graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Rare && v.Cell.BoardSlot == slot)
            .OrderBy(v => v.Cell.Y).ThenBy(v => v.Cell.X)
            .Skip(rank)
            .First().Cell;

    // ── MaxrollBuildImporter ─────────────────────────────────────────────

    [Fact]
    public void Planner_urls_parse_and_reserved_slugs_do_not()
    {
        MaxrollBuildImporter.TryParsePlannerUrl("https://maxroll.gg/d4/planner/sjeyt70h#3", out string id)
            .ShouldBeTrue();
        id.ShouldBe("sjeyt70h");
        MaxrollBuildImporter.TryParsePlannerUrl("https://maxroll.gg/d4/planner/builds", out _).ShouldBeFalse();
        MaxrollBuildImporter.TryParsePlannerUrl("https://maxroll.gg/d4/build-guides/foo", out _).ShouldBeFalse();
    }

    [Fact]
    public void Guide_html_yields_planner_ids_most_referenced_first_without_reserved_slugs()
    {
        const string html = """
            <a href="/d4/planner/builds">planner</a>
            <div data-href="https://maxroll.gg/d4/planner/abc12345">x</div>
            "d4/planner/abc12345" and also 'd4/planner/zzz999'
            """;
        var ids = MaxrollBuildImporter.ExtractPlannerIds(html);
        ids.ShouldBe(["abc12345", "zzz999"]);
    }

    [Fact]
    public void Profile_json_becomes_deduplicated_titled_variants()
    {
        var early = new[]
        {
            new MaxrollBoardEntry
            {
                Id = "Paragon_Sorc_00",
                Nodes = new Dictionary<string, int> { ["10"] = 1, ["31"] = 1 },
                Position = new MaxrollPosition(),
            },
        };
        var late = new[]
        {
            new MaxrollBoardEntry
            {
                Id = "Paragon_Sorc_00",
                Nodes = new Dictionary<string, int> { ["10"] = 1, ["31"] = 1, ["52"] = 1 },
                Position = new MaxrollPosition(),
            },
        };
        string dataJson = JsonSerializer.Serialize(new
        {
            profiles = new object[]
            {
                new
                {
                    name = "Starter",
                    paragon = new
                    {
                        steps = new object[]
                        {
                            new { name = "Early", data = early },
                            new { name = "Late", data = late },
                        },
                    },
                },
                new
                {
                    name = "Endgame",
                    paragon = new { steps = new object[] { new { name = "Early", data = early } } },
                },
            },
        }, MaxrollJson);
        string profileJson = JsonSerializer.Serialize(new { name = "Test Build", data = dataJson });

        var variants = MaxrollBuildImporter.ExtractVariants(profileJson);

        variants.Count.ShouldBe(2);
        variants[0].Title.ShouldBe("Test Build — Early");
        variants[0].Entries.Single().Nodes!.Count.ShouldBe(2);
        variants[1].Title.ShouldBe("Test Build — Starter: Late");
        variants[1].Entries.Single().Nodes!.Count.ShouldBe(3);
    }

    private static readonly JsonSerializerOptions MaxrollJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── BuildComparer ────────────────────────────────────────────────────

    [Fact]
    public void Comparing_two_builds_reports_points_and_a_verdict()
    {
        var layout = ParagonLayout.Single(Board("Paragon_Sorc_00"));
        var graph = ComposedGraph.Build(layout);
        var a = Snapshot("Current", layout, [RareOn(graph, 0, 0), RareOn(graph, 0, 1)]);
        var b = Snapshot("Import", layout, [RareOn(graph, 0, 1)]);

        string report = BuildComparer.Compare(a, b, ParagonDatabase.Data);

        report.ShouldContain("Current:");
        report.ShouldContain("Import:");
        report.ShouldContain("points");
        report.ShouldContain("Verdict:");
        // A covers a superset of B's rares at a higher cost — both should win something.
        report.ShouldContain("spends fewer points");
        report.ShouldContain("takes more rare nodes");
    }

    [Fact]
    public void Comparing_a_build_with_itself_is_a_draw()
    {
        var layout = ParagonLayout.Single(Board("Paragon_Sorc_00"));
        var graph = ComposedGraph.Build(layout);
        var a = Snapshot("A", layout, [RareOn(graph, 0)]);
        var b = a with { Name = "B" };

        BuildComparer.Compare(a, b, ParagonDatabase.Data)
            .ShouldContain("equivalent on every compared metric");
    }

    // ── BuildCombiner ────────────────────────────────────────────────────

    [Fact]
    public void Combining_builds_unions_boards_and_targets_both_builds_key_nodes()
    {
        var starterLayout = ParagonLayout.Single(Board("Paragon_Sorc_00"));
        var starterGraph = ComposedGraph.Build(starterLayout);
        var rareA = RareOn(starterGraph, 0, 0);
        var a = Snapshot("Current", starterLayout, [rareA]);

        var twoBoards = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard
            {
                Board = Board("Paragon_Sorc_03"),
                ParentSlot = 0,
                AttachEdge = BoardEdge.Top,
            },
        ]);
        var twoGraph = ComposedGraph.Build(twoBoards);
        var rareB = RareOn(twoGraph, 1);
        var b = Snapshot("Import", twoBoards, [rareB]);

        var combined = BuildCombiner.Merge(a, b, ParagonDatabase.Data);

        combined.Build.Layout.Boards.Count.ShouldBe(2);
        combined.Targets.ShouldContain(rareA);
        combined.Targets.ShouldContain(rareB);

        // The combined plan must be solvable.
        var graph = ComposedGraph.Build(combined.Build.Layout);
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = combined.Targets });
        solved.Success.ShouldBeTrue(solved.Error);
        solved.PurchasedCells.ShouldContain(rareA);
        solved.PurchasedCells.ShouldContain(rareB);
    }

    [Fact]
    public void Combining_builds_of_different_classes_fails_clearly()
    {
        var sorc = Snapshot("A", ParagonLayout.Single(Board("Paragon_Sorc_00")), []);
        var barbLayout = ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(bd => bd.ClassName == "Barbarian" && bd.BoardIndex == 0));
        var barb = Snapshot("B", barbLayout, []);

        Should.Throw<FormatException>(() => BuildCombiner.Merge(sorc, barb, ParagonDatabase.Data))
            .Message.ShouldContain("different classes");
    }
}
