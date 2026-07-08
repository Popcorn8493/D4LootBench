using System.Windows;
using System.Windows.Controls;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.Views;

/// <summary>
/// Start-from-scratch layout planning: choose must-use boards, a pool the optimizer may draw
/// from, and the glyphs to socket. Selections feed <see cref="LayoutOptimizer"/>.
/// </summary>
public partial class LayoutOptimizerWindow : Window
{
    private readonly List<BoardChoice> _boards;
    private readonly List<GlyphChoice> _glyphs;

    public LayoutOptimizerWindow(IReadOnlyList<ParagonBoardDef> boards, IReadOnlyList<ParagonGlyphDef> glyphs)
    {
        InitializeComponent();
        _boards = boards.Select(b => new BoardChoice(b)).ToList();
        _glyphs = glyphs.Select(g => new GlyphChoice(g)).ToList();
        BoardList.ItemsSource = _boards;
        GlyphList.ItemsSource = _glyphs;
        MaxBoardsBox.ItemsSource = Enumerable.Range(1, ParagonLayout.MaxBoards).ToList();
        MaxBoardsBox.SelectedItem = ParagonLayout.MaxBoards;
    }

    public IReadOnlyList<ParagonBoardDef> MustUseBoards { get; private set; } = [];
    public IReadOnlyList<ParagonBoardDef> PoolBoards { get; private set; } = [];
    public IReadOnlyList<ParagonGlyphDef> SelectedGlyphs { get; private set; } = [];
    public int MaxBoards { get; private set; } = ParagonLayout.MaxBoards;
    public int GlyphLevel { get; private set; } = 100;
    public double RequiredStat { get; private set; } = 40;

    private void OnOptimize(object sender, RoutedEventArgs e)
    {
        MustUseBoards = _boards.Where(b => b.Mode == BoardChoice.Must).Select(b => b.Board).ToList();
        PoolBoards = _boards.Where(b => b.Mode == BoardChoice.Pool).Select(b => b.Board).ToList();
        SelectedGlyphs = _glyphs.Where(g => g.Selected).Select(g => g.Glyph).ToList();
        MaxBoards = MaxBoardsBox.SelectedItem is int max ? max : ParagonLayout.MaxBoards;
        if (int.TryParse(GlyphLevelBox.Text, out int level) && level > 0)
            GlyphLevel = level;
        if (double.TryParse(RequiredStatBox.Text, out double required) && required > 0)
            RequiredStat = required;
        DialogResult = true;
    }

    private void OnGlyphFilterChanged(object sender, TextChangedEventArgs e)
    {
        string filter = GlyphFilter.Text.Trim();
        GlyphList.ItemsSource = filter.Length == 0
            ? _glyphs
            : _glyphs.Where(g => g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public sealed class BoardChoice(ParagonBoardDef board)
    {
        public const string Pool = "Consider";
        public const string Must = "Must use";
        public const string Ignore = "Ignore";

        public ParagonBoardDef Board { get; } = board;
        public string Name { get; } = board.Name ?? board.InternalName;
        public IReadOnlyList<string> Modes { get; } = [Pool, Must, Ignore];
        public string Mode { get; set; } = Pool;
    }

    public sealed class GlyphChoice(ParagonGlyphDef glyph)
    {
        public ParagonGlyphDef Glyph { get; } = glyph;
        public string Name { get; } = glyph.Name ?? glyph.InternalName;
        public bool Selected { get; set; }
    }
}
