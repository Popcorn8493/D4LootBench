using System.ComponentModel;
using System.Windows;
using D4LootBench.App.Services;
using D4LootBench.App.ViewModels;
using D4LootBench.App.Views;

namespace D4LootBench.App;

public partial class MainWindow
{
    private readonly MainWindowViewModel  _vm;
    private readonly WindowSettingsService _windowSettings;
    private double _savedPanelHeight = 220;
    private HelpWindow? _helpWindow;
    private ParagonPlannerWindow? _paragonWindow;

    public MainWindow(MainWindowViewModel vm, WindowSettingsService windowSettings)
    {
        InitializeComponent();
        _vm             = vm;
        _windowSettings = windowSettings;
        DataContext     = _vm;
        _vm.ShowRawEditorRequested += OnShowRawEditorRequested;
        _vm.ShowParagonPlannerRequested += OnShowParagonPlannerRequested;
        _vm.OpenHelpRequested     += OnOpenHelpRequested;
        _vm.ShowAboutRequested    += OnShowAboutRequested;
        _vm.PropertyChanged       += OnVmPropertyChanged;

        RestoreWindowSettings();
    }

    private void RestoreWindowSettings()
    {
        _savedPanelHeight = _windowSettings.AiPanelHeight;
        Width             = _windowSettings.Width;
        Height            = _windowSettings.Height;

        if (_windowSettings.Top  is { } top)  Top  = top;
        if (_windowSettings.Left is { } left) Left = left;

        WindowState = _windowSettings.State;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var panelRow = ContentGrid.RowDefinitions[2];
        if (panelRow.ActualHeight > 0)
            _savedPanelHeight = panelRow.ActualHeight;

        _windowSettings.AiPanelHeight = _savedPanelHeight;
        _windowSettings.Save(this);
        base.OnClosing(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsAiPanelVisible))
            ApplyAiPanelLayout(_vm.IsAiPanelVisible);
    }

    private void ApplyAiPanelLayout(bool visible)
    {
        var splitterRow = ContentGrid.RowDefinitions[1];
        var panelRow    = ContentGrid.RowDefinitions[2];

        if (visible)
        {
            splitterRow.Height    = new GridLength(4);
            panelRow.MinHeight    = 140;
            panelRow.Height       = new GridLength(_savedPanelHeight);
            AiSplitter.Visibility = Visibility.Visible;
            AiPanel.Visibility    = Visibility.Visible;
        }
        else
        {
            // Preserve whatever height the user last dragged to
            if (panelRow.ActualHeight > 0)
                _savedPanelHeight = panelRow.ActualHeight;

            splitterRow.Height    = new GridLength(0);
            panelRow.MinHeight    = 0;
            panelRow.Height       = new GridLength(0);
            AiSplitter.Visibility = Visibility.Collapsed;
            AiPanel.Visibility    = Visibility.Collapsed;
        }
    }

    private void OnShowRawEditorRequested(RawEditorViewModel vm)
    {
        var window = new RawEditorWindow
        {
            DataContext = vm,
            Owner = this
        };
        window.Show();
    }

    private void OnShowParagonPlannerRequested()
    {
        if (_paragonWindow is null || !_paragonWindow.IsLoaded)
        {
            _paragonWindow = new ParagonPlannerWindow
            {
                DataContext = new ParagonPlannerViewModel(),
                Owner = this,
            };
            _paragonWindow.Closed += (_, _) => _paragonWindow = null;
            _paragonWindow.Show();
        }
        else
        {
            _paragonWindow.Activate();
        }
    }

    private void OnOpenHelpRequested(string topic)
    {
        if (_helpWindow is null || !_helpWindow.IsLoaded)
        {
            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
        }
        else
        {
            _helpWindow.Activate();
        }
        _helpWindow.NavigateTo(topic);
    }

    private void OnShowAboutRequested()
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }
}
