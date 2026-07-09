using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.Core.Compare;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;

namespace D4LootBench.App.ViewModels;

/// <summary>A pickable catalog entry (item type, affix, or unique) in the compare window.</summary>
public sealed record CompareOption(uint Hash, string Name);

/// <summary>One affix line of a candidate item: which affix, its roll value, GA or not.</summary>
public partial class AffixRowViewModel : ObservableObject
{
    [ObservableProperty]
    private CompareOption? _selectedAffix;

    [ObservableProperty]
    private string _valueText = "";

    [ObservableProperty]
    private bool _isGreater;
}

/// <summary>One of the items being compared: slot, optional unique identity, affix lines.</summary>
public partial class CandidateItemViewModel : ObservableObject
{
    private readonly string _defaultLabel;

    public CandidateItemViewModel(string defaultLabel)
    {
        _defaultLabel = defaultLabel;
        _label = defaultLabel;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private CompareOption? _selectedItemType;

    /// <summary>Hash 0 is the "not a unique" sentinel.</summary>
    [ObservableProperty]
    private CompareOption? _selectedUnique;

    public ObservableCollection<AffixRowViewModel> AffixRows { get; } = [new(), new(), new(), new()];

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _isWinner;

    public CandidateItem ToCandidate()
    {
        var affixes = new List<CandidateAffix>();
        foreach (var row in AffixRows)
        {
            if (row.SelectedAffix is not CompareOption affix)
                continue;
            double? value = double.TryParse(row.ValueText, out double parsed) && parsed > 0 ? parsed : null;
            affixes.Add(new CandidateAffix(affix.Hash, affix.Name, value, row.IsGreater));
        }
        return new CandidateItem(
            string.IsNullOrWhiteSpace(Label) ? _defaultLabel : Label.Trim(),
            SelectedItemType?.Hash,
            SelectedUnique is { Hash: > 0 } unique ? unique.Hash : null,
            affixes);
    }
}

/// <summary>
/// Item Compare: enter two candidate drops (e.g. two pairs of gloves) and score them against
/// the loaded filter (the guide's per-slot affix wish list and target uniques) plus the open
/// paragon build's unmet threshold deficits. Every point is itemized, so "objectively better"
/// is auditable.
/// </summary>
public partial class ItemCompareViewModel : ObservableObject
{
    private readonly FilterRuleset? _reference;
    private readonly IReadOnlyList<StatNeed> _statNeeds;

    public ItemCompareViewModel(
        IFilterDataService data, FilterRuleset? reference, IReadOnlyList<StatNeed> statNeeds)
    {
        _reference = reference;
        _statNeeds = statNeeds;

        ItemTypeOptions = data.ItemTypes.All
            .Select(t => new CompareOption(t.Hash, t.Name))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AffixOptions = data.Affixes.All
            .Select(a => new CompareOption(a.Hash, a.Name))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UniqueOptions = new List<CompareOption> { new(0, "— not a unique —") }
            .Concat(data.Uniques.Released
                .Select(u => new CompareOption(u.SnoId, u.Name))
                .DistinctBy(o => o.Name)
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        ItemA.SelectedUnique = UniqueOptions[0];
        ItemB.SelectedUnique = UniqueOptions[0];

        string filterPart = reference is null
            ? "No filter loaded — open or import one so guide affix priorities can score."
            : $"Filter reference: \"{reference.Name}\" ({reference.Rules.Count} rule(s)).";
        string paragonPart = statNeeds.Count == 0
            ? "No unmet paragon thresholds — open the Paragon Planner with a solved build to weigh core stats."
            : "Paragon needs: " + string.Join("; ",
                statNeeds.Select(n => $"{n.NodeName} is {n.Deficit:0} {n.StatName} short")) + ".";
        ReferenceSummary = filterPart + Environment.NewLine + paragonPart;
    }

    public IReadOnlyList<CompareOption> ItemTypeOptions { get; }
    public IReadOnlyList<CompareOption> AffixOptions { get; }
    public IReadOnlyList<CompareOption> UniqueOptions { get; }

    public CandidateItemViewModel ItemA { get; } = new("Item A");
    public CandidateItemViewModel ItemB { get; } = new("Item B");

    public string ReferenceSummary { get; }

    [ObservableProperty]
    private string _verdict = "";

    /// <summary>
    /// GA rolls are deterministic in-game (max natural roll × 1.5), so an empty value box on a
    /// GA line fills itself from the best natural roll entered for the same affix.
    /// </summary>
    private void AutoFillGreaterRolls()
    {
        var rows = ItemA.AffixRows.Concat(ItemB.AffixRows)
            .Where(r => r.SelectedAffix is not null)
            .ToList();
        var bestNaturalByAffix = rows
            .Where(r => !r.IsGreater && double.TryParse(r.ValueText, out double value) && value > 0)
            .GroupBy(r => r.SelectedAffix!.Hash)
            .ToDictionary(g => g.Key, g => g.Max(r => double.Parse(r.ValueText)));
        foreach (var row in rows)
        {
            if (row.IsGreater && string.IsNullOrWhiteSpace(row.ValueText)
                && bestNaturalByAffix.TryGetValue(row.SelectedAffix!.Hash, out double natural))
                row.ValueText = (natural * ItemChoiceComparer.GreaterAffixRollFactor).ToString("0.##");
        }
    }

    [RelayCommand]
    private void Compare()
    {
        AutoFillGreaterRolls();
        var candidates = new[] { ItemA, ItemB };
        var outcome = ItemChoiceComparer.Compare(
            candidates.Select(c => c.ToCandidate()).ToList(), _reference, _statNeeds);

        double best = outcome.Scores.Max(s => s.Total);
        for (int i = 0; i < candidates.Length; i++)
        {
            var score = outcome.Scores[i];
            candidates[i].ResultText = string.Join(Environment.NewLine, score.Lines
                    .Select(l => $"{(l.Points > 0 ? $"+{l.Points:0.#}" : "  0")}   {l.Reason}"))
                + Environment.NewLine + $"Total: {score.Total:0.#} point(s)";
            candidates[i].IsWinner = outcome.Scores.Count(s => s.Total == best) == 1 && score.Total == best;
        }
        Verdict = outcome.Verdict;
    }
}
