using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>One occupied cell on the rendered paragon layout.</summary>
public partial class ParagonCellViewModel : ObservableObject
{
    public ParagonCellViewModel(CellRef cell, ParagonNodeDef node, double canvasLeft, double canvasTop)
    {
        Cell = cell;
        Node = node;
        CanvasLeft = canvasLeft;
        CanvasTop = canvasTop;
    }

    public CellRef Cell { get; }
    public ParagonNodeDef Node { get; }
    public double CanvasLeft { get; }
    public double CanvasTop { get; }

    public string Kind => Node.Kind.ToString();
    public bool IsStart => Node.Kind == ParagonNodeKind.Start;
    public string ToolTipText => Paragon.ParagonDisplay.DescribeNode(Node);

    [ObservableProperty]
    private bool _isTarget;

    [ObservableProperty]
    private bool _isPurchased;
}
