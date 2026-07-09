using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// "Any:" stat groups span every node granting an attribute regardless of kind, so a limit can
/// cap a stat (e.g. max life) instead of one bucket — rare nodes grant many stats incidentally.
/// </summary>
public class StatGroupTests
{
    private const string StatKey = "Stat:Hitpoints_Max_Percent_Bonus";
    private const string MagicKey = "Magic:Hitpoints_Max_Percent_Bonus:";

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

    [Fact]
    public void Stat_groups_span_node_kinds()
    {
        var graph = BarbGraph();
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);

        // Rare nodes (e.g. Tenacity) grant max life too, so the stat group is strictly larger.
        cellsByGroup[StatKey].Count.ShouldBeGreaterThan(cellsByGroup[MagicKey].Count);

        var anyLife = NodeGrouping.StatGroupsIn(graph).Single(g => g.Key == StatKey);
        anyLife.DisplayName.ShouldStartWith("Any: ");
        anyLife.CellCount.ShouldBe(cellsByGroup[StatKey].Count);
    }

    [Fact]
    public void An_Any_stat_limit_caps_the_stat_across_kinds()
    {
        var graph = BarbGraph();
        var statCells = NodeGrouping.CellsByGroup(graph)[StatKey];
        var legendary = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Legendary && v.Cell.BoardSlot == 1).Cell;

        // Chase max life hard with prefer-rare: without a rule this blows past 2.
        int Run(IReadOnlyList<NodeRule> rules)
        {
            var request = new PlanRequest { Targets = [legendary], NodeRules = rules };
            var solved = PlanSolver.Solve(graph, request);
            solved.Success.ShouldBeTrue(solved.Error);
            var purchased = solved.PurchasedCells.ToHashSet();
            PointMaximizer.Extend(graph, purchased, 100,
                new MaximizeFocus(["Hitpoints_Max_Percent_Bonus"], PreferRare: true), request);
            return statCells.Count(purchased.Contains);
        }

        Run([]).ShouldBeGreaterThan(2, "the scenario must actually tempt the maximizer");
        Run([new NodeRule(StatKey, NodeRuleMode.Limit, 2)]).ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Overlapping_group_and_stat_limits_both_hold()
    {
        var graph = BarbGraph();
        var cellsByGroup = NodeGrouping.CellsByGroup(graph);
        var legendary = graph.Vertices.First(v =>
            v.Node.Kind == ParagonNodeKind.Legendary && v.Cell.BoardSlot == 1).Cell;

        var request = new PlanRequest
        {
            Targets = [legendary],
            NodeRules =
            [
                new NodeRule(MagicKey, NodeRuleMode.Limit, 1),
                new NodeRule(StatKey, NodeRuleMode.Limit, 2),
            ],
        };
        var solved = PlanSolver.Solve(graph, request);
        solved.Success.ShouldBeTrue(solved.Error);
        var purchased = solved.PurchasedCells.ToHashSet();
        PointMaximizer.Extend(graph, purchased, 100,
            new MaximizeFocus(["Hitpoints_Max_Percent_Bonus"], PreferRare: true), request);

        cellsByGroup[MagicKey].Count(purchased.Contains).ShouldBeLessThanOrEqualTo(1);
        cellsByGroup[StatKey].Count(purchased.Contains).ShouldBeLessThanOrEqualTo(2);
    }
}
