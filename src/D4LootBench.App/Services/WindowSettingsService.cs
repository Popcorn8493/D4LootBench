using System.Text.Json.Serialization;
using System.Windows;

namespace D4LootBench.App.Services;

public sealed class WindowSettingsService
{
    private readonly JsonFileStore<StoredSettings> _store;

    public WindowState State         { get; private set; } = WindowState.Normal;
    public double      Width         { get; private set; } = 1100;
    public double      Height        { get; private set; } = 700;
    public double?     Top           { get; private set; }
    public double?     Left          { get; private set; }
    public double      AiPanelHeight { get; set; } = 220;
    public double      RuleListWidth { get; set; } = 320;
    public bool        DarkMode      { get; set; }

    public WindowSettingsService(string? path = null)
    {
        _store = new JsonFileStore<StoredSettings>(path ?? JsonFileStore.AppDataPath("window-settings.json"));
        Load();
    }

    /// <summary>Persists the window's placement and the panel settings. Never throws — it runs
    /// from OnClosing, where an exception would abort shutdown.</summary>
    public void Save(Window window)
    {
        try
        {
            SaveCore(window);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Saving window settings");
        }
    }

    private void SaveCore(Window window)
    {
        var state  = window.WindowState;
        var bounds = state == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        // A window saved before it is measured (or with RestoreBounds still Rect.Empty) yields
        // NaN/infinite values, which JSON cannot store — fall back to the last known-good ones.
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Left))
        {
            bounds = new Rect(Left ?? 0, Top ?? 0, Width, Height);
        }

        var stored = new StoredSettings(
            state == WindowState.Minimized ? WindowState.Normal : state,
            bounds.Width,
            bounds.Height,
            bounds.Top,
            bounds.Left,
            AiPanelHeight,
            RuleListWidth,
            DarkMode);

        _store.Save(stored);
    }

    private void Load()
    {
        // A corrupt file is moved aside by the store — use defaults.
        if (_store.Load() is not { } stored)
            return;

        State         = stored.State;
        Width         = stored.Width         > 0 ? stored.Width         : Width;
        Height        = stored.Height        > 0 ? stored.Height        : Height;
        Top           = stored.Top;
        Left          = stored.Left;
        AiPanelHeight = stored.AiPanelHeight > 0 ? stored.AiPanelHeight : AiPanelHeight;
        RuleListWidth = stored.RuleListWidth > 0  ? stored.RuleListWidth : RuleListWidth;
        DarkMode      = stored.DarkMode;
    }

    private sealed record StoredSettings(
        [property: JsonPropertyName("state")]         WindowState State,
        [property: JsonPropertyName("width")]         double      Width,
        [property: JsonPropertyName("height")]        double      Height,
        [property: JsonPropertyName("top")]           double?     Top,
        [property: JsonPropertyName("left")]          double?     Left,
        [property: JsonPropertyName("aiPanelHeight")] double      AiPanelHeight,
        [property: JsonPropertyName("ruleListWidth")] double      RuleListWidth,
        [property: JsonPropertyName("darkMode")]      bool        DarkMode = false);
}
