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
    // ── Save / open the planner session as a project file ────────────────

    private const string ProjectDialogFilter = "Paragon Project|*.paragon.json|All Files|*.*";

    /// <summary>Recently saved or opened projects, newest first — feeds the Recent menu.</summary>
    public ObservableCollection<RecentProject> RecentProjects { get; } = [];

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in _recentProjects.Paths)
            RecentProjects.Add(new RecentProject(path));
    }

    private void RememberRecentProject(string path)
    {
        _recentProjects.Touch(path);
        RefreshRecentProjects();
    }

    /// <summary>The file Ctrl+S writes to; null until the session is saved or opened once.</summary>
    private string? _currentProjectPath;

    /// <summary>Window title — carries the current project file name once one exists.</summary>
    [ObservableProperty]
    private string _windowTitle = "Paragon Planner — D4LootBench";

    private void SetCurrentProject(string? path)
    {
        _currentProjectPath = path;
        WindowTitle = path is null
            ? "Paragon Planner — D4LootBench"
            : $"Paragon Planner — {Path.GetFileName(path)}";
    }

    /// <summary>Ctrl+S: saves straight to the current file; falls back to Save As the first time.</summary>
    [RelayCommand]
    private void SaveProject()
    {
        if (_layout is null)
            return;
        if (_currentProjectPath is null)
        {
            SaveProjectAs();
            return;
        }
        WriteProject(_currentProjectPath);
    }

    /// <summary>Ctrl+Shift+S: always asks where to save, then becomes the Ctrl+S target.</summary>
    [RelayCommand]
    private void SaveProjectAs()
    {
        if (_layout is null)
            return;
        string suggested = _currentProjectPath is null
            ? $"{SelectedClass.ToLowerInvariant()}-paragon"
            : Path.GetFileName(_currentProjectPath);
        if (_dialogs.ShowSaveFileDialog("Save Paragon Project", ProjectDialogFilter, ".paragon.json",
                suggested, this) is not string path)
            return;
        WriteProject(path);
    }

    private void WriteProject(string path)
    {
        try
        {
            File.WriteAllText(path, ParagonProjectSerializer.ToJson(CaptureProject()));
            SetCurrentProject(path);
            RememberRecentProject(path);
            SetStatus($"Saved project \"{Path.GetFileName(path)}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Save failed: {ex.Message}", error: true);
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (_dialogs.ShowOpenFileDialog("Open Paragon Project", ProjectDialogFilter, ".paragon.json", this)
            is not string path)
            return;
        OpenProjectFile(path);
    }

    /// <summary>Opens an entry of the Recent menu directly, skipping the file dialog.</summary>
    [RelayCommand]
    private void OpenRecentProject(RecentProject recent) => OpenProjectFile(recent.FullPath);

    // ── Undo / redo ───────────────────────────────────────────────────────
    // Snapshot-based: every mutating action first serializes the whole session (the same
    // ParagonProject a save writes), and undo restores it through ApplyProject. Glyph picks,
    // rule tweaks, and focus checkboxes are directly reversible by hand and not snapshotted.

    private const int UndoDepth = 30;
    private readonly List<string> _undoStack = [];
    private readonly List<string> _redoStack = [];

    /// <summary>Suppresses snapshots while ApplyProject rebuilds state (open, undo, redo).</summary>
    private bool _restoring;

    /// <summary>Set around nested mutations of an action that already recorded its snapshot.</summary>
    private bool _suppressUndo;

    /// <summary>Call BEFORE mutating; consecutive identical states collapse, so a recorded
    /// action that then fails or no-ops never produces a dead undo step.</summary>
    private void RecordUndo()
    {
        if (_restoring || _suppressUndo || _layout is null)
            return;
        string snapshot = ParagonProjectSerializer.ToJson(CaptureProject());
        if (_undoStack.Count > 0 && _undoStack[^1] == snapshot)
            return;
        _undoStack.Add(snapshot);
        if (_undoStack.Count > UndoDepth)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        string current = ParagonProjectSerializer.ToJson(CaptureProject());
        // Snapshots equal to the present state are leftovers of actions that changed nothing.
        while (_undoStack.Count > 0 && _undoStack[^1] == current)
            _undoStack.RemoveAt(_undoStack.Count - 1);
        if (_undoStack.Count == 0)
        {
            UndoCommand.NotifyCanExecuteChanged();
            SetStatus("Nothing to undo.");
            return;
        }
        string snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(current);
        RestoreSnapshot(snapshot);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SetStatus("Undid the last change (Ctrl+Y redoes it).");
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        string current = ParagonProjectSerializer.ToJson(CaptureProject());
        while (_redoStack.Count > 0 && _redoStack[^1] == current)
            _redoStack.RemoveAt(_redoStack.Count - 1);
        if (_redoStack.Count == 0)
        {
            RedoCommand.NotifyCanExecuteChanged();
            SetStatus("Nothing to redo.");
            return;
        }
        string snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(current);
        RestoreSnapshot(snapshot);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SetStatus("Redid the undone change.");
    }

    private bool CanRedo() => _redoStack.Count > 0;

    private void RestoreSnapshot(string json)
    {
        try
        {
            ApplyProject(ParagonProjectSerializer.FromJson(json), refitView: false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            // Snapshots round-trip our own state, so this is effectively unreachable.
            SetStatus($"Restore failed: {ex.Message}", error: true);
        }
    }

    private void OpenProjectFile(string path)
    {
        try
        {
            var project = ParagonProjectSerializer.FromJson(File.ReadAllText(path));
            RecordUndo();
            ApplyProject(project);
            // The active character's real facts beat whatever the project file was saved with.
            if (SelectedCharacter is { } character)
                ImposeCharacter(character);
            SetCurrentProject(path);
            RememberRecentProject(path);
            SetStatus($"Opened project \"{Path.GetFileName(path)}\": {_placedBoards.Count} board(s), " +
                      $"{Cells.Count(c => c.IsPurchased)} allocated node(s)." +
                      (SelectedCharacter is { } c ? $" Character '{c.Name}' re-applied." : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or ArgumentException or InvalidOperationException)
        {
            // A vanished file is stale in the Recent menu — drop it so the menu stays honest.
            if (!File.Exists(path))
            {
                _recentProjects.Remove(path);
                RefreshRecentProjects();
            }
            SetStatus($"Open failed: {ex.Message}", error: true);
        }
    }

    /// <summary>The full planner session as a serializable project.</summary>
    private ParagonProject CaptureProject() => new()
    {
        ClassName = SelectedClass,
        Boards = _placedBoards.Select(b => new ParagonProjectBoard(
            b.Board.InternalName, b.ParentSlot, b.AttachEdge, b.RotationSteps)).ToList(),
        Targets = _targets.ToList(),
        AvoidCells = Cells.Where(c => c.Constraint == CellConstraint.Avoid).Select(c => c.Cell).ToList(),
        ExcludeCells = Cells.Where(c => c.Constraint == CellConstraint.Exclude).Select(c => c.Cell).ToList(),
        PurchasedCells = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToList(),
        Glyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new ParagonProjectGlyph(
                s.Socket.BoardSlot, s.SelectedGlyph!.InternalName, s.Level, s.RequiredStat, s.EnsureActive)
            {
                LockGlyph = s.IsGlyphLocked,
                LockBoard = s.IsBoardLocked,
            })
            .ToList(),
        NodeRules = NodeRules
            .Where(r => r.Mode != NodeRuleMode.Allow)
            .Select(r => new ParagonProjectRule(r.Group.Key, r.Mode, r.Limit))
            .ToList(),
        FocusStats = FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute).ToList(),
        References = _references
            .Select(r => new ParagonProjectReference(r.Source, r.Emphasis) { SkillText = r.SkillText })
            .ToList(),
        FocusWeights = FocusStats
            .Where(f => f.IsSelected && Math.Abs(f.Weight - 1.0) > 1e-9)
            .ToDictionary(f => f.Attribute, f => f.Weight),
        PreferRareNodes = PreferRareNodes,
        RealisticRares = RealisticRares,
        BalanceDamageBuckets = BalanceDamageBuckets,
        DefenseShare = DefenseShareOf(),
        ActivateThresholds = ActivateThresholds,
        IncludeLegendaryNodes = IncludeLegendaryNodes,
        TotalPoints = TotalPoints,
        SheetStrength = SheetStrength,
        SheetIntelligence = SheetIntelligence,
        SheetWillpower = SheetWillpower,
        SheetDexterity = SheetDexterity,
        SheetAdditiveDamage = SheetAdditiveDamage,
        SheetSituationalDamage = SheetSituationalDamage,
    };

    /// <summary>Restores a saved session; validates everything before touching the live state.</summary>
    private void ApplyProject(ParagonProject project, bool refitView = true)
    {
        if (!Classes.Contains(project.ClassName))
            throw new FormatException($"Unknown class '{project.ClassName}'.");
        _restoring = true;
        try
        {
            ApplyProjectCore(project);
        }
        finally
        {
            _restoring = false;
        }
        if (refitView)
            NotifyLayoutReplaced();
    }

    private void ApplyProjectCore(ParagonProject project)
    {
        var boardsByName = ParagonDatabase.BoardsByInternalName;
        var placed = project.Boards.Select(b => new PlacedBoard
        {
            Board = boardsByName.TryGetValue(b.BoardInternalName, out var def)
                ? def
                : throw new FormatException($"Unknown board '{b.BoardInternalName}' — removed in a data update?"),
            ParentSlot = b.ParentSlot,
            AttachEdge = b.AttachEdge,
            RotationSteps = b.RotationSteps,
        }).ToList();
        ComposedGraph.Build(new ParagonLayout(placed));

        if (SelectedClass != project.ClassName)
            SelectedClass = project.ClassName;
        _placedBoards.Clear();
        _placedBoards.AddRange(placed);
        RebuildLayout();

        if (project.TotalPoints > 0)
            TotalPoints = project.TotalPoints;
        SheetStrength = project.SheetStrength;
        SheetIntelligence = project.SheetIntelligence;
        SheetWillpower = project.SheetWillpower;
        SheetDexterity = project.SheetDexterity;
        SheetAdditiveDamage = project.SheetAdditiveDamage;
        SheetSituationalDamage = project.SheetSituationalDamage;
        PreferRareNodes = project.PreferRareNodes;
        RealisticRares = project.RealisticRares;
        BalanceDamageBuckets = project.BalanceDamageBuckets;
        SurvivabilityLevel = SurvivabilityLevelFor(project.DefenseShare);
        ActivateThresholds = project.ActivateThresholds;
        IncludeLegendaryNodes = project.IncludeLegendaryNodes;

        foreach (var glyph in project.Glyphs)
        {
            var socket = GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == glyph.BoardSlot);
            if (socket is null)
                continue;
            socket.SelectedGlyph = socket.Glyphs.FirstOrDefault(g =>
                string.Equals(g.InternalName, glyph.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
            socket.Level = glyph.Level;
            socket.RequiredStat = glyph.RequiredStat;
            socket.EnsureActive = glyph.EnsureActive && socket.SelectedGlyph is not null;
            socket.IsGlyphLocked = glyph.LockGlyph;
            socket.IsBoardLocked = glyph.LockBoard;
        }

        var ruleByKey = project.NodeRules.ToDictionary(r => r.GroupKey, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in NodeRules)
        {
            if (ruleByKey.TryGetValue(rule.Group.Key, out var saved))
            {
                rule.Mode = saved.Mode;
                rule.Limit = saved.Limit;
            }
        }

        RestoreCells(
            project.Targets,
            project.AvoidCells.Select(c => (c, CellConstraint.Avoid))
                .Concat(project.ExcludeCells.Select(c => (c, CellConstraint.Exclude))));

        var focusSet = project.FocusStats.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var focusWeights = new Dictionary<string, double>(project.FocusWeights, StringComparer.OrdinalIgnoreCase);
        foreach (var focus in FocusStats)
        {
            focus.IsSelected = focusSet.Contains(focus.Attribute);
            focus.Priority = focusWeights.TryGetValue(focus.Attribute, out double weight)
                ? FocusStatViewModel.PriorityForWeight(weight)
                : "Normal";
        }

        // Restore the saved references so the consensus can be extended, but rebuild only the
        // summary — the focus selections above are the saved state, possibly hand-tuned after
        // the references were applied, and must not be overwritten by a re-derivation.
        _references.Clear();
        _references.AddRange(project.References.Select(r => (r.Source, r.Emphasis, r.SkillText)));
        if (_references.Count > 0)
        {
            ApplyReferencePriorities(assignFocus: false);
        }
        else
        {
            ReferenceSummary = "";
            RefreshReferenceItems();
        }

        var purchased = project.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
        ClearDiffMarks();
        RefreshBuildSummary();
    }
}
