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

    private readonly IDialogService _dialogs;
    private readonly RecentProjectsService _recentProjects;
    private readonly SavedCharacterService _characterLibrary;

    public ParagonPlannerViewModel(
        IDialogService dialogs, RecentProjectsService recentProjects, SavedCharacterService characterLibrary)
    {
        _dialogs = dialogs;
        _recentProjects = recentProjects;
        _characterLibrary = characterLibrary;
        NodeRulesView = System.Windows.Data.CollectionViewSource.GetDefaultView(NodeRules);
        NodeRulesView.Filter = o =>
            string.IsNullOrWhiteSpace(RuleFilter)
            || (o is NodeRuleViewModel rule
                && rule.DisplayName.Contains(RuleFilter, StringComparison.OrdinalIgnoreCase));
        SelectedClass = Classes[0];
        RefreshRecentProjects();
        foreach (var character in _characterLibrary.Characters)
            Characters.Add(character);
        if (_characterLibrary.ActiveName is string active)
        {
            SelectedCharacter = Characters.FirstOrDefault(c =>
                string.Equals(c.Name, active, StringComparison.OrdinalIgnoreCase));
        }
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

    /// <summary>Sidebar Boards tab rows — rebuilt with the layout, linked to the glyph sockets.</summary>
    public ObservableCollection<BoardRowViewModel> BoardRows { get; } = [];

    [ObservableProperty]
    private bool _preferRareNodes;

    /// <summary>
    /// Solve/Re-analyze always route the path through every board's legendary node — boards
    /// are attached FOR their legendary power, so imports and optimizations must not path
    /// around them. Default on; user-toggleable for exotic stat-stick layouts.
    /// </summary>
    [ObservableProperty]
    private bool _includeLegendaryNodes = true;

    /// <summary>Buy rares by threshold attainability (met, then realistically meetable) first.</summary>
    [ObservableProperty]
    private bool _realisticRares;

    /// <summary>
    /// Value additive "+% damage" stats at their marginal real contribution (D4 sums them all
    /// into ONE bucket — see <see cref="DamageModel"/>), so a saturated bucket stops
    /// outcompeting main stat, crit chance, and utility in the spend and placement analysis.
    /// </summary>
    [ObservableProperty]
    private bool _balanceDamageBuckets = true;

    /// <summary>Current build's damage-bucket profile (refreshed with the Build Summary).</summary>
    private DamageProfile? _damageProfile;

    /// <summary>The paragon totals' own additive bucket (whole, situational slice) — what the
    /// stat-sheet scan subtracts, since the in-game sheet folds paragon and gear together.</summary>
    private (double Additive, double Situational) _paragonAdditiveSlices;

    [ObservableProperty]
    private string _damageSummary = "";

    [ObservableProperty]
    private string _damageSummaryDetail = "";

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

    /// <summary>
    /// The gear's ALWAYS-ON "+X% damage" sum as a PERCENT (350 = +350%): additive damage affixes
    /// with no condition attached. Feeds the damage-bucket model so saturation starts from the
    /// character's real bucket, not the paragon-only slice.
    /// </summary>
    [ObservableProperty]
    private double _sheetAdditiveDamage;

    /// <summary>
    /// The conditional slice of the gear's "+X% damage" as a PERCENT: vulnerable/close/crit
    /// damage and other "damage while/vs/to …" affixes. Joins the same additive bucket but is
    /// reported as situational — the expected multiplier shows a full-uptime ceiling and an
    /// always-on floor.
    /// </summary>
    [ObservableProperty]
    private double _sheetSituationalDamage;

    // Keystroke-rate inputs: coalesce into one summary refresh once typing pauses.
    partial void OnSheetStrengthChanged(double value) => ScheduleBuildSummary();
    partial void OnSheetIntelligenceChanged(double value) => ScheduleBuildSummary();
    partial void OnSheetWillpowerChanged(double value) => ScheduleBuildSummary();
    partial void OnSheetDexterityChanged(double value) => ScheduleBuildSummary();
    partial void OnSheetAdditiveDamageChanged(double value) => ScheduleBuildSummary();
    partial void OnSheetSituationalDamageChanged(double value) => ScheduleBuildSummary();
    partial void OnTotalPointsChanged(int value) => ScheduleBuildSummary();

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
                Owner.EndBusy();
        }
    }

    private CancellationTokenSource? _busyCancellation;

    /// <summary>True while the running operation can be cancelled — shows the overlay's Cancel button.</summary>
    [ObservableProperty]
    private bool _canCancelBusy;

    /// <summary>A busy scope whose operation honors the overlay's Cancel button (and Esc).</summary>
    private (BusyScope Scope, CancellationToken Token) BeginCancellableBusy()
    {
        _busyCancellation?.Dispose();
        _busyCancellation = new CancellationTokenSource();
        CanCancelBusy = true;
        return (BeginBusy(), _busyCancellation.Token);
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancelBusy = false;
        _busyCancellation?.Dispose();
        _busyCancellation = null;
    }

    [RelayCommand]
    private void CancelBusy()
    {
        if (_busyCancellation is not { IsCancellationRequested: false } cancellation)
            return;
        cancellation.Cancel();
        CanCancelBusy = false;
        SetStatus("Cancelling…");
    }

    /// <summary>
    /// The one wrapper every long-running command goes through: a solver/import failure lands
    /// in the status line (and error.log) instead of escaping the async command as an app-level
    /// crash, and the busy overlay is always released. Pending debounced summary refreshes are
    /// flushed first, so the operation reads current Build Summary state (damage profile).
    /// </summary>
    private async Task RunGuardedAsync(string operation, Func<Task> action)
    {
        FlushBuildSummary();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{operation} cancelled.");
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Paragon planner: {operation}");
            SetStatus($"{operation} failed: {ex.Message}", error: true);
        }
        finally
        {
            // Busy scopes release themselves on unwind; this is the backstop for a scope an
            // exception path never reached (the overlay also blocks the keyboard shortcuts).
            if (_busyDepth <= 0)
            {
                _busyDepth = 0;
                EndBusy();
            }
        }
    }

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
    private Task PlanLayout() => RunGuardedAsync("Layout planning", PlanLayoutCoreAsync);

    private async Task PlanLayoutCoreAsync()
    {
        var boards = ParagonDatabase.BoardsForClass(SelectedClass)
            .Where(b => b.BoardIndex != 0)
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var glyphs = ParagonDatabase.Data.Glyphs
            .Where(g => g.Name is not null && (g.Classes.Count == 0 || g.Classes.Contains(SelectedClass)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dialog = new LayoutOptimizerWindow(boards, glyphs);
        if (_dialogs.ShowDialog(dialog, this) != true)
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
            s => (Glyph: s.SelectedGlyph?.InternalName, s.Level, s.RequiredStat, s.EnsureActive,
                s.HighlightRadius, s.IsGlyphLocked, s.IsBoardLocked));
        // Detach before dropping: a stale socket still bound somewhere (e.g. a Boards-tab row
        // mid-rebuild) must not keep driving this view model.
        foreach (var socket in GlyphSockets)
            socket.PropertyChanged -= OnGlyphSocketChanged;
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
                socket.IsGlyphLocked = previous.IsGlyphLocked;
                socket.IsBoardLocked = previous.IsBoardLocked;
            }
            socket.PropertyChanged += OnGlyphSocketChanged;
            GlyphSockets.Add(socket);
        }

        // Boards tab rows: one per attached board, linked to the slot's socket for the live
        // glyph name and the shared board lock.
        BoardRows.Clear();
        for (int slot = 0; slot < _placedBoards.Count; slot++)
        {
            BoardRows.Add(new BoardRowViewModel(slot, _placedBoards[slot],
                GlyphSockets.FirstOrDefault(s => s.Socket.BoardSlot == slot)));
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

        // Focusable stats for the point maximizer. The list spans EVERY stat the class can
        // reach — all of its boards' nodes plus its glyphs' output attributes — not just the
        // attached boards, so selections and weights survive board swaps and remain editable
        // even while the stat is temporarily off the layout. On-layout stats are flagged.
        var previousFocus = FocusStats.ToDictionary(
            f => f.Attribute, f => (f.IsSelected, f.Priority), StringComparer.OrdinalIgnoreCase);
        var onLayout = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in _graph.Vertices)
        {
            foreach (var attribute in vertex.Node.Attributes)
            {
                if (!attribute.IsThresholdBonus && attribute.Value is not null)
                    onLayout.Add(attribute.Attribute);
            }
        }
        var focusable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodesBySnoId = ParagonDatabase.NodesBySnoId;
        foreach (var board in ParagonDatabase.BoardsForClass(SelectedClass))
        {
            foreach (var placement in board.Nodes)
            {
                if (!nodesBySnoId.TryGetValue(placement.Node, out var node))
                    continue;
                foreach (var attribute in node.Attributes)
                {
                    if (!attribute.IsThresholdBonus && attribute.Value is not null)
                        focusable.TryAdd(attribute.Attribute, ParagonDisplay.FormatAttributeName(attribute.Attribute));
                }
            }
        }
        foreach (var glyph in ParagonDatabase.GlyphsForClass(SelectedClass))
        {
            if (GlyphInfo.DestinationAttribute(glyph) is { } destination)
                focusable.TryAdd(destination, ParagonDisplay.FormatAttributeName(destination));
        }
        FocusStats.Clear();
        foreach (var (attribute, display) in focusable
                     .OrderBy(kv => MaximizeFocus.CoreStats.Contains(kv.Key) ? 0 : 1)
                     .ThenBy(kv => onLayout.Contains(kv.Key) ? 0 : 1)
                     .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var previous = previousFocus.GetValueOrDefault(attribute, (IsSelected: false, Priority: "Normal"));
            FocusStats.Add(new FocusStatViewModel(attribute, display)
            {
                IsOnBoards = onLayout.Contains(attribute),
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
            ScheduleBuildSummary(); // level/stat boxes change per keystroke; bulk restores per socket
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

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
