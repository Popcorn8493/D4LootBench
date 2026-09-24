using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>Per-cell solver steering, cycled by right-clicking the cell.</summary>
public enum CellConstraint
{
    None,
    Avoid,
    Exclude,
}

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

    /// <summary>Built on demand when the cell's tooltip opens (the view reads it lazily).</summary>
    public string ToolTipText => string.Join(
        Environment.NewLine + Environment.NewLine,
        new[] { _description ??= Paragon.ParagonDisplay.DescribeNode(Node), BuffInfo, DynamicInfo }
            .Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>The static node description — the node never changes, so it's formatted once.</summary>
    private string? _description;

    /// <summary>Live extra tooltip lines: threshold math on rares, the socketed glyph on sockets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _dynamicInfo;

    /// <summary>Effective values under "+X% to [rarity] nodes in radius" glyph buffs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _buffInfo;

    /// <summary>Purchased and granting the stat picked in the Stat Totals panel.</summary>
    [ObservableProperty]
    private bool _isStatHighlighted;

    /// <summary>Added by the last purchase-changing action — "here go your new points".</summary>
    [ObservableProperty]
    private bool _isNewlyAdded;

    /// <summary>Dropped by the last purchase-changing action — "refund this one in game".</summary>
    [ObservableProperty]
    private bool _isRemoved;

    [ObservableProperty]
    private bool _isTarget;

    [ObservableProperty]
    private bool _isPurchased;

    /// <summary>Inside a highlighted glyph socket's radius (see GlyphSocketViewModel.HighlightRadius).</summary>
    [ObservableProperty]
    private bool _isInGlyphRadius;

    /// <summary>A glyph socket cell with a glyph assigned — rendered solid instead of hollow.</summary>
    [ObservableProperty]
    private bool _hasSocketedGlyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvoided))]
    [NotifyPropertyChangedFor(nameof(IsExcluded))]
    private CellConstraint _constraint;

    public bool IsAvoided => Constraint == CellConstraint.Avoid;
    public bool IsExcluded => Constraint == CellConstraint.Exclude;
}
