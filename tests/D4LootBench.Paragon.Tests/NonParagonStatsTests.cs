using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class NonParagonStatsTests
{
    [Fact]
    public void Uniform_applies_the_same_offset_to_every_stat()
    {
        var stats = NonParagonStats.Uniform(120);
        stats.For("Willpower_Total").ShouldBe(120);
        stats.For("Strength_Total").ShouldBe(120);
        NonParagonStats.None.For("Willpower_Total").ShouldBe(0);
    }

    [Fact]
    public void PerStat_matches_only_the_named_stat_and_ignores_suffixes()
    {
        var stats = NonParagonStats.PerStat(new Dictionary<string, double>
        {
            ["Willpower"] = 250,
            ["Strength_Core"] = 80, // suffix on the key is ignored when matching
        });
        stats.For("Willpower_Total").ShouldBe(250);
        stats.For("Willpower").ShouldBe(250);
        stats.For("Strength_Total").ShouldBe(80);
        stats.For("Dexterity_Total").ShouldBe(0);
    }

    [Fact]
    public void PerStat_offsets_unlock_the_same_thresholds_as_an_equal_uniform_offset()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00")));
        var rare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var solved = PlanSolver.Solve(graph, new PlanRequest { Targets = [rare.Cell] });
        solved.Success.ShouldBeTrue(solved.Error);
        var purchased = solved.PurchasedCells.ToHashSet();

        var uniform = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.Uniform(100_000), "Sorcerer");
        var perStat = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.PerStat(new Dictionary<string, double>
            {
                ["Strength"] = 100_000,
                ["Intelligence"] = 100_000,
                ["Willpower"] = 100_000,
                ["Dexterity"] = 100_000,
            }), "Sorcerer");

        perStat.ThresholdsMet.ShouldBe(uniform.ThresholdsMet);
        perStat.Thresholds.Count.ShouldBe(uniform.Thresholds.Count);

        // An offset on stats no requirement checks changes nothing vs having none at all.
        var none = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.None, "Sorcerer");
        var unrelated = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Nonexistent_Stat"] = 100_000 }),
            "Sorcerer");
        unrelated.ThresholdsMet.ShouldBe(none.ThresholdsMet);
    }

    [Fact]
    public void PerStat_offset_on_a_different_stat_does_not_unlock_a_threshold()
    {
        var graph = ComposedGraph.Build(ParagonLayout.Single(
            ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Sorc_00")));
        var rare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var purchased = new HashSet<CellRef> { rare.Cell };

        var report = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.None, "Sorcerer");
        var status = report.Thresholds.Single(t => t.Cell == rare.Cell);
        status.Met.ShouldBeFalse("a lone rare node cannot meet its own requirement");

        // Boost every stat EXCEPT the one the requirement checks — still not met.
        var otherStats = new[] { "Strength", "Intelligence", "Willpower", "Dexterity" }
            .Where(s => !status.Attribute.StartsWith(s, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(s => s, _ => 100_000d);
        var boosted = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.PerStat(otherStats), "Sorcerer");
        boosted.Thresholds.Single(t => t.Cell == rare.Cell).Met.ShouldBeFalse();

        // Boost the checked stat — met.
        var matching = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.PerStat(new Dictionary<string, double> { [status.Attribute] = 100_000 }),
            "Sorcerer");
        matching.Thresholds.Single(t => t.Cell == rare.Cell).Met.ShouldBeTrue();
    }
}
