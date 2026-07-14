using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Views;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;
using Microsoft.Win32;

namespace D4LootBench.App.ViewModels;

/// <summary>A floating text label positioned on the board canvas.</summary>
public sealed record BoardLabel(string Text, double CanvasLeft, double CanvasTop);

/// <summary>A backing plate drawn behind one board, so boards read as distinct objects.</summary>
public sealed record BoardPlate(double CanvasLeft, double CanvasTop, double Size);

/// <summary>One purchased-path segment between two adjacent allocated cells (canvas coordinates).</summary>
public sealed record PathEdge(double X1, double Y1, double X2, double Y2);

/// <summary>One entry of the Recent projects menu.</summary>
public sealed record RecentProject(string FullPath)
{
    public string FileName => Path.GetFileName(FullPath);
}

/// <summary>
/// One imported build, plus the skill setup its guide variant carried when the source page
/// embeds one (Mobalytics build guides do; Maxroll planner data has no skill text).
/// </summary>
public sealed record ParagonImport(
    ConvertedMaxrollBuild Build, string Source, MobalyticsSkillVariant? Skills = null);

/// <summary>One row of the Optimize tab's reference list; Detail is the tooltip breakdown.</summary>
public sealed record ReferenceListItem(string Source, string Detail);

/// <summary>
/// An unmet rare-node threshold of the current paragon build — the item compare tool weighs
/// gear core stats against these deficits (requirements check the character TOTAL).
/// </summary>
public sealed record ParagonStatNeed(string Attribute, string StatName, double Deficit, string NodeName);

/// <summary>One row of the effective stat totals panel; Detail is the tooltip breakdown.
/// Clicking a row highlights the purchased nodes granting <see cref="Attribute"/>.</summary>
public sealed partial class StatTotalLine : ObservableObject
{
    public StatTotalLine(string name, string value, bool isCore, string attribute, string? detail = null,
        string possible = "")
    {
        Name = name;
        Value = value;
        IsCore = isCore;
        Attribute = attribute;
        Detail = detail;
        Possible = possible;
    }

    public string Name { get; }
    public string Value { get; }
    public bool IsCore { get; }
    public string Attribute { get; }
    public string? Detail { get; }

    /// <summary>"/ max" suffix: the total if every remaining node granting the stat were bought.</summary>
    public string Possible { get; }

    [ObservableProperty]
    private bool _isHighlighted;
}

/// <summary>
/// Paragon planner: build a board layout (starter plus up to four attached boards with
/// rotation), click nodes to mark targets, and Solve finds the cheapest connected path
/// from the start node through every target.
/// </summary>
public partial class ParagonPlannerViewModel : ObservableObject
{
    public const double CellSize = 26;
    private const double BoardGap = 30;
    private const double TopPadding = 24;

    /// <summary>300 from leveling plus 42 from seasonal rank rewards (Season 14).</summary>
    private const int MaxParagonPoints = 342;

    private readonly List<PlacedBoard> _placedBoards = [];
    private readonly HashSet<CellRef> _targets = [];
    private ParagonLayout? _layout;
    private ComposedGraph? _graph;

    public ParagonPlannerViewModel()
    {
        NodeRulesView = System.Windows.Data.CollectionViewSource.GetDefaultView(NodeRules);
        NodeRulesView.Filter = o =>
            string.IsNullOrWhiteSpace(RuleFilter)
            || (o is NodeRuleViewModel rule
                && rule.DisplayName.Contains(RuleFilter, StringComparison.OrdinalIgnoreCase));
        SelectedClass = Classes[0];
        RefreshRecentProjects();
    }

    /// <summary>The rules list filtered by <see cref="RuleFilter"/> (the list has ~50 rows).</summary>
    public System.ComponentModel.ICollectionView NodeRulesView { get; }

    [ObservableProperty]
    private string _ruleFilter = "";

    partial void OnRuleFilterChanged(string value) => NodeRulesView.Refresh();

    public IReadOnlyList<string> Classes { get; } =
        ["Barbarian", "Druid", "Necromancer", "Rogue", "Sorcerer", "Spiritborn", "Paladin", "Warlock"];

    public ObservableCollection<ParagonBoardDef> AttachableBoards { get; } = [];
    public ObservableCollection<int> ParentSlots { get; } = [];
    public IReadOnlyList<BoardEdge> Edges { get; } =
        [BoardEdge.Top, BoardEdge.Left, BoardEdge.Right, BoardEdge.Bottom];
    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public ObservableCollection<ParagonCellViewModel> Cells { get; } = [];
    public ObservableCollection<BoardLabel> BoardLabels { get; } = [];
    public ObservableCollection<BoardPlate> BoardPlates { get; } = [];

    /// <summary>Segments connecting adjacent purchased cells, drawn under the nodes as the path.</summary>
    public ObservableCollection<PathEdge> PathEdges { get; } = [];

    /// <summary>One row per glyph socket in the layout: glyph, level, activation goal.</summary>
    public ObservableCollection<GlyphSocketViewModel> GlyphSockets { get; } = [];

    /// <summary>One rule row per node group in the layout (avoid / exclude / limit).</summary>
    public ObservableCollection<NodeRuleViewModel> NodeRules { get; } = [];

    /// <summary>Actionable results of Analyze Placement — each can be applied, then reverted.</summary>
    public ObservableCollection<PlacementSuggestion> Suggestions { get; } = [];

    /// <summary>Stats the point maximizer can chase; none selected means the four core stats.</summary>
    public ObservableCollection<FocusStatViewModel> FocusStats { get; } = [];

    [ObservableProperty]
    private bool _preferRareNodes;

    /// <summary>Buy rares by threshold attainability (met, then realistically meetable) first.</summary>
    [ObservableProperty]
    private bool _realisticRares;

    public static IReadOnlyList<string> SurvivabilityLevels { get; } = ["None", "Light", "Balanced", "Heavy"];

    /// <summary>Share of leftover points reserved for defensive stats (see MaximizeFocus.DefenseShare).</summary>
    [ObservableProperty]
    private string _survivabilityLevel = "None";

    private double DefenseShareOf() => SurvivabilityLevel switch
    {
        "Light" => 0.15,
        "Balanced" => 0.30,
        "Heavy" => 0.50,
        _ => 0,
    };

    private static string SurvivabilityLevelFor(double share) => share switch
    {
        >= 0.40 => "Heavy",
        >= 0.22 => "Balanced",
        > 0 => "Light",
        _ => "None",
    };

    /// <summary>Spend Remaining Points first buys the stats unmet rare-node thresholds are short of.</summary>
    [ObservableProperty]
    private bool _activateThresholds;

    /// <summary>The player's total point pool; the maximizer spends what solve left over.</summary>
    [ObservableProperty]
    private int _totalPoints = MaxParagonPoints;

    /// <summary>
    /// Character core stats from everything except paragon (level, gear, item bonuses) —
    /// rare-node threshold requirements check the character TOTAL, and paragon is only part of it.
    /// </summary>
    [ObservableProperty]
    private double _sheetStrength;

    [ObservableProperty]
    private double _sheetIntelligence;

    [ObservableProperty]
    private double _sheetWillpower;

    [ObservableProperty]
    private double _sheetDexterity;

    partial void OnSheetStrengthChanged(double value) => RefreshBuildSummary();
    partial void OnSheetIntelligenceChanged(double value) => RefreshBuildSummary();
    partial void OnSheetWillpowerChanged(double value) => RefreshBuildSummary();
    partial void OnSheetDexterityChanged(double value) => RefreshBuildSummary();
    partial void OnTotalPointsChanged(int value) => RefreshBuildSummary();

    private NonParagonStats SheetStatOffsets() => NonParagonStats.PerStat(new Dictionary<string, double>
    {
        ["Strength"] = SheetStrength,
        ["Intelligence"] = SheetIntelligence,
        ["Willpower"] = SheetWillpower,
        ["Dexterity"] = SheetDexterity,
    });

    /// <summary>
    /// Per-cell stat multipliers from "+X% to [rarity] nodes in radius" glyph buffs — only
    /// glyphs whose socket is actually purchased buff anything.
    /// </summary>
    private IReadOnlyDictionary<CellRef, double> CellMultipliers(IReadOnlySet<CellRef> purchased)
    {
        if (_graph is null)
            return new Dictionary<CellRef, double>();
        var socketed = GlyphSockets
            .Where(s => s.SelectedGlyph is not null && purchased.Contains(s.Socket))
            .Select(s => new SocketedGlyph(s.Socket, s.SelectedGlyph!, s.Level))
            .ToList();
        return GlyphNodeBuffs.MultipliersFor(_graph, socketed);
    }

    /// <summary>Board canvas scale — bound to the zoom slider; Ctrl+wheel adjusts it too.</summary>
    [ObservableProperty]
    private double _zoom = 1.0;

    /// <summary>Shows the quick-start card on the canvas until the session has any real content.</summary>
    [ObservableProperty]
    private bool _showGettingStarted = true;

    private void UpdateGettingStarted() =>
        ShowGettingStarted = _placedBoards.Count <= 1 && _targets.Count == 0 && !Cells.Any(c => c.IsPurchased);

    /// <summary>Raised when an open/import/plan replaces the whole layout — the window refits the zoom.</summary>
    public event EventHandler? LayoutReplaced;

    private void NotifyLayoutReplaced() => LayoutReplaced?.Invoke(this, EventArgs.Empty);

    /// <summary>True while a solver or import operation runs — shows the canvas busy overlay.</summary>
    [ObservableProperty]
    private bool _isBusy;

    private int _busyDepth;

    /// <summary>Nesting-safe busy flag (Combine awaits Solve inside its own scope); dispose to release.</summary>
    private BusyScope BeginBusy()
    {
        _busyDepth++;
        IsBusy = true;
        return new BusyScope(this);
    }

    private sealed record BusyScope(ParagonPlannerViewModel Owner) : IDisposable
    {
        public void Dispose()
        {
            if (--Owner._busyDepth == 0)
                Owner.IsBusy = false;
        }
    }

    // ── Pinned build summary (recomputed whenever the purchase set changes) ──

    [ObservableProperty]
    private string _pointsSummary = "";

    /// <summary>Spent fraction of the point pool, clamped to 1 for the progress bar.</summary>
    [ObservableProperty]
    private double _pointsFraction;

    [ObservableProperty]
    private bool _pointsOverBudget;

    [ObservableProperty]
    private string _nodeMixSummary = "";

    /// <summary>Points per board — compare against the game's per-board totals to find where a
    /// hand-copied build diverges.</summary>
    [ObservableProperty]
    private string _perBoardSummary = "";

    /// <summary>Shown when the path enters boards in a different order than the slot numbers —
    /// the game prices threshold tiers by that entry (attachment) order.</summary>
    [ObservableProperty]
    private string _attachOrderSummary = "";

    [ObservableProperty]
    private string _glyphSummary = "";

    [ObservableProperty]
    private string _thresholdSummary = "";

