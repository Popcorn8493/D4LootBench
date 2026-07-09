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

        _placedBoards.Add(candidate[^1]);
        RebuildLayout();
        SetStatus($"Attached {BoardDisplayName(board)} ({edge} of slot {parentSlot}).");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveLastBoard))]
    private void RemoveLastBoard()
    {
        // Only the newest board is removable — later slots may attach to earlier ones.
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
                socket.Level = request.GlyphLevel;
                socket.RequiredStat = request.RequiredStat;
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
    private async Task<(ConvertedMaxrollBuild Build, string Source)?> ImportFromClipboardAsync()
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
                    return await ImportMobalyticsBuildAsync(text);
                if (text.Contains("maxroll.gg", StringComparison.OrdinalIgnoreCase))
                    return await ImportMaxrollUrlAsync(text);
                SetStatus("The clipboard URL is neither a maxroll.gg nor a mobalytics.gg page.", error: true);
                return null;
            }
            if (text.StartsWith('['))
            {
                var build = MaxrollParagonCodec.ToLayout(
                    MaxrollParagonCodec.Decode(text), ParagonDatabase.BoardsByInternalName);
                ComposedGraph.Build(build.Layout);
                return (build, "Maxroll code");
            }
            return await ImportMobalyticsBuildAsync(text); // pasted page HTML
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

    private async Task<(ConvertedMaxrollBuild Build, string Source)?> ImportMobalyticsBuildAsync(string text)
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
            var variant = variants.Count == 1 ? variants[0] : PickVariant(variants);
            if (variant is null)
            {
                SetStatus("Import cancelled.");
                return null;
            }
            var build = MobalyticsParagonImporter.ToBuild(variant, ParagonDatabase.Data);
            ComposedGraph.Build(build.Layout);
            return (build, $"Mobalytics '{variant.Title}'");
        }
        catch (FormatException) when (html.Contains("cf_chl", StringComparison.Ordinal))
        {
            SetStatus($"Cloudflare blocked the fetch. {ManualHtmlHint}", error: true);
            return null;
        }
    }

    private async Task<(ConvertedMaxrollBuild Build, string Source)?> ImportMaxrollUrlAsync(string url)
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
        int index = 0;
        if (variants.Count > 1)
        {
            index = PickIndex(variants.Select(v => $"{v.Title} — {v.Entries.Count} board(s)").ToList());
            if (index < 0)
            {
                SetStatus("Import cancelled.");
                return null;
            }
        }
        var build = MaxrollParagonCodec.ToLayout(variants[index].Entries, ParagonDatabase.BoardsByInternalName);
        ComposedGraph.Build(build.Layout);
        return (build, $"Maxroll '{variants[index].Title}'");
    }

    private const string ManualHtmlHint =
        "Open the build in a browser, view the page source (Ctrl+U), copy it all, " +
        "then click Import Mobalytics again with the HTML in the clipboard.";

    private static Task<string> FetchPageAsync(string url) => Services.GuidePageFetcher.FetchPageAsync(url);

    private static MobalyticsParagonVariant? PickVariant(IReadOnlyList<MobalyticsParagonVariant> variants)
    {
        var dialog = new MobalyticsVariantPickerWindow(variants)
        {
            Owner = Application.Current?.Windows.OfType<ParagonPlannerWindow>().FirstOrDefault(),
        };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private static int PickIndex(IReadOnlyList<string> labels)
    {
        var dialog = new MobalyticsVariantPickerWindow(labels)
        {
            Owner = Application.Current?.Windows.OfType<ParagonPlannerWindow>().FirstOrDefault(),
        };
        return dialog.ShowDialog() == true ? dialog.SelectedIndex : -1;
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
        string report = await Task.Run(() =>
            BuildComparer.Compare(current, imported, ParagonDatabase.Data, sheetStats));
        SolveDetails = report;
        SetStatus($"Compared the current build against {import.Source} — see the report below.");
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
            await SolveAsync();
        if (combined.Notes.Count > 0)
        {
            SolveDetails = string.Join(Environment.NewLine, combined.Notes) +
                           (SolveDetails.Length > 0 ? Environment.NewLine + SolveDetails : "");
        }
    }

    private void ApplyImportedBuild(ConvertedMaxrollBuild build, string source)
    {
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
        RefreshBuildSummary();

        var assigned = GlyphSockets.Where(s => s.SelectedGlyph is not null).ToList();
        SolveDetails = assigned.Count == 0
            ? ""
            : "Imported glyphs: " + string.Join(", ", assigned.Select(DescribeGlyph));
        SetStatus($"Imported {source} build: {_placedBoards.Count} board(s), {allocated.Count} allocated node(s).");
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

    [RelayCommand]
    private void SaveProject()
    {
        if (_layout is null)
            return;
        var dlg = new SaveFileDialog
        {
            Title      = "Save Paragon Project",
            Filter     = ProjectDialogFilter,
            DefaultExt = ".paragon.json",
            FileName   = $"{SelectedClass.ToLowerInvariant()}-paragon",
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dlg.FileName, ParagonProjectSerializer.ToJson(CaptureProject()));
            RememberRecentProject(dlg.FileName);
            SetStatus($"Saved project \"{Path.GetFileName(dlg.FileName)}\".");
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

    private void OpenProjectFile(string path)
    {
        try
        {
            ApplyProject(ParagonProjectSerializer.FromJson(File.ReadAllText(path)));
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
        FocusWeights = FocusStats
            .Where(f => f.IsSelected && Math.Abs(f.Weight - 1.0) > 1e-9)
            .ToDictionary(f => f.Attribute, f => f.Weight),
        PreferRareNodes = PreferRareNodes,
        RealisticRares = RealisticRares,
        ActivateThresholds = ActivateThresholds,
        TotalPoints = TotalPoints,
        SheetStrength = SheetStrength,
        SheetIntelligence = SheetIntelligence,
        SheetWillpower = SheetWillpower,
        SheetDexterity = SheetDexterity,
    };

    /// <summary>Restores a saved session; validates everything before touching the live state.</summary>
    private void ApplyProject(ParagonProject project)
    {
        if (!Classes.Contains(project.ClassName))
            throw new FormatException($"Unknown class '{project.ClassName}'.");
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

        var purchased = project.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
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
        foreach (var rule in NodeRules.Where(r => r.Mode == NodeRuleMode.Limit))
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

        cell.IsTarget = !cell.IsTarget;
        if (cell.IsTarget)
            _targets.Add(cell.Cell);
        else
            _targets.Remove(cell.Cell);

        ClearSolution();
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
    private Task Solve() => SolveAsync();

    /// <summary>
    /// One-click re-run after any change (targets, rules, glyphs, stats): solve the path fresh,
    /// then spend every remaining point of the pool with the Optimize-tab settings.
    /// </summary>
    [RelayCommand]
    private async Task Reanalyze()
    {
        if (_graph is null)
            return;
        using var busy = BeginBusy();
        if (!await SolveAsync())
            return;
        if (TotalPoints - Cells.Count(c => c.IsPurchased) > 0)
            await MaximizePointsAsync();
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

        cell.Constraint = cell.Constraint switch
        {
            CellConstraint.None => CellConstraint.Avoid,
            CellConstraint.Avoid => CellConstraint.Exclude,
            _ => CellConstraint.None,
        };
        if (cell.Constraint == CellConstraint.Exclude && cell.IsTarget)
        {
            cell.IsTarget = false;
            _targets.Remove(cell.Cell);
        }

        ClearSolution();
        SetStatus(cell.Constraint switch
        {
            CellConstraint.Avoid => "Node marked avoid — taken only when it saves several plain nodes.",
            CellConstraint.Exclude => "Node excluded — the path will never go through it.",
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
        using var busy = BeginBusy();
        SetStatus("Analyzing alternate board rotations, glyph placements, and board swaps…");
        var (baseline, suggestions) = await Task.Run(() =>
        {
            var solved = PlanSolver.Solve(graph, request);
            if (!solved.Success)
                return (solved, (IReadOnlyList<PlacementSuggestion>)Array.Empty<PlacementSuggestion>());
            var found = PlacementAnalyzer.SuggestRotations(layout, request, solved)
                .Take(3)
                .Concat(PlacementAnalyzer.SuggestGlyphPlacements(graph, layout, solved.PurchasedCells, request.GlyphGoals))
                .Concat(PlacementAnalyzer.SuggestBoardSwaps(layout, request, solved, spareBoards))
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
        var before = CaptureState();
        int pointsBefore = Cells.Count(c => c.IsPurchased);

        switch (suggestion.Change)
        {
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

        _revertState = before;
        RevertPlacementCommand.NotifyCanExecuteChanged();
        await SolveAsync();
        int pointsAfter = Cells.Count(c => c.IsPurchased);
        SetStatus($"Applied — path re-solved at {pointsAfter} points (was {pointsBefore}). " +
                  "Revert flips back to compare.", error: StatusIsError);
    }

    [RelayCommand(CanExecute = nameof(CanRevertPlacement))]
    private async Task RevertPlacement()
    {
        if (_revertState is not PlannerState state)
            return;
        _placedBoards.Clear();
        _placedBoards.AddRange(state.Boards);
        RebuildLayout(); // clears _revertState — reverting is one-shot
        RestoreCells(state.Targets, state.Constraints);
        RestoreGlyphs(state.Glyphs);
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
            await SolveAsync();
        SetStatus("Reverted to the layout before the applied suggestion.");
    }

    private bool CanRevertPlacement() => _revertState is not null;

    // ── Spending leftover points ──────────────────────────────────────────

    /// <summary>
    /// Spends whatever the current path leaves of <see cref="TotalPoints"/> on the focused stats
    /// (and rare nodes first, when preferred), growing the purchased tree greedily.
    /// </summary>
    [RelayCommand]
    private Task MaximizePoints() => MaximizePointsAsync();

    private async Task MaximizePointsAsync()
    {
        if (_graph is null)
            return;
        var purchased = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();
        int remaining = TotalPoints - purchased.Count;
        if (remaining <= 0)
        {
            SetStatus($"No points left — {purchased.Count} of {TotalPoints} are already spent.", error: true);
            return;
        }

        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var stat in FocusStats.Where(f => f.IsSelected && Math.Abs(f.Weight - 1.0) > 1e-9))
            weights[stat.Attribute] = stat.Weight;
        var focus = new MaximizeFocus(
            FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute).ToList(),
            PreferRareNodes,
            ActivateThresholds)
        {
            Weights = weights.Count > 0 ? weights : null,
            RealisticRares = RealisticRares,
        };
        var context = new ThresholdContext(
            ParagonDatabase.Data, SelectedClass, SheetStatOffsets(), CellMultipliers(purchased));
        var request = BuildPlanRequest();
        var graph = _graph;
        using var busy = BeginBusy();
        SetStatus($"Spending up to {remaining} remaining point(s)…");
        var outcome = await Task.Run(() => PointMaximizer.Extend(graph, purchased, remaining, focus, request, context));

        if (outcome.AddedCells.Count == 0)
        {
            SetStatus(outcome.Notes.FirstOrDefault()
                ?? "Nothing worthwhile is reachable with the remaining points — no nodes added.", error: true);
            return;
        }
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);
        RefreshBuildSummary();

        string gains = string.Join(", ", outcome.Gains
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{FormatGain(kv.Value)} {ParagonDisplay.FormatAttributeName(kv.Key)}"));
        string rares = outcome.RaresAdded > 0 ? $"{outcome.RaresAdded} rare node(s), " : "";
        string thresholdsPart = outcome.ThresholdsActivated > 0
            ? $"{outcome.ThresholdsActivated} threshold bonus(es) activated, "
            : "";
        string details = BuildPurchaseReport(purchased, [], outcome.Notes);
        SolveDetails = $"Spent {outcome.AddedCells.Count} leftover point(s): {rares}{thresholdsPart}" +
                       $"{(gains.Length > 0 ? "gained " + gains : "no focused stat gains")}." +
                       (details.Length > 0 ? Environment.NewLine + details : "");
        SetStatus($"{purchased.Count} of {TotalPoints} points spent " +
                  $"(+{outcome.AddedCells.Count} maximizing{(PreferRareNodes ? " rare nodes and" : "")} focused stats" +
                  $"{(outcome.ThresholdsActivated > 0 ? $", {outcome.ThresholdsActivated} threshold(s) activated" : "")}).",
            error: outcome.Notes.Count > 0);
    }

    /// <summary>Fractional stat values are percentages (see <see cref="ParagonDisplay.FormatAttribute"/>).</summary>
    private static string FormatGain(double value) =>
        Math.Abs(value) < 1 && value != 0 ? $"{value * 100:0.##}%" : $"{value:0.##}";

    [RelayCommand]
    private void ClearTargets()
    {
        _targets.Clear();
        foreach (var cell in Cells)
            cell.IsTarget = false;
        ClearSolution();
        SetStatus("Targets cleared.");
    }

    private void ClearSolution()
    {
        foreach (var cell in Cells)
            cell.IsPurchased = false;
        RefreshBuildSummary();
    }

    /// <summary>Recomputes the pinned build summary and the effective stat totals panel.</summary>
    private void RefreshBuildSummary()
    {
        StatTotals.Clear();
        ThresholdWarnings.Clear();
        var purchasedCells = Cells.Where(c => c.IsPurchased).ToList();
        int spent = purchasedCells.Count;
        PointsSummary = $"{spent} of {TotalPoints} points spent";
        PointsFraction = TotalPoints > 0 ? Math.Min(1.0, (double)spent / TotalPoints) : 0;
        PointsOverBudget = spent > TotalPoints;

        if (_graph is null || spent == 0)
        {
            NodeMixSummary = "No nodes allocated yet — solve a path or import a build.";
            GlyphSummary = "";
            ThresholdSummary = "";
            UnmetThresholdNeeds = [];
            PathEdges.Clear();
            foreach (var rule in NodeRules)
                rule.UsedCount = 0;
            UpdateStatHighlights();
            if (_graph is not null)
                UpdateThresholdTooltips(new Dictionary<string, double>());
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
        UpdateThresholdTooltips(report.Totals);
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
    private void UpdateThresholdTooltips(IReadOnlyDictionary<string, double> totals)
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

            double required = BuildStats.RequirementAt(requirement, cell.Cell.BoardSlot);
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
                $"Threshold at this board slot: needs {required:0} {ParagonDisplay.FormatAttributeName(attribute)}" +
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
