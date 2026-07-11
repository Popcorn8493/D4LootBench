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

    /// <summary>Drops the Recent-projects menu below its button on left-click.</summary>
    private void RecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Zooms so the whole layout fits the visible board area (clamped to the slider range).</summary>
    private void FitZoom_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ParagonPlannerViewModel viewModel
            || viewModel.CanvasWidth <= 0 || viewModel.CanvasHeight <= 0)
            return;
        // The ScrollViewer pads the canvas by 20 on each side.
        double fit = Math.Min(
            (BoardScroll.ViewportWidth - 40) / viewModel.CanvasWidth,
            (BoardScroll.ViewportHeight - 40) / viewModel.CanvasHeight);
        viewModel.Zoom = Math.Clamp(fit, 0.4, 2.0);
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
