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
    // ── Import: Maxroll codes and URLs, Mobalytics pages ─────────────────

    [RelayCommand]
    private Task ImportBuild() => RunGuardedAsync("Import", ImportBuildCoreAsync);

    private async Task ImportBuildCoreAsync()
    {
        using var busy = BeginBusy();
        if (await ImportFromClipboardAsync() is { } import)
            ApplyImportedBuild(import.Build, import.Source);
    }

    /// <summary>
    /// Builds a paragon import from whatever is on the clipboard: a Maxroll variant code, a
    /// maxroll.gg planner or build-guide URL, a mobalytics.gg build URL, or pasted page HTML.
    /// Reports errors itself; returns null on failure or cancellation.
    /// </summary>
    private async Task<ParagonImport?> ImportFromClipboardAsync() =>
        await ImportManyFromClipboardAsync(allowMultiple: false) is { Count: > 0 } imports
            ? imports[0]
            : null;

    /// <summary>
    /// The multi-capable core of <see cref="ImportFromClipboardAsync"/>: with
    /// <paramref name="allowMultiple"/> the variant picker takes any number of a guide's build
    /// versions (e.g. a Mobalytics Selig setup AND the standard setup), each returned as its
    /// own build. Layout imports keep single-pick — the planner holds one layout at a time.
    /// </summary>
    private async Task<IReadOnlyList<ParagonImport>?>
        ImportManyFromClipboardAsync(bool allowMultiple)
    {
        string text = ClipboardHelper.TryGetText()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Copy a Maxroll variant code, a maxroll.gg, mobalytics.gg, or d4builds.gg " +
                      "URL, or a build page's HTML to the clipboard first.", error: true);
            return null;
        }

        try
        {
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("mobalytics.gg", StringComparison.OrdinalIgnoreCase))
                    return await ImportMobalyticsBuildsAsync(text, allowMultiple);
                if (text.Contains("maxroll.gg", StringComparison.OrdinalIgnoreCase))
                    return await ImportMaxrollUrlAsync(text, allowMultiple);
                if (text.Contains("d4builds.gg", StringComparison.OrdinalIgnoreCase))
                    return await ImportD4BuildsUrlAsync(text, allowMultiple);
                SetStatus("The clipboard URL is not a maxroll.gg, mobalytics.gg, or d4builds.gg page.",
                    error: true);
                return null;
            }
            if (text.StartsWith('['))
            {
                var build = MaxrollParagonCodec.ToLayout(
                    MaxrollParagonCodec.Decode(text), ParagonDatabase.BoardsByInternalName);
                ComposedGraph.Build(build.Layout);
                return [new ParagonImport(build, "Maxroll code")];
            }
            return await ImportMobalyticsBuildsAsync(text, allowMultiple); // pasted page HTML
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"Import failed: {ex.Message}", error: true);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetStatus($"Couldn't fetch the page ({ex.Message}). {ManualHtmlHint}", error: true);
            return null;
        }
    }

    private async Task<IReadOnlyList<ParagonImport>?>
        ImportMobalyticsBuildsAsync(string text, bool allowMultiple)
    {
        string html = text;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Fetching the Mobalytics page…");
            html = await FetchPageAsync(text.Split('#')[0]);
        }

        try
        {
            var variants = MobalyticsParagonImporter.ExtractVariants(html);
            var chosen = variants.Count == 1 ? [variants[0]] : PickVariants(variants, allowMultiple);
            if (chosen is not { Count: > 0 })
            {
                SetStatus("Import cancelled.");
                return null;
            }

            // The same page embeds the variants' skill setups — carry them along so reference
            // imports can vote skills too. Absent or unparseable skill data never blocks builds.
            IReadOnlyList<MobalyticsSkillVariant> skillVariants = [];
            try
            {
                skillVariants = MobalyticsSkillImporter.ExtractVariants(html);
            }
            catch (FormatException)
            {
            }

            var builds = new List<ParagonImport>();
            foreach (var variant in chosen)
            {
                var build = MobalyticsParagonImporter.ToBuild(variant, ParagonDatabase.Data);
                ComposedGraph.Build(build.Layout);
                builds.Add(new ParagonImport(
                    build, $"Mobalytics '{variant.Title}'", MatchSkills(skillVariants, variant.Title)));
            }
            return builds;
        }
        catch (FormatException) when (html.Contains("cf_chl", StringComparison.Ordinal))
        {
            SetStatus($"Cloudflare blocked the fetch. {ManualHtmlHint}", error: true);
            return null;
        }
    }

    /// <summary>
    /// Finds the skill variant belonging to a paragon variant. Identical paragon sections merge
    /// their titles ("A / B"), so any part matching a skill variant's title counts; a page with
    /// a single skill setup covers every paragon variant.
    /// </summary>
    private static MobalyticsSkillVariant? MatchSkills(
        IReadOnlyList<MobalyticsSkillVariant> skillVariants, string paragonTitle)
    {
        if (skillVariants.Count == 1)
            return skillVariants[0];
        var parts = paragonTitle.Split(" / ", StringSplitOptions.TrimEntries);
        return skillVariants.FirstOrDefault(s =>
            parts.Contains(s.Title, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ParagonImport>?>
        ImportMaxrollUrlAsync(string url, bool allowMultiple)
    {
        if (!MaxrollBuildImporter.TryParsePlannerUrl(url, out string plannerId))
        {
            SetStatus("Fetching the Maxroll page…");
            string html = await FetchPageAsync(url.Split('#')[0]);
            plannerId = MaxrollBuildImporter.ExtractPlannerIds(html).FirstOrDefault()
                ?? throw new FormatException("No planner link found in the Maxroll page — is it a build guide?");
        }

        SetStatus("Fetching the Maxroll planner data…");
        string json = await FetchPageAsync(string.Format(MaxrollBuildImporter.ProfileApiFormat, plannerId));
        var variants = MaxrollBuildImporter.ExtractVariants(json);
        IReadOnlyList<int> indices = [0];
        if (variants.Count > 1)
        {
            indices = PickIndices(
                variants.Select(v => $"{v.Title} — {v.Entries.Count} board(s)").ToList(), allowMultiple);
            if (indices.Count == 0)
            {
                SetStatus("Import cancelled.");
                return null;
            }
        }
        var builds = new List<ParagonImport>();
        foreach (int index in indices)
        {
            var build = MaxrollParagonCodec.ToLayout(variants[index].Entries, ParagonDatabase.BoardsByInternalName);
            ComposedGraph.Build(build.Layout);
            builds.Add(new ParagonImport(build, $"Maxroll '{variants[index].Title}'"));
        }
        return builds;
    }

    /// <summary>
    /// d4builds.gg pages render client-side from a public Firestore document, so the build's
    /// uuid from the URL fetches every variant's paragon setup without touching the page itself.
    /// </summary>
    private async Task<IReadOnlyList<ParagonImport>?>
        ImportD4BuildsUrlAsync(string url, bool allowMultiple)
    {
        if (!D4BuildsImporter.TryParseBuildUrl(url, out string buildId))
        {
            // Curated meta builds use a pretty slug; its prerendered page-data names the uuid.
            if (!D4BuildsImporter.TryParseBuildSlugUrl(url, out string slug))
            {
                SetStatus("The d4builds.gg link has no build id — copy a build page's URL " +
                          "(d4builds.gg/builds/…).", error: true);
                return null;
            }
            SetStatus("Resolving the d4builds.gg build…");
            buildId = D4BuildsImporter.ExtractBuildId(
                await FetchPageAsync(string.Format(D4BuildsImporter.PageDataApiFormat, slug)));
        }

        SetStatus("Fetching the d4builds.gg build…");
        string json = await FetchPageAsync(string.Format(D4BuildsImporter.BuildDocumentApiFormat, buildId));
        var variants = D4BuildsImporter.ExtractVariants(json);

        IReadOnlyList<int> indices = [0];
        if (variants.Count > 1)
        {
            indices = PickIndices(
                variants.Select(v => $"{v.Title} — {v.Boards.Count} board(s)").ToList(), allowMultiple);
            if (indices.Count == 0)
            {
                SetStatus("Import cancelled.");
                return null;
            }
        }

        var builds = new List<ParagonImport>();
        foreach (int index in indices)
        {
            var build = D4BuildsImporter.ToBuild(variants[index], ParagonDatabase.Data);
            ComposedGraph.Build(build.Layout);
            builds.Add(new ParagonImport(build, $"d4builds '{variants[index].Title}'"));
        }
        return builds;
    }

    private const string ManualHtmlHint =
        "Open the build in a browser, view the page source (Ctrl+U), copy it all, " +
        "then click Import Mobalytics again with the HTML in the clipboard.";

    private static Task<string> FetchPageAsync(string url) => GuidePageFetcher.FetchPageAsync(url);

    private IReadOnlyList<MobalyticsParagonVariant>? PickVariants(
        IReadOnlyList<MobalyticsParagonVariant> variants, bool allowMultiple)
    {
        var dialog = new MobalyticsVariantPickerWindow(variants, allowMultiple);
        return _dialogs.ShowDialog(dialog, this) == true ? dialog.SelectedVariants : null;
    }

    private IReadOnlyList<int> PickIndices(IReadOnlyList<string> labels, bool allowMultiple)
    {
        var dialog = new MobalyticsVariantPickerWindow(labels, allowMultiple);
        return _dialogs.ShowDialog(dialog, this) == true ? dialog.SelectedIndices : [];
    }

    /// <summary>The engine-side glyph activation default the socket editor also starts from.</summary>
    private const double DefaultGlyphActivationStat = 40;

    /// <summary>
    /// The import's glyphs as activation goals, so trimming an over-budget import spares the
    /// in-radius stat that keeps them active for as long as the budget allows. The active
    /// character's real glyph level (and so radius) beats the guide's assumption.
    /// </summary>
    private IReadOnlyList<GlyphGoal> ImportGlyphGoals(ConvertedMaxrollBuild build)
    {
        var graph = ComposedGraph.Build(build.Layout);
        var socketBySlot = graph.Vertices
            .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
            .ToDictionary(v => v.Cell.BoardSlot, v => v.Cell);
        var goals = new List<GlyphGoal>();
        foreach (var assignment in build.Glyphs)
        {
            var glyph = ParagonDatabase.Data.Glyphs.FirstOrDefault(g =>
                string.Equals(g.InternalName, assignment.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
            if (glyph is null || GlyphInfo.PrimarySourceAttribute(glyph) is not string sourceAttribute
                || !socketBySlot.TryGetValue(assignment.BoardSlot, out var socket))
                continue;
            int level = SelectedCharacter?.GlyphLevels.TryGetValue(assignment.GlyphInternalName, out int owned) == true
                ? owned
                : assignment.Level ?? 100;
            goals.Add(new GlyphGoal(socket, sourceAttribute, DefaultGlyphActivationStat,
                GlyphRadius.RadiusForLevel(level), glyph.Name));
        }
        return goals;
    }

    private void ApplyImportedBuild(ConvertedMaxrollBuild build, string source)
    {
        RecordUndo();

        // Fit the import to the current point pool: a guide build usually spends a max-level
        // budget, and a leveling player can't buy it all yet. Trim the least-valued nodes (by
        // the build's own priorities, glyph activations spared while the budget allows) and
        // surface the removed nodes as the buy-back plan for the way up.
        TrimToBudgetResult? trim = null;
        if (TotalPoints > 0)
        {
            var fitted = PointParity.TrimToBudget(
                new BuildSnapshot(source, build.Layout, build.AllocatedCells, build.Glyphs),
                TotalPoints, ParagonDatabase.Data, ImportGlyphGoals(build));
            if (fitted.RemovedCells.Count > 0)
            {
                trim = fitted;
                build = build with { AllocatedCells = fitted.Build.AllocatedCells };
            }
        }

        string? className = build.Layout.Boards[0].Board.ClassName;
        if (className is not null && className != SelectedClass)
        {
            // Snapshotted above already — the class switch's own RecordUndo would serialize the
            // whole session a second time only to dedupe it against that snapshot.
            _suppressUndo = true;
            try
            {
                SelectedClass = className;
            }
            finally
            {
                _suppressUndo = false;
            }
        }

        _placedBoards.Clear();
        _placedBoards.AddRange(build.Layout.Boards);
        RebuildLayout();

        foreach (var glyph in build.Glyphs)
        {
            var socket = GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == glyph.BoardSlot);
            if (socket is null)
                continue;
            socket.SelectedGlyph = socket.Glyphs.FirstOrDefault(g =>
                string.Equals(g.InternalName, glyph.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
            if (glyph.Level is int level)
                socket.Level = level;
        }
        // The import carries the guide's glyph levels; the active character's REAL levels win.
        int characterGlyphs = SelectedCharacter is { } activeCharacter
            ? ApplyCharacterGlyphLevels(activeCharacter)
            : 0;

        var allocated = build.AllocatedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = allocated.Contains(cell.Cell);
        ClearDiffMarks(); // a wholesale replacement — nothing meaningful to diff against
        RefreshBuildSummary();

        var assigned = GlyphSockets.Where(s => s.SelectedGlyph is not null).ToList();
        SolveDetails = assigned.Count == 0
            ? ""
            : "Imported glyphs: " + string.Join(", ", assigned.Select(DescribeGlyph));

        string trimSuffix = "";
        if (trim is not null)
        {
            string BuyBackName(CellRef cell) => _graph is not null && _graph.TryGetVertex(cell, out int v)
                ? $"{_graph.Vertices[v].Node.Name ?? _graph.Vertices[v].Node.InternalName} (slot {cell.BoardSlot})"
                : $"(slot {cell.BoardSlot}: {cell.X},{cell.Y})";
            // Removal took the least valuable first, so the reverse is the buy-back order.
            var buyBack = trim.RemovedCells.Reverse().Select(BuyBackName).ToList();
            var lines = new List<string>
            {
                $"Fitted the import to your {TotalPoints}-point pool " +
                $"({trim.PointsBefore} → {trim.PointsAfter}): removed the " +
                $"{trim.RemovedCells.Count} node(s) the build values least.",
            };
            lines.AddRange(trim.Notes);
            lines.Add("Buy back in this order while leveling: " + string.Join(", ", buyBack.Take(30)) +
                      (buyBack.Count > 30 ? $", … and {buyBack.Count - 30} more" : "") + ".");
            SolveDetails = string.Join(Environment.NewLine, lines) +
                           (SolveDetails.Length > 0 ? Environment.NewLine + SolveDetails : "");
            trimSuffix = $" Trimmed {trim.RemovedCells.Count} node(s) to fit {TotalPoints} points — " +
                         "buy-back order is in the details below.";
        }

        string characterSuffix = characterGlyphs > 0
            ? $" {characterGlyphs} glyph level(s) set from character '{SelectedCharacter!.Name}'."
            : "";
        SetStatus($"Imported {source} build: {_placedBoards.Count} board(s), {allocated.Count} allocated node(s)."
                  + trimSuffix + characterSuffix);
        NotifyLayoutReplaced();
    }

    [RelayCommand]
    private void ExportMaxrollCode()
    {
        if (_layout is null || _graph is null)
            return;

        var allocated = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToList();
        allocated.Add(_graph.Vertices[_graph.StartVertex].Cell);
        if (allocated.Count <= 1)
        {
            SetStatus("Nothing to export — solve a path (or import a build) first.", error: true);
            return;
        }

        var glyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new MaxrollGlyphAssignment(s.Socket.BoardSlot, s.SelectedGlyph!.InternalName, s.Level))
            .ToList();
        string code = MaxrollParagonCodec.Encode(
            MaxrollParagonCodec.FromLayout(_layout, allocated, glyphs));
        if (!ClipboardHelper.TrySetText(code))
        {
            SetStatus("Couldn't copy — another app is holding the clipboard. Try again.", error: true);
            return;
        }
        SetStatus($"Maxroll variant code copied to the clipboard ({allocated.Count} node(s)). " +
                  "Paste it into the Maxroll planner's Import Variant box.");
    }
}
