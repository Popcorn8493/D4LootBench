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
    // ── Solving the path (and Re-analyze: solve + full spend) ─────────────

    /// <summary>The active (non-Allow) node rules; group keys survive layout changes.</summary>
    private List<NodeRule> CurrentNodeRules() => NodeRules
        .Where(r => r.Mode != NodeRuleMode.Allow)
        .Select(r => new NodeRule(r.Group.Key, r.Mode, r.Limit))
        .ToList();

    /// <summary>Targets, rules, per-cell overrides and glyph goals as one immutable solver request.</summary>
    private PlanRequest BuildPlanRequest()
    {
        var excludeCells = Cells.Where(c => c.Constraint == CellConstraint.Exclude).Select(c => c.Cell).ToList();
        var targets = _targets.ToList();
        // Boards are attached FOR their legendary nodes — route through every one unless the
        // user turned the setting off or excluded the cell explicitly.
        if (IncludeLegendaryNodes && _graph is not null)
        {
            targets = targets
                .Concat(PlanSolver.LegendaryCells(_graph, excludeCells.ToHashSet()))
                .Distinct()
                .ToList();
        }
        return new PlanRequest
        {
            Targets = targets,
            NodeRules = CurrentNodeRules(),
            AvoidCells = Cells.Where(c => c.Constraint == CellConstraint.Avoid).Select(c => c.Cell).ToList(),
            ExcludeCells = excludeCells,
            GlyphGoals = GlyphSockets
                .Where(s => s.EnsureActive && s.SourceAttribute is not null)
                .Select(s => new GlyphGoal(s.Socket, s.SourceAttribute!, s.RequiredStat, s.Radius, s.SelectedGlyph?.Name))
                .ToList(),
        };
    }

    [RelayCommand]
    private Task Solve() => RunGuardedAsync("Solve", SolveCoreAsync);

    private async Task SolveCoreAsync()
    {
        RecordUndo();
        var before = CurrentPurchases();
        if (await SolveAsync())
            MarkPurchaseDiff(before);
    }

    /// <summary>
    /// One-click re-run after any change (targets, rules, glyphs, stats): solve the path fresh,
    /// then spend every remaining point of the pool with the Optimize-tab settings.
    /// </summary>
    [RelayCommand]
    private Task Reanalyze() => RunGuardedAsync("Re-analyze", ReanalyzeCoreAsync);

    private async Task ReanalyzeCoreAsync()
    {
        if (_graph is null)
            return;
        RecordUndo();
        var before = CurrentPurchases();
        using var busy = BeginBusy();
        if (!await SolveAsync())
            return;
        // With thresholds on, a full pool still gets the reallocation check.
        if (TotalPoints - CurrentPointCost() > 0 || ActivateThresholds)
            await MaximizePointsAsync();
        MarkPurchaseDiff(before);
    }

    /// <summary>Solve as a plain task so Combine/Apply/Revert can await it inside their busy scope.</summary>
    private async Task<bool> SolveAsync()
    {
        if (_graph is null)
            return false;
        var request = BuildPlanRequest();
        if (request.Targets.Count == 0 && request.GlyphGoals.Count == 0)
        {
            SetStatus("Mark at least one target node (or enable a glyph activation goal) first.", error: true);
            return false;
        }

        using var busy = BeginBusy();
        SetStatus("Solving the cheapest connected path…");
        var graph = _graph;
        var result = await Task.Run(() => PlanSolver.Solve(graph, request));
        if (!result.Success)
        {
            SetStatus(result.Error ?? "Solve failed.", error: true);
            return false;
        }

        var purchased = result.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
        RefreshBuildSummary();

        SolveDetails = BuildSolveDetails(result);

        string perBoard = string.Join(", ", result.PurchasedCells
            .GroupBy(c => c.BoardSlot)
            .OrderBy(g => g.Key)
            .Select(g => $"slot {g.Key}: {g.Count()}"));
        string quality = result.IsOptimal ? "optimal" : "heuristic";
        string budget = result.PointsSpent > MaxParagonPoints
            ? $" Exceeds the {MaxParagonPoints}-point cap!"
            : "";
        string warning = result.Notes.Count == 0
            ? ""
            : $" ⚠ {result.Notes[0]}" +
              (result.Notes.Count > 1 ? $" (+{result.Notes.Count - 1} more — see details below)" : "");
        int legendaries = request.Targets.Count - _targets.Count;
        string targetSummary = legendaries > 0
            ? $"{_targets.Count} target(s) + {legendaries} legendary node(s)"
            : $"{_targets.Count} target(s)";
        SetStatus($"{result.PointsSpent} paragon points for {targetSummary} ({quality}) — {perBoard}.{budget}{warning}",
            error: budget.Length > 0 || result.Notes.Count > 0);
        return true;
    }
}
