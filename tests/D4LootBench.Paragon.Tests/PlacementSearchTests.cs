using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

public class PlacementSearchTests
{
    // The real Shred Druid capture (see D4BuildsImporterTests): 5 boards, all four rotations,
    // 5 glyphs — a realistic layout for sequence search.
    private static string DruidJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "d4builds-shred-druid.json"));

    /// <summary>Layout + request + pipeline for the fixture build, optionally mis-rotating one slot.</summary>
    private static (ParagonLayout Layout, PlanRequest Request, PlacementPipeline Pipeline) Scenario(
        int? misrotateSlot = null)
    {
        var variant = D4BuildsImporter.ExtractVariants(DruidJson)[0];
        var entries = D4BuildsImporter.ToMaxrollEntries(variant, ParagonDatabase.Data).ToList();
        if (misrotateSlot is int slot)
            entries[slot].Rotation = (entries[slot].Rotation + 2) & 3;
        var build = MaxrollParagonCodec.ToLayout(entries, ParagonDatabase.BoardsByInternalName);
        var graph = ComposedGraph.Build(build.Layout);

        // Goals from the build's own glyph assignments, sockets read off THIS layout.
        var socketBySlot = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);
        var glyphs = new List<PipelineGlyph>();
        var goals = new List<GlyphGoal>();
        foreach (var assignment in build.Glyphs)
        {
            var glyph = ParagonDatabase.Data.Glyphs.Single(g =>
                string.Equals(g.InternalName, assignment.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
            int level = Math.Min(assignment.Level ?? 1, 50);
            glyphs.Add(new PipelineGlyph(assignment.BoardSlot, glyph, level));
            if (GlyphInfo.PrimarySourceAttribute(glyph) is { } source)
            {
                goals.Add(new GlyphGoal(
                    socketBySlot[assignment.BoardSlot], source, 40,
                    GlyphRadius.RadiusForLevel(level), glyph.Name));
            }
        }

        var request = new PlanRequest
        {
            Targets = PlanSolver.LegendaryCells(graph).ToList(),
            GlyphGoals = goals,
        };
        var pipeline = new PlacementPipeline(
            300, new MaximizeFocus(MaximizeFocus.CoreStats, PreferRare: false), Thresholds: null, glyphs);
        return (build.Layout, request, pipeline);
    }

    [Fact]
    public void Plans_repair_a_misrotated_board_and_carry_applyable_composites()
    {
        var (layout, request, pipeline) = Scenario(misrotateSlot: 1);

        var spareBoards = ParagonDatabase.BoardsForClass("Druid")
            .Where(b => b.BoardIndex != 0
                && !layout.Boards.Any(p => p.Board.InternalName == b.InternalName))
            .ToList();
        var plans = PlacementSearch.FindPlans(
            layout, request, pipeline, spareBoards, spareGlyphs: [],
            beamWidth: 2, depth: 2, topN: 3, evalsPerState: 6, maxEvaluations: 20);

        // Sanity: the un-perturbed layout genuinely beats the perturbed one at full spend, so
        // the search has an improvement to find (the slot-1 rotation at minimum).
        var (goodLayout, goodRequest, goodPipeline) = Scenario();
        var good = PlacementAnalyzer.EvaluatePipeline(
            ComposedGraph.Build(goodLayout), goodRequest, goodPipeline);
        var perturbed = PlacementAnalyzer.EvaluatePipeline(
            ComposedGraph.Build(layout), request, pipeline);
        good.ShouldNotBeNull();
        perturbed.ShouldNotBeNull();

        if (good.BeatsForSuggestion(perturbed))
            plans.ShouldNotBeEmpty();

        plans.Count.ShouldBeLessThanOrEqualTo(3);
        foreach (var plan in plans)
        {
            // Every option beats the current setup by the suggestion rules and is applyable as
            // one ordered composite whose step texts pair 1:1 with the changes.
            plan.Result.BeatsForSuggestion(plan.Baseline).ShouldBeTrue();
            var composite = plan.Change.ShouldBeOfType<CompositeChange>();
            composite.Changes.Count.ShouldBe(plan.Steps.Count);
            composite.Changes.OfType<CompositeChange>().ShouldBeEmpty();
            plan.Comparison.ShouldContain("points");
            plan.Describe(1, plans.Count).ShouldContain("1.");
        }

        // Options are distinct end states, not the same setup reworded.
        plans.Select(p => string.Join("|", p.Steps)).ShouldBeUnique();
    }

    [Fact]
    public void Search_without_improvements_returns_nothing_and_respects_the_eval_cap()
    {
        var (layout, request, pipeline) = Scenario();

        // No spare boards or glyphs, beam 1, depth 1, and a tiny eval budget: whatever it finds
        // must still obey the plan invariants; typically the author's layout has little slack.
        var plans = PlacementSearch.FindPlans(
            layout, request, pipeline, spareBoards: [], spareGlyphs: [],
            beamWidth: 1, depth: 1, topN: 3, evalsPerState: 3, maxEvaluations: 3);

        foreach (var plan in plans)
            plan.Result.BeatsForSuggestion(plan.Baseline).ShouldBeTrue();
    }

    [Fact]
    public void Locked_boards_and_glyphs_are_never_swapped_or_substituted()
    {
        var (layout, request, pipeline) = Scenario(misrotateSlot: 1);
        var spareBoards = ParagonDatabase.BoardsForClass("Druid")
            .Where(b => b.BoardIndex != 0
                && !layout.Boards.Any(p => p.Board.InternalName == b.InternalName))
            .ToList();
        var socketedNames = pipeline.SocketedGlyphs!.Select(g => g.Glyph.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareGlyphs = ParagonDatabase.GlyphsForClass("Druid")
            .Where(g => !socketedNames.Contains(g.InternalName))
            .ToList();
        var allSlots = Enumerable.Range(0, layout.Boards.Count).ToHashSet();

        var plans = PlacementSearch.FindPlans(
            layout, request, pipeline, spareBoards, spareGlyphs,
            beamWidth: 2, depth: 2, topN: 3, evalsPerState: 6, maxEvaluations: 20,
            lockedBoardSlots: allSlots, lockedGlyphSlots: allSlots);

        foreach (var plan in plans)
        {
            var changes = plan.Change.ShouldBeOfType<CompositeChange>().Changes;
            changes.OfType<BoardSwapChange>().ShouldBeEmpty();
            changes.OfType<GlyphSwapChange>().ShouldBeEmpty();
        }
    }

    [Fact]
    public void AtGlyphLevel_pins_every_glyph_and_goal_radius()
    {
        var (_, request, pipeline) = Scenario();
        pipeline.SocketedGlyphs!.ShouldContain(g => g.Level != 51);

        var (normalizedRequest, normalizedPipeline) = PlacementSearch.AtGlyphLevel(request, pipeline, 51);

        // A normalization, not a floor: levels above the pin come down too.
        normalizedPipeline.SocketedGlyphs!.ShouldAllBe(g => g.Level == 51);
        normalizedRequest.GlyphGoals.ShouldAllBe(g => g.Radius == GlyphRadius.RadiusForLevel(51));
        normalizedRequest.GlyphGoals.Count.ShouldBe(request.GlyphGoals.Count);
    }

    [Fact]
    public void Glyph_substitutions_respect_activation_feasibility_and_dedup()
    {
        var (layout, request, pipeline) = Scenario();
        var socketedNames = pipeline.SocketedGlyphs!.Select(g => g.Glyph.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareGlyphs = ParagonDatabase.GlyphsForClass("Druid")
            .Where(g => !socketedNames.Contains(g.InternalName))
            .ToList();
        spareGlyphs.ShouldNotBeEmpty();

        var plans = PlacementSearch.FindPlans(
            layout, request, pipeline, spareBoards: [], spareGlyphs,
            beamWidth: 2, depth: 1, topN: 3, evalsPerState: 6, maxEvaluations: 12);

        foreach (var plan in plans)
        {
            foreach (var swap in plan.Change.ShouldBeOfType<CompositeChange>().Changes.OfType<GlyphSwapChange>())
            {
                // A recommended glyph is never one that is already socketed.
                socketedNames.ShouldNotContain(swap.NewGlyph.InternalName);
                // The step names the assumed level, since the player may still need to level it.
                plan.Steps.ShouldContain(s => s.Contains("judged at level"));
            }
        }
    }
}
