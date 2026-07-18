using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using D4LootBench.Core.Validation;

namespace D4LootBench.App.Views;

public partial class IssuesPanel : UserControl
{
    public static readonly DependencyProperty IssuesProperty =
        DependencyProperty.Register(
            nameof(Issues),
            typeof(IEnumerable),
            typeof(IssuesPanel),
            new PropertyMetadata(null));

    public IEnumerable? Issues
    {
        get => (IEnumerable?)GetValue(IssuesProperty);
        set => SetValue(IssuesProperty, value);
    }

    /// <summary>Raised when the user clicks an issue that points at a rule.</summary>
    public event Action<ValidationIssue>? IssueActivated;

    public IssuesPanel()
    {
        InitializeComponent();
    }

    private void IssueRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ValidationIssue { RuleIndex: not null } issue)
            IssueActivated?.Invoke(issue);
    }
}
