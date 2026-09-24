using System.Windows;
using System.Windows.Input;
using D4LootBench.App.ViewModels;

namespace D4LootBench.App.Views;

public partial class ParagonPlannerWindow : Window
{
    public ParagonPlannerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ParagonPlannerViewModel oldViewModel)
                oldViewModel.LayoutReplaced -= OnLayoutReplaced;
            if (e.NewValue is ParagonPlannerViewModel viewModel)
                viewModel.LayoutReplaced += OnLayoutReplaced;
        };
    }

    /// <summary>
    /// An open/import/plan just swapped the whole layout — refit the zoom so the user sees all
    /// of it instead of a corner. Deferred until WPF has measured the new canvas. Never magnifies
    /// past 100%: small layouts at actual size beat a surprise close-up.
    /// </summary>
    private void OnLayoutReplaced(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => FitToView(maxZoom: 1.0),
            System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Keyboard shortcuts run on the tunneling pass so no focused child (board button, text box,
    /// combo, scroll viewer) can swallow them — Window.InputBindings only fire when the KeyDown
    /// bubbles back to the window unhandled, which users reported failing for Ctrl+S.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ParagonPlannerViewModel viewModel)
            return;
        if (viewModel.IsBusy)
        {
            // While busy, Esc is the only shortcut: it cancels a cancellable search.
            if (e.Key == Key.Escape && viewModel.CanCancelBusy)
            {
                viewModel.CancelBusyCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        // A focused text box keeps its own editing shortcuts (its local Ctrl+Z undo stack).
        bool inTextBox = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        ICommand? command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.S, ModifierKeys.Control) => viewModel.SaveProjectCommand,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => viewModel.SaveProjectAsCommand,
            (Key.O, ModifierKeys.Control) => viewModel.OpenProjectCommand,
            (Key.I, ModifierKeys.Control) => viewModel.ImportBuildCommand,
            (Key.Z, ModifierKeys.Control) when !inTextBox => viewModel.UndoCommand,
            (Key.Y, ModifierKeys.Control) when !inTextBox => viewModel.RedoCommand,
            (Key.Z, ModifierKeys.Control | ModifierKeys.Shift) when !inTextBox => viewModel.RedoCommand,
            (Key.F5, ModifierKeys.None) => viewModel.SolveCommand,
            (Key.F6, ModifierKeys.None) => viewModel.ReanalyzeCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null))
            return;
        e.Handled = true;
        command.Execute(null);
    }

    /// <summary>
    /// Lazy node tooltips: the template carries a placeholder, and the real text is built only
    /// when a tooltip actually opens — instead of formatting (and re-formatting on every summary
    /// refresh) a description for each of the hundreds of cells on the canvas.
    /// </summary>
    private void Cell_ToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ParagonCellViewModel cell } element)
            return;
        string text = cell.ToolTipText;
        if (text.Length == 0)
        {
            e.Handled = true; // nothing to show
            return;
        }
        element.ToolTip = text;
    }

    /// <summary>Right-click on a node: an explicit menu instead of blind state cycling.</summary>
    private void Cell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ParagonCellViewModel cell } element
            || DataContext is not ParagonPlannerViewModel viewModel || cell.IsStart)
            return;
        e.Handled = true;

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = element };
        void Add(string header, bool isChecked, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsChecked = isChecked };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add(cell.IsTarget ? "Remove target" : "Mark as target", cell.IsTarget,
            () => viewModel.ToggleTargetCommand.Execute(cell));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Avoid — cross only when it saves several nodes", cell.Constraint == CellConstraint.Avoid,
            () => viewModel.SetCellConstraint(cell, CellConstraint.Avoid));
        Add("Off-limits — never path through or buy", cell.Constraint == CellConstraint.Exclude,
            () => viewModel.SetCellConstraint(cell, CellConstraint.Exclude));
        if (cell.Constraint != CellConstraint.None)
            Add("Clear mark", false, () => viewModel.SetCellConstraint(cell, CellConstraint.None));

        menu.IsOpen = true;
    }

    /// <summary>Zooms so the whole layout fits the visible board area (clamped to the slider range).</summary>
    private void FitZoom_Click(object sender, RoutedEventArgs e) => FitToView(maxZoom: 2.0);

    private void FitToView(double maxZoom)
    {
        if (DataContext is not ParagonPlannerViewModel viewModel
            || viewModel.CanvasWidth <= 0 || viewModel.CanvasHeight <= 0
            || BoardScroll.ViewportWidth <= 40 || BoardScroll.ViewportHeight <= 40)
            return;
        // The ScrollViewer pads the canvas by 20 on each side.
        double fit = Math.Min(
            (BoardScroll.ViewportWidth - 40) / viewModel.CanvasWidth,
            (BoardScroll.ViewportHeight - 40) / viewModel.CanvasHeight);
        viewModel.Zoom = Math.Clamp(fit, 0.4, maxZoom);
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ParagonPlannerViewModel viewModel)
            viewModel.Zoom = 1.0;
    }

    private void Board_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer scroll)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && DataContext is ParagonPlannerViewModel viewModel)
        {
            double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            viewModel.Zoom = Math.Clamp(viewModel.Zoom * factor, 0.4, 2.0);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // ── Middle-drag panning of the board canvas ───────────────────────────

    private Point? _panStart;
    private (double H, double V) _panOrigin;

    private void Board_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not System.Windows.Controls.ScrollViewer scroll)
            return;
        _panStart = e.GetPosition(scroll);
        _panOrigin = (scroll.HorizontalOffset, scroll.VerticalOffset);
        scroll.CaptureMouse();
        scroll.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Board_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not Point start || sender is not System.Windows.Controls.ScrollViewer scroll)
            return;
        var position = e.GetPosition(scroll);
        scroll.ScrollToHorizontalOffset(_panOrigin.H - (position.X - start.X));
        scroll.ScrollToVerticalOffset(_panOrigin.V - (position.Y - start.Y));
    }

    private void Board_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not System.Windows.Controls.ScrollViewer scroll)
            return;
        _panStart = null;
        scroll.ReleaseMouseCapture();
        scroll.Cursor = null;
    }

    /// <summary>
    /// Nested scroll areas swallow the wheel even when they can't scroll further — bubble the
    /// event to the surrounding panel scroll so the wheel never feels stuck.
    /// </summary>
    private void InnerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer scroll)
            return;
        bool atTop = e.Delta > 0 && scroll.VerticalOffset <= 0;
        bool atBottom = e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight;
        if (!atTop && !atBottom)
            return;
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        if (scroll.Parent is UIElement parent)
            parent.RaiseEvent(forwarded);
    }
}