    /// <summary>Effective stat totals of the purchase set, core stats (with sheet offsets) first.</summary>
    public ObservableCollection<StatTotalLine> StatTotals { get; } = [];

    /// <summary>
    /// Threshold bonuses that CANNOT be met by buying more nodes — the boards can't supply
    /// enough of the stat, so the rest must come from level/gear (Character Stats).
    /// </summary>
    public ObservableCollection<string> ThresholdWarnings { get; } = [];

    /// <summary>Unmet thresholds from the last summary refresh — read by the item compare tool.</summary>
    public IReadOnlyList<ParagonStatNeed> UnmetThresholdNeeds { get; private set; } = [];

    [ObservableProperty]
    private string _selectedClass = "Sorcerer";

    [ObservableProperty]
    private ParagonBoardDef? _selectedAttachBoard;

    [ObservableProperty]
    private int _selectedParentSlot;

    [ObservableProperty]
    private BoardEdge _selectedEdge = BoardEdge.Top;

    [ObservableProperty]
    private int _selectedRotation;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>Multi-line solve breakdown: glyph socket radius totals, imported glyph info.</summary>
    [ObservableProperty]
    private string _solveDetails = "";

    [ObservableProperty]
    private double _canvasWidth;

    [ObservableProperty]
    private double _canvasHeight;

    // Changing the class wipes the whole layout — snapshot BEFORE the property lands so the
    // undo state is consistent (old class with old boards). No-op during ApplyProject.
    partial void OnSelectedClassChanging(string value) => RecordUndo();

    partial void OnSelectedClassChanged(string value)
    {
        AttachableBoards.Clear();
        foreach (var board in ParagonDatabase.BoardsForClass(value).Where(b => b.BoardIndex != 0))
            AttachableBoards.Add(board);
        SelectedAttachBoard = AttachableBoards.FirstOrDefault();

        _placedBoards.Clear();
        _placedBoards.Add(new PlacedBoard { Board = StarterBoard });
        RebuildLayout();
    }

    private ParagonBoardDef StarterBoard =>
        ParagonDatabase.BoardsForClass(SelectedClass).Single(b => b.BoardIndex == 0);

    private string BoardDisplayName(ParagonBoardDef board) => board.Name ?? board.InternalName;

    [RelayCommand]
    private void AddBoard()
    {
        if (SelectedAttachBoard is not ParagonBoardDef board)
            return;
        if (_placedBoards.Count >= ParagonLayout.MaxBoards)
        {
            SetStatus($"A layout allows at most {ParagonLayout.MaxBoards} boards.", error: true);
            return;
        }

        int parentSlot = SelectedParentSlot;
        var edge = SelectedEdge;
        var candidate = new List<PlacedBoard>(_placedBoards)
        {
            new()
            {
                Board = board,
                ParentSlot = parentSlot,
                AttachEdge = edge,
                RotationSteps = SelectedRotation / 90,
            },
        };

        try
        {
            // Validate the whole layout (gate availability, overlap) before committing.
            ComposedGraph.Build(new ParagonLayout(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus(ex.Message, error: true);
            return;
        }

        RecordUndo();
        _placedBoards.Add(candidate[^1]);
        RebuildLayout();
        SetStatus($"Attached {BoardDisplayName(board)} ({edge} of slot {parentSlot}).");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveLastBoard))]
    private void RemoveLastBoard()
    {
        // Only the newest board is removable — later slots may attach to earlier ones.
        RecordUndo();
        _placedBoards.RemoveAt(_placedBoards.Count - 1);
        RebuildLayout();
        SetStatus("Removed the last attached board.");
    }

    private bool CanRemoveLastBoard() => _placedBoards.Count > 1;

    /// <summary>
    /// Start from the goal instead of the layout: pick boards (must-use or a pool to draw from)
    /// and glyphs, and let the optimizer place, rotate, socket, and solve.
    /// </summary>
    [RelayCommand]
    private async Task PlanLayout()
    {
        var boards = ParagonDatabase.BoardsForClass(SelectedClass)
            .Where(b => b.BoardIndex != 0)
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var glyphs = ParagonDatabase.Data.Glyphs
            .Where(g => g.Name is not null && (g.Classes.Count == 0 || g.Classes.Contains(SelectedClass)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dialog = new LayoutOptimizerWindow(boards, glyphs)
        {
            Owner = Application.Current?.Windows.OfType<ParagonPlannerWindow>().FirstOrDefault(),
        };
        if (dialog.ShowDialog() != true)
            return;
        RecordUndo();

        var request = new LayoutOptimizerRequest
        {
            StarterBoard = StarterBoard,
            MustUseBoards = dialog.MustUseBoards,
            PoolBoards = dialog.PoolBoards,
            MaxBoards = dialog.MaxBoards,
            Glyphs = dialog.SelectedGlyphs,
            GlyphLevel = dialog.GlyphLevel,
            RequiredStat = dialog.RequiredStat,
            NonParagonStats = SheetStatOffsets(),
            NodeRules = CurrentNodeRules(),
        };
        using var busy = BeginBusy();
        SetStatus("Optimizing board arrangement, rotations, and glyph placement…");
        var result = await Task.Run(() => LayoutOptimizer.Optimize(request, ParagonDatabase.Data));
        if (!result.Success)
        {
            SetStatus(result.Error ?? "Layout optimization failed.", error: true);
            return;
        }

        // The plan moves glyphs to new boards, so their user-set levels/thresholds/highlights
        // travel by glyph identity — only glyphs the user never configured get the dialog values.
        var keptGlyphSettings = new Dictionary<string, (int Level, double RequiredStat, bool HighlightRadius)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var socket in GlyphSockets)
        {
            if (socket.SelectedGlyph is not null)
                keptGlyphSettings.TryAdd(socket.SelectedGlyph.InternalName,
                    (socket.Level, socket.RequiredStat, socket.HighlightRadius));
        }

        _placedBoards.Clear();
        _placedBoards.AddRange(result.Layout!.Boards);
        RebuildLayout();

        var placementBySlot = result.GlyphPlacements.ToDictionary(p => p.BoardSlot);
        foreach (var socket in GlyphSockets)
        {
            if (placementBySlot.TryGetValue(socket.Socket.BoardSlot, out var placement))
            {
                socket.SelectedGlyph = socket.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.InternalName, placement.Glyph.InternalName, StringComparison.OrdinalIgnoreCase));
                if (keptGlyphSettings.TryGetValue(placement.Glyph.InternalName, out var kept))
                {
                    socket.Level = kept.Level;
                    socket.RequiredStat = kept.RequiredStat;
                    socket.HighlightRadius = kept.HighlightRadius;
                }
                else
                {
                    socket.Level = request.GlyphLevel;
                    socket.RequiredStat = request.RequiredStat;
                }
                socket.EnsureActive = socket.SelectedGlyph is not null;
            }
            else
                socket.SelectedGlyph = null;
        }

        var targetSet = result.Targets.ToHashSet();
        _targets.Clear();
        foreach (var cell in Cells)
        {
            cell.IsTarget = !cell.IsStart && targetSet.Contains(cell.Cell);
            if (cell.IsTarget)
                _targets.Add(cell.Cell);
        }

        var plan = result.Plan!;
        var purchased = plan.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
        ClearDiffMarks(); // Plan Layout builds from scratch — nothing meaningful to diff against

        string details = BuildSolveDetails(plan);
        SolveDetails = result.Notes.Count == 0
            ? details
            : string.Join(Environment.NewLine, result.Notes) +
              (details.Length > 0 ? Environment.NewLine + details : "");

