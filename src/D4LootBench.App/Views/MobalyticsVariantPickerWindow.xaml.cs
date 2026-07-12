using System.Windows;
using System.Windows.Controls;
using D4LootBench.Paragon.Import;

namespace D4LootBench.App.Views;

/// <summary>
/// Lets the user pick a paragon variant when a build contains several — or, with
/// <c>allowMultiple</c>, any number of them (reference analysis wants e.g. both the
/// Selig setup and the standard setup voting in the consensus).
/// </summary>
public partial class MobalyticsVariantPickerWindow : Window
{
    public MobalyticsVariantPickerWindow(
        IReadOnlyList<MobalyticsParagonVariant> variants, bool allowMultiple = false)
        : this(variants
            .Select(v => $"{v.Title} — {v.Section.Boards.Count} board(s), {v.Section.Nodes.Count} node(s)")
            .ToList(), allowMultiple)
    {
        _variants = variants;
    }

    /// <summary>Generic form: pick among labeled variants; read <see cref="SelectedIndices"/> after.</summary>
    public MobalyticsVariantPickerWindow(IReadOnlyList<string> labels, bool allowMultiple = false)
    {
        InitializeComponent();
        VariantList.ItemsSource = labels;
        VariantList.SelectedIndex = 0;
        if (allowMultiple)
        {
            VariantList.SelectionMode = SelectionMode.Extended;
            Prompt.Text = "This build guide has multiple variants — pick any number to use " +
                          "(Ctrl/Shift-click), each becomes its own reference:";
        }
    }

    private readonly IReadOnlyList<MobalyticsParagonVariant>? _variants;

    public int SelectedIndex { get; private set; } = -1;

    /// <summary>All chosen indices in list order; a single pick yields one entry.</summary>
    public IReadOnlyList<int> SelectedIndices { get; private set; } = [];

    public MobalyticsParagonVariant? Selected =>
        _variants is not null && SelectedIndex >= 0 ? _variants[SelectedIndex] : null;

    public IReadOnlyList<MobalyticsParagonVariant> SelectedVariants =>
        _variants is null ? [] : SelectedIndices.Select(i => _variants[i]).ToList();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (VariantList.SelectedIndex < 0)
            return;
        SelectedIndex = VariantList.SelectedIndex;
        // Items are plain strings and titles can repeat, so selection is read off the row
        // containers by index instead of IndexOf (which finds the first equal string).
        var indices = new List<int>();
        for (int i = 0; i < VariantList.Items.Count; i++)
        {
            if (VariantList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsSelected: true })
                indices.Add(i);
        }
        SelectedIndices = indices.Count > 0 ? indices : [VariantList.SelectedIndex];
        DialogResult = true;
    }
}
