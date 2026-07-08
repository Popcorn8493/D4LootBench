using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using D4LootBench.App.ViewModels.Conditions;

namespace D4LootBench.App.Views;

public partial class ItemPickerControl
{
    /// <summary>In-process drag payload: entries dragged from one picker list to another.</summary>
    private sealed record PickerDragPayload(PickerViewModel Source, IReadOnlyList<PickerEntry> Items, bool FromSelected);

    private Point _dragStart;
    private ListBox? _dragList;

    public ItemPickerControl() => InitializeComponent();

    private void AvailableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PickerViewModel vm)
            vm.SyncAvailableSelection(AvailableList.SelectedItems);
    }

    private void SelectedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PickerViewModel vm)
            vm.SyncCurrentSelection(SelectedList.SelectedItems);
    }

    private void AddButton_Click(object _, RoutedEventArgs _1)
    {
        if (DataContext is not PickerViewModel vm) return;
        var items = AvailableList.SelectedItems.OfType<PickerEntry>().ToList();
        if (items.Count > 0) vm.AddItems(items);
    }

    private void RemoveButton_Click(object _, RoutedEventArgs _1)
    {
        if (DataContext is not PickerViewModel vm) return;
        var items = SelectedList.SelectedItems.OfType<PickerEntry>().ToList();
        if (items.Count > 0) vm.RemoveItems(items);
    }

    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PickerViewModel vm) return;
        if (e.OriginalSource is FrameworkElement { DataContext: PickerEntry item })
            vm.AddItems([item]);
    }

    private void SelectedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PickerViewModel vm) return;
        if (e.OriginalSource is FrameworkElement { DataContext: PickerEntry item })
            vm.RemoveItems([item]);
    }

    // ── Drag & drop ───────────────────────────────────────────────────────
    // Entries can be dragged from either list into any picker's Selected list:
    // Available → Selected adds; Selected → another picker's Selected moves the
    // entry (copies when either picker is non-owning, e.g. the greater-affix
    // picker); Selected → own Available removes.

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragList  = e.OriginalSource is FrameworkElement { DataContext: PickerEntry } ? (ListBox)sender : null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragList is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (DataContext is not PickerViewModel vm) return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var list  = _dragList;
        _dragList = null;

        var items = list.SelectedItems.OfType<PickerEntry>().ToList();
        if (items.Count == 0) return;

        var payload = new PickerDragPayload(vm, items, FromSelected: ReferenceEquals(list, SelectedList));
        DragDrop.DoDragDrop(list, new DataObject(typeof(PickerDragPayload), payload), DragDropEffects.Move);
    }

    private void SelectedList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is PickerViewModel vm && GetPayload(e) is { } p &&
                    !(p.FromSelected && ReferenceEquals(p.Source, vm)) &&
                    p.Items.Any(vm.CanAccept)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SelectedList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not PickerViewModel target || GetPayload(e) is not { } p) return;
        if (p.FromSelected && ReferenceEquals(p.Source, target)) return;

        var accepted = p.Items.Where(target.CanAccept).ToList();
        if (accepted.Count == 0) return;
        target.AddItems(accepted);

        // Cross-picker Selected → Selected is a move unless either side merely flags
        // entries owned elsewhere (greater-affix picker).
        if (p.FromSelected && !ReferenceEquals(p.Source, target) && p.Source.OwnsEntries && target.OwnsEntries)
            p.Source.RemoveItems(accepted);

        e.Handled = true;
    }

    private void AvailableList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is PickerViewModel vm && GetPayload(e) is { } p &&
                    p.FromSelected && ReferenceEquals(p.Source, vm)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AvailableList_Drop(object sender, DragEventArgs e)
    {
        // Dropping own selected entries back onto Available = remove (same as ←).
        if (DataContext is not PickerViewModel vm || GetPayload(e) is not { } p) return;
        if (!p.FromSelected || !ReferenceEquals(p.Source, vm)) return;
        vm.RemoveItems(p.Items);
        e.Handled = true;
    }

    private static PickerDragPayload? GetPayload(DragEventArgs e) =>
        e.Data.GetData(typeof(PickerDragPayload)) as PickerDragPayload;
}
