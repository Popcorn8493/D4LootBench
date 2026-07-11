using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// The game prices rare-node thresholds by ATTACHMENT order, which follows the path's gate
/// purchases — not the planner's slot numbering. When the path routes around a slot's parent
/// crossing (the user's WW2 barb entered slot 2 last, via slots 4 and 3), the effective tier
/// must follow the path.
/// </summary>
public class AttachTierTests
{
    private static ParagonBoardDef Board(string name) =>
        ParagonDatabase.Data.Boards.Single(b => b.InternalName == name);

    /// <summary>The user's topology: 1 above start, 2 above 1, 3 right of 2, 4 right of 1.</summary>
    private static ParagonLayout RingLayout() => new(
    [
        new PlacedBoard { Board = Board("Paragon_Barb_00") },
        new PlacedBoard { Board = Board("Paragon_Barb_03"), ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = 2 },
        new PlacedBoard { Board = Board("Paragon_Barb_02"), ParentSlot = 1, AttachEdge = BoardEdge.Top, RotationSteps = 2 },
        new PlacedBoard { Board = Board("Paragon_Barb_07"), ParentSlot = 2, AttachEdge = BoardEdge.Right, RotationSteps = 3 },
        new PlacedBoard { Board = Board("Paragon_Barb_06"), ParentSlot = 1, AttachEdge = BoardEdge.Right, RotationSteps = 0 },
    ]);

    [Fact]
    public void Path_entry_order_defines_the_attach_tiers()
    {
        var layout = RingLayout();
        var graph = ComposedGraph.Build(layout);

        // Block the direct 1↔2 crossing so the path must route 1 → 4 → 3 → 2.
        var blockedGates = new List<CellRef>();
        for (int v = 0; v < graph.Vertices.Count; v++)
        {
            if (graph.Vertices[v].Node.Kind != ParagonNodeKind.Gate)
                continue;
            foreach (int u in graph.Adjacency[v])
            {
                var (a, b) = (graph.Vertices[v].Cell.BoardSlot, graph.Vertices[u].Cell.BoardSlot);
                if ((a, b) is (1, 2) or (2, 1))
                    blockedGates.Add(graph.Vertices[v].Cell);
            }
        }
        blockedGates.ShouldNotBeEmpty("the layout must have a direct 1-2 crossing to block");

        var target = graph.Vertices.First(v =>
            v.Cell.BoardSlot == 2 && v.Node.Kind == ParagonNodeKind.Legendary).Cell;
        var solve = PlanSolver.Solve(graph, new PlanRequest
        {
            Targets = [target],
            ExcludeCells = blockedGates,
        });
        solve.Success.ShouldBeTrue(solve.Error);

        var purchased = solve.PurchasedCells.ToHashSet();
        var tiers = BuildStats.EffectiveAttachTiers(graph, purchased);

        tiers[0].ShouldBe(0);
        tiers[1].ShouldBe(1);                       // entered first from the start board
        tiers[2].ShouldBeGreaterThan(tiers[3]);     // slot 2 is entered AFTER slot 3 on this path
        tiers[2].ShouldBeGreaterThan(tiers[4]);     // and after slot 4

        // The real attachment structure: slot 2 is entered from slot 3, not from its planner
        // parent (slot 1) whose crossing is blocked; the parent gate sits on the entering board.
        var entries = BuildStats.EffectiveAttachments(graph, purchased);
        entries[2].EnteredFromSlot.ShouldBe(3);
        entries[2].ParentGate!.Value.BoardSlot.ShouldBe(3);
        purchased.ShouldContain(entries[2].ParentGate!.Value);

        // BuildStats must price slot-2 thresholds at the higher, path-derived tier.
        var report = BuildStats.Compute(graph, purchased, ParagonDatabase.Data,
            NonParagonStats.Uniform(0), "Barbarian");
        var slot2 = report.Thresholds.FirstOrDefault(t => t.Cell.BoardSlot == 2);
        if (slot2 is not null)
        {
            var node = graph.Vertices.First(v => v.Cell == slot2.Cell).Node;
            var def = ParagonDatabase.Data.Thresholds.First(t => node.Thresholds.Contains(t.SnoId));
            slot2.Requirement.ShouldBe(
                BuildStats.RequirementAt(def.Requirements[0], tiers[2]));
            slot2.Requirement.ShouldBeGreaterThan(
                BuildStats.RequirementAt(def.Requirements[0], 2));
        }
    }

    [Fact]
    public void Straight_chains_keep_slot_order()
    {
        var layout = new ParagonLayout(
        [
            new PlacedBoard { Board = Board("Paragon_Barb_00") },
            new PlacedBoard { Board = Board("Paragon_Barb_03"), ParentSlot = 0, AttachEdge = BoardEdge.Top, RotationSteps = 2 },
            new PlacedBoard { Board = Board("Paragon_Barb_02"), ParentSlot = 1, AttachEdge = BoardEdge.Top, RotationSteps = 2 },
        ]);
        var graph = ComposedGraph.Build(layout);
        var target = graph.Vertices.First(v =>
            v.Cell.BoardSlot == 2 && v.Node.Kind == ParagonNodeKind.Legendary).Cell;
        var solve = PlanSolver.Solve(graph, new PlanRequest { Targets = [target] });
        solve.Success.ShouldBeTrue(solve.Error);

        var tiers = BuildStats.EffectiveAttachTiers(graph, solve.PurchasedCells.ToHashSet());
        tiers.ShouldBe([0, 1, 2]);
    }
}
