using System.Windows;
using D4LootBench.Paragon.Import;

namespace D4LootBench.App.Views;

/// <summary>Lets the user pick one paragon variant when a build contains several.</summary>
public partial class MobalyticsVariantPickerWindow : Window
{
    public MobalyticsVariantPickerWindow(IReadOnlyList<MobalyticsParagonVariant> variants)
        : this(variants
            .Select(v => $"{v.Title} — {v.Section.Boards.Count} board(s), {v.Section.Nodes.Count} node(s)")
            .ToList())
    {
        _variants = variants;
    }

    /// <summary>Generic form: pick among labeled variants; read <see cref="SelectedIndex"/> after.</summary>
    public MobalyticsVariantPickerWindow(IReadOnlyList<string> labels)
    {
        InitializeComponent();
        VariantList.ItemsSource = labels;
        VariantList.SelectedIndex = 0;
    }

    private readonly IReadOnlyList<MobalyticsParagonVariant>? _variants;

    public int SelectedIndex { get; private set; } = -1;

    public MobalyticsParagonVariant? Selected =>
        _variants is not null && SelectedIndex >= 0 ? _variants[SelectedIndex] : null;

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (VariantList.SelectedIndex < 0)
            return;
        SelectedIndex = VariantList.SelectedIndex;
        DialogResult = true;
    }
}
