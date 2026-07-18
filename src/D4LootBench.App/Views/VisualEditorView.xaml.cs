using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using D4LootBench.App.Services;
using D4LootBench.App.ViewModels;

namespace D4LootBench.App.Views;

public partial class VisualEditorView : UserControl
{
    private WindowSettingsService? _windowSettings;

    private Point _ruleDragStart;
    private FilterRuleViewModel? _ruleDragCandidate;

    public VisualEditorView()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        // Selection can change programmatically (issues panel jump-to-rule) — keep it visible.
        RuleList.SelectionChanged += (_, _) =>
        {
            if (RuleList.SelectedItem is not null)
                RuleList.ScrollIntoView(RuleList.SelectedItem);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowSettings = App.Services.GetRequiredService<WindowSettingsService>();
        EditorGrid.ColumnDefinitions[0].Width = new GridLength(_windowSettings.RuleListWidth);
        RuleListSplitter.DragCompleted += OnSplitterDragCompleted;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RuleListSplitter.DragCompleted -= OnSplitterDragCompleted;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_windowSettings is not null)
            _windowSettings.RuleListWidth = EditorGrid.ColumnDefinitions[0].ActualWidth;
    }

    // ── Rule list drag-to-reorder ─────────────────────────────────────────

    private void RuleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ruleDragStart = e.GetPosition(RuleList);
        // Don't start a drag from the enabled checkbox — that's a click target.
        _ruleDragCandidate = FindAncestor<CheckBox>(e.OriginalSource) is null
            ? RuleAt(e.OriginalSource)
            : null;
    }

    private void RuleList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_ruleDragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(RuleList) - _ruleDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var rule = _ruleDragCandidate;
        _ruleDragCandidate = null;
        DragDrop.DoDragDrop(RuleList, new DataObject(typeof(FilterRuleViewModel), rule), DragDropEffects.Move);
    }

    private void RuleList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(FilterRuleViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void RuleList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not VisualEditorViewModel vm) return;
        if (e.Data.GetData(typeof(FilterRuleViewModel)) is not FilterRuleViewModel dragged) return;

        var from = vm.Rules.IndexOf(dragged);
        if (from < 0) return;

        var insert = InsertionIndexAt(e.GetPosition(RuleList), vm.Rules.Count);
        vm.MoveRule(from, insert > from ? insert - 1 : insert);
        vm.SelectedRule = dragged;
        e.Handled = true;
    }

    /// <summary>Insertion index [0..count] for a drop at the given position — above or below the hovered item's midpoint.</summary>
    private int InsertionIndexAt(Point position, int count)
    {
        if (RuleList.InputHitTest(position) is not DependencyObject hit) return count;
        if (FindAncestor<ListBoxItem>(hit) is not { } item) return count;

        var index = RuleList.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) return count;

        var midpoint = item.TranslatePoint(new Point(0, item.ActualHeight / 2), RuleList).Y;
        return position.Y <= midpoint ? index : index + 1;
    }

    private FilterRuleViewModel? RuleAt(object originalSource) =>
        FindAncestor<ListBoxItem>(originalSource)?.DataContext as FilterRuleViewModel;

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null and not T)
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        return current as T;
    }
}
