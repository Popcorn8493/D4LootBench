using System.Windows;

namespace D4LootBench.App.Services;

/// <summary>
/// App-wide light/dark theming: WPF's Fluent theme restyles every standard control
/// (<see cref="Application.ThemeMode"/>), and the semantic chrome brushes in
/// Themes/Light.xaml / Themes/Dark.xaml swap alongside so app-specific borders, muted text,
/// and hover tints follow. Board-canvas colors are game-styled and never change.
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;
        IsDark = dark;

        // Swap OUR brush dictionary first. The match must be exact: WPF injects its Fluent
        // dictionary as "…PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml" —
        // a loose "Themes/" match would replace (and thereby disable) Fluent itself.
        var dictionaries = app.Resources.MergedDictionaries;
        var previous = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString is "Themes/Light.xaml" or "Themes/Dark.xaml");
        var theme = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        if (previous is not null)
        {
            int index = dictionaries.IndexOf(previous);
            dictionaries[index] = theme;
        }
        else
        {
            dictionaries.Add(theme);
        }

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
    }
}
