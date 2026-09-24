using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using D4LootBench.App.Services;

namespace D4LootBench.App.Views.Help;

public partial class CustomizingDataTopic : UserControl
{
    private readonly Func<Task> _extractAction;

    public CustomizingDataTopic(Func<Task> extractAction)
    {
        _extractAction = extractAction;
        InitializeComponent();
    }

    private async void OnExtractClick(object sender, RoutedEventArgs e)
    {
        ExtractButton.IsEnabled = false;
        try
        {
            await _extractAction();
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would reach the dispatcher as a crash.
            ErrorLog.Write(ex, "Extracting game data");
            string message = $"Extracting the game data failed:\n\n{ex.Message}";
            if (Window.GetWindow(this) is { } owner)
                MessageBox.Show(owner, message, "Extract Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(message, "Extract Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
        }
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
