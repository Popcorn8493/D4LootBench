using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.Ai;
using D4LootBench.App.Services;
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

    public void Clear()
    {
        SelectedAffix = null;
        ValueText = "";
        IsGreater = false;
    }
}

/// <summary>One of the items being compared: slot, optional unique identity, affix lines, and
/// transfigured stat lines (Horadric transfiguration — extra stats beyond the natural affixes).</summary>
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

    public ObservableCollection<AffixRowViewModel> TransfiguredRows { get; } = [new(), new()];

    /// <summary>The gear-library entry picked in this candidate's Saved combo.</summary>
    [ObservableProperty]
    private string? _selectedSavedName;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _isWinner;

    [RelayCommand]
    private void AddAffixRow() => AffixRows.Add(new());

    [RelayCommand]
    private void AddTransfiguredRow() => TransfiguredRows.Add(new());

    public string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? _defaultLabel : Label.Trim();

    /// <param name="componentsOf">Expands an aggregate stat's synthetic id to the catalog
    /// affixes it grants (null for plain affixes).</param>
    public CandidateItem ToCandidate(Func<uint, IReadOnlyList<uint>?> componentsOf)
    {
        var affixes = new List<CandidateAffix>();
        foreach (var row in AffixRows)
        {
            if (row.SelectedAffix is not CompareOption affix)
                continue;
            affixes.Add(new CandidateAffix(affix.Hash, affix.Name, ParseValue(row.ValueText), row.IsGreater,
                ComponentIds: componentsOf(affix.Hash)));
        }
        foreach (var row in TransfiguredRows)
        {
            if (row.SelectedAffix is not CompareOption affix)
                continue;
            affixes.Add(new CandidateAffix(
                affix.Hash, affix.Name, ParseValue(row.ValueText), IsGreater: false, IsTransfigured: true,
                ComponentIds: componentsOf(affix.Hash)));
        }
        return new CandidateItem(EffectiveLabel, SelectedItemType?.Hash,
            SelectedUnique is { Hash: > 0 } unique ? unique.Hash : null,
            affixes);
    }

    private static double? ParseValue(string text) =>
        double.TryParse(text, out double parsed) && parsed > 0 ? parsed : null;
}

/// <summary>One priority slot of the user's own reference wish list (top row weighs most).</summary>
public partial class ReferenceRowViewModel : ObservableObject
{
    [ObservableProperty]
    private CompareOption? _selectedAffix;

    [ObservableProperty]
    private bool _greaterWanted;
}

/// <summary>
/// Item Compare: enter two candidate drops (e.g. two pairs of gloves) and score them against
/// a reference — the loaded filter (the guide's per-slot affix wish list and target uniques)
/// or the user's own persisted priority list — plus the open paragon build's unmet threshold
/// deficits. Every point is itemized, so "objectively better" is auditable. Candidates can be
/// filled by OCR from a tooltip screenshot on the clipboard and saved to a persistent gear
/// library (typically the currently equipped piece).
/// </summary>
public partial class ItemCompareViewModel : ObservableObject
{
    private readonly FilterRuleset? _reference;
    private readonly IReadOnlyList<StatNeed> _statNeeds;
    private readonly NameResolver _resolver;
    private readonly SavedGearService _savedGear;
    private readonly CompareReferenceService _referenceStore;

    /// <summary>Synthetic aggregate id → the catalog affix hashes it grants (e.g. All Stats →
    /// the four core stats), resolved once against the live catalog.</summary>
    private readonly Dictionary<uint, IReadOnlyList<uint>> _aggregateComponents;

