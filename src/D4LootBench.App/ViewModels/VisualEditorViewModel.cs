using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.ViewModels.Conditions;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;
using D4LootBench.Core.Validation;

namespace D4LootBench.App.ViewModels;

public partial class VisualEditorViewModel : ObservableObject
{
    private readonly IConditionViewModelFactory _conditionFactory;

    public ObservableCollection<FilterRuleViewModel> Rules { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private FilterRuleViewModel? _selectedRule;

    [ObservableProperty]
    private string _filterName = null!;

    [ObservableProperty]
    private PlayerClass _selectedClass = PlayerClass.All;

    public static IReadOnlyList<PlayerClass> PlayerClasses { get; } = Enum.GetValues<PlayerClass>();

    public string RuleCountDisplay => $"{Rules.Count} / {FilterRuleset.MaxRuleCount}";

    public VisualEditorViewModel(IConditionViewModelFactory conditionFactory, FilterRuleset ruleset)
    {
        _conditionFactory = conditionFactory;
        _filterName       = ruleset.Name;
        foreach (var rule in ruleset.Rules)
            Rules.Add(MakeRuleVm(rule));
        Rules.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(RuleCountDisplay));
            OnPropertyChanged(nameof(IsAtRuleLimit));
            AddRuleCommand.NotifyCanExecuteChanged();
            DuplicateRuleCommand.NotifyCanExecuteChanged();
        };
    }

    public bool IsAtRuleLimit => Rules.Count >= FilterRuleset.MaxRuleCount;

    public FilterRuleset BuildRuleset() =>
        new(FilterName, Rules.Select(r => r.BuildRule()));

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private void AddRule()
    {
        var vm = MakeRuleVm(new FilterRule($"Rule #{Rules.Count + 1}", Visibility.Show, FilterColors.GameDefault, []));
        Rules.Add(vm);
        SelectedRule = vm;
    }

    private bool CanAddRule() => Rules.Count < FilterRuleset.MaxRuleCount;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteRule()
    {
        if (SelectedRule is null) return;
        var idx = Rules.IndexOf(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = Rules.Count > 0 ? Rules[Math.Min(idx, Rules.Count - 1)] : null;
    }

    [RelayCommand(CanExecute = nameof(CanDuplicateRule))]
    private void DuplicateRule()
    {
        if (SelectedRule is null) return;
        var copy = SelectedRule.BuildRule();
        copy.Name = MakeCopyName(copy.Name);
        var vm = MakeRuleVm(copy);
        Rules.Insert(Rules.IndexOf(SelectedRule) + 1, vm);
        SelectedRule = vm;
    }

    private bool CanDuplicateRule() => SelectedRule is not null && Rules.Count < FilterRuleset.MaxRuleCount;

    private static string MakeCopyName(string name)
    {
        const string suffix = " copy";
        return name.Length + suffix.Length <= FilterValidator.MaxRuleNameLength
            ? name + suffix
            : name[..(FilterValidator.MaxRuleNameLength - suffix.Length)] + suffix;
    }

    /// <summary>
    /// Removes rules that duplicate an earlier rule (same visibility and conditions),
    /// after user confirmation.
    /// </summary>
    [RelayCommand]
    private void RemoveDuplicateRules()
    {
        var duplicates = RedundancyAnalyzer.FindDuplicateRules(BuildRuleset());
        if (duplicates.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No duplicate rules found.", "Clean Up",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var lines = duplicates.Select(d =>
            $"• \"{Rules[d.Index].Name}\" (duplicate of \"{Rules[d.DuplicateOfIndex].Name}\")");
        var confirm = System.Windows.MessageBox.Show(
            $"Remove {duplicates.Count} duplicate rule(s)?\n\n{string.Join("\n", lines)}",
            "Clean Up",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        foreach (var d in duplicates.OrderByDescending(d => d.Index))
            Rules.RemoveAt(d.Index);

        if (SelectedRule is not null && !Rules.Contains(SelectedRule))
            SelectedRule = Rules.FirstOrDefault();
    }

    /// <summary>
    /// Combines groups of rules that differ only in one item type / unique / any-of affix
    /// list (the merge suggestions Validate reports), after user confirmation. Each group
    /// collapses into its first rule with the lists combined.
    /// </summary>
    [RelayCommand]
    private void CombineRules()
    {
        var candidates = RedundancyAnalyzer.Analyze(BuildRuleset()).MergeCandidates;
        if (candidates.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No combinable rules found.\n\nRules can be combined when they are identical except for one item type, unique item, or any-of affix list.",
                "Combine Rules",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var lines = candidates.Select(c =>
            $"• {string.Join(" + ", c.Indices.Select(i => $"\"{Rules[i].Name}\""))} → one rule with the lists combined");
        var freed = candidates.Sum(c => c.Indices.Count - 1);
        var confirm = System.Windows.MessageBox.Show(
            $"Combine {candidates.Count} group(s) of rules, freeing {freed} rule slot(s)?\n\n{string.Join("\n", lines)}",
            "Combine Rules",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        // Replace each group's first rule in place (index-stable), then remove the rest.
        var mergedVms = candidates.Select(c =>
        {
            var vm = MakeRuleVm(c.MergedRule);
            Rules[c.Indices[0]] = vm;
            return vm;
        }).ToList();
        foreach (var index in candidates.SelectMany(c => c.Indices.Skip(1)).OrderByDescending(i => i))
            Rules.RemoveAt(index);

        SelectedRule = mergedVms.FirstOrDefault();
    }

    /// <summary>Selects the rule at the given index — used by the issues panel to jump to a finding.
    /// Indices from stale validation results (rules edited since) are ignored when out of range.</summary>
    public void SelectRuleAt(int index)
    {
        if (index >= 0 && index < Rules.Count)
            SelectedRule = Rules[index];
    }

    /// <summary>Moves a rule to a new position — used by drag-and-drop reordering in the rule list.</summary>
    public void MoveRule(int from, int to)
    {
        if (from < 0 || from >= Rules.Count) return;
        to = Math.Clamp(to, 0, Rules.Count - 1);
        if (from == to) return;
        Rules.Move(from, to);
        RefreshMoveCanExecute();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedRule is null) return;
        var idx = Rules.IndexOf(SelectedRule);
        Rules.Move(idx, idx - 1);
        RefreshMoveCanExecute();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedRule is null) return;
        var idx = Rules.IndexOf(SelectedRule);
        Rules.Move(idx, idx + 1);
        RefreshMoveCanExecute();
    }

    private bool HasSelection()  => SelectedRule is not null;
    private bool CanMoveUp()     => SelectedRule is not null && Rules.IndexOf(SelectedRule) > 0;
    private bool CanMoveDown()   => SelectedRule is not null && Rules.IndexOf(SelectedRule) < Rules.Count - 1;

    private void RefreshMoveCanExecute()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedClassChanged(PlayerClass value)
    {
        foreach (var rule in Rules)
            rule.ApplyClassFilter(value);
    }

    public void AddGeneratedRule(FilterRule rule)
    {
        var vm = MakeRuleVm(rule);
        Rules.Add(vm);
        SelectedRule = vm;
    }

    private FilterRuleViewModel MakeRuleVm(FilterRule rule)
    {
        var vm = new FilterRuleViewModel(_conditionFactory, rule,
            self => Rules.Where(r => r != self).Select(r => r.Color));
        vm.ApplyClassFilter(SelectedClass);
        return vm;
    }
}
