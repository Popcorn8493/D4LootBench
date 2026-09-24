using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace D4LootBench.App.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = DisplayVersion(Assembly.GetExecutingAssembly());
    }

    /// <summary>
    /// Release builds get <c>-p:Version=&lt;tag&gt;</c> (release.yml); local builds carry the
    /// Directory.Build.props default <c>0.0.0-dev</c>. InformationalVersion keeps prerelease
    /// labels but the SDK appends <c>+&lt;commit sha&gt;</c>, which is stripped for display.
    /// </summary>
    private static string DisplayVersion(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = assembly.GetName().Version?.ToString(3);
        if (string.IsNullOrWhiteSpace(info))
            return "Development Build";

        var plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        return info.StartsWith("0.0.0", StringComparison.Ordinal) ? "Development Build" : $"v{info}";
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
