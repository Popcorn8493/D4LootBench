using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon.Solver;

/// <summary>
/// The merged layout plus the key nodes (rare, legendary) both source builds valued — re-solve
/// with these as targets to get the cheapest tree covering both builds' priorities.
/// </summary>
public sealed record CombinedBuild(
    ConvertedMaxrollBuild Build,
    IReadOnlyList<CellRef> Targets,
    IReadOnlyList<string> Notes);

/// <summary>
/// Merges two builds of the same class into one layout: boards at the same position union their
/// allocations, a board both builds use at different positions is folded onto its first position
/// (node keys are rotation-independent), and boards unique to the second build are added while
/// they fit the board cap and stay gate-connected. Conflicts are reported, never fatal.
/// </summary>
public static class BuildCombiner
{
    public static CombinedBuild Merge(BuildSnapshot a, BuildSnapshot b, ParagonData data)
    {
        string? classA = a.Layout.Boards[0].Board.ClassName;
        string? classB = b.Layout.Boards[0].Board.ClassName;
        if (!string.Equals(classA, classB, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Cannot combine builds of different classes ({classA} vs {classB}).");

        var notes = new List<string>();
        var merged = MaxrollParagonCodec.FromLayout(a.Layout, a.AllocatedCells, a.Glyphs)
            .ToDictionary(e => (e.Position?.X ?? 0, e.Position?.Y ?? 0));
        var mergedById = merged.Values.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var additions = new List<MaxrollBoardEntry>();
        foreach (var entry in MaxrollParagonCodec.FromLayout(b.Layout, b.AllocatedCells, b.Glyphs))
        {
            var position = (entry.Position?.X ?? 0, entry.Position?.Y ?? 0);
            if (merged.TryGetValue(position, out var resident))
            {
                if (string.Equals(resident.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                    Fold(resident, entry);
                else
                    notes.Add($"Skipped {b.Name}'s {BoardName(entry.Id, data)} at ({position.Item1}, {position.Item2}) — " +
                              $"{a.Name} has {BoardName(resident.Id, data)} there.");
            }
            else if (mergedById.TryGetValue(entry.Id, out var elsewhere))
            {
                // The same board can't appear twice; node keys are unrotated, so its
                // allocations transfer to wherever the first build placed it.
                Fold(elsewhere, entry);
                notes.Add($"Folded {b.Name}'s {BoardName(entry.Id, data)} allocations onto {a.Name}'s placement of it.");
            }
            else
                additions.Add(entry);
        }

        // Board cap: keep the additions with the most allocations.
        additions = additions.OrderByDescending(e => e.Nodes?.Count ?? 0).ToList();
        int free = ParagonLayout.MaxBoards - merged.Count;
        if (additions.Count > Math.Max(0, free))
        {
            foreach (var dropped in additions.Skip(Math.Max(0, free)))
                notes.Add($"Dropped {b.Name}'s {BoardName(dropped.Id, data)} — the {ParagonLayout.MaxBoards}-board cap is reached.");
            additions = additions.Take(Math.Max(0, free)).ToList();
        }

        // Gate connectivity: additions sit at their original positions, which may not touch the
        // merged cluster. Drop the least-allocated unconnectable ones until the layout converts.
        var boardsByInternalName = data.Boards
            .Where(bd => !string.IsNullOrEmpty(bd.InternalName))
            .ToDictionary(bd => bd.InternalName, StringComparer.OrdinalIgnoreCase);
        ConvertedMaxrollBuild converted;
        while (true)
        {
            try
            {
                converted = MaxrollParagonCodec.ToLayout(
                    merged.Values.Concat(additions).ToList(), boardsByInternalName);
                break;
            }
            catch (FormatException) when (additions.Count > 0)
            {
                var dropped = additions[^1];
                additions.RemoveAt(additions.Count - 1);
                notes.Add($"Dropped {b.Name}'s {BoardName(dropped.Id, data)} — " +
                          "it doesn't connect to the combined layout through gates.");
            }
        }

        // The game allows each glyph only once — keep the first assignment of a duplicate.
        var seenGlyphs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var glyphs = new List<MaxrollGlyphAssignment>();
        foreach (var glyph in converted.Glyphs)
        {
            if (seenGlyphs.Add(glyph.GlyphInternalName))
                glyphs.Add(glyph);
            else
                notes.Add($"Dropped a duplicate {GlyphName(glyph.GlyphInternalName, data)} glyph assignment " +
                          $"on slot {glyph.BoardSlot}.");
        }
        converted = converted with { Glyphs = glyphs };

        // Key nodes both builds paid for: rare and legendary cells of the union become targets.
        var graph = ComposedGraph.Build(
            converted.Layout, data.Nodes.ToDictionary(n => n.SnoId, StringComparer.OrdinalIgnoreCase));
        var targets = converted.AllocatedCells
            .Where(cell => graph.TryGetVertex(cell, out int v)
                           && graph.Vertices[v].Node.Kind is ParagonNodeKind.Rare or ParagonNodeKind.Legendary)
            .Distinct()
            .ToList();

        return new CombinedBuild(converted, targets, notes);

        void Fold(MaxrollBoardEntry into, MaxrollBoardEntry from)
        {
            if (from.Nodes is not null)
            {
                into.Nodes ??= [];
                foreach (var (key, value) in from.Nodes)
                    into.Nodes[key] = Math.Max(into.Nodes.GetValueOrDefault(key), value);
            }
            if (from.Glyph is { Length: > 0 })
            {
                if (string.IsNullOrEmpty(into.Glyph))
                {
                    into.Glyph = from.Glyph;
                    into.GlyphLevel = from.GlyphLevel;
                }
                else if (!string.Equals(into.Glyph, from.Glyph, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"Kept {a.Name}'s {GlyphName(into.Glyph, data)} on {BoardName(into.Id, data)} — " +
                              $"{b.Name} sockets {GlyphName(from.Glyph, data)} there instead.");
                }
            }
        }
    }

    private static string BoardName(string internalName, ParagonData data) =>
        data.Boards.FirstOrDefault(bd =>
            string.Equals(bd.InternalName, internalName, StringComparison.OrdinalIgnoreCase))?.Name
        ?? internalName;

    private static string GlyphName(string internalName, ParagonData data) =>
        data.Glyphs.FirstOrDefault(g =>
            string.Equals(g.InternalName, internalName, StringComparison.OrdinalIgnoreCase))?.Name
        ?? internalName;
}
