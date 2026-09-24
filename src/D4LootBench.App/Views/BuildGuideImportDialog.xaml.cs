using System.Windows;
using D4LootBench.App.ViewModels;
using D4LootBench.Ai.Import;
using D4LootBench.Core.Import;

namespace D4LootBench.App.Views;

public partial class BuildGuideImportDialog : Window
{
    public BuildGuideImportViewModel Vm { get; }

    public BuildGuideImportDialog(BuildGuideImporter importer, BuildGuideFilterGenerator generator)
    {
        InitializeComponent();
        Vm = new BuildGuideImportViewModel(importer, generator);
        Vm.PickOption = labels =>
        {
            var picker = new MobalyticsVariantPickerWindow(labels) { Owner = this };
            return picker.ShowDialog() == true ? picker.SelectedIndex : -1;
        };
        DataContext = Vm;
        Vm.ImportSucceeded += () => DialogResult = true;
        Closed += (_, _) => Vm.CancelPendingFetch();
    }
}
