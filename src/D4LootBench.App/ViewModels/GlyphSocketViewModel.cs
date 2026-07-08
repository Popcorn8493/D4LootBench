using CommunityToolkit.Mvvm.ComponentModel;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

/// <summary>
/// One glyph socket of the current layout: which glyph sits in it, at what level, and whether
/// Solve should buy enough in-radius stat to activate it. The activation threshold is
/// engine-side data (like the radius breakpoints), so it stays user-editable.
/// </summary>
public partial class GlyphSocketViewModel : ObservableObject
{
    public GlyphSocketViewModel(CellRef socket, string boardName, IReadOnlyList<ParagonGlyphDef> glyphs)
    {
        Socket = socket;
        BoardName = boardName;
        Glyphs = glyphs;
    }

    public CellRef Socket { get; }
    public string BoardName { get; }
    public string Header => $"Slot {Socket.BoardSlot} · {BoardName}";

    /// <summary>Glyphs usable by the selected class, for the picker.</summary>
    public IReadOnlyList<ParagonGlyphDef> Glyphs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatName))]
    private ParagonGlyphDef? _selectedGlyph;

    partial void OnSelectedGlyphChanged(ParagonGlyphDef? value) => EnsureActive = value is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Radius))]
    private int _level = 100;

    /// <summary>In-radius stat total required for the glyph's additional bonus.</summary>
    [ObservableProperty]
    private double _requiredStat = 40;

    /// <summary>When set, Solve extends the path until the requirement is met.</summary>
    [ObservableProperty]
    private bool _ensureActive;

    public int Radius => GlyphRadius.RadiusForLevel(Level);

    public string? SourceAttribute => SelectedGlyph is null ? null : GlyphInfo.PrimarySourceAttribute(SelectedGlyph);

    public string StatName => SourceAttribute is string attribute
        ? ParagonDisplay.FormatAttributeName(attribute)
        : "";
}
