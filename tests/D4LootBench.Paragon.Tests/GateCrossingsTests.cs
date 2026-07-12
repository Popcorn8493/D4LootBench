using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class GateCrossingsTests
{
    private static ParagonBoardDef Board(string internalName) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == internalName);

    /// <summary>Starter plus one board attached on the starter's single (top) gate.</summary>
    private static ComposedGraph TwoBoardGraph() =>
        ComposedGraph.Build(new ParagonLayout([
            new PlacedBoard { Board = Board("Paragon_Sorc_00") },
            new PlacedBoard
            {
                Board = Board("Paragon_Sorc_03"),
                ParentSlot = 0,
                AttachEdge = BoardEdge.Top,
                RotationSteps = 0,
            },
        ]));

    private static (CellRef Near, CellRef Far) CrossingGates(ComposedGraph graph)
    {
        var pairs = GateCrossings.PairMap(graph);
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            if (pairs[v] >= 0 && graph.Vertices[v].Cell.BoardSlot == 0)
                return (graph.Vertices[v].Cell, graph.Vertices[pairs[v]].Cell);
        }
        throw new InvalidOperationException("no crossing gate pair found");
    }

    [Fact]
    public void Pair_map_links_exactly_the_two_gates_of_a_crossing()
    {
        var graph = TwoBoardGraph();
        var pairs = GateCrossings.PairMap(graph);

        var linked = Enumerable.Range(0, graph.Vertices.Count).Where(v => pairs[v] >= 0).ToList();
        linked.Count.ShouldBe(2); // one crossing: the starter's top gate and its partner
        foreach (int v in linked)
        {
            graph.Vertices[v].Node.Kind.ShouldBe(ParagonNodeKind.Gate);
            pairs[pairs[v]].ShouldBe(v); // symmetric
            graph.Vertices[pairs[v]].Cell.BoardSlot.ShouldNotBe(graph.Vertices[v].Cell.BoardSlot);
        }
    }

    [Fact]
    public void A_purchased_gate_pair_costs_one_point()
    {
        var graph = TwoBoardGraph();
        var (near, far) = CrossingGates(graph);

        GateCrossings.FreeCredits(graph, [near, far]).ShouldBe(1);
        GateCrossings.PointCost(graph, [near, far]).ShouldBe(1);
    }

    [Fact]
    public void A_lone_gate_still_costs_its_point()
    {
        var graph = TwoBoardGraph();
        var (near, _) = CrossingGates(graph);

        GateCrossings.FreeCredits(graph, [near]).ShouldBe(0);
        GateCrossings.PointCost(graph, [near]).ShouldBe(1);
    }

    [Fact]
    public void Non_gate_cells_earn_no_credit()
    {
        var graph = TwoBoardGraph();
        var normals = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.Normal)
            .Take(10)
            .Select(v => v.Cell)
            .ToList();

        GateCrossings.FreeCredits(graph, normals).ShouldBe(0);
        GateCrossings.PointCost(graph, normals).ShouldBe(normals.Count);
    }

    [Fact]
    public void Solve_across_a_board_reports_the_gate_credited_cost()
    {
        var graph = TwoBoardGraph();
        var target = graph.Vertices
            .Where(v => v.Cell.BoardSlot == 1 && v.Node.Kind == ParagonNodeKind.Rare)
            .OrderBy(v => v.Cell.Y) // any rare on the attached board forces the crossing
            .First().Cell;

        var plan = PlanSolver.Solve(graph, new PlanRequest { Targets = [target] });

        plan.Success.ShouldBeTrue(plan.Error);
        plan.GateCredits.ShouldBe(1); // exactly one crossing
        plan.PointsSpent.ShouldBe(plan.PurchasedCells.Count - 1);
    }

    [Fact]
    public void Gate_pair_grants_its_attributes_once_in_build_stats()
    {
        var graph = TwoBoardGraph();
        var (near, far) = CrossingGates(graph);

        // A connected path from start through both gate halves.
        var toGate = PlanSolver.Solve(graph, new PlanRequest { Targets = [near, far] });
        toGate.Success.ShouldBeTrue(toGate.Error);
        var withPair = toGate.PurchasedCells.ToList();
        var withoutFar = withPair.Where(c => c != far).ToList();

        var pairReport = BuildStats.Compute(graph, withPair, ParagonDatabase.Data, 0);
        var halfReport = BuildStats.Compute(graph, withoutFar, ParagonDatabase.Data, 0);

        // The far (auto-purchased) half adds nothing: +5 all attributes is granted per PAIR.
        graph.TryGetVertex(near, out int nearVertex).ShouldBeTrue();
        foreach (var attribute in graph.Vertices[nearVertex].Node.Attributes)
        {
            pairReport.Totals.GetValueOrDefault(attribute.Attribute)
                .ShouldBe(halfReport.Totals.GetValueOrDefault(attribute.Attribute),
                    $"{attribute.Attribute} must not be granted twice for one crossing");
        }
    }
}
