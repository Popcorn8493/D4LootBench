using System.Windows;
using Microsoft.Win32;

namespace D4LootBench.App.Services;

/// <summary>
/// Message boxes, file dialogs, and owned modal windows for view models. Owner resolution lives
/// in one place: a view model passes itself as <c>ownerContext</c>, and the dialog is owned by
/// the window showing that view model — falling back to the active window, then the main window.
/// </summary>
public interface IDialogService
{
    MessageBoxResult ShowMessage(string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        object? ownerContext = null);

    /// <summary>The chosen file, or null when cancelled.</summary>
    string? ShowOpenFileDialog(string title, string filter, string? defaultExt = null,
        object? ownerContext = null);

    /// <summary>The chosen file, or null when cancelled.</summary>
    string? ShowSaveFileDialog(string title, string filter, string? defaultExt = null,
        string? fileName = null, object? ownerContext = null);

    /// <summary>Shows <paramref name="window"/> modally, owned by the resolved owner window.</summary>
    bool? ShowDialog(Window window, object? ownerContext = null);

    /// <summary>The window a dialog for <paramref name="ownerContext"/> should be owned by.</summary>
    Window? ResolveOwner(object? ownerContext = null);
}

public sealed class DialogService : IDialogService
{
    public MessageBoxResult ShowMessage(string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        object? ownerContext = null) =>
        ResolveOwner(ownerContext) is { } owner
            ? MessageBox.Show(owner, text, caption, buttons, icon)
            : MessageBox.Show(text, caption, buttons, icon);

    public string? ShowOpenFileDialog(string title, string filter, string? defaultExt = null,
        object? ownerContext = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, DefaultExt = defaultExt ?? "" };
        return dialog.ShowDialog(ResolveOwner(ownerContext)) == true ? dialog.FileName : null;
    }

    public string? ShowSaveFileDialog(string title, string filter, string? defaultExt = null,
        string? fileName = null, object? ownerContext = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExt ?? "",
            FileName = fileName ?? "",
        };
        return dialog.ShowDialog(ResolveOwner(ownerContext)) == true ? dialog.FileName : null;
    }

    public bool? ShowDialog(Window window, object? ownerContext = null)
    {
        if (window.Owner is null && ResolveOwner(ownerContext) is { } owner && !ReferenceEquals(owner, window))
            window.Owner = owner;
        return window.ShowDialog();
    }

    public Window? ResolveOwner(object? ownerContext = null)
    {
        var app = Application.Current;
        if (app is null)
            return null;
        var windows = app.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        if (ownerContext is not null
            && windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, ownerContext)) is { } hosting)
            return hosting;
        return windows.FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }
}
