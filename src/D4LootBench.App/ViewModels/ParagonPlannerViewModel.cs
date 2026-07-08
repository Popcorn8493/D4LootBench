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
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>A floating text label positioned on the board canvas.</summary>
public sealed record BoardLabel(string Text, double CanvasLeft, double CanvasTop);

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
        SelectedClass = Classes[0];
    }

    public IReadOnlyList<string> Classes { get; } =
        ["Barbarian", "Druid", "Necromancer", "Rogue", "Sorcerer", "Spiritborn", "Paladin", "Warlock"];

    public ObservableCollection<ParagonBoardDef> AttachableBoards { get; } = [];
    public ObservableCollection<int> ParentSlots { get; } = [];
    public IReadOnlyList<BoardEdge> Edges { get; } =
        [BoardEdge.Top, BoardEdge.Left, BoardEdge.Right, BoardEdge.Bottom];
    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public ObservableCollection<ParagonCellViewModel> Cells { get; } = [];
    public ObservableCollection<BoardLabel> BoardLabels { get; } = [];

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

    /// <summary>The player's total point pool; the maximizer spends what solve left over.</summary>
    [ObservableProperty]
    private int _totalPoints = MaxParagonPoints;

    /// <summary>
    /// Character stat from level and gear (applied to each core stat) — rare-node threshold
    /// requirements check the character TOTAL, and paragon is only part of it.
    /// </summary>
    [ObservableProperty]
    private double _sheetStats;

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
            NonParagonStat = SheetStats,
        };
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

        _layout = new ParagonLayout(_placedBoards.ToList());
        _graph = ComposedGraph.Build(_layout);

        int width = StarterBoard.Width;
        double boardSpan = width * CellSize;
        int minX = _layout.BoardPositions.Min(p => p.X);
        int minY = _layout.BoardPositions.Min(p => p.Y);

        var origins = new (double Left, double Top)[_placedBoards.Count];
        for (int slot = 0; slot < _placedBoards.Count; slot++)
        {
            var (bx, by) = _layout.BoardPositions[slot];
            origins[slot] = ((bx - minX) * (boardSpan + BoardGap),
                             TopPadding + (by - minY) * (boardSpan + BoardGap));
            BoardLabels.Add(new BoardLabel(
                $"{slot} · {BoardDisplayName(_placedBoards[slot].Board)}",
                origins[slot].Left,
                origins[slot].Top - TopPadding + 4));
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
            s => (Glyph: s.SelectedGlyph?.InternalName, s.Level, s.RequiredStat, s.EnsureActive));
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
            }
            GlyphSockets.Add(socket);
        }

        // Keep rule settings for groups that survive the layout change (e.g. adding a board).
        var previousRules = NodeRules.ToDictionary(r => r.Group.Key, r => (r.Mode, r.Limit));
        NodeRules.Clear();
        foreach (var group in NodeGrouping.GroupsIn(_graph))
        {
            var rule = new NodeRuleViewModel(group);
            if (previousRules.TryGetValue(group.Key, out var previous))
            {
                rule.Mode = previous.Mode;
                rule.Limit = previous.Limit;
            }
            NodeRules.Add(rule);
        }

        // Focusable stats for the point maximizer (selection survives layout changes).
        var previousFocus = FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            FocusStats.Add(new FocusStatViewModel(attribute, display)
            {
                IsSelected = previousFocus.Contains(attribute),
            });
        }

        Suggestions.Clear();
        _revertState = null;
        RevertPlacementCommand.NotifyCanExecuteChanged();

        SolveDetails = "";
        SetStatus("Click nodes to mark targets (right-click to avoid/exclude), then Solve.");
    }

    // ── Import: Maxroll codes and URLs, Mobalytics pages ─────────────────

    [RelayCommand]
    private async Task ImportBuild()
    {
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

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>
    /// Mobalytics sits behind Cloudflare, which 403s HttpClient by TLS fingerprint but lets the
    /// in-box Windows curl.exe through — prefer it, and fall back to HttpClient without it.
    /// </summary>
    private static async Task<string> FetchPageAsync(string url)
    {
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (File.Exists(curl))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(curl)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in new[] { "-sL", "--compressed", "--max-time", "20", "-H", $"User-Agent: {BrowserUserAgent}", url })
                psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("curl.exe failed to start.");
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && output.Length > 0)
                return output;
        }
        return await Http.GetStringAsync(url);
    }

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

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
        SetStatus("Comparing…");
        double sheetStats = SheetStats;
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
            Solve();
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

            lines.Add($"Socket on {socket.BoardName} ({glyph}): " +
                      $"{(stats.Length > 0 ? stats : "no stats")} in radius {radius}{activation}{delivery}");
        }

        // Rare-node threshold bonuses: requirements scale with the board's attachment slot.
        var report = BuildStats.Compute(_graph, purchased, ParagonDatabase.Data, SheetStats, SelectedClass);
        if (report.Thresholds.Count > 0)
        {
            lines.Add($"Threshold bonuses: {report.ThresholdsMet} of {report.Thresholds.Count} active " +
                      $"(counting {SheetStats:0} sheet stat from level/gear).");
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

    /// <summary>Targets, rules, per-cell overrides and glyph goals as one immutable solver request.</summary>
    private PlanRequest BuildPlanRequest() => new()
    {
        Targets = _targets.ToList(),
        NodeRules = NodeRules
            .Where(r => r.Mode != NodeRuleMode.Allow)
            .Select(r => new NodeRule(r.Group.Key, r.Mode, r.Limit))
            .ToList(),
        AvoidCells = Cells.Where(c => c.Constraint == CellConstraint.Avoid).Select(c => c.Cell).ToList(),
        ExcludeCells = Cells.Where(c => c.Constraint == CellConstraint.Exclude).Select(c => c.Cell).ToList(),
        GlyphGoals = GlyphSockets
            .Where(s => s.EnsureActive && s.SourceAttribute is not null)
            .Select(s => new GlyphGoal(s.Socket, s.SourceAttribute!, s.RequiredStat, s.Radius, s.SelectedGlyph?.Name))
            .ToList(),
    };

    [RelayCommand]
    private void Solve()
    {
        if (_graph is null)
            return;
        var request = BuildPlanRequest();
        if (request.Targets.Count == 0 && request.GlyphGoals.Count == 0)
        {
            SetStatus("Mark at least one target node (or enable a glyph activation goal) first.", error: true);
            return;
        }

        var result = PlanSolver.Solve(_graph, request);
        if (!result.Success)
        {
            SetStatus(result.Error ?? "Solve failed.", error: true);
            return;
        }

        var purchased = result.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);

        SolveDetails = BuildSolveDetails(result);

        string perBoard = string.Join(", ", result.PurchasedCells
            .GroupBy(c => c.BoardSlot)
            .OrderBy(g => g.Key)
            .Select(g => $"slot {g.Key}: {g.Count()}"));
        string quality = result.IsOptimal ? "optimal" : "heuristic";
        string budget = result.PointsSpent > MaxParagonPoints
            ? $" Exceeds the {MaxParagonPoints}-point cap!"
            : "";
        SetStatus($"{result.PointsSpent} paragon points for {_targets.Count} target(s) ({quality}) — {perBoard}.{budget}",
            error: budget.Length > 0 || result.Notes.Count > 0);
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
        SetStatus("Analyzing alternate board rotations and glyph placements…");
        var (baseline, suggestions) = await Task.Run(() =>
        {
            var solved = PlanSolver.Solve(graph, request);
            if (!solved.Success)
                return (solved, (IReadOnlyList<PlacementSuggestion>)Array.Empty<PlacementSuggestion>());
            var found = PlacementAnalyzer.SuggestRotations(layout, request, solved)
                .Take(3)
                .Concat(PlacementAnalyzer.SuggestGlyphPlacements(graph, layout, solved.PurchasedCells, request.GlyphGoals))
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
    private void ApplySuggestion(PlacementSuggestion suggestion)
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
        Solve();
        int pointsAfter = Cells.Count(c => c.IsPurchased);
        SetStatus($"Applied — path re-solved at {pointsAfter} points (was {pointsBefore}). " +
                  "Revert flips back to compare.", error: StatusIsError);
    }

    [RelayCommand(CanExecute = nameof(CanRevertPlacement))]
    private void RevertPlacement()
    {
        if (_revertState is not PlannerState state)
            return;
        _placedBoards.Clear();
        _placedBoards.AddRange(state.Boards);
        RebuildLayout(); // clears _revertState — reverting is one-shot
        RestoreCells(state.Targets, state.Constraints);
        RestoreGlyphs(state.Glyphs);
        if (_targets.Count > 0 || GlyphSockets.Any(s => s.EnsureActive))
            Solve();
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
        if (_graph is null)
            return;
        var purchased = Cells.Where(c => c.IsPurchased).Select(c => c.Cell).ToHashSet();
        int remaining = TotalPoints - purchased.Count;
        if (remaining <= 0)
        {
            SetStatus($"No points left — {purchased.Count} of {TotalPoints} are already spent.", error: true);
            return;
        }

        var focus = new MaximizeFocus(
            FocusStats.Where(f => f.IsSelected).Select(f => f.Attribute).ToList(),
            PreferRareNodes);
        var request = BuildPlanRequest();
        var graph = _graph;
        SetStatus($"Spending up to {remaining} remaining point(s)…");
        var outcome = await Task.Run(() => PointMaximizer.Extend(graph, purchased, remaining, focus, request));

        if (outcome.AddedCells.Count == 0)
        {
            SetStatus("Nothing worthwhile is reachable with the remaining points — no nodes added.", error: true);
            return;
        }
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);

        string gains = string.Join(", ", outcome.Gains
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{FormatGain(kv.Value)} {ParagonDisplay.FormatAttributeName(kv.Key)}"));
        string rares = outcome.RaresAdded > 0 ? $"{outcome.RaresAdded} rare node(s), " : "";
        string details = BuildPurchaseReport(purchased, [], []);
        SolveDetails = $"Spent {outcome.AddedCells.Count} leftover point(s): {rares}" +
                       $"{(gains.Length > 0 ? "gained " + gains : "no focused stat gains")}." +
                       (details.Length > 0 ? Environment.NewLine + details : "");
        SetStatus($"{purchased.Count} of {TotalPoints} points spent " +
                  $"(+{outcome.AddedCells.Count} maximizing{(PreferRareNodes ? " rare nodes and" : "")} focused stats).");
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
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