    public ItemCompareViewModel(
        IFilterDataService data, FilterRuleset? reference, IReadOnlyList<StatNeed> statNeeds,
        SavedGearService? savedGear = null, CompareReferenceService? referenceStore = null)
    {
        _reference = reference;
        _statNeeds = statNeeds;
        _resolver = new NameResolver(data);
        _savedGear = savedGear ?? new SavedGearService();
        _referenceStore = referenceStore ?? new CompareReferenceService();

        _aggregateComponents = AggregateStats.All.ToDictionary(
            s => s.Id,
            s => (IReadOnlyList<uint>)s.ComponentAffixNames
                .SelectMany(n => data.Affixes.All.Where(a =>
                    a.Name.TrimStart('+', '%', ' ').Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Hash)
                .Distinct()
                .ToList());

        ItemTypeOptions = data.ItemTypes.All
            .Select(t => new CompareOption(t.Hash, t.Name))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AffixOptions = data.Affixes.All
            .Select(a => new CompareOption(a.Hash, a.Name))
            .Concat(AggregateStats.All.Select(s => new CompareOption(s.Id, s.Name)))
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
        RefreshSavedNames();
        LoadStoredReference();
        ReferenceSummary = BuildReferenceSummary();
    }

    public IReadOnlyList<CompareOption> ItemTypeOptions { get; }
    public IReadOnlyList<CompareOption> AffixOptions { get; }
    public IReadOnlyList<CompareOption> UniqueOptions { get; }

    public ObservableCollection<string> SavedGearNames { get; } = [];

    public CandidateItemViewModel ItemA { get; } = new("Item A");
    public CandidateItemViewModel ItemB { get; } = new("Item B");

    [ObservableProperty]
    private string _referenceSummary = "";

    [ObservableProperty]
    private string _verdict = "";

    /// <summary>Feedback from scan/save/load actions, shown under the reference summary.</summary>
    [ObservableProperty]
    private string _statusText = "";

    // ── Custom reference ─────────────────────────────────────────────────

    /// <summary>When on, the user's own priority list replaces the loaded filter as the
    /// wish-list side of the score (paragon threshold needs always apply).</summary>
    [ObservableProperty]
    private bool _useCustomReference;

    /// <summary>Radio-button counterpart of <see cref="UseCustomReference"/>.</summary>
    public bool UseLoadedFilter
    {
        get => !UseCustomReference;
        set => UseCustomReference = !value;
    }

    partial void OnUseCustomReferenceChanged(bool value)
    {
        OnPropertyChanged(nameof(UseLoadedFilter));
        ReferenceSummary = BuildReferenceSummary();
    }

    public ObservableCollection<ReferenceRowViewModel> ReferenceRows { get; } = [];

    /// <summary>The unique the custom reference hunts (hash 0 = none).</summary>
    [ObservableProperty]
    private CompareOption? _selectedReferenceUnique;

    partial void OnSelectedReferenceUniqueChanged(CompareOption? value)
    {
        if (UseCustomReference)
            ReferenceSummary = BuildReferenceSummary();
    }

    [RelayCommand]
    private void AddReferenceRow() => ReferenceRows.Add(new());

    [RelayCommand]
    private void RemoveReferenceRow(ReferenceRowViewModel row) => ReferenceRows.Remove(row);

    [RelayCommand]
    private void MoveReferenceRowUp(ReferenceRowViewModel row)
    {
        int at = ReferenceRows.IndexOf(row);
        if (at > 0)
            ReferenceRows.Move(at, at - 1);
    }

    [RelayCommand]
    private void MoveReferenceRowDown(ReferenceRowViewModel row)
    {
        int at = ReferenceRows.IndexOf(row);
        if (at >= 0 && at < ReferenceRows.Count - 1)
            ReferenceRows.Move(at, at + 1);
    }

    private void LoadStoredReference()
    {
        var stored = _referenceStore.Current;
        foreach (var affix in stored.Affixes)
        {
            if (AffixOptions.FirstOrDefault(o => o.Hash == affix.AffixId) is not CompareOption option)
                continue;
            ReferenceRows.Add(new ReferenceRowViewModel
            {
                SelectedAffix = option,
                GreaterWanted = affix.GreaterWanted,
            });
        }
        if (ReferenceRows.Count == 0)
            ReferenceRows.Add(new());
        SelectedReferenceUnique = stored.TargetUniqueId is uint uniqueId
            ? UniqueOptions.FirstOrDefault(o => o.Hash == uniqueId) ?? UniqueOptions[0]
            : UniqueOptions[0];
        UseCustomReference = stored.UseCustom;
    }

    /// <summary>Saves the custom reference to disk; called on Compare and by the window on
    /// close, so a hand-built list survives the session.</summary>
    public void PersistReference()
    {
        _referenceStore.Save(new StoredCompareReference(
            UseCustomReference,
            ReferenceRows
                .Where(r => r.SelectedAffix is not null)
                .Select(r => new StoredReferenceAffix(
                    r.SelectedAffix!.Hash, r.SelectedAffix.Name, r.GreaterWanted))
                .ToList(),
            SelectedReferenceUnique is { Hash: > 0 } unique ? unique.Hash : null));
    }

    private CompareWishList? BuildCustomReference()
    {
        if (!UseCustomReference)
            return null;
        var desired = ReferenceRows
            .Where(r => r.SelectedAffix is not null)
            .Select(r => new WishEntry(
                ComponentsOf(r.SelectedAffix!.Hash) is { } components
                    ? [r.SelectedAffix.Hash, .. components]
                    : [r.SelectedAffix.Hash],
                r.GreaterWanted, r.SelectedAffix.Name))
            .ToList();
        var uniques = new HashSet<uint>();
        if (SelectedReferenceUnique is { Hash: > 0 } unique)
            uniques.Add(unique.Hash);
        return new CompareWishList(desired, uniques);
    }

    private IReadOnlyList<uint>? ComponentsOf(uint hash) =>
        _aggregateComponents.TryGetValue(hash, out var components) && components.Count > 0
            ? components
            : null;

    private string BuildReferenceSummary()
    {
        string referencePart;
        if (UseCustomReference)
        {
            int count = ReferenceRows.Count(r => r.SelectedAffix is not null);
            referencePart = $"Custom reference: {count} affix priorit{(count == 1 ? "y" : "ies")}"
                + (SelectedReferenceUnique is { Hash: > 0 } unique ? $", hunting {unique.Name}." : ".");
        }
        else
        {
            referencePart = _reference is null
                ? "No filter loaded — open or import one, or switch to a custom reference below."
                : $"Filter reference: \"{_reference.Name}\" ({_reference.Rules.Count} rule(s)).";
        }
        string paragonPart = _statNeeds.Count == 0
            ? "No unmet paragon thresholds — open the Paragon Planner with a solved build to weigh core stats."
            : "Paragon needs: " + string.Join("; ",
                _statNeeds.Select(n => $"{n.NodeName} is {n.Deficit:0} {n.StatName} short")) + ".";
        return referencePart + Environment.NewLine + paragonPart;
    }

    // ── Gear library ─────────────────────────────────────────────────────

    private void RefreshSavedNames()
    {
        SavedGearNames.Clear();
        foreach (var item in _savedGear.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            SavedGearNames.Add(item.Name);
    }

    /// <summary>Saves the candidate under its label (overwriting a same-named entry).</summary>
    [RelayCommand]
    private void SaveItem(CandidateItemViewModel item)
    {
        static IEnumerable<SavedGearAffix> RowsOf(IEnumerable<AffixRowViewModel> rows, bool transfigured) =>
            rows.Where(r => r.SelectedAffix is not null)
                .Select(r => new SavedGearAffix(
                    r.SelectedAffix!.Hash, r.SelectedAffix.Name, r.ValueText.Trim(),
                    !transfigured && r.IsGreater, transfigured));

        var saved = new SavedGearItem(
            item.EffectiveLabel,
            item.SelectedItemType?.Hash,
            item.SelectedUnique is { Hash: > 0 } unique ? unique.Hash : null,
            RowsOf(item.AffixRows, transfigured: false)
                .Concat(RowsOf(item.TransfiguredRows, transfigured: true))
                .ToList());
        _savedGear.Save(saved);
        RefreshSavedNames();
        item.SelectedSavedName = saved.Name;
        StatusText = $"Saved \"{saved.Name}\" ({saved.Affixes.Count} stat line(s)) to the gear library.";
    }

    [RelayCommand]
    private void LoadSaved(CandidateItemViewModel item)
    {
        if (item.SelectedSavedName is not string name || _savedGear.Find(name) is not SavedGearItem saved)
        {
            StatusText = "Pick a saved gear piece first.";
            return;
        }

        item.Label = saved.Name;
        item.SelectedItemType = saved.ItemTypeId is uint typeId
            ? ItemTypeOptions.FirstOrDefault(o => o.Hash == typeId)
            : null;
        item.SelectedUnique = saved.UniqueId is uint uniqueId
            ? UniqueOptions.FirstOrDefault(o => o.Hash == uniqueId) ?? UniqueOptions[0]
            : UniqueOptions[0];

        var missing = new List<string>();
        FillRows(item.AffixRows, saved.Affixes.Where(a => !a.IsTransfigured), missing);
        FillRows(item.TransfiguredRows, saved.Affixes.Where(a => a.IsTransfigured), missing);
        StatusText = $"Loaded \"{saved.Name}\" into {item.EffectiveLabel}." +
                     (missing.Count > 0
                         ? $" Not in the current catalog: {string.Join(", ", missing)}."
                         : "");
    }

    [RelayCommand]
    private void DeleteSaved(CandidateItemViewModel item)
    {
        if (item.SelectedSavedName is not string name || !_savedGear.Delete(name))
        {
            StatusText = "Pick a saved gear piece to delete first.";
            return;
        }
        RefreshSavedNames();
        item.SelectedSavedName = null;
        StatusText = $"Deleted \"{name}\" from the gear library.";
    }

    private void FillRows(
        ObservableCollection<AffixRowViewModel> rows, IEnumerable<SavedGearAffix> affixes, List<string> missing)
    {
        var list = affixes.ToList();
        EnsureRows(rows, list.Count);
        foreach (var row in rows)
            row.Clear();
        int at = 0;
        foreach (var affix in list)
        {
            if (AffixOptions.FirstOrDefault(o => o.Hash == affix.AffixId) is not CompareOption option)
            {
                missing.Add(affix.Name);
                continue;
            }
            rows[at].SelectedAffix = option;
            rows[at].ValueText = affix.Value;
            rows[at].IsGreater = affix.IsGreater;
            at++;
        }
    }

    // ── OCR scan ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fills a candidate from an item-tooltip screenshot on the clipboard (Win+Shift+S in game,
    /// then Scan): in-box Windows OCR reads the text, <see cref="ItemTooltipParser"/> structures
    /// it, and names resolve fuzzily against the catalogs. Everything stays editable afterwards.
    /// </summary>
    [RelayCommand]
    private async Task ScanItem(CandidateItemViewModel item)
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
        {
            StatusText = "No screenshot on the clipboard — snip the item tooltip with Win+Shift+S first.";
            return;
        }

        try
        {
            StatusText = "Reading the screenshot…";
            var lines = await TooltipOcrService.ReadLinesAsync(image);
            var parsed = ItemTooltipParser.Parse(lines);
            if (parsed.ItemName is null && parsed.Stats.Count == 0)
            {
                StatusText = "Nothing recognizable on the screenshot — snip just the item tooltip.";
                return;
            }
            ApplyParsed(item, parsed);
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
    }

    private void ApplyParsed(CandidateItemViewModel item, ParsedTooltip parsed)
    {
        var notes = new List<string>();

        if (parsed.ItemName is string name)
            item.Label = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());

        item.SelectedItemType = null;
        if (parsed.SlotText is string slot)
        {
            if (_resolver.TryResolveItemType(slot, out uint typeHash, out _))
                item.SelectedItemType = ItemTypeOptions.FirstOrDefault(o => o.Hash == typeHash);
            else
                notes.Add($"slot \"{slot}\" not recognized");
        }

        item.SelectedUnique = UniqueOptions[0];
        if (parsed.IsUnique && parsed.ItemName is string uniqueName)
        {
            if (_resolver.TryResolveUnique(uniqueName, out uint uniqueHash, out _))
                item.SelectedUnique = UniqueOptions.FirstOrDefault(o => o.Hash == uniqueHash) ?? UniqueOptions[0];
            else
                notes.Add($"unique \"{item.Label}\" not recognized");
        }

        int matched = FillScannedRows(item.AffixRows, parsed.Stats.Where(s => !s.IsTransfigured), notes)
                    + FillScannedRows(item.TransfiguredRows, parsed.Stats.Where(s => s.IsTransfigured), notes);

        StatusText = $"Scanned {item.EffectiveLabel}: {matched} stat line(s) recognized." +
                     (notes.Count > 0 ? $" Check manually: {string.Join("; ", notes)}." : "");
    }

    private int FillScannedRows(
        ObservableCollection<AffixRowViewModel> rows, IEnumerable<TooltipStatLine> stats, List<string> notes)
    {
        var list = stats.ToList();
        EnsureRows(rows, list.Count);
        foreach (var row in rows)
            row.Clear();
        int at = 0;
        foreach (var stat in list)
        {
            // Aggregates first: "All Stats" would otherwise fuzzy-land on a single core stat.
            uint hash;
            if (AggregateStats.MatchName(stat.StatText) is AggregateStat aggregate)
                hash = aggregate.Id;
            else if (!_resolver.TryResolveAffix(stat.StatText, out hash, out _))
            {
                notes.Add($"stat \"{stat.StatText}\" not matched");
                continue;
            }
            if (AffixOptions.FirstOrDefault(o => o.Hash == hash) is not CompareOption option)
            {
                notes.Add($"stat \"{stat.StatText}\" not matched");
                continue;
            }
            rows[at].SelectedAffix = option;
            rows[at].ValueText = stat.Value?.ToString("0.##") ?? "";
            rows[at].IsGreater = stat.IsGreater;
            at++;
        }
        return at;
    }

    private static void EnsureRows(ObservableCollection<AffixRowViewModel> rows, int count)
    {
        while (rows.Count < count)
            rows.Add(new());
    }

    // ── Compare ──────────────────────────────────────────────────────────

    /// <summary>
    /// GA rolls are deterministic in-game (max natural roll × 1.5), so an empty value box on a
    /// GA line fills itself from the best natural roll entered for the same affix (transfigured
    /// lines count as natural sources).
    /// </summary>
    private void AutoFillGreaterRolls()
    {
        var candidates = new[] { ItemA, ItemB };
        var rows = candidates
            .SelectMany(c => c.AffixRows.Concat(c.TransfiguredRows))
            .Where(r => r.SelectedAffix is not null)
            .ToList();
        var greaterTargets = candidates
            .SelectMany(c => c.AffixRows)
            .Where(r => r.SelectedAffix is not null && r.IsGreater)
            .ToList();
        var bestNaturalByAffix = rows
            .Where(r => !r.IsGreater && double.TryParse(r.ValueText, out double value) && value > 0)
            .GroupBy(r => r.SelectedAffix!.Hash)
            .ToDictionary(g => g.Key, g => g.Max(r => double.Parse(r.ValueText)));
        foreach (var row in greaterTargets)
        {
            if (string.IsNullOrWhiteSpace(row.ValueText)
                && bestNaturalByAffix.TryGetValue(row.SelectedAffix!.Hash, out double natural))
                row.ValueText = (natural * ItemChoiceComparer.GreaterAffixRollFactor).ToString("0.##");
        }
    }

    [RelayCommand]
    private void Compare()
    {
        AutoFillGreaterRolls();
        PersistReference();
        ReferenceSummary = BuildReferenceSummary();
        var candidates = new[] { ItemA, ItemB };
        var outcome = ItemChoiceComparer.Compare(
            candidates.Select(c => c.ToCandidate(ComponentsOf)).ToList(), _reference, _statNeeds,
            BuildCustomReference());

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