        RefreshBuildSummary();
        string boardNames = string.Join(", ", result.Layout.Boards.Skip(1).Select(p => BoardDisplayName(p.Board)));
        int activated = plan.GlyphOutcomes.Count(o => o.Met);
        SetStatus($"Planned layout: {(boardNames.Length > 0 ? boardNames : "starter only")} — " +
                  $"{plan.PointsSpent} points reaching every legendary node, " +
                  $"{activated} of {result.GlyphPlacements.Count} glyph(s) activated.",
            error: plan.Notes.Count > 0);
        NotifyLayoutReplaced();
    }

    private void RebuildLayout()
    {
        _targets.Clear();
        Cells.Clear();
        BoardLabels.Clear();
        BoardPlates.Clear();

        _layout = new ParagonLayout(_placedBoards.ToList());
        _graph = ComposedGraph.Build(_layout);

        int width = StarterBoard.Width;
        double boardSpan = width * CellSize;
        int minX = _layout.BoardPositions.Min(p => p.X);
        int minY = _layout.BoardPositions.Min(p => p.Y);

        var origins = new (double Left, double Top)[_placedBoards.Count];
        _labelPositions = new (double Left, double Top)[_placedBoards.Count];
        for (int slot = 0; slot < _placedBoards.Count; slot++)
        {
            var (bx, by) = _layout.BoardPositions[slot];
            origins[slot] = ((bx - minX) * (boardSpan + BoardGap),
                             TopPadding + (by - minY) * (boardSpan + BoardGap));
            _labelPositions[slot] = (origins[slot].Left, origins[slot].Top - TopPadding + 4);
            BoardPlates.Add(new BoardPlate(origins[slot].Left - 4, origins[slot].Top - 4, boardSpan + 8));
        }

        foreach (var vertex in _graph.Vertices)
        {
            var origin = origins[vertex.Cell.BoardSlot];
            Cells.Add(new ParagonCellViewModel(
                vertex.Cell,
                vertex.Node,
                origin.Left + vertex.Cell.X * CellSize,
                origin.Top + vertex.Cell.Y * CellSize));
        }

        CanvasWidth = (_layout.BoardPositions.Max(p => p.X) - minX + 1) * (boardSpan + BoardGap) - BoardGap;
        CanvasHeight = TopPadding + (_layout.BoardPositions.Max(p => p.Y) - minY + 1) * (boardSpan + BoardGap) - BoardGap;

        ParentSlots.Clear();
        for (int slot = 0; slot < _placedBoards.Count; slot++)
            ParentSlots.Add(slot);
        SelectedParentSlot = _placedBoards.Count - 1;

        RemoveLastBoardCommand.NotifyCanExecuteChanged();

        var classGlyphs = ParagonDatabase.Data.Glyphs
            .Where(g => g.Name is not null && (g.Classes.Count == 0 || g.Classes.Contains(SelectedClass)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Keep glyph picks for board slots that survive the layout change (e.g. a rotation).
        var previousGlyphs = GlyphSockets.ToDictionary(
            s => s.Socket.BoardSlot,
            s => (Glyph: s.SelectedGlyph?.InternalName, s.Level, s.RequiredStat, s.EnsureActive, s.HighlightRadius));
        GlyphSockets.Clear();
        foreach (var vertex in _graph.Vertices
                     .Where(v => v.Node.Kind == ParagonNodeKind.GlyphSocket)
                     .OrderBy(v => v.Cell.BoardSlot))
        {
            var socket = new GlyphSocketViewModel(
                vertex.Cell,
                BoardDisplayName(_placedBoards[vertex.Cell.BoardSlot].Board),
                classGlyphs);
            if (previousGlyphs.TryGetValue(vertex.Cell.BoardSlot, out var previous))
            {
                socket.SelectedGlyph = previous.Glyph is null
                    ? null
                    : classGlyphs.FirstOrDefault(g =>
                        string.Equals(g.InternalName, previous.Glyph, StringComparison.OrdinalIgnoreCase));
                socket.Level = previous.Level;
                socket.RequiredStat = previous.RequiredStat;
                socket.EnsureActive = previous.EnsureActive && socket.SelectedGlyph is not null;
                socket.HighlightRadius = previous.HighlightRadius;
            }
            socket.PropertyChanged += OnGlyphSocketChanged;
            GlyphSockets.Add(socket);
        }

        // Keep rule settings for groups that survive the layout change (e.g. adding a board).
        // "Any:" stat groups follow the kind groups: one row per stat that several groups grant.
        var previousRules = NodeRules.ToDictionary(r => r.Group.Key, r => (r.Mode, r.Limit));
        NodeRules.Clear();
        foreach (var group in NodeGrouping.GroupsIn(_graph).Concat(NodeGrouping.StatGroupsIn(_graph)))
        {
            var rule = new NodeRuleViewModel(group);
            if (previousRules.TryGetValue(group.Key, out var previous))
            {
                rule.Mode = previous.Mode;
                rule.Limit = previous.Limit;
            }
            NodeRules.Add(rule);
        }

        // Focusable stats for the point maximizer (selection and priority survive layout changes).
        var previousFocus = FocusStats.ToDictionary(
            f => f.Attribute, f => (f.IsSelected, f.Priority), StringComparer.OrdinalIgnoreCase);
        var focusable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in _graph.Vertices)
        {
            foreach (var attribute in vertex.Node.Attributes)
            {
                if (!attribute.IsThresholdBonus && attribute.Value is not null)
                    focusable.TryAdd(attribute.Attribute, ParagonDisplay.FormatAttributeName(attribute.Attribute));
            }
        }
        FocusStats.Clear();
        foreach (var (attribute, display) in focusable
                     .OrderBy(kv => MaximizeFocus.CoreStats.Contains(kv.Key) ? 0 : 1)
                     .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var previous = previousFocus.GetValueOrDefault(attribute, (IsSelected: false, Priority: "Normal"));
            FocusStats.Add(new FocusStatViewModel(attribute, display)
            {
                IsSelected = previous.IsSelected,
                Priority = previous.Priority,
            });
        }

        Suggestions.Clear();
        _revertState = null;
        RevertPlacementCommand.NotifyCanExecuteChanged();

        SolveDetails = "";
        UpdateRadiusHighlights();
        UpdateGlyphLabels();
        RefreshBuildSummary();
        SetStatus("Click nodes to mark targets (right-click to avoid/exclude), then Solve.");
    }

    private void OnGlyphSocketChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GlyphSocketViewModel.HighlightRadius)
            or nameof(GlyphSocketViewModel.Level)
            or nameof(GlyphSocketViewModel.SelectedGlyph))
            UpdateRadiusHighlights();
        if (e.PropertyName is nameof(GlyphSocketViewModel.SelectedGlyph)
            or nameof(GlyphSocketViewModel.Level))
            UpdateGlyphLabels();
        if (e.PropertyName is nameof(GlyphSocketViewModel.SelectedGlyph)
            or nameof(GlyphSocketViewModel.Level)
            or nameof(GlyphSocketViewModel.RequiredStat))
            RefreshBuildSummary();
    }

    /// <summary>Marks the cells inside every highlight-enabled socket's Manhattan diamond.</summary>
    private void UpdateRadiusHighlights()
    {
        var highlighted = GlyphSockets.Where(s => s.HighlightRadius).ToList();
        foreach (var cell in Cells)
        {
            cell.IsInGlyphRadius = highlighted.Any(s =>
                s.Socket.BoardSlot == cell.Cell.BoardSlot
                && Math.Abs(cell.Cell.X - s.Socket.X) + Math.Abs(cell.Cell.Y - s.Socket.Y) <= s.Radius);
        }
    }

    // ── Import: Maxroll codes and URLs, Mobalytics pages ─────────────────

    [RelayCommand]
    private async Task ImportBuild()
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
        string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Copy a Maxroll variant code, a maxroll.gg or mobalytics.gg URL, " +
                      "or a build page's HTML to the clipboard first.", error: true);
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
                SetStatus("The clipboard URL is neither a maxroll.gg nor a mobalytics.gg page.", error: true);
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

    private const string ManualHtmlHint =
        "Open the build in a browser, view the page source (Ctrl+U), copy it all, " +
        "then click Import Mobalytics again with the HTML in the clipboard.";

    private static Task<string> FetchPageAsync(string url) => Services.GuidePageFetcher.FetchPageAsync(url);

    private static IReadOnlyList<MobalyticsParagonVariant>? PickVariants(
        IReadOnlyList<MobalyticsParagonVariant> variants, bool allowMultiple)
    {
        var dialog = new MobalyticsVariantPickerWindow(variants, allowMultiple)
        {
            Owner = Application.Current?.Windows.OfType<ParagonPlannerWindow>().FirstOrDefault(),
        };
        return dialog.ShowDialog() == true ? dialog.SelectedVariants : null;
    }

    private static IReadOnlyList<int> PickIndices(IReadOnlyList<string> labels, bool allowMultiple)
    {
        var dialog = new MobalyticsVariantPickerWindow(labels, allowMultiple)
        {
            Owner = Application.Current?.Windows.OfType<ParagonPlannerWindow>().FirstOrDefault(),
        };
        return dialog.ShowDialog() == true ? dialog.SelectedIndices : [];
    }

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
    private async Task CompareImport()
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
        using var busy = BeginBusy();
        SetStatus("Comparing…");
        var sheetStats = SheetStatOffsets();
        // Point parity keeps the verdict about build quality, not budget: the smaller build is
        // grown by its own priorities (or the larger trimmed) until both spend the same.
        string report = await Task.Run(() =>
            BuildComparer.Compare(current, imported, ParagonDatabase.Data, sheetStats, matchPoints: true));
        SolveDetails = report;
        SetStatus($"Compared the current build against {import.Source} at matched points — see the report below.");
    }

    [RelayCommand]
    private async Task CombineImport()
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

    private void ApplyImportedBuild(ConvertedMaxrollBuild build, string source)
    {
        RecordUndo();
        string? className = build.Layout.Boards[0].Board.ClassName;
        if (className is not null && className != SelectedClass)
            SelectedClass = className;

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

        var allocated = build.AllocatedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = allocated.Contains(cell.Cell);
        ClearDiffMarks(); // a wholesale replacement — nothing meaningful to diff against
        RefreshBuildSummary();

        var assigned = GlyphSockets.Where(s => s.SelectedGlyph is not null).ToList();
        SolveDetails = assigned.Count == 0
            ? ""
            : "Imported glyphs: " + string.Join(", ", assigned.Select(DescribeGlyph));
        SetStatus($"Imported {source} build: {_placedBoards.Count} board(s), {allocated.Count} allocated node(s).");
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
        Clipboard.SetText(code);
        SetStatus($"Maxroll variant code copied to the clipboard ({allocated.Count} node(s)). " +
                  "Paste it into the Maxroll planner's Import Variant box.");
    }

    // ── Save / open the planner session as a project file ────────────────

    private const string ProjectDialogFilter = "Paragon Project|*.paragon.json|All Files|*.*";

    private readonly Services.RecentProjectsService _recentProjects = new();

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
        var dlg = new SaveFileDialog
        {
            Title      = "Save Paragon Project",
            Filter     = ProjectDialogFilter,
            DefaultExt = ".paragon.json",
            FileName   = _currentProjectPath is null
                ? $"{SelectedClass.ToLowerInvariant()}-paragon"
                : Path.GetFileName(_currentProjectPath),
        };
        if (dlg.ShowDialog() != true)
            return;
        WriteProject(dlg.FileName);
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
        var dlg = new OpenFileDialog
        {
            Title      = "Open Paragon Project",
            Filter     = ProjectDialogFilter,
            DefaultExt = ".paragon.json",
        };
        if (dlg.ShowDialog() != true)
            return;
        OpenProjectFile(dlg.FileName);
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

    /// <summary>Call BEFORE mutating; consecutive identical states collapse, so a recorded
    /// action that then fails or no-ops never produces a dead undo step.</summary>
    private void RecordUndo()
    {
        if (_restoring || _layout is null)
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
            SetCurrentProject(path);
            RememberRecentProject(path);
            SetStatus($"Opened project \"{Path.GetFileName(path)}\": {_placedBoards.Count} board(s), " +
                      $"{Cells.Count(c => c.IsPurchased)} allocated node(s).");
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
                s.Socket.BoardSlot, s.SelectedGlyph!.InternalName, s.Level, s.RequiredStat, s.EnsureActive))
            .ToList(),
        NodeRules = NodeRules
            .Where(r => r.Mode != NodeRuleMode.Allow)
            .Select(r => new ParagonProjectRule(r.Group.Key, r.Mode, r.Limit))
            .ToList(),
        FocusStats = FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute).ToList(),
        References = _references.Select(r => new ParagonProjectReference(r.Source, r.Emphasis)).ToList(),
        FocusWeights = FocusStats
            .Where(f => f.IsSelected && Math.Abs(f.Weight - 1.0) > 1e-9)
            .ToDictionary(f => f.Attribute, f => f.Weight),
        PreferRareNodes = PreferRareNodes,
        RealisticRares = RealisticRares,
        DefenseShare = DefenseShareOf(),
        ActivateThresholds = ActivateThresholds,
        TotalPoints = TotalPoints,
        SheetStrength = SheetStrength,
        SheetIntelligence = SheetIntelligence,
        SheetWillpower = SheetWillpower,
        SheetDexterity = SheetDexterity,
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
        PreferRareNodes = project.PreferRareNodes;
        RealisticRares = project.RealisticRares;
        SurvivabilityLevel = SurvivabilityLevelFor(project.DefenseShare);
        ActivateThresholds = project.ActivateThresholds;

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
        _references.AddRange(project.References.Select(r => (r.Source, r.Emphasis)));
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

    /// <summary>Per purchased glyph socket: in-radius stat totals plus activation status, then solver notes.</summary>
    private string BuildSolveDetails(PlanResult result) =>
        BuildPurchaseReport(result.PurchasedCells.ToHashSet(), result.GlyphOutcomes, result.Notes);

    private string BuildPurchaseReport(
        HashSet<CellRef> purchased, IReadOnlyList<GlyphGoalOutcome> outcomes, IReadOnlyCollection<string> notes)
    {
        if (_graph is null)
            return "";

        var lines = new List<string>();
        foreach (var socket in GlyphSockets)
        {
            if (!purchased.Contains(socket.Socket))
                continue;

            int radius = socket.SelectedGlyph is null ? GlyphRadius.RadiusForLevel(50) : socket.Radius;
            var totals = GlyphRadius.AttributeTotalsInRange(
                _graph, socket.Socket, purchased, radius, GlyphRadius.GameMetric);
            string stats = string.Join(", ", totals
                .Where(kv => kv.Key.EndsWith("_Core", StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value:0} {ParagonDisplay.FormatAttributeName(kv.Key)}"));

            string glyph = socket.SelectedGlyph is null
                ? "no glyph assigned, assumes level 50+"
                : $"{socket.SelectedGlyph.Name} lvl {socket.Level}";
            var outcome = outcomes.FirstOrDefault(o => o.Goal.Socket == socket.Socket);
            string activation = "";
            if (outcome is not null)
            {
                activation = $" — {(outcome.Met ? "activated" : "NOT activated")} " +
                             $"({outcome.AchievedTotal:0}/{outcome.Goal.RequiredTotal:0} {ParagonDisplay.FormatAttributeName(outcome.Goal.SourceAttribute)})";
            }
            else if (socket.SourceAttribute is string sourceAttribute)
            {
                double have = totals.GetValueOrDefault(sourceAttribute);
                activation = $" — {(have >= socket.RequiredStat ? "activated" : "NOT activated")} " +
                             $"({have:0}/{socket.RequiredStat:0} {ParagonDisplay.FormatAttributeName(sourceAttribute)})";
            }

            // What the glyph turns the radius into. Scores are relative (game units aren't in the data).
            string delivery = "";
            if (socket.SelectedGlyph is ParagonGlyphDef def
                && GlyphInfo.BonusScalarAt(def, socket.Level) is double scalar)
            {
                delivery = GlyphInfo.IsAttributeMapped(def) && socket.SourceAttribute is string source
                    ? $", delivers {GlyphInfo.DeliveryTarget(def)} from {totals.GetValueOrDefault(source):0} " +
                      $"{ParagonDisplay.FormatAttributeName(source)} (benefit score {GlyphInfo.DeliveredBonus(def, socket.Level, totals.GetValueOrDefault(source)):0.#})"
                    : $", boosts {GlyphInfo.DeliveryTarget(def)} (scalar {scalar:0.#} at lvl {socket.Level})";
            }
            if (socket.SelectedGlyph is not null)
            {
                delivery += string.Join("", GlyphNodeBuffs.BuffsAt(socket.SelectedGlyph, socket.Level)
                    .Select(b => $", +{b.Percent:0.#}% to {b.Kind} nodes in radius (counted in the stat totals)"));
            }

            lines.Add($"Socket on {socket.BoardName} ({glyph}): " +
                      $"{(stats.Length > 0 ? stats : "no stats")} in radius {radius}{activation}{delivery}");
        }

        // A Limit rule caps its GROUP, not the stat: rare nodes granting the same attribute are
        // separate per-name groups, which reads as "the limit broke" — say so explicitly.
        var cellsByGroup = NodeGrouping.CellsByGroup(_graph);
        foreach (var rule in NodeRules.Where(r => r.Mode is NodeRuleMode.Limit or NodeRuleMode.Minimal))
        {
            // Magic/Normal group keys are "<Kind>:<Attribute>:<Param>".
            var parts = rule.Group.Key.Split(':');
            if (parts.Length < 2 || parts[0] is not ("Magic" or "Normal"))
                continue;
            string attribute = parts[1];
            var groupSet = (cellsByGroup.GetValueOrDefault(rule.Group.Key) ?? []).ToHashSet();
            var outside = _graph.Vertices
                .Where(v => purchased.Contains(v.Cell) && !groupSet.Contains(v.Cell)
                    && v.Node.Attributes.Any(a => a.Value is not null
                        && string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase)))
                .Select(v => v.Node.Name ?? v.Node.InternalName)
                .Distinct()
                .ToList();
            if (outside.Count > 0)
            {
                string statName = ParagonDisplay.FormatAttributeName(attribute);
                lines.Add($"Limit note: '{rule.Group.DisplayName}' held at {groupSet.Count(purchased.Contains)} " +
                          $"of {rule.Limit}, but {outside.Count} other purchased node(s) also grant " +
                          $"{statName}: {string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? ", …" : "")} — " +
                          $"to cap the stat across every node kind, limit the 'Any: {statName}' row instead.");
            }
        }

        // Rare-node threshold bonuses: requirements scale with the board's attachment slot.
        var report = BuildStats.Compute(_graph, purchased, ParagonDatabase.Data, SheetStatOffsets(), SelectedClass,
            CellMultipliers(purchased));
        if (report.Thresholds.Count > 0)
        {
            lines.Add($"Threshold bonuses: {report.ThresholdsMet} of {report.Thresholds.Count} active " +
                      "(counting the character sheet stats from level/gear).");
            foreach (var status in report.Thresholds.Where(t => !t.Met).Take(6))
            {
                lines.Add($"  Not active: {status.NodeName} (slot {status.Cell.BoardSlot}) needs " +
                          $"{status.Requirement:0} {ParagonDisplay.FormatAttributeName(status.Attribute)} — have {status.Have:0}.");
            }
        }

        lines.AddRange(notes);
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeGlyph(GlyphSocketViewModel socket) =>
        $"{socket.SelectedGlyph!.Name} (lvl {socket.Level}, slot {socket.Socket.BoardSlot})";

    [RelayCommand]
    private void ToggleTarget(ParagonCellViewModel cell)
    {
        if (cell.IsStart)
            return;

        RecordUndo();
        cell.IsTarget = !cell.IsTarget;
        if (cell.IsTarget)
            _targets.Add(cell.Cell);
        else
            _targets.Remove(cell.Cell);

        ClearSolution();
        UpdateGettingStarted();
        SetStatus($"{_targets.Count} target(s) selected.");
    }

    /// <summary>The active (non-Allow) node rules; group keys survive layout changes.</summary>
    private List<NodeRule> CurrentNodeRules() => NodeRules
        .Where(r => r.Mode != NodeRuleMode.Allow)
        .Select(r => new NodeRule(r.Group.Key, r.Mode, r.Limit))
        .ToList();

    /// <summary>Targets, rules, per-cell overrides and glyph goals as one immutable solver request.</summary>
    private PlanRequest BuildPlanRequest() => new()
    {
        Targets = _targets.ToList(),
        NodeRules = CurrentNodeRules(),
        AvoidCells = Cells.Where(c => c.Constraint == CellConstraint.Avoid).Select(c => c.Cell).ToList(),
        ExcludeCells = Cells.Where(c => c.Constraint == CellConstraint.Exclude).Select(c => c.Cell).ToList(),
        GlyphGoals = GlyphSockets
            .Where(s => s.EnsureActive && s.SourceAttribute is not null)
            .Select(s => new GlyphGoal(s.Socket, s.SourceAttribute!, s.RequiredStat, s.Radius, s.SelectedGlyph?.Name))
            .ToList(),
    };

    [RelayCommand]
    private async Task Solve()
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
    private async Task Reanalyze()
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
        SetStatus($"{result.PointsSpent} paragon points for {_targets.Count} target(s) ({quality}) — {perBoard}.{budget}{warning}",
            error: budget.Length > 0 || result.Notes.Count > 0);
        return true;
    }

    [RelayCommand]
    private void CycleCellConstraint(ParagonCellViewModel cell)
    {
        if (cell.IsStart)
            return;
        SetCellConstraint(cell, cell.Constraint switch
        {
            CellConstraint.None => CellConstraint.Avoid,
            CellConstraint.Avoid => CellConstraint.Exclude,
            _ => CellConstraint.None,
        });
    }

    /// <summary>Marks a node avoid / off-limits (exclude) / clear — the right-click menu's verbs.</summary>
    public void SetCellConstraint(ParagonCellViewModel cell, CellConstraint constraint)
    {
        if (cell.IsStart)
            return;

        RecordUndo();
        cell.Constraint = constraint;
        if (cell.Constraint == CellConstraint.Exclude && cell.IsTarget)
        {
            cell.IsTarget = false;
            _targets.Remove(cell.Cell);
        }

        ClearSolution();
        SetStatus(cell.Constraint switch
        {
            CellConstraint.Avoid => "Node marked avoid — taken only when it saves several plain nodes.",
            CellConstraint.Exclude => "Node marked off-limits — no path or purchase will ever touch it.",
            _ => "Node constraint cleared.",
        });
    }

    /// <summary>
    /// Re-solves the current plan under every alternate rotation of each attached board, and
    /// checks whether the socketed glyphs would see more of their stat elsewhere.
    /// </summary>
    [RelayCommand]
    private async Task AnalyzePlacement()
    {
        if (_graph is null || _layout is null)
            return;
        var request = BuildPlanRequest();
        if (request.Targets.Count == 0 && request.GlyphGoals.Count == 0)
        {
            SetStatus("Mark targets (or enable a glyph activation goal) before analyzing placement.", error: true);
            return;
        }

        var layout = _layout;
        var graph = _graph;
        // Swap candidates: every board of the class not already in the layout — a superset of any
        // pool the user planned from, so good alternates surface even after the dialog is gone.
        var placedNames = _placedBoards.Select(b => b.Board.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var spareBoards = ParagonDatabase.BoardsForClass(SelectedClass)
            .Where(b => b.BoardIndex != 0 && !placedNames.Contains(b.InternalName))
            .ToList();
        // Candidates are judged by the FINISHED build: solve + full spend of the point pool
        // with the current Optimize settings, compared on glyphs → thresholds → focus value.
        // Socketed glyphs ride along by board slot so node buffs count per candidate.
        var socketedGlyphs = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .Select(s => new PipelineGlyph(s.Socket.BoardSlot, s.SelectedGlyph!, s.Level))
            .ToList();
        var pipeline = new PlacementPipeline(
            TotalPoints,
            CurrentMaximizeFocus(),
            new ThresholdContext(ParagonDatabase.Data, SelectedClass, SheetStatOffsets()),
            socketedGlyphs);
        using var busy = BeginBusy();
        SetStatus("Analyzing rotations, re-attachments, glyph placements, and board swaps at full point spend…");
        var (baseline, suggestions) = await Task.Run(() =>
        {
            var solved = PlanSolver.Solve(graph, request);
            if (!solved.Success)
                return (solved, (IReadOnlyList<PlacementSuggestion>)Array.Empty<PlacementSuggestion>());
            var found = PlacementAnalyzer.SuggestGlyphAssignment(graph, request, solved, pipeline)
                .Concat(PlacementAnalyzer.SuggestRotations(layout, request, solved, pipeline).Take(3))
                .Concat(PlacementAnalyzer.SuggestGlyphPlacements(graph, layout, solved.PurchasedCells, request.GlyphGoals))
                .Concat(PlacementAnalyzer.SuggestBoardSwaps(layout, request, solved, spareBoards, pipeline: pipeline))
                .Concat(PlacementAnalyzer.SuggestReattachments(layout, request, solved, pipeline))
                .ToList();
            return (solved, (IReadOnlyList<PlacementSuggestion>)found);
        });

        if (!baseline.Success)
        {
            SetStatus(baseline.Error ?? "Solve failed.", error: true);
            return;
        }

        Suggestions.Clear();
        foreach (var suggestion in suggestions)
            Suggestions.Add(suggestion);

        SolveDetails = suggestions.Count == 0
            ? $"Placement analysis: no better board rotation or glyph socket found (baseline {baseline.PointsSpent} points)."
            : "Placement suggestions are listed in the panel — Apply one to see the change on the board, " +
              "then Revert to flip back and compare.";
        SetStatus($"Placement analysis complete — {suggestions.Count} suggestion(s).");
    }

    // ── Applying and reverting placement suggestions ─────────────────────

    private PlannerState? _revertState;

    private sealed record PlannerState(
        List<PlacedBoard> Boards,
        List<CellRef> Targets,
        List<(CellRef Cell, CellConstraint Constraint)> Constraints,
        List<(int Slot, string? Glyph, int Level, double RequiredStat, bool EnsureActive)> Glyphs);

    private PlannerState CaptureState() => new(
        [.. _placedBoards],
        [.. _targets],
        Cells.Where(c => c.Constraint != CellConstraint.None).Select(c => (c.Cell, c.Constraint)).ToList(),
        GlyphSockets.Select(s =>
            (s.Socket.BoardSlot, s.SelectedGlyph?.InternalName, s.Level, s.RequiredStat, s.EnsureActive)).ToList());

    private void RestoreCells(
        IEnumerable<CellRef> targets, IEnumerable<(CellRef Cell, CellConstraint Constraint)> constraints)
    {
        var cellByRef = Cells.ToDictionary(c => c.Cell);
        _targets.Clear();
        foreach (var target in targets)
        {
            if (cellByRef.TryGetValue(target, out var cell) && !cell.IsStart)
            {
                cell.IsTarget = true;
                _targets.Add(target);
            }
        }
        foreach (var (cellRef, constraint) in constraints)
        {
            if (cellByRef.TryGetValue(cellRef, out var cell))
                cell.Constraint = constraint;
        }
    }

    private void RestoreGlyphs(
        IEnumerable<(int Slot, string? Glyph, int Level, double RequiredStat, bool EnsureActive)> glyphs)
    {
        foreach (var saved in glyphs)
        {
            var socket = GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == saved.Slot);
            if (socket is null)
                continue;
            socket.SelectedGlyph = saved.Glyph is null
                ? null
                : socket.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.InternalName, saved.Glyph, StringComparison.OrdinalIgnoreCase));
            socket.Level = saved.Level;
            socket.RequiredStat = saved.RequiredStat;
            socket.EnsureActive = saved.EnsureActive && socket.SelectedGlyph is not null;
        }
    }

    /// <summary>Applies a suggestion to the live board and re-solves, so the difference is visible.</summary>
    [RelayCommand]
    private async Task ApplySuggestion(PlacementSuggestion suggestion)
    {
        if (_layout is null || suggestion.Change is null)
            return;
        RecordUndo();
        var before = CaptureState();
        var purchasesBefore = CurrentPurchases();
        int pointsBefore = purchasesBefore.Count;

        ApplyChange(suggestion.Change, before);

        _revertState = before;
        RevertPlacementCommand.NotifyCanExecuteChanged();
        await SolveAsync();
        MarkPurchaseDiff(purchasesBefore);
        int pointsAfter = Cells.Count(c => c.IsPurchased);
        SetStatus($"Applied — path re-solved at {pointsAfter} points (was {pointsBefore}). " +
                  "Revert flips back to compare.", error: StatusIsError);
    }

    private void ApplyChange(PlacementChange change, PlannerState before)
    {
        switch (change)
        {
            case CompositeChange composite:
            {
                foreach (var child in composite.Changes)
                    ApplyChange(child, before);
                break;
            }
            case ReattachChange reattach:
            {
                var placed = _placedBoards[reattach.Slot];
                // The board keeps its cells; only the rotation delta moves them.
                int delta = (reattach.RotationSteps - placed.RotationSteps + 4) & 3;
                int width = placed.Board.Width;
                CellRef Remap(CellRef cell)
                {
                    if (cell.BoardSlot != reattach.Slot)
                        return cell;
                    var (x, y) = ParagonLayout.Rotate(cell.X, cell.Y, delta, width);
                    return cell with { X = x, Y = y };
                }
                var targets = _targets.Select(Remap).ToList();
                var constraints = before.Constraints.Select(c => (Remap(c.Cell), c.Constraint)).ToList();
                _placedBoards[reattach.Slot] = new PlacedBoard
                {
                    Board = placed.Board,
                    ParentSlot = reattach.ParentSlot,
                    AttachEdge = reattach.Edge,
                    RotationSteps = reattach.RotationSteps,
                };
                RebuildLayout(); // keeps glyph picks per board slot
                RestoreCells(targets, constraints);
                break;
            }
            case GlyphSlotReassignment slotMoves:
            {
                var socketBySlot = GlyphSockets.ToDictionary(s => s.Socket.BoardSlot);
                var picks = slotMoves.Moves
                    .Where(m => socketBySlot.ContainsKey(m.FromSlot) && socketBySlot.ContainsKey(m.ToSlot))
                    .Select(m => (Target: socketBySlot[m.ToSlot],
                                  socketBySlot[m.FromSlot].SelectedGlyph,
                                  socketBySlot[m.FromSlot].Level,
                                  socketBySlot[m.FromSlot].RequiredStat,
                                  socketBySlot[m.FromSlot].EnsureActive))
                    .ToList();
                foreach (var move in slotMoves.Moves)
                {
                    if (socketBySlot.TryGetValue(move.FromSlot, out var from))
                    {
                        from.SelectedGlyph = null;
                        from.EnsureActive = false;
                    }
                }
                foreach (var (target, glyph, level, requiredStat, ensureActive) in picks)
                {
                    target.SelectedGlyph = glyph;
                    target.Level = level;
                    target.RequiredStat = requiredStat;
                    target.EnsureActive = ensureActive && glyph is not null;
                }
                break;
            }
            case RotationChange rotation:
            {
                var placed = _placedBoards[rotation.Slot];
                int delta = (rotation.RotationSteps - placed.RotationSteps + 4) & 3;
                int width = placed.Board.Width;
                CellRef Remap(CellRef cell)
                {
                    if (cell.BoardSlot != rotation.Slot)
                        return cell;
                    var (x, y) = ParagonLayout.Rotate(cell.X, cell.Y, delta, width);
                    return cell with { X = x, Y = y };
                }
                var targets = _targets.Select(Remap).ToList();
                var constraints = before.Constraints.Select(c => (Remap(c.Cell), c.Constraint)).ToList();
                _placedBoards[rotation.Slot] = new PlacedBoard
                {
                    Board = placed.Board,
                    ParentSlot = placed.ParentSlot,
                    AttachEdge = placed.AttachEdge,
                    RotationSteps = rotation.RotationSteps,
                };
                RebuildLayout(); // keeps glyph picks per board slot
                RestoreCells(targets, constraints);
                break;
            }
            case BoardSwapChange swap:
            {
                var placed = _placedBoards[swap.Slot];
                // Targets and per-cell constraints on the outgoing board don't exist on the new
                // one; the new board's legendary node(s) become the slot's targets instead.
                var keepTargets = _targets.Where(t => t.BoardSlot != swap.Slot).ToList();
                var constraints = before.Constraints.Where(c => c.Cell.BoardSlot != swap.Slot).ToList();
                _placedBoards[swap.Slot] = new PlacedBoard
                {
                    Board = swap.NewBoard,
                    ParentSlot = placed.ParentSlot,
                    AttachEdge = placed.AttachEdge,
                    RotationSteps = swap.RotationSteps,
                };
                RebuildLayout(); // keeps the slot's glyph pick, so the glyph lands on the new board
                var newTargets = Cells
                    .Where(c => c.Cell.BoardSlot == swap.Slot && c.Node.Kind == ParagonNodeKind.Legendary)
                    .Select(c => c.Cell);
                RestoreCells(keepTargets.Concat(newTargets), constraints);
                break;
            }
            case GlyphReassignment reassignment:
            {
                var socketByCell = GlyphSockets.ToDictionary(s => s.Socket);
                var picks = reassignment.Moves
                    .Where(m => socketByCell.ContainsKey(m.FromSocket) && socketByCell.ContainsKey(m.ToSocket))
                    .Select(m => (Target: socketByCell[m.ToSocket],
                                  socketByCell[m.FromSocket].SelectedGlyph,
                                  socketByCell[m.FromSocket].Level,
                                  socketByCell[m.FromSocket].RequiredStat))
                    .ToList();
                foreach (var move in reassignment.Moves)
                {
                    if (socketByCell.TryGetValue(move.FromSocket, out var from))
                        from.SelectedGlyph = null;
                }
                foreach (var (target, glyph, level, requiredStat) in picks)
                {
                    target.SelectedGlyph = glyph;
                    target.Level = level;
                    target.RequiredStat = requiredStat;
                }
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertPlacement))]
    private async Task RevertPlacement()
    {
        if (_revertState is not PlannerState state)
            return;
        RecordUndo();
        var purchasesBefore = CurrentPurchases();
        _placedBoards.Clear();
        _placedBoards.AddRange(state.Boards);
        RebuildLayout(); // clears _revertState — reverting is one-shot
        RestoreCells(state.Targets, state.Constraints);
        RestoreGlyphs(state.Glyphs);
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
            await SolveAsync();
        MarkPurchaseDiff(purchasesBefore);
        SetStatus("Reverted to the layout before the applied suggestion.");
    }

    private bool CanRevertPlacement() => _revertState is not null;

    // ── Spending leftover points ──────────────────────────────────────────

    /// <summary>
    /// Spends whatever the current path leaves of <see cref="TotalPoints"/> on the focused stats
    /// (and rare nodes first, when preferred), growing the purchased tree greedily.
    /// </summary>
    [RelayCommand]
    private async Task MaximizePoints()
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

    /// <summary>What the reference-derived priorities came from, shown in the Optimize tab.</summary>
    [ObservableProperty]
    private string _referenceSummary = "";

    /// <summary>References the priorities derive from; saved with the project and restored on load.</summary>
    private readonly List<(string Source, IReadOnlyList<ReferenceEmphasis> Emphasis)> _references = [];

    /// <summary>The reference list rendered in the Optimize tab, one removable row per reference.</summary>
    public ObservableCollection<ReferenceListItem> ReferenceItems { get; } = [];

    private void RefreshReferenceItems()
    {
        ReferenceItems.Clear();
        foreach (var (source, emphasis) in _references)
        {
            string detail = "Votes for: " + string.Join(", ", emphasis
                .OrderByDescending(e => e.Score)
                .Take(6)
                .Select(e => ParagonDisplay.FormatAttributeName(e.Attribute)));
            ReferenceItems.Add(new ReferenceListItem(source, detail));
        }
    }

    /// <summary>Drops one reference and re-derives the consensus from what remains.</summary>
    [RelayCommand]
    private void RemoveReference(ReferenceListItem item)
    {
        int index = _references.FindIndex(r => r.Source == item.Source);
        if (index < 0)
            return;
        _references.RemoveAt(index);
        if (_references.Count == 0)
        {
            ReferenceSummary = "";
            RefreshReferenceItems();
            SetStatus("Reference removed — focus stats keep their current selection.");
            return;
        }
        ApplyReferencePriorities();
    }

    /// <summary>Sources compare without their trailing count suffix, so "Loaded filter (19 affix(es))"
    /// updates "Loaded filter (12 affix(es))" and a re-imported variant refreshes its old emphasis.</summary>
    private void AddOrReplaceReference(string source, IReadOnlyList<ReferenceEmphasis> emphasis)
    {
        static string KeyOf(string s) => System.Text.RegularExpressions.Regex
            .Replace(s, @"\s*\([^()]*\d+ (?:nodes|skills|affix\(es\))[^()]*\)$", "").Trim();
        string key = KeyOf(source);
        int existing = _references.FindIndex(r => KeyOf(r.Source).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _references[existing] = (source, emphasis);
        else
            _references.Add((source, emphasis));
    }

    /// <summary>
    /// Imports builds (clipboard: Maxroll code/URL, Mobalytics URL, or page HTML) as REFERENCES
    /// only: the current layout stays untouched, but the focus stats and their priorities are
    /// derived from what the reference allocations actually stack — so the maximizer chases
    /// proven builds' emphasis instead of a hand-picked checkbox list. When a guide carries
    /// several build versions (e.g. a Selig setup and the standard setup) any number can be
    /// picked and each votes as its own reference; re-importing a variant updates its old
    /// entry. Add several guides and the priorities become their consensus: a stat every
    /// reference stacks ranks high, a single reference's outlier gets diluted.
    /// </summary>
    [RelayCommand]
    private async Task AddBuildReference()
    {
        using var busy = BeginBusy();
        if (await ImportManyFromClipboardAsync(allowMultiple: true) is not { Count: > 0 } imports)
            return;

        SetStatus("Deriving stat priorities from the reference build(s)…");
        int empty = 0;
        var skillNotes = new List<string>();
        foreach (var import in imports)
        {
            var emphasis = await Task.Run(() =>
            {
                var referenceGraph = ComposedGraph.Build(import.Build.Layout);
                return BuildReference.EmphasisOf(referenceGraph, import.Build.AllocatedCells);
            });
            if (emphasis.Count > 0)
                AddOrReplaceReference($"{import.Source} ({import.Build.AllocatedCells.Count} nodes)", emphasis);
            else
                empty++;

            // The guide's skill setup votes as its own reference — gear, boards, and skills
            // then pull the focus priorities together.
            if (import.Skills is { ActiveSkills.Count: > 0 } skills)
            {
                var skillResult = SkillReference.EmphasisOf(skills);
                if (skillResult.Emphasis.Count > 0)
                {
                    AddOrReplaceReference(
                        $"{import.Source} skills ({skills.ActiveSkills.Count} skills, {skillResult.PrimaryDamageType.ToLowerInvariant()})",
                        skillResult.Emphasis);
                    skillNotes.Add($"{string.Join(", ", skillResult.ActiveSkillNames)} " +
                                   $"({skillResult.PrimaryDamageType.ToLowerInvariant()} damage)");
                }
            }
        }

        if (_references.Count == 0)
        {
            SetStatus("The reference build(s) have no allocated nodes or skills to learn from.", error: true);
            return;
        }
        ApplyReferencePriorities();
        string skillSuffix = skillNotes.Count > 0
            ? $" Skills read from the guide: {string.Join(" · ", skillNotes.Distinct())}."
            : "";
        string emptySuffix = empty > 0 ? $" {empty} variant(s) had no allocated nodes." : "";
        SetStatus($"Focus priorities set from {_references.Count} reference(s) — " +
                  $"run Re-analyze to apply.{skillSuffix}{emptySuffix}");
    }

    [RelayCommand]
    private void ClearBuildReferences()
    {
        _references.Clear();
        ReferenceSummary = "";
        RefreshReferenceItems();
        SetStatus("References cleared — focus stats keep their current selection.");
    }

    /// <summary>
    /// The main window's loot filter as (affix name, guide weight) pairs — set by MainWindow so
    /// this window stays free of Core dependencies. Null when opened without a host.
    /// </summary>
    public Func<IReadOnlyList<GearStatPriority>>? GearPriorityProvider { get; set; }

    /// <summary>
    /// Adds the loaded loot filter as a reference: its per-slot affix priorities are translated
    /// to paragon attributes (<see cref="GearReference"/>) and vote in the same consensus as
    /// imported reference builds — the build's GEAR wish list and its paragon allocation then
    /// pull the focus stats together.
    /// </summary>
    [RelayCommand]
    private void AddGearReference()
    {
        var gear = GearPriorityProvider?.Invoke();
        if (gear is null || gear.Count == 0)
        {
            SetStatus("Load or import a filter with affix rules in the main window first — " +
                      "its affix priorities become the reference.", error: true);
            return;
        }

        var result = GearReference.EmphasisOf(gear);
        if (result.Emphasis.Count == 0)
        {
            SetStatus("None of the filter's affixes correspond to stats paragon boards grant.", error: true);
            return;
        }

        AddOrReplaceReference($"Loaded filter ({gear.Count} affix(es))", result.Emphasis);
        ApplyReferencePriorities();
        if (result.UnmappedStats.Count > 0)
        {
            var examples = string.Join(", ", result.UnmappedStats.Take(3));
            SetStatus($"Focus priorities set from {_references.Count} reference(s). " +
                      $"{result.UnmappedStats.Count} filter affix(es) have no paragon counterpart " +
                      $"(e.g. {examples}) — run Re-analyze to apply.");
        }
    }

    /// <summary>
    /// Maps the references' combined emphasis onto this layout's focusable stats. Core stats
    /// structurally dominate every allocation (most nodes grant them), so core and secondary
    /// stats rank against their own tier's best: ≥60% → High, ≥30% → Normal, ≥15% → Low,
    /// below → unselected noise. With <paramref name="assignFocus"/> false only the summary
    /// and details are rebuilt (project load: the saved focus selection stays authoritative).
    /// </summary>
    private void ApplyReferencePriorities(bool assignFocus = true)
    {
        var emphasisLists = _references.Select(r => r.Emphasis).ToList();
        var combined = BuildReference.Combine(emphasisLists, MaximizeFocus.CoreStats);
        var coreSet = MaximizeFocus.CoreStats.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoreByAttribute = combined.ToDictionary(
            e => e.Attribute, e => e.Score, StringComparer.OrdinalIgnoreCase);
        double coreTop = combined.Where(e => coreSet.Contains(e.Attribute))
            .Select(e => e.Score).DefaultIfEmpty(0).Max();
        double secondaryTop = combined.Where(e => !coreSet.Contains(e.Attribute))
            .Select(e => e.Score).DefaultIfEmpty(0).Max();

        string Agreement(string attribute) => _references.Count > 1
            ? $" ({BuildReference.AgreementCount(emphasisLists, attribute, MaximizeFocus.CoreStats)}/{_references.Count})"
            : "";

        var high = new List<string>();
        var normal = new List<string>();
        var low = new List<string>();
        foreach (var focus in FocusStats)
        {
            double score = scoreByAttribute.GetValueOrDefault(focus.Attribute);
            double tierTop = coreSet.Contains(focus.Attribute) ? coreTop : secondaryTop;
            if (tierTop <= 0 || score < tierTop * 0.15)
            {
                if (assignFocus)
                {
                    focus.IsSelected = false;
                    focus.Priority = "Normal";
                }
                continue;
            }
            string priority = score >= tierTop * 0.60 ? "High" : score >= tierTop * 0.30 ? "Normal" : "Low";
            if (assignFocus)
            {
                focus.IsSelected = true;
                focus.Priority = priority;
            }
            string label = focus.DisplayName.Split(" ×")[0] + Agreement(focus.Attribute);
            (priority == "High" ? high : priority == "Normal" ? normal : low).Add(label);
        }

        // Stats the references stack but this layout can't supply are worth knowing about.
        var focusable = FocusStats.Select(f => f.Attribute).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailable = combined
            .Where(e => !focusable.Contains(e.Attribute)
                && e.Score >= (coreSet.Contains(e.Attribute) ? coreTop : secondaryTop) * 0.30)
            .Select(e => ParagonDisplay.FormatAttributeName(e.Attribute))
            .Take(4)
            .ToList();

        RefreshReferenceItems();
        ReferenceSummary = "Derived priorities — " +
            $"High: {(high.Count > 0 ? string.Join(", ", high) : "—")}. " +
            $"Normal: {(normal.Count > 0 ? string.Join(", ", normal) : "—")}. " +
            $"Low: {(low.Count > 0 ? string.Join(", ", low) : "—")}." +
            (unavailable.Count > 0
                ? $" Not on your boards: {string.Join(", ", unavailable)}."
                : "");
        SolveDetails = $"Combined reference emphasis (relative to each build's own tier best" +
            $"{(_references.Count > 1 ? ", averaged across the guides" : "")}):" +
            Environment.NewLine +
            string.Join(Environment.NewLine, combined
                .Where(e => e.Score >= 0.05)
                .Select(e => $"  {e.Score,5:0.00}  {ParagonDisplay.FormatAttributeName(e.Attribute)}{Agreement(e.Attribute)}"));
        if (assignFocus)
            SetStatus($"Focus priorities set from {_references.Count} reference(s) — run Re-analyze to apply them.");
    }

    /// <summary>
    /// Rewrites the board list so slot numbers AND parent/edge links match how the purchased
    /// path actually assembles the build — the planner's tree is only positional and can claim
    /// attachments (e.g. "2 hangs off 1's top") whose crossing the path never buys. Positions,
    /// rotations, targets, marks, purchases, and glyphs are all preserved.
    /// </summary>
    [RelayCommand]
    private void RenumberToPathOrder()
    {
        if (_graph is null || _layout is null)
            return;
        var purchased = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();
        if (purchased.Count == 0)
        {
            SetStatus("Solve or import a build first — the attach order comes from the purchased path.", error: true);
            return;
        }
        RecordUndo();
        var entries = BuildStats.EffectiveAttachments(_graph, purchased);
        if (Enumerable.Range(0, entries.Count).All(s => entries[s].Tier == s))
        {
            SetStatus("Slot numbering already matches the path's attach order.");
            return;
        }

        int count = _placedBoards.Count;
        var oldByNew = Enumerable.Range(0, count).OrderBy(s => entries[s].Tier).ToList();
        var newByOld = new int[count];
        for (int n = 0; n < count; n++)
            newByOld[oldByNew[n]] = n;

        var newBoards = new List<PlacedBoard> { _placedBoards[0] };
        for (int n = 1; n < count; n++)
        {
            int old = oldByNew[n];
            var placed = _placedBoards[old];
            int parentOld;
            BoardEdge edge;
            if (entries[old] is { EnteredFromSlot: int fromSlot, ParentGate: CellRef gate })
            {
                // Re-parent to the crossing the path actually uses.
                parentOld = fromSlot;
                edge = EdgeOfGate(gate, _placedBoards[fromSlot].Board.Width);
            }
            else
            {
                parentOld = placed.ParentSlot!.Value;
                edge = placed.AttachEdge!.Value;
            }
            newBoards.Add(new PlacedBoard
            {
                Board = placed.Board,
                ParentSlot = newByOld[parentOld],
                AttachEdge = edge,
                RotationSteps = placed.RotationSteps,
            });
        }

        try
        {
            ComposedGraph.Build(new ParagonLayout(newBoards));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus($"Couldn't renumber to the path order: {ex.Message}", error: true);
            return;
        }

        CellRef Remap(CellRef cell) => cell with { BoardSlot = newByOld[cell.BoardSlot] };
        var targets = _targets.Select(Remap).ToList();
        var constraints = Cells.Where(c => c.Constraint != CellConstraint.None)
            .Select(c => (Remap(c.Cell), c.Constraint)).ToList();
        var newPurchased = purchased.Select(Remap).ToHashSet();
        var glyphPicks = GlyphSockets.Select(s =>
            (NewSlot: newByOld[s.Socket.BoardSlot], Glyph: s.SelectedGlyph?.InternalName,
             s.Level, s.RequiredStat, s.EnsureActive, s.HighlightRadius)).ToList();

        _placedBoards.Clear();
        _placedBoards.AddRange(newBoards);
        RebuildLayout(); // per-slot glyph preservation is wrong after renumbering — restore below
        RestoreCells(targets, constraints);
        var socketBySlot = GlyphSockets.ToDictionary(s => s.Socket.BoardSlot);
        foreach (var pick in glyphPicks)
        {
            if (!socketBySlot.TryGetValue(pick.NewSlot, out var socket))
                continue;
            socket.SelectedGlyph = pick.Glyph is null
                ? null
                : socket.Glyphs.FirstOrDefault(g =>
                    string.Equals(g.InternalName, pick.Glyph, StringComparison.OrdinalIgnoreCase));
            socket.Level = pick.Level;
            socket.RequiredStat = pick.RequiredStat;
            socket.EnsureActive = pick.EnsureActive && socket.SelectedGlyph is not null;
            socket.HighlightRadius = pick.HighlightRadius;
        }
        foreach (var cell in Cells)
            cell.IsPurchased = newPurchased.Contains(cell.Cell);
        ClearDiffMarks(); // same nodes, renumbered slots — a diff would be pure noise
        RefreshBuildSummary();
        SetStatus("Boards renumbered to the path's attach order — slot numbers and parent links now " +
                  "reflect the crossings the path actually uses.");
    }

    /// <summary>Which edge of a board a gate cell sits on (rotated coordinates).</summary>
    private static BoardEdge EdgeOfGate(CellRef gate, int width) =>
        new[] { BoardEdge.Top, BoardEdge.Bottom, BoardEdge.Left, BoardEdge.Right }
            .First(edge => ParagonLayout.GateCell(edge, width) == (gate.X, gate.Y));

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
            // activation goal — weighted by its scalar so stronger glyphs attract more stat.
            GlyphDeliveries = GlyphDelivery.For(GlyphSockets
                .Where(s => s.SelectedGlyph is not null)
                .Select(s => (s.Socket, s.SelectedGlyph!, s.Level))),
        };
    }

    /// <summary>Fractional stat values are percentages (see <see cref="ParagonDisplay.FormatAttribute"/>).</summary>
    private static string FormatGain(double value) =>
        Math.Abs(value) < 1 && value != 0 ? $"{value * 100:0.##}%" : $"{value:0.##}";

    [RelayCommand]
    private void ClearTargets()
    {
        RecordUndo();
        _targets.Clear();
        foreach (var cell in Cells)
            cell.IsTarget = false;
        ClearSolution();
        SetStatus("Targets cleared.");
    }

    private void ClearSolution()
    {
        foreach (var cell in Cells)
        {
            cell.IsPurchased = false;
            cell.IsNewlyAdded = false;
            cell.IsRemoved = false;
        }
        RefreshBuildSummary();
    }

    private HashSet<CellRef> CurrentPurchases() =>
        Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();

    /// <summary>
    /// Rings the diff of the last action: cyan = added, dashed red = removed (refund in game).
    /// A wholesale replacement (empty baseline) shows no rings — everything would be "added".
    /// </summary>
    private void MarkPurchaseDiff(HashSet<CellRef> before)
    {
        if (before.Count == 0)
        {
            ClearDiffMarks();
            return;
        }
        foreach (var cell in Cells)
        {
            cell.IsNewlyAdded = cell.IsPurchased && !before.Contains(cell.Cell);
            cell.IsRemoved = !cell.IsPurchased && before.Contains(cell.Cell);
        }
    }

    private void ClearDiffMarks()
    {
        foreach (var cell in Cells)
        {
            cell.IsNewlyAdded = false;
            cell.IsRemoved = false;
        }
    }

    /// <summary>In-game cost of the current purchases: a crossing's gate pair costs one point.</summary>
    private int CurrentPointCost() =>
        _graph is null
            ? Cells.Count(c => c.IsPurchased)
            : GateCrossings.PointCost(_graph, Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToList());

    /// <summary>Recomputes the pinned build summary and the effective stat totals panel.</summary>
    private void RefreshBuildSummary()
    {
        UpdateGettingStarted();
        StatTotals.Clear();
        ThresholdWarnings.Clear();
        var purchasedCells = Cells.Where(c => c.IsPurchased).ToList();
        int spent = _graph is null
            ? purchasedCells.Count
            : GateCrossings.PointCost(_graph, purchasedCells.Select(c => c.Cell).ToList());
        int freeGates = purchasedCells.Count - spent;
        PointsSummary = $"{spent} of {TotalPoints} points spent" +
            (freeGates > 0 ? $" ({freeGates} crossing gate{(freeGates == 1 ? "" : "s")} free)" : "");
        PointsFraction = TotalPoints > 0 ? Math.Min(1.0, (double)spent / TotalPoints) : 0;
        PointsOverBudget = spent > TotalPoints;

        if (_graph is null || spent == 0)
        {
            NodeMixSummary = "No nodes allocated yet — solve a path or import a build.";
            PerBoardSummary = "";
            AttachOrderSummary = "";
            GlyphSummary = "";
            ThresholdSummary = "";
            UnmetThresholdNeeds = [];
            PathEdges.Clear();
            foreach (var rule in NodeRules)
                rule.UsedCount = 0;
            UpdateStatHighlights();
            UpdateBuffTooltips(new Dictionary<CellRef, double>());
            if (_graph is not null)
                UpdateThresholdTooltips(new Dictionary<string, double>(),
                    BuildStats.EffectiveAttachTiers(_graph, []));
            return;
        }

        var kindCounts = purchasedCells.CountBy(c => c.Node.Kind).ToDictionary();
        string Mix(ParagonNodeKind kind, string label) =>
            kindCounts.TryGetValue(kind, out int count) ? $"{count} {label}" : "";
        NodeMixSummary = string.Join("  ·  ", new[]
        {
            Mix(ParagonNodeKind.Normal, "normal"),
            Mix(ParagonNodeKind.Magic, "magic"),
            Mix(ParagonNodeKind.Rare, "rare"),
            Mix(ParagonNodeKind.Legendary, "legendary"),
            Mix(ParagonNodeKind.GlyphSocket, "socket"),
            Mix(ParagonNodeKind.Gate, "gate"),
        }.Where(part => part.Length > 0));

        PerBoardSummary = "Per board: " + string.Join("  ·  ", purchasedCells
            .GroupBy(c => c.Cell.BoardSlot)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {BoardDisplayName(_placedBoards[g.Key].Board)}: {g.Count()}"));

        // The game assigns threshold tiers by the order boards are ATTACHED, which follows the
        // path's gate purchases — say so whenever that differs from the slot numbering.
        var attachTiers = BuildStats.EffectiveAttachTiers(
            _graph, purchasedCells.Select(c => c.Cell).ToHashSet());
        AttachOrderSummary = Enumerable.Range(0, attachTiers.Count).Any(s => attachTiers[s] != s)
            ? "Attach order on the path: " + string.Join(" → ", Enumerable.Range(1, attachTiers.Count - 1)
                  .OrderBy(s => attachTiers[s])
                  .Select(s => $"{attachTiers[s]}. {BoardDisplayName(_placedBoards[s].Board)}")) +
              " — threshold requirements follow this order, not the slot numbers."
            : "";

        var purchased = purchasedCells.Select(c => c.Cell).ToHashSet();
        UpdatePathEdges(purchased);

        // Per-rule usage, so a Limit row shows the rule held (or by how much it couldn't).
        var cellsByGroup = NodeGrouping.CellsByGroup(_graph);
        foreach (var rule in NodeRules)
        {
            rule.UsedCount = cellsByGroup.TryGetValue(rule.Group.Key, out var groupCells)
                ? groupCells.Count(purchased.Contains)
                : 0;
        }

        int socketsBought = 0, glyphsAssigned = 0, glyphsActive = 0;
        foreach (var socket in GlyphSockets)
        {
            if (!purchased.Contains(socket.Socket))
                continue;
            socketsBought++;
            if (socket.SelectedGlyph is null)
                continue;
            glyphsAssigned++;
            if (socket.SourceAttribute is string source)
            {
                double have = GlyphRadius.AttributeTotalsInRange(
                        _graph, socket.Socket, purchased, socket.Radius, GlyphRadius.GameMetric)
                    .GetValueOrDefault(source);
                if (have >= socket.RequiredStat)
                    glyphsActive++;
            }
        }
        GlyphSummary = socketsBought == 0
            ? "No glyph sockets on the path."
            : $"{socketsBought} socket(s) on the path, {glyphsAssigned} glyph(s) socketed, {glyphsActive} activated";

        var multipliers = CellMultipliers(purchased);
        UpdateBuffTooltips(multipliers);
        var report = BuildStats.Compute(
            _graph, purchased, ParagonDatabase.Data, SheetStatOffsets(), SelectedClass, multipliers);
        ThresholdSummary = report.Thresholds.Count == 0
            ? ""
            : $"{report.ThresholdsMet} of {report.Thresholds.Count} rare-node threshold bonus(es) active";

        UnmetThresholdNeeds = report.Thresholds
            .Where(t => !t.Met)
            .Select(t => new ParagonStatNeed(
                t.Attribute,
                ParagonDisplay.FormatAttributeName(t.Attribute),
                t.Requirement - t.Have,
                t.NodeName))
            .ToList();

        // Warn when a threshold can't be met even by buying every remaining stat node — the
        // missing amount has to come from level/gear (the Character Stats inputs).
        ThresholdWarnings.Clear();
        foreach (var status in report.Thresholds.Where(t => !t.Met).OrderBy(t => t.Requirement - t.Have))
        {
            string paragonKey = status.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                ? status.Attribute[..^"_Total".Length] + "_Core"
                : status.Attribute;
            double available = _graph.Vertices
                .Where(v => !purchased.Contains(v.Cell))
                .Sum(v => v.Node.Attributes
                    .Where(a => !a.IsThresholdBonus && a.Value is not null
                        && string.Equals(a.Attribute, paragonKey, StringComparison.OrdinalIgnoreCase))
                    .Sum(a => a.Value!.Value)
                    * multipliers.GetValueOrDefault(v.Cell, 1.0));
            double deficit = status.Requirement - status.Have;
            if (available >= deficit)
                continue;
            if (ThresholdWarnings.Count == 4)
            {
                ThresholdWarnings.Add("…and more — see the solve details.");
                break;
            }
            ThresholdWarnings.Add(
                $"⚠ {status.NodeName} needs {deficit:0} more {ParagonDisplay.FormatAttributeName(status.Attribute)} " +
                $"and the boards can only add {available:0} — enter at least {deficit - available:0} more " +
                "from level/gear in Character Stats.");
        }

        // What every remaining (unpurchased) node could still add, per attribute — the "/ max"
        // suffix shows the ceiling these boards can reach for each stat.
        var availableByAttribute = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in _graph.Vertices)
        {
            if (purchased.Contains(vertex.Cell))
                continue;
            double multiplier = multipliers.GetValueOrDefault(vertex.Cell, 1.0);
            foreach (var attribute in vertex.Node.Attributes)
            {
                if (attribute.IsThresholdBonus || attribute.Value is not double value)
                    continue;
                availableByAttribute[attribute.Attribute] =
                    availableByAttribute.GetValueOrDefault(attribute.Attribute) + value * multiplier;
            }
        }

        // Core stats first, folding the sheet offset into a character total; then the rest.
        var sheet = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Strength_Core"] = SheetStrength,
            ["Intelligence_Core"] = SheetIntelligence,
            ["Willpower_Core"] = SheetWillpower,
            ["Dexterity_Core"] = SheetDexterity,
        };
        foreach (var core in MaximizeFocus.CoreStats)
        {
            double paragon = report.Totals.GetValueOrDefault(core);
            double offset = sheet.GetValueOrDefault(core);
            if (paragon == 0 && offset == 0)
                continue;
            double available = availableByAttribute.GetValueOrDefault(core);
            StatTotals.Add(new StatTotalLine(
                ParagonDisplay.FormatAttributeName(core),
                $"{paragon + offset:0.##}",
                isCore: true,
                attribute: core,
                detail: $"{paragon:0.##} from paragon + {offset:0.##} from level/gear" +
                        (available > 0 ? $"; the boards can still add {available:0.##}" : ""),
                possible: available > 0 ? $"/ {paragon + offset + available:0.##}" : ""));
        }
        foreach (var (attribute, value) in report.Totals
                     .Where(kv => kv.Value != 0
                         && !MaximizeFocus.CoreStats.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(kv => ParagonDisplay.FormatAttributeName(kv.Key), StringComparer.OrdinalIgnoreCase))
        {
            double available = availableByAttribute.GetValueOrDefault(attribute);
            StatTotals.Add(new StatTotalLine(
                ParagonDisplay.FormatAttributeName(attribute), FormatGain(value), isCore: false, attribute,
                detail: available > 0 ? $"The boards can still add {FormatGain(available)}" : null,
                possible: available > 0 ? $"/ {FormatGain(value + available)}" : ""));
        }

        UpdateStatHighlights();
        UpdateThresholdTooltips(report.Totals, attachTiers);
    }

    /// <summary>
    /// Rebuilds the line segments joining adjacent purchased cells (the start node counts as
    /// purchased), so the allocation reads as a connected route instead of scattered rings.
    /// </summary>
    private void UpdatePathEdges(IReadOnlySet<CellRef> purchased)
    {
        PathEdges.Clear();
        if (_graph is null || purchased.Count == 0)
            return;

        var cellByRef = Cells.ToDictionary(c => c.Cell);
        bool InPath(int vertex) =>
            vertex == _graph.StartVertex || purchased.Contains(_graph.Vertices[vertex].Cell);
        const double half = CellSize / 2;
        for (int i = 0; i < _graph.Vertices.Count; i++)
        {
            if (!InPath(i))
                continue;
            foreach (int j in _graph.Adjacency[i])
            {
                if (j <= i || !InPath(j))
                    continue;
                if (!cellByRef.TryGetValue(_graph.Vertices[i].Cell, out var a)
                    || !cellByRef.TryGetValue(_graph.Vertices[j].Cell, out var b))
                    continue;
                PathEdges.Add(new PathEdge(
                    a.CanvasLeft + half, a.CanvasTop + half,
                    b.CanvasLeft + half, b.CanvasTop + half));
            }
        }
    }

    /// <summary>
    /// Shows the effective node values under "+X% to [rarity] nodes in radius" glyph buffs
    /// (e.g. Marshal) on each affected cell's tooltip: "7 → 28.4 Willpower (×4.05 glyph buff)".
    /// Unpurchased cells in a buffed radius get it too — that's what buying one would grant.
    /// </summary>
    private void UpdateBuffTooltips(IReadOnlyDictionary<CellRef, double> multipliers)
    {
        foreach (var cell in Cells)
        {
            double multiplier = multipliers.GetValueOrDefault(cell.Cell, 1.0);
            if (Math.Abs(multiplier - 1.0) < 1e-9)
            {
                cell.BuffInfo = null;
                continue;
            }
            var effective = cell.Node.Attributes
                .Where(a => !a.IsThresholdBonus && a.Value is not null)
                .Select(a => $"{FormatGain(a.Value!.Value)} → {FormatGain(a.Value.Value * multiplier)} " +
                             ParagonDisplay.FormatAttributeName(a.Attribute))
                .ToList();
            cell.BuffInfo = effective.Count == 0
                ? null
                : $"Glyph node buff ×{multiplier:0.##} ({multiplier - 1:+0%;-0%}): {string.Join(", ", effective)}";
        }
    }

    // ── Click a Stat Totals row to light up its contributing nodes ───────

    private string? _highlightedStat;

    [RelayCommand]
    private void ToggleStatHighlight(StatTotalLine line)
    {
        _highlightedStat = string.Equals(_highlightedStat, line.Attribute, StringComparison.OrdinalIgnoreCase)
            ? null
            : line.Attribute;
        UpdateStatHighlights();
        SetStatus(_highlightedStat is null
            ? "Stat highlight cleared."
            : $"Highlighting allocated nodes granting {line.Name} — click the row again to clear.");
    }

    private void UpdateStatHighlights()
    {
        foreach (var line in StatTotals)
            line.IsHighlighted = string.Equals(_highlightedStat, line.Attribute, StringComparison.OrdinalIgnoreCase);
        foreach (var cell in Cells)
        {
            cell.IsStatHighlighted = _highlightedStat is not null && cell.IsPurchased
                && cell.Node.Attributes.Any(a => a.Value is not null
                    && string.Equals(a.Attribute, _highlightedStat, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Live tooltips: threshold math on rares, socketed glyph on sockets ─

    private static readonly Dictionary<string, ParagonThresholdDef> ThresholdsBySno =
        ParagonDatabase.Data.Thresholds.ToDictionary(t => t.SnoId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts the exact requirement-vs-have math on every threshold rare's tooltip.</summary>
    private void UpdateThresholdTooltips(IReadOnlyDictionary<string, double> totals, IReadOnlyList<int> attachTiers)
    {
        var sheet = SheetStatOffsets();
        foreach (var cell in Cells)
        {
            if (cell.Node.Thresholds.Count == 0)
                continue;
            var defs = cell.Node.Thresholds
                .Select(sno => ThresholdsBySno.GetValueOrDefault(sno))
                .Where(def => def is not null)
                .ToList();
            var def = defs.FirstOrDefault(d =>
                    d!.Classes.Count == 0 || d.Classes.Contains(SelectedClass, StringComparer.OrdinalIgnoreCase))
                ?? defs.FirstOrDefault();
            if (def?.Requirements.FirstOrDefault() is not ThresholdRequirement requirement)
                continue;

            double required = BuildStats.RequirementAt(requirement, attachTiers[cell.Cell.BoardSlot]);
            string attribute = requirement.Attribute;
            string paragonKey = attribute.EndsWith("_Total", StringComparison.Ordinal)
                ? attribute[..^"_Total".Length] + "_Core"
                : attribute;
            double paragonPart = totals.GetValueOrDefault(paragonKey) + totals.GetValueOrDefault(attribute);
            double sheetPart = attribute.EndsWith("_Total", StringComparison.Ordinal) ? sheet.For(attribute) : 0;
            double have = paragonPart + sheetPart;
            string status = have >= required
                ? cell.IsPurchased ? "ACTIVE" : "would activate if purchased"
                : $"{required - have:0} short";
            cell.DynamicInfo =
                $"Threshold at this board's attach tier ({attachTiers[cell.Cell.BoardSlot]}): " +
                $"needs {required:0} {ParagonDisplay.FormatAttributeName(attribute)}" +
                Environment.NewLine +
                $"Character total: {have:0} ({paragonPart:0} paragon + {sheetPart:0} level/gear) — {status}";
        }
    }

    /// <summary>Board-label anchor per slot, kept so glyph changes can retitle without a relayout.</summary>
    private (double Left, double Top)[] _labelPositions = [];

    /// <summary>
    /// Retitles each board label as "slot · Board / Glyph", fills the socket cell solid, and puts
    /// the glyph details on the socket's hover tooltip.
    /// </summary>
    private void UpdateGlyphLabels()
    {
        var glyphBySlot = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .ToDictionary(s => s.Socket.BoardSlot, s => s);

        BoardLabels.Clear();
        for (int slot = 0; slot < _placedBoards.Count && slot < _labelPositions.Length; slot++)
        {
            string text = $"{slot} · {BoardDisplayName(_placedBoards[slot].Board)}";
            if (glyphBySlot.TryGetValue(slot, out var socketed))
                text += $" / {socketed.SelectedGlyph!.Name}";
            BoardLabels.Add(new BoardLabel(text, _labelPositions[slot].Left, _labelPositions[slot].Top));
        }

        var cellByRef = Cells.ToDictionary(c => c.Cell);
        foreach (var socket in GlyphSockets)
        {
            if (!cellByRef.TryGetValue(socket.Socket, out var cell))
                continue;
            cell.HasSocketedGlyph = socket.SelectedGlyph is not null;
            cell.DynamicInfo = socket.SelectedGlyph is null
                ? null
                : $"Socketed: {socket.SelectedGlyph.Name} (lvl {socket.Level}, radius {socket.Radius})";
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
