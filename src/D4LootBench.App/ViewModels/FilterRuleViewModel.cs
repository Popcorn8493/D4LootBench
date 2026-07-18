using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Utilities;
using D4LootBench.App.ViewModels.Conditions;
using D4LootBench.App.Views;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels;

/// <summary>A named color entry used in the color swatch palette.</summary>
public sealed class NamedColor(string name, uint argb)
{
    public string          Name  { get; } = name;
    public uint            Argb  { get; } = argb;
    public SolidColorBrush Brush { get; } = new(ColorUtility.ArgbToWpf(argb));
}

public partial class FilterRuleViewModel : ObservableObject
{
    private readonly IConditionViewModelFactory _conditionFactory;
    private readonly Func<FilterRuleViewModel, IEnumerable<uint>> _getPeerColors;
    private SolidColorBrush? _wpfBrush;
    private PlayerClass _classFilter = PlayerClass.All;

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecolor))]
    [NotifyPropertyChangedFor(nameof(DisplayBrush))]
    private Visibility _visibility;

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WpfBrush))]
    [NotifyPropertyChangedFor(nameof(DisplayBrush))]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private uint _color;

    public ObservableCollection<ConditionViewModel> Conditions { get; } = [];

    public static IReadOnlyList<NamedColor> StandardColors { get; } =
    [
        new("Game Default",   FilterColors.GameDefault),
        new("Blue",           FilterColors.Blue),
        new("Cyan",           FilterColors.Cyan),
        new("Green",          FilterColors.Green),
        new("Orange",         FilterColors.Orange),
        new("Gold",           FilterColors.Gold)
    ];

    public static Visibility[] VisibilityValues { get; } = Enum.GetValues<Visibility>();

    public bool IsRecolor => Visibility == Visibility.Recolor;

    private static readonly SolidColorBrush GameDefaultBrush =
        new(ColorUtility.ArgbToWpf(FilterColors.GameDefault));

    public SolidColorBrush DisplayBrush => IsRecolor ? WpfBrush : GameDefaultBrush;

    public SolidColorBrush WpfBrush
    {
        get
        {
            _wpfBrush ??= new SolidColorBrush(ColorUtility.ArgbToWpf(Color));
            return _wpfBrush;
        }
    }

    public string ColorHex
    {
        // Display as 6-char RRGGBB to match the in-game color picker format (alpha omitted, always FF).
        get => $"{Color >> 16 & 0xFF:X2}{Color >> 8 & 0xFF:X2}{Color & 0xFF:X2}";
        set
        {
            var s = value.TrimStart('#');
            Color = s.Length switch
            {
                6 when uint.TryParse(s, NumberStyles.HexNumber, null, out var rgb)  => 0xFF000000u | rgb,
                8 when uint.TryParse(s, NumberStyles.HexNumber, null, out var argb) => argb,
                _ => Color
            };
        }
    }

    [ObservableProperty]
    private ConditionType _selectedNewConditionType;

    public IEnumerable<ConditionType> AvailableConditionTypes =>
        Enum.GetValues<ConditionType>().Where(t => Conditions.All(c => _conditionFactory.GetConditionType(c) != t));

    public FilterRuleViewModel(
        IConditionViewModelFactory conditionFactory,
        FilterRule rule,
        Func<FilterRuleViewModel, IEnumerable<uint>>? getPeerColors = null)
    {
        _conditionFactory = conditionFactory;
        _name             = rule.Name;
        _visibility       = rule.Visibility;
        _color            = rule.Color;
        _isEnabled        = rule.IsEnabled;
        _getPeerColors    = getPeerColors ?? (_ => []);

        foreach (var condition in rule.Conditions)
            Conditions.Add(_conditionFactory.FromModel(condition));

        foreach (var itemType in Conditions.OfType<ItemTypeConditionViewModel>())
            itemType.Picker.Selected.CollectionChanged += OnItemTypeSelectionChanged;
        foreach (var condition in Conditions)
            WireAffixEntryMove(condition);

        Conditions.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(AvailableConditionTypes));
            ConvertAffixConditionCommand.NotifyCanExecuteChanged();

            foreach (var itemType in (e.OldItems ?? Array.Empty<object>()).OfType<ItemTypeConditionViewModel>())
                itemType.Picker.Selected.CollectionChanged -= OnItemTypeSelectionChanged;
            foreach (var itemType in (e.NewItems ?? Array.Empty<object>()).OfType<ItemTypeConditionViewModel>())
                itemType.Picker.Selected.CollectionChanged += OnItemTypeSelectionChanged;
            foreach (var condition in (e.NewItems ?? Array.Empty<object>()).OfType<ConditionViewModel>())
                WireAffixEntryMove(condition);

            RefreshAllowedClasses();
        };

        RefreshAllowedClasses();
    }

    public void ApplyClassFilter(PlayerClass playerClass)
    {
        _classFilter = playerClass;
        foreach (var condition in Conditions)
            condition.ApplyClassFilter(playerClass);
        RefreshAllowedClasses();
    }

    private void OnItemTypeSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RefreshAllowedClasses();

    private void RefreshAllowedClasses()
    {
        var allowed = ComputeAllowedClasses();
        foreach (var condition in Conditions)
            condition.ApplyAllowedClasses(allowed);
    }

    /// <summary>
    /// The rule's effective class restriction: the global class filter intersected with the
    /// classes implied by the rule's Item Type selection (e.g. Scythe → Necromancer only).
    /// Null means unrestricted.
    /// </summary>
    private IReadOnlySet<string>? ComputeAllowedClasses()
    {
        var fromTypes = Conditions.OfType<ItemTypeConditionViewModel>()
            .FirstOrDefault()?.SelectedTypeClasses;
        IReadOnlySet<string>? fromGlobal = _classFilter == PlayerClass.All
            ? null
            : new HashSet<string> { _classFilter.ToString() };

        if (fromTypes is null) return fromGlobal;
        if (fromGlobal is null) return fromTypes;
        return fromTypes.Intersect(fromGlobal).ToHashSet();
    }

    public FilterRule BuildRule() =>
        new(Name, Visibility, Color, Conditions.Select(c => c.BuildModel()).ToList(), IsEnabled);

    [RelayCommand]
    private void AddCondition()
    {
        if (Conditions.Any(c => _conditionFactory.GetConditionType(c) == SelectedNewConditionType))
            return;

        var vm = _conditionFactory.CreateNew(SelectedNewConditionType);
        vm.ApplyClassFilter(_classFilter);
        Conditions.Add(vm);
    }

    // value unused intentionally — only need to invalidate the cached brush on any change.
    // ReSharper disable once UnusedParameter.Local
    partial void OnColorChanging(uint value) => _wpfBrush = null;

    [RelayCommand]
    private void SelectColor(uint argb) => Color = argb;

    // ReSharper disable once UnusedMember.Local — called by generated PickColorCommand
    [RelayCommand]
    private void PickColor()
    {
        var dialog = new ColorPickerDialog(Color == 0 ? FilterColors.Gold : Color)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
            Color = dialog.ResultColor;
    }

    [RelayCommand]
    private void GenerateDistinctColor() =>
        Color = ColorUtility.GenerateDistinctColor(_getPeerColors(this));

    /// <summary>
    /// Swaps a Required Affixes condition to Optional Affixes (or back), keeping the
    /// selected affixes, minimum count, and greater-affix flags. Blocked while the rule
    /// already has a condition of the target type — each type can appear only once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertAffixCondition))]
    private void ConvertAffixCondition(ConditionViewModel condition)
    {
        var idx = Conditions.IndexOf(condition);
        if (idx < 0) return;

        Condition? converted = condition.BuildModel() switch
        {
            AffixCondition m => new OptionalAffixCondition(m.AffixIds, m.MinimumCount)
                { GreaterEntries = m.GreaterEntries, Field5 = m.Field5 },
            // Required Affixes with a minimum of 0 would match everything; clamp to 1.
            OptionalAffixCondition m => new AffixCondition(m.AffixIds, Math.Max(1, m.MinimumCount))
                { GreaterEntries = m.GreaterEntries, Field5 = m.Field5 },
            _ => null
        };
        if (converted is null) return;

        var vm = _conditionFactory.FromModel(converted);
        vm.ApplyClassFilter(_classFilter);
        vm.IsExpanded = condition.IsExpanded;
        Conditions[idx] = vm;
    }

    private bool CanConvertAffixCondition(ConditionViewModel? condition) => condition switch
    {
        AffixConditionViewModel         => !Conditions.OfType<OptionalAffixConditionViewModel>().Any(),
        OptionalAffixConditionViewModel => !Conditions.OfType<AffixConditionViewModel>().Any(),
        _ => false
    };

    /// <summary>
    /// Offers "move this affix to the counterpart condition" on an affix picker's Selected
    /// entries (Required ⇄ Optional), creating the counterpart condition when the rule
    /// doesn't have one yet.
    /// </summary>
    private void WireAffixEntryMove(ConditionViewModel condition)
    {
        switch (condition)
        {
            case AffixConditionViewModel required:
                required.Picker.EntryActionLabel = "Move to Optional Affixes";
                required.Picker.EntryAction = entries =>
                    MoveAffixEntries(required.Picker, ConditionType.OptionalAffixes, entries);
                break;
            case OptionalAffixConditionViewModel optional:
                optional.Picker.EntryActionLabel = "Move to Required Affixes";
                optional.Picker.EntryAction = entries =>
                    MoveAffixEntries(optional.Picker, ConditionType.RequiredAffixes, entries);
                break;
        }
    }

    private void MoveAffixEntries(PickerViewModel source, ConditionType targetType, IReadOnlyList<PickerEntry> entries)
    {
        ConditionViewModel? target = targetType == ConditionType.RequiredAffixes
            ? Conditions.OfType<AffixConditionViewModel>().FirstOrDefault()
            : Conditions.OfType<OptionalAffixConditionViewModel>().FirstOrDefault();
        if (target is null)
        {
            target = _conditionFactory.CreateNew(targetType);
            target.ApplyClassFilter(_classFilter);
            Conditions.Add(target);
        }

        var targetPicker = target switch
        {
            AffixConditionViewModel a         => a.Picker,
            OptionalAffixConditionViewModel o => o.Picker,
            _                                 => null
        };
        if (targetPicker is null || ReferenceEquals(targetPicker, source)) return;

        var moved = false;
        foreach (var entry in entries)
        {
            // Already in the target — just take it out of the source.
            if (targetPicker.Selected.Any(s => s.Hash == entry.Hash))
            {
                source.Selected.Remove(entry);
                moved = true;
                continue;
            }
            // Target full (game-enforced 15) — leave the remainder where they are.
            if (targetPicker.IsAtMax) continue;
            targetPicker.Selected.Add(entry);
            source.Selected.Remove(entry);
            moved = true;
        }
        if (moved) target.IsExpanded = true;
    }

    [RelayCommand]
    private void DeleteCondition(ConditionViewModel condition)
    {
        var idx = Conditions.IndexOf(condition);
        if (idx < 0) return;
        _lastDeletedCondition = condition;
        _lastDeletedIndex     = idx;
        Conditions.RemoveAt(idx);
        UndoDeleteConditionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LastDeletedConditionTypeName));
    }

    private ConditionViewModel? _lastDeletedCondition;
    private int _lastDeletedIndex;

    public bool HasUndoableDelete => _lastDeletedCondition is not null;

    public string LastDeletedConditionTypeName => _lastDeletedCondition?.TypeName ?? "";

    [RelayCommand(CanExecute = nameof(HasUndoableDelete))]
    private void UndoDeleteCondition()
    {
        if (_lastDeletedCondition is null) return;
        var insertAt = Math.Clamp(_lastDeletedIndex, 0, Conditions.Count);
        Conditions.Insert(insertAt, _lastDeletedCondition);
        _lastDeletedCondition = null;
        UndoDeleteConditionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LastDeletedConditionTypeName));
        OnPropertyChanged(nameof(HasUndoableDelete));
    }
}
