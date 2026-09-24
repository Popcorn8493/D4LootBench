using System.Windows;
using System.Windows.Controls;
using D4LootBench.App.Services;
using D4LootBench.App.Views.Help;
using Microsoft.Extensions.DependencyInjection;

namespace D4LootBench.App.Views;

public partial class HelpWindow : Window
{
    private readonly Dictionary<string, Lazy<UserControl>> _topics;
    private bool _navigating;

    public HelpWindow()
    {
        InitializeComponent();
        _topics = new Dictionary<string, Lazy<UserControl>>
        {
            ["GettingStarted"]      = new(() => new GettingStartedTopic()),
            ["BuildGuideImport"]    = new(() => new BuildGuideImportTopic()),
            ["AiSetup"]             = new(() => new AiSetupTopic()),
            ["CustomizingData"] = new(() => new CustomizingDataTopic(
                                      async () => await GameDataHelper.ExtractAsync(
                                          App.Services.GetRequiredService<IDialogService>()))),
            ["Troubleshooting"] = new(() => new TroubleshootingTopic()),
            ["FilterRules"]     = new(() => new FilterRulesTopic()),
            ["Attribution"]     = new(() => new AttributionTopic()),
        };
        NavigateTo("GettingStarted");
    }

    public void NavigateTo(string topicKey)
    {
        if (!_topics.TryGetValue(topicKey, out var lazy)) return;

        _navigating = true;
        try
        {
            TopicContent.Content = lazy.Value;

            // Sync sidebar selection
            foreach (ListBoxItem item in TopicList.Items)
            {
                if (item.Tag as string == topicKey)
                {
                    TopicList.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        if (TopicList.SelectedItem is ListBoxItem { Tag: string key })
            NavigateTo(key);
    }
}
