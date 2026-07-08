using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Views;
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
    private readonly List<MaxrollGlyphAssignment> _importedGlyphs = [];
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
        _importedGlyphs.Clear();
        SolveDetails = "";
        SetStatus("Click nodes to mark targets, then Solve.");
    }

    // ── Maxroll variant code / Mobalytics page interop ───────────────────

    [RelayCommand]
    private void ImportMaxrollCode()
    {
        string code = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus("Copy a Maxroll paragon variant code to the clipboard first.", error: true);
            return;
        }

        ConvertedMaxrollBuild build;
        try
        {
            build = MaxrollParagonCodec.ToLayout(
                MaxrollParagonCodec.Decode(code), ParagonDatabase.BoardsByInternalName);
            ComposedGraph.Build(build.Layout);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"Import failed: {ex.Message}", error: true);
            return;
        }

        ApplyImportedBuild(build, "Maxroll");
    }

    [RelayCommand]
    private async Task ImportMobalytics()
    {
        string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Copy a Mobalytics build guide URL (or the page's HTML) to the clipboard first.", error: true);
            return;
        }

        string html = text;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains("mobalytics.gg", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("The clipboard URL is not a mobalytics.gg build page.", error: true);
                return;
            }
            SetStatus("Fetching the Mobalytics page…");
            try
            {
                html = await FetchPageAsync(text.Split('#')[0]);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                SetStatus($"Couldn't fetch the page ({ex.Message}). {ManualHtmlHint}", error: true);
                return;
            }
        }

        ConvertedMaxrollBuild build;
        string title;
        try
        {
            var variants = MobalyticsParagonImporter.ExtractVariants(html);
            var variant = variants.Count == 1 ? variants[0] : PickVariant(variants);
            if (variant is null)
            {
                SetStatus("Import cancelled.");
                return;
            }
            build = MobalyticsParagonImporter.ToBuild(variant, ParagonDatabase.Data);
            ComposedGraph.Build(build.Layout);
            title = variant.Title;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            SetStatus(html.Contains("cf_chl", StringComparison.Ordinal)
                ? $"Cloudflare blocked the fetch. {ManualHtmlHint}"
                : $"Import failed: {ex.Message}", error: true);
            return;
        }

        ApplyImportedBuild(build, $"Mobalytics '{title}'");
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

    private void ApplyImportedBuild(ConvertedMaxrollBuild build, string source)
    {
        string? className = build.Layout.Boards[0].Board.ClassName;
        if (className is not null && className != SelectedClass)
            SelectedClass = className;

        _placedBoards.Clear();
        _placedBoards.AddRange(build.Layout.Boards);
        RebuildLayout();

        _importedGlyphs.Clear();
        _importedGlyphs.AddRange(build.Glyphs);

        var allocated = build.AllocatedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = allocated.Contains(cell.Cell);

        SolveDetails = _importedGlyphs.Count == 0
            ? ""
            : "Imported glyphs: " + string.Join(", ", _importedGlyphs.Select(DescribeGlyph));
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

        string code = MaxrollParagonCodec.Encode(
            MaxrollParagonCodec.FromLayout(_layout, allocated, _importedGlyphs));
        Clipboard.SetText(code);
        SetStatus($"Maxroll variant code copied to the clipboard ({allocated.Count} node(s)). " +
                  "Paste it into the Maxroll planner's Import Variant box.");
    }

    /// <summary>Per purchased glyph socket: purchased core-stat totals inside the glyph's diamond radius.</summary>
    private string BuildSocketReport(IReadOnlyList<CellRef> purchased)
    {
        if (_graph is null)
            return "";

        var lines = new List<string>();
        foreach (var cell in purchased)
        {
            _graph.TryGetVertex(cell, out int vertex);
            if (_graph.Vertices[vertex].Node.Kind != ParagonNodeKind.GlyphSocket)
                continue;

            var glyph = _importedGlyphs.FirstOrDefault(g => g.BoardSlot == cell.BoardSlot);
            int radius = GlyphRadius.RadiusForLevel(glyph?.Level ?? 50);
            var totals = GlyphRadius.AttributeTotalsInRange(_graph, cell, purchased, radius, GlyphRadius.GameMetric);

            string stats = string.Join(", ", totals
                .Where(kv => kv.Key.EndsWith("_Core", StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value:0} {kv.Key.Replace("_Core", "")}"));
            string boardName = _placedBoards[cell.BoardSlot].Board.Name ?? _placedBoards[cell.BoardSlot].Board.InternalName;
            lines.Add($"Glyph socket on {boardName}: {(stats.Length > 0 ? stats : "no stats")} purchased in radius {radius}" +
                      (glyph is null ? " (assumes glyph level 50+)" : $" ({DescribeGlyph(glyph)})"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeGlyph(MaxrollGlyphAssignment glyph)
    {
        var known = ParagonDatabase.Data.Glyphs
            .FirstOrDefault(g => string.Equals(g.InternalName, glyph.GlyphInternalName, StringComparison.OrdinalIgnoreCase));
        string name = known?.Name ?? glyph.GlyphInternalName;
        return glyph.Level is int level ? $"{name} (lvl {level}, slot {glyph.BoardSlot})" : $"{name} (slot {glyph.BoardSlot})";
    }

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

    [RelayCommand]
    private void Solve()
    {
        if (_graph is null)
            return;
        if (_targets.Count == 0)
        {
            SetStatus("Mark at least one target node first.", error: true);
            return;
        }

        var result = SteinerSolver.Solve(_graph, _targets);
        if (!result.Success)
        {
            SetStatus(result.Error ?? "Solve failed.", error: true);
            return;
        }

        var purchased = result.PurchasedCells.ToHashSet();
        foreach (var cell in Cells)
            cell.IsPurchased = purchased.Contains(cell.Cell);

        SolveDetails = BuildSocketReport(result.PurchasedCells);

        string perBoard = string.Join(", ", result.PurchasedCells
            .GroupBy(c => c.BoardSlot)
            .OrderBy(g => g.Key)
            .Select(g => $"slot {g.Key}: {g.Count()}"));
        string quality = result.IsOptimal ? "optimal" : "heuristic";
        string budget = result.PointsSpent > MaxParagonPoints
            ? $" Exceeds the {MaxParagonPoints}-point cap!"
            : "";
        SetStatus($"{result.PointsSpent} paragon points for {_targets.Count} target(s) ({quality}) — {perBoard}.{budget}",
            error: budget.Length > 0);
    }

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
