using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace D4LootBench.App.Services;

/// <summary>
/// Clipboard access that tolerates the clipboard being held by another process (clipboard
/// managers, remote-desktop sync, the snipping tool mid-write): WPF surfaces that as a
/// COMException (CLIPBRD_E_CANT_OPEN) or ExternalException, which would otherwise crash the
/// command. Each call retries briefly, then reports failure as null/false.
/// </summary>
public static class ClipboardHelper
{
    private const int Attempts = 5;
    private const int RetryDelayMs = 40;

    /// <summary>The clipboard text, or null when there is none or the clipboard is unavailable.</summary>
    public static string? TryGetText() =>
        Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>True when the text landed on the clipboard.</summary>
    public static bool TrySetText(string text) =>
        Retry(() =>
        {
            Clipboard.SetText(text);
            return true;
        });

    /// <summary>The clipboard image, or null when there is none or the clipboard is unavailable.</summary>
    public static BitmapSource? TryGetImage() =>
        Retry(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);

    private static T? Retry<T>(Func<T?> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (attempt >= Attempts)
                {
                    ErrorLog.Write(ex, "Clipboard unavailable");
                    return default;
                }
                // Short synchronous wait: the holder usually releases within a few ms, and the
                // clipboard is only reachable from the UI (STA) thread anyway.
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}
