using System.Windows;
using System.Windows.Input;
using D4LootBench.App.ViewModels;

namespace D4LootBench.App.Views;

public partial class ParagonPlannerWindow : Window
{
    public ParagonPlannerWindow()
    {
        InitializeComponent();
    }

    private void Cell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParagonCellViewModel cell } &&
            DataContext is ParagonPlannerViewModel viewModel)
        {
            viewModel.CycleCellConstraintCommand.Execute(cell);
            e.Handled = true;
        }
    }
}
