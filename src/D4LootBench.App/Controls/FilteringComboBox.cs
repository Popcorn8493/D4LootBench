using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace D4LootBench.App.Controls;

/// <summary>
/// An editable ComboBox that filters its dropdown as you type: every whitespace-separated token
/// must appear somewhere in the item's display text, case-insensitively — so "crit chance" finds
/// "+Critical Strike Chance" despite the leading '+' that defeats WPF's prefix-only TextSearch.
/// Enter commits the first visible item; closing the dropdown without a pick restores the last
/// committed selection (clearing the text clears the selection). The bound ItemsSource is wrapped
/// in a private view so several boxes can share one options list without fighting over a filter.
/// </summary>
public sealed class FilteringComboBox : ComboBox
{
    private TextBox? _editBox;
    private object? _lastCommitted;
    /// <summary>What the user last typed; null once a pick supersedes it. Committing reads this
    /// rather than the edit box — closing the dropdown with no selection wipes the box first.</summary>
    private string? _typedText;
    private bool _syncing;
    private bool _wrapping;
    private PropertyInfo? _displayProperty;

    public FilteringComboBox()
    {
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_editBox is not null)
            _editBox.TextChanged -= OnEditTextChanged;
        _editBox = GetTemplateChild("PART_EditableTextBox") as TextBox;
        if (_editBox is not null)
            _editBox.TextChanged += OnEditTextChanged;
    }

    /// <summary>Each box needs its own ICollectionView — filtering the default view of a shared
    /// options list would filter (and deselect) every sibling bound to the same list.</summary>
    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        if (_wrapping || newValue is null or ICollectionView)
            return;
        _wrapping = true;
        try
        {
            ItemsSource = new ListCollectionView(newValue as IList ?? newValue.Cast<object>().ToList());
        }
        finally
        {
            _wrapping = false;
        }
    }

    private void OnEditTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !IsKeyboardFocusWithin)
            return;
        string text = _editBox!.Text;
        if (SelectedItem is not null && string.Equals(DisplayTextOf(SelectedItem), text, StringComparison.Ordinal))
            return; // the box is just echoing a pick

        _typedText = text;
        _syncing = true;
        try
        {
            // Filtering out the current selection clears it, which wipes the edit text
            // mid-typing — put the typed text back if that happened.
            ApplyFilter(text);
            if (!string.Equals(_editBox.Text, text, StringComparison.Ordinal))
                _editBox.Text = text;

            if (!IsDropDownOpen && text.Length > 0)
                IsDropDownOpen = true;
            // Opening (or restoring) selects the whole text; put the caret back at the end.
            _editBox.SelectionStart = _editBox.Text.Length;
            _editBox.SelectionLength = 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (SelectedItem is not null)
        {
            _lastCommitted = SelectedItem;
            _typedText = null; // a pick supersedes whatever was typed
        }
        // The ComboBox writes the pick's text into the edit box right after this; don't
        // mistake that echo for typing and re-filter the (now closing) dropdown.
        _syncing = true;
        Dispatcher.BeginInvoke(() => _syncing = false);
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        CommitText();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsDropDownOpen && !IsKeyboardFocusWithin)
            CommitText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IsDropDownOpen && SelectedItem is null)
        {
            var first = Items.Cast<object>().FirstOrDefault();
            if (first is not null)
            {
                SelectedItem = first;
                IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void CommitText()
    {
        string text = (_typedText ?? _editBox?.Text ?? "").Trim();
        _typedText = null;
        ClearFilter();

        _syncing = true;
        try
        {
            if (text.Length == 0)
            {
                _lastCommitted = null;
                SelectedItem = null;
                if (_editBox is not null)
                    _editBox.Text = "";
                return;
            }

            var exact = Items.Cast<object>().FirstOrDefault(o =>
                string.Equals(DisplayTextOf(o), text, StringComparison.OrdinalIgnoreCase));
            var tokens = Tokenize(text);
            var matches = Items.Cast<object>().Where(o => Matches(DisplayTextOf(o), tokens)).Take(2).ToList();
            object? pick = exact ?? (matches.Count == 1 ? matches[0] : null) ?? _lastCommitted;

            SelectedItem = pick;
            if (_editBox is not null)
                _editBox.Text = pick is null ? "" : DisplayTextOf(pick);
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _syncing = false);
        }
    }

    private void ApplyFilter(string text)
    {
        var tokens = Tokenize(text);
        Items.Filter = tokens.Length == 0 ? null : o => Matches(DisplayTextOf(o), tokens);
    }

    private void ClearFilter()
    {
        if (Items.Filter is not null)
            Items.Filter = null;
    }

    private static string[] Tokenize(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string display, string[] tokens) =>
        tokens.All(t => display.Contains(t, StringComparison.OrdinalIgnoreCase));

    private string DisplayTextOf(object item)
    {
        string path = DisplayMemberPath;
        if (string.IsNullOrEmpty(path))
            return item.ToString() ?? "";
        if (_displayProperty is null || _displayProperty.DeclaringType?.IsInstanceOfType(item) != true)
            _displayProperty = item.GetType().GetProperty(path);
        return _displayProperty?.GetValue(item)?.ToString() ?? item.ToString() ?? "";
    }
}
