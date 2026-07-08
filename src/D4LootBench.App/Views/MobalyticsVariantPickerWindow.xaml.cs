using System.Windows;
using D4LootBench.Paragon.Import;

namespace D4LootBench.App.Views;

/// <summary>Lets the user pick one paragon variant when a Mobalytics page contains several.</summary>
public partial class MobalyticsVariantPickerWindow : Window
{
    public MobalyticsVariantPickerWindow(IReadOnlyList<MobalyticsParagonVariant> variants)
    {
        InitializeComponent();
        VariantList.ItemsSource = variants
            .Select(v => new VariantItem(
                v, $"{v.Title} — {v.Section.Boards.Count} board(s), {v.Section.Nodes.Count} node(s)"))
            .ToList();
        VariantList.SelectedIndex = 0;
    }

    public MobalyticsParagonVariant? Selected { get; private set; }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        Selected = (VariantList.SelectedItem as VariantItem)?.Variant;
        if (Selected is not null)
            DialogResult = true;
    }

    private sealed record VariantItem(MobalyticsParagonVariant Variant, string Label);
}
