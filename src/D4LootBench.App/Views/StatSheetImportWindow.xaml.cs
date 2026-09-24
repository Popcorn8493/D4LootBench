using System.Windows;
using D4LootBench.App.ViewModels;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.Views;

/// <summary>
/// Review dialog of the stat-sheet scan — the merge/recompute logic lives in
/// <see cref="StatSheetImportViewModel"/>; this only hosts it and closes on Apply.
/// </summary>
public partial class StatSheetImportWindow : Window
{
    public StatSheetImportViewModel ViewModel { get; }

    /// <summary>The Additive dmg % field value (percent), valid when DialogResult is true.</summary>
    public double AdditiveResult => ViewModel.AdditiveResult;

    /// <summary>The Situational dmg % field value (percent), valid when DialogResult is true.</summary>
    public double SituationalResult => ViewModel.SituationalResult;

    /// <param name="paragonAlwaysOnPercent">The planner build's always-on paragon additive
    /// (percent, situational slice excluded).</param>
    /// <param name="paragonSituationalPercent">Its situational paragon additive (percent).</param>
    public StatSheetImportWindow(
        IReadOnlyList<SheetStatLine> lines,
        double paragonAlwaysOnPercent,
        double paragonSituationalPercent)
    {
        InitializeComponent();
        ViewModel = new StatSheetImportViewModel(lines, paragonAlwaysOnPercent, paragonSituationalPercent);
        DataContext = ViewModel;
    }

    private void OnAddRow(object sender, RoutedEventArgs e) => RowList.ScrollIntoView(ViewModel.AddRow());

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
