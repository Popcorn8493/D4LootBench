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
    // ── Spending leftover points ──────────────────────────────────────────

    /// <summary>
    /// Spends whatever the current path leaves of <see cref="TotalPoints"/> on the focused stats
    /// (and rare nodes first, when preferred), growing the purchased tree greedily.
    /// </summary>
    [RelayCommand]
    private Task MaximizePoints() => RunGuardedAsync("Spending points", MaximizePointsCoreAsync);

    private async Task MaximizePointsCoreAsync()
    {
        RecordUndo();
        var before = CurrentPurchases();
        await MaximizePointsAsync();
        MarkPurchaseDiff(before);
    }

    private async Task MaximizePointsAsync()
    {
        if (_graph is null)
            return;
        var purchased = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();
        // In-game cost, not cell count: a crossing's gate pair costs one point (GateCrossings).
        int costBefore = GateCrossings.PointCost(_graph, purchased);
        int remaining = TotalPoints - costBefore;
        // With thresholds on, a zero budget still runs the reallocation pass — it trades
        // already-spent points for small threshold deficits without needing new ones.
        if (remaining <= 0 && !ActivateThresholds)
        {
            SetStatus($"No points left — {costBefore} of {TotalPoints} are already spent.", error: true);
            return;
        }
        remaining = Math.Max(0, remaining);

        var focus = CurrentMaximizeFocus();
        var context = new ThresholdContext(
            ParagonDatabase.Data, SelectedClass, SheetStatOffsets(), CellMultipliers(purchased));
        var request = BuildPlanRequest();
        var graph = _graph;
        using var busy = BeginBusy();
        SetStatus(remaining > 0
            ? $"Spending up to {remaining} remaining point(s)…"
            : "No points left — checking whether reallocating any closes a threshold…");
        var outcome = await Task.Run(() => PointMaximizer.Extend(graph, purchased, remaining, focus, request, context));

        int spent = Math.Max(0, GateCrossings.PointCost(graph, purchased) - costBefore);
        // "Reallocated"/"Rebalanced" notes describe swaps the maximizer made, not problems.
        bool reallocated = outcome.Notes.Any(IsSwapNote);
        if (outcome.AddedCells.Count == 0 && !reallocated)
        {
            SetStatus(outcome.Notes.FirstOrDefault()
                ?? (remaining > 0
                    ? "Nothing worthwhile is reachable with the remaining points — no nodes added."
                    : $"No points left and no beneficial reallocation found — {costBefore} of {TotalPoints} spent."),
                error: true);
            return;
        }
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
        RefreshBuildSummary();

        string gains = string.Join(", ", outcome.Gains
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{FormatGain(kv.Value)} {ParagonDisplay.FormatAttributeName(kv.Key)}"));
        string rares = outcome.RaresAdded > 0 ? $"{outcome.RaresAdded} rare node(s), " : "";
        string thresholdsPart = outcome.ThresholdsActivated > 0
            ? $"{outcome.ThresholdsActivated} threshold bonus(es) activated, "
            : "";
        string details = BuildPurchaseReport(purchased, [], outcome.Notes);
        SolveDetails = $"Spent {spent} leftover point(s): {rares}{thresholdsPart}" +
                       $"{(gains.Length > 0 ? "gained " + gains : "no focused stat gains")}." +
                       (details.Length > 0 ? Environment.NewLine + details : "");
        SetStatus($"{GateCrossings.PointCost(graph, purchased)} of {TotalPoints} points spent " +
                  $"(+{spent} maximizing{(PreferRareNodes ? " rare nodes and" : "")} focused stats" +
                  $"{(outcome.ThresholdsActivated > 0 ? $", {outcome.ThresholdsActivated} threshold(s) activated" : "")}" +
                  $"{(reallocated ? ", some points reallocated" : "")}) — " +
                  "changes ringed on the board: cyan added, dashed red removed.",
            error: outcome.Notes.Any(n => !IsSwapNote(n)));
    }

    private static bool IsSwapNote(string note) =>
        note.StartsWith("Reallocated", StringComparison.Ordinal)
        || note.StartsWith("Rebalanced", StringComparison.Ordinal);

    /// <summary>The Optimize-tab settings as one maximizer focus (stats, weights, rare/threshold flags).</summary>
    private MaximizeFocus CurrentMaximizeFocus()
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var stat in FocusStats.Where(f => f.IsSelected && Math.Abs(f.Weight - 1.0) > 1e-9))
            weights[stat.Attribute] = stat.Weight;
        return new MaximizeFocus(
            FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute).ToList(),
            PreferRareNodes,
            ActivateThresholds)
        {
            Weights = weights.Count > 0 ? weights : null,
            RealisticRares = RealisticRares,
            DefenseShare = DefenseShareOf(),
            // Every socketed attribute-mapped glyph pulls — not just the ones with an
            // activation goal — weighted by its scalar so stronger glyphs attract more stat,
            // and by the focus weight of what it converts INTO, so a glyph feeding a focused
            // stat outranks an equal-scalar glyph feeding an off-build one.
            GlyphDeliveries = GlyphDelivery.For(GlyphSockets
                .Where(s => s.SelectedGlyph is not null)
                .Select(s => (s.Socket, s.SelectedGlyph!, s.Level)),
                weights.Count > 0 ? weights : null),
            // The build's current additive bucket — additive "+% damage" attrs are then valued
            // at their marginal real contribution instead of face value.
            AdditiveDamageFraction = BalanceDamageBuckets ? _damageProfile?.AdditiveFraction : null,
        };
    }

    /// <summary>Fractional stat values are percentages (see <see cref="ParagonDisplay.FormatAttribute"/>).</summary>
    private static string FormatGain(double value) =>
        Math.Abs(value) < 1 && value != 0 ? $"{value * 100:0.##}%" : $"{value:0.##}";
}
