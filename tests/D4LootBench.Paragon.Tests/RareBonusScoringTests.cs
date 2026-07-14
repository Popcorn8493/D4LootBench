using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// RealisticRares end to end on synthetic thresholds: candidate (unbought) rares get a real
/// threshold status via BuildStats' evaluate parameter, attainability outranks raw value, and
/// among equals the BONUS magnitude counts toward the score.
/// </summary>
public class RareBonusScoringTests
{
    private static readonly PlanRequest EmptyRequest = new() { Targets = [] };

    [Fact]
    public void Met_bonus_magnitude_decides_between_equal_rares()
    {
        // A S B — identical direct grants and both thresholds met by the sheet; A's bonus is
        // 50 Dex, B's is 1. The single point must buy A.
        var data = new ParagonData
        {
            Thresholds = [SyntheticBoards.Threshold("T", "Strength_Total", 10)],
        };
        var graph = SyntheticBoards.Graph(["ASB"], new()
        {
            ['A'] = SyntheticBoards.Rare("A", [("Dexterity_Core", 5)], [("Dexterity_Core", 50)], "T"),
            ['B'] = SyntheticBoards.Rare("B", [("Dexterity_Core", 5)], [("Dexterity_Core", 1)], "T"),
        });
        var context = new ThresholdContext(data, null,
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Strength"] = 100 }));

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Dexterity_Core"], PreferRare: true) { RealisticRares = true },
            EmptyRequest, context);

        purchased.ShouldBe([new CellRef(0, 0, 0)]);
    }

    [Fact]
    public void A_met_threshold_outranks_a_bigger_unattainable_rare()
    {
        // A's threshold is met by the sheet; B's needs 10000 Willpower no board can supply.
        // B grants five times the direct stat — attainability must still win.
        var data = new ParagonData
        {
            Thresholds =
            [
                SyntheticBoards.Threshold("TStr", "Strength_Total", 10),
                SyntheticBoards.Threshold("TWil", "Willpower_Total", 10_000),
            ],
        };
        var graph = SyntheticBoards.Graph(["ASB"], new()
        {
            ['A'] = SyntheticBoards.Rare("A", [("Dexterity_Core", 2)], [("Dexterity_Core", 5)], "TStr"),
            ['B'] = SyntheticBoards.Rare("B", [("Dexterity_Core", 10)], [("Dexterity_Core", 5)], "TWil"),
        });
        var context = new ThresholdContext(data, null,
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Strength"] = 100 }));

        var purchased = new HashSet<CellRef>();
        PointMaximizer.Extend(graph, purchased, 1,
            new MaximizeFocus(["Dexterity_Core"], PreferRare: true) { RealisticRares = true },
            EmptyRequest, context);

        purchased.ShouldBe([new CellRef(0, 0, 0)]);
    }

    [Fact]
    public void Evaluate_reports_candidate_cells_without_granting_their_stats()
    {
        var data = new ParagonData
        {
            Thresholds = [SyntheticBoards.Threshold("T", "Strength_Total", 10)],
        };
        var graph = SyntheticBoards.Graph(["SA"], new()
        {
            ['A'] = SyntheticBoards.Rare("A", [("Dexterity_Core", 5)], [("Dexterity_Core", 50)], "T"),
        });
        var candidate = new CellRef(0, 1, 0);

        var report = BuildStats.Compute(graph, new HashSet<CellRef>(), data,
            NonParagonStats.PerStat(new Dictionary<string, double> { ["Strength"] = 100 }),
            className: null, cellMultipliers: null, evaluate: [candidate]);

        // Status is reported (met via the sheet), but nothing is granted to the totals.
        var status = report.Thresholds.Single(t => t.Cell == candidate);
        status.Met.ShouldBeTrue();
        report.Totals.GetValueOrDefault("Dexterity_Core").ShouldBe(0);
    }
}
