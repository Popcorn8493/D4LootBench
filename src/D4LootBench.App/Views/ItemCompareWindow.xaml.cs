using System.Windows;
using D4LootBench.App.ViewModels;

namespace D4LootBench.App.Views;

public partial class ItemCompareWindow : Window
{
    public ItemCompareWindow(ItemCompareViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
