using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>An attachable-board choice; a null Board means "starter board only".</summary>
public sealed record BoardOption(string Label, ParagonBoardDef? Board)
{
    public override string ToString() => Label;
}

/// <summary>
/// Paragon planner: class starter board plus optionally one board attached at the top gate.
/// Click nodes to mark targets; Solve finds the cheapest connected path from the start node.
/// </summary>
public partial class ParagonPlannerViewModel : ObservableObject
{
    public const double CellSize = 26;
    private const double BoardGap = 16;

    private readonly HashSet<CellRef> _targets = [];
    private ComposedGraph? _graph;

    public ParagonPlannerViewModel()
    {
        SelectedClass = Classes[0];
    }

    public IReadOnlyList<string> Classes { get; } =
        ["Barbarian", "Druid", "Necromancer", "Rogue", "Sorcerer", "Spiritborn", "Paladin", "Warlock"];

    public ObservableCollection<BoardOption> BoardOptions { get; } = [];

    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public ObservableCollection<ParagonCellViewModel> Cells { get; } = [];

    [ObservableProperty]
    private string _selectedClass = "Sorcerer";

    [ObservableProperty]
    private BoardOption? _selectedBoardOption;

    [ObservableProperty]
    private int _selectedRotation;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private double _canvasWidth;

    [ObservableProperty]
    private double _canvasHeight;

    partial void OnSelectedClassChanged(string value)
    {
        BoardOptions.Clear();
        BoardOptions.Add(new BoardOption("(starter board only)", null));
        foreach (var board in ParagonDatabase.BoardsForClass(value).Where(b => b.BoardIndex != 0))
            BoardOptions.Add(new BoardOption(board.Name ?? board.InternalName, board));
        SelectedBoardOption = BoardOptions[0];
    }

    partial void OnSelectedBoardOptionChanged(BoardOption? value) => RebuildLayout();

    partial void OnSelectedRotationChanged(int value) => RebuildLayout();

    private ParagonBoardDef StarterBoard =>
        ParagonDatabase.BoardsForClass(SelectedClass).Single(b => b.BoardIndex == 0);

    private void RebuildLayout()
    {
        _targets.Clear();
        Cells.Clear();

        var placed = new List<PlacedBoard> { new() { Board = StarterBoard } };
        if (SelectedBoardOption?.Board is ParagonBoardDef attached)
        {
            placed.Add(new PlacedBoard
            {
                Board = attached,
                ParentSlot = 0,
                AttachEdge = BoardEdge.Top,
                RotationSteps = SelectedRotation / 90,
            });
        }

        var layout = new ParagonLayout(placed);
        _graph = ComposedGraph.Build(layout);

        // The attached board (slot 1) sits above the starter, connected gate-to-gate.
        int width = StarterBoard.Width;
        bool hasAttached = placed.Count > 1;
        double starterTop = hasAttached ? width * CellSize + BoardGap : 0;

        foreach (var vertex in _graph.Vertices)
        {
            double left = vertex.Cell.X * CellSize;
            double top = vertex.Cell.BoardSlot == 0
                ? starterTop + vertex.Cell.Y * CellSize
                : vertex.Cell.Y * CellSize;
            Cells.Add(new ParagonCellViewModel(vertex.Cell, vertex.Node, left, top));
        }

        CanvasWidth = width * CellSize;
        CanvasHeight = starterTop + width * CellSize;
        SetStatus("Click nodes to mark targets, then Solve.");
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

        string quality = result.IsOptimal ? "optimal" : "heuristic";
        SetStatus($"{result.PointsSpent} paragon points for {_targets.Count} target(s) ({quality}).");
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
