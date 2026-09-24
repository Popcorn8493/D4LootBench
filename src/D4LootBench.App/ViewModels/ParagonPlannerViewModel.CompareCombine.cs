using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Services;
using D4LootBench.App.Views;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

public partial class ParagonPlannerViewModel
{
    // ── Compare / combine against another build ──────────────────────────

    /// <summary>The planner's current state as a build: layout, purchased cells, glyphs.</summary>
    private BuildSnapshot? CurrentSnapshot(string name)
    {
        if (_layout is null || _graph is null)
            return null;
        var allocated = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToList();
        if (allocated.Count == 0)
            return null;
        allocated.Add(_graph.Vertices[_graph.StartVertex].Cell);
        var glyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new MaxrollGlyphAssignment(s.Socket.BoardSlot, s.SelectedGlyph!.InternalName, s.Level))
            .ToList();
        return new BuildSnapshot(name, _layout, allocated, glyphs);
    }

    [RelayCommand]
    private Task CompareImport() => RunGuardedAsync("Compare", CompareImportCoreAsync);

    private async Task CompareImportCoreAsync()
    {
        if (CurrentSnapshot("Current") is not BuildSnapshot current)
        {
            SetStatus("Solve a path or import a build first — there is nothing to compare against.", error: true);
            return;
        }
        if (await ImportFromClipboardAsync() is not { } import)
            return;

        var imported = new BuildSnapshot(
            "Import", import.Build.Layout, import.Build.AllocatedCells, import.Build.Glyphs);

        // Judge BOTH builds at this planner's current glyph levels — imports carry the guide's
        // (often default-100) levels, and glyph radius/delivery scale with level. Same glyph =
        // my level; a glyph not socketed here gets the average of my levels.
        var myLevels = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .GroupBy(s => s.SelectedGlyph!.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Level, StringComparer.OrdinalIgnoreCase);
        // The character library knows glyphs that aren't currently socketed here.
        if (SelectedCharacter is { } activeCharacter)
        {
            foreach (var (glyph, level) in activeCharacter.GlyphLevels)
                myLevels.TryAdd(glyph, level);
        }
        int fallbackLevel = myLevels.Count > 0 ? (int)Math.Round(myLevels.Values.Average()) : 100;
        current = BuildComparer.WithGlyphLevels(current, myLevels, fallbackLevel);
        imported = BuildComparer.WithGlyphLevels(imported, myLevels, fallbackLevel);

        using var busy = BeginBusy();
        SetStatus("Comparing…");
        var sheetStats = SheetStatOffsets();
        // Point parity keeps the verdict about build quality, not budget: the smaller build is
        // grown by its own priorities (or the larger trimmed) until both spend the same.
        double gearAdditive = SheetAdditiveDamage / 100.0;
        double gearSituational = SheetSituationalDamage / 100.0;
        string report = await Task.Run(() =>
            BuildComparer.Compare(current, imported, ParagonDatabase.Data, sheetStats,
                matchPoints: true, additiveDamageOffset: gearAdditive,
                situationalDamageOffset: gearSituational));
        if (current.Glyphs.Count > 0 || imported.Glyphs.Count > 0)
        {
            report = "Glyph levels: both builds judged at this planner's current levels" +
                     $" (glyphs not socketed here assume lvl {fallbackLevel})." + Environment.NewLine + report;
        }
        SolveDetails = report;
        SetStatus($"Compared the current build against {import.Source} at matched points — see the report below.");
    }

    [RelayCommand]
    private Task CombineImport() => RunGuardedAsync("Combine", CombineImportCoreAsync);

    private async Task CombineImportCoreAsync()
    {
        if (CurrentSnapshot("the current build") is not BuildSnapshot current)
        {
            SetStatus("Solve a path or import a build first — there is nothing to combine with.", error: true);
            return;
        }
        using var busy = BeginBusy();
        if (await ImportFromClipboardAsync() is not { } import)
            return;

        var imported = new BuildSnapshot(
            "the import", import.Build.Layout, import.Build.AllocatedCells, import.Build.Glyphs);
        var purchasesBefore = CurrentPurchases();
        CombinedBuild combined;
        try
        {
            combined = await Task.Run(() => BuildCombiner.Merge(current, imported, ParagonDatabase.Data));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"Combine failed: {ex.Message}", error: true);
            return;
        }

        ApplyImportedBuild(combined.Build, $"combined build ({import.Source})");

        // Both builds' key nodes become targets; re-solve for the cheapest tree covering them all.
        var targetSet = combined.Targets.ToHashSet();
        _targets.Clear();
        foreach (var cell in Cells)
        {
            cell.IsTarget = !cell.IsStart && targetSet.Contains(cell.Cell);
            if (cell.IsTarget)
                _targets.Add(cell.Cell);
        }
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
        {
            await SolveAsync();
            MarkPurchaseDiff(purchasesBefore);
        }
        if (combined.Notes.Count > 0)
        {
            SolveDetails = string.Join(Environment.NewLine, combined.Notes) +
                           (SolveDetails.Length > 0 ? Environment.NewLine + SolveDetails : "");
        }
    }
}
