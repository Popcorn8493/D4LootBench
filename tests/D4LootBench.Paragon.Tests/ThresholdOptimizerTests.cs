using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The maximizer's threshold phase: buy the stats unmet rare-node threshold bonuses are short
/// of, and say when the boards cannot supply them (the rest must come from level/gear).
/// </summary>
public class ThresholdOptimizerTests
{
    private static ComposedGraph BarbGraph() => ComposedGraph.Build(new ParagonLayout(
    [
        new PlacedBoard { Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_00") },
        new PlacedBoard
        {
            Board = ParagonDatabase.Data.Boards.Single(b => b.InternalName == "Paragon_Barb_03"),
            ParentSlot = 0,
            AttachEdge = BoardEdge.Top,
        },
    ]));

    /// <summary>A solved tree holding at least one threshold rare, plus its unmet status at the given sheet offset.</summary>
    private static (HashSet<CellRef> Purchased, ThresholdStatus Status, PlanRequest Request) SolvedThresholdBuild(
        ComposedGraph graph, NonParagonStats sheet)
    {
        var rare = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Rare && v.Node.Thresholds.Count > 0);
        var request = new PlanRequest { Targets = [rare.Cell] };
        var solved = PlanSolver.Solve(graph, request);
        solved.Success.ShouldBeTrue(solved.Error);
        var purchased = solved.PurchasedCells.ToHashSet();

        var report = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, sheet, "Barbarian");
        var status = report.Thresholds.Single(t => t.Cell == rare.Cell);
        return (purchased, status, request);
    }

    [Fact]
    public void Maximizer_buys_the_deficit_stat_and_activates_the_threshold()
    {
        var graph = BarbGraph();
        // Sheet stats sit just under the requirement, so a couple of stat nodes close the gap.
        var (baseline, status0, _) = SolvedThresholdBuild(graph, NonParagonStats.None);
        double deficit0 = status0.Requirement - status0.Have;
        var sheet = NonParagonStats.Uniform(deficit0 - 12);
        var (purchased, status, request) = SolvedThresholdBuild(graph, sheet);
        status.Met.ShouldBeFalse("the scenario needs an unmet threshold to chase");

        var context = new ThresholdContext(ParagonDatabase.Data, "Barbarian", sheet);
        var outcome = PointMaximizer.Extend(graph, purchased, 30,
            new MaximizeFocus([], PreferRare: false, ActivateThresholds: true), request, context);

        outcome.ThresholdsActivated.ShouldBeGreaterThanOrEqualTo(1);
        var after = BuildStats.Compute(graph, purchased, ParagonDatabase.Data, sheet, "Barbarian");
        after.Thresholds.Single(t => t.Cell == status.Cell).Met.ShouldBeTrue();

        // Without the flag the same budget chases core stats blindly — no activation guarantee,
        // and the flagged run must never do worse.
        var plain = baseline.ToHashSet();
        PointMaximizer.Extend(graph, plain, 30, new MaximizeFocus([], false), request);
        after.ThresholdsMet.ShouldBeGreaterThanOrEqualTo(
            BuildStats.Compute(graph, plain, ParagonDatabase.Data, sheet, "Barbarian").ThresholdsMet);
    }

    [Fact]
    public void Warns_with_the_level_gear_amount_when_the_boards_cannot_supply_the_stat()
    {
        var graph = BarbGraph();
        var (purchased, status, _) = SolvedThresholdBuild(graph, NonParagonStats.None);
        status.Met.ShouldBeFalse();

        // Exclude every unpurchased node granting the required stat: nothing buyable remains.
        string paragonKey = status.Attribute.Replace("_Total", "_Core", StringComparison.Ordinal);
        var request = new PlanRequest
        {
            Targets = [],
            ExcludeCells = graph.Vertices
                .Where(v => !purchased.Contains(v.Cell) && v.Node.Attributes.Any(a =>
                    !a.IsThresholdBonus && a.Value is not null
                    && string.Equals(a.Attribute, paragonKey, StringComparison.OrdinalIgnoreCase)))
                .Select(v => v.Cell)
                .ToList(),
        };

        var context = new ThresholdContext(ParagonDatabase.Data, "Barbarian", NonParagonStats.None);
        var outcome = PointMaximizer.Extend(graph, purchased, 50,
            new MaximizeFocus([], PreferRare: false, ActivateThresholds: true), request, context);

        outcome.Notes.ShouldContain(n => n.Contains("level/gear"),
            "the user must be told the stat has to come from outside the boards");
        outcome.ThresholdsActivated.ShouldBe(0);
    }

    [Fact]
    public void Without_a_context_or_flag_the_maximizer_behaves_as_before()
    {
        var graph = BarbGraph();
        var (purchased, _, request) = SolvedThresholdBuild(graph, NonParagonStats.None);
        var outcome = PointMaximizer.Extend(graph, purchased, 10, new MaximizeFocus([], false), request);
        outcome.Notes.ShouldBeEmpty();
        outcome.ThresholdsActivated.ShouldBe(0);
    }
}
