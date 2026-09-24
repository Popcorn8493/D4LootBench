using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D4LootBench.App.Services;
using D4LootBench.App.Views;
using D4LootBench.Paragon;
using D4LootBench.Paragon.Data;
using D4LootBench.Paragon.Import;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Serialization;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.App.ViewModels;

public partial class ParagonPlannerViewModel
{
    // ── Build references: guides, skills, and the loot filter vote on focus ─

    /// <summary>What the reference-derived priorities came from, shown in the Optimize tab.</summary>
    [ObservableProperty]
    private string _referenceSummary = "";

    /// <summary>References the priorities derive from; saved with the project and restored on load.</summary>
    private readonly List<(string Source, IReadOnlyList<ReferenceEmphasis> Emphasis, string? SkillText)> _references = [];

    /// <summary>What the build is known to do — class resource, focus stats, and any skill
    /// reference's text — so placement judging credits only the legendary node conditions the
    /// build actually meets.</summary>
    private LegendaryContext CurrentLegendaryContext() =>
        LegendaryContext.From(CurrentMaximizeFocus(),
            _references.Select(r => r.SkillText).OfType<string>());

    /// <summary>The reference list rendered in the Optimize tab, one removable row per reference.</summary>
    public ObservableCollection<ReferenceListItem> ReferenceItems { get; } = [];

    private void RefreshReferenceItems()
    {
        ReferenceItems.Clear();
        foreach (var (source, emphasis, _) in _references)
        {
            string detail = "Votes for: " + string.Join(", ", emphasis
                .OrderByDescending(e => e.Score)
                .Take(6)
                .Select(e => ParagonDisplay.FormatAttributeName(e.Attribute)));
            ReferenceItems.Add(new ReferenceListItem(source, detail));
        }
    }

    /// <summary>Drops one reference and re-derives the consensus from what remains.</summary>
    [RelayCommand]
    private void RemoveReference(ReferenceListItem item)
    {
        int index = _references.FindIndex(r => r.Source == item.Source);
        if (index < 0)
            return;
        _references.RemoveAt(index);
        if (_references.Count == 0)
        {
            ReferenceSummary = "";
            RefreshReferenceItems();
            SetStatus("Reference removed — focus stats keep their current selection.");
            return;
        }
        ApplyReferencePriorities();
    }

    /// <summary>Sources compare without their trailing count suffix, so "Loaded filter (19 affix(es))"
    /// updates "Loaded filter (12 affix(es))" and a re-imported variant refreshes its old emphasis.</summary>
    private void AddOrReplaceReference(string source, IReadOnlyList<ReferenceEmphasis> emphasis, string? skillText = null)
    {
        static string KeyOf(string s) => System.Text.RegularExpressions.Regex
            .Replace(s, @"\s*\([^()]*\d+ (?:nodes|skills|affix\(es\))[^()]*\)$", "").Trim();
        string key = KeyOf(source);
        int existing = _references.FindIndex(r => KeyOf(r.Source).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _references[existing] = (source, emphasis, skillText);
        else
            _references.Add((source, emphasis, skillText));
    }

    /// <summary>
    /// Imports builds (clipboard: Maxroll code/URL, Mobalytics URL, or page HTML) as REFERENCES
    /// only: the current layout stays untouched, but the focus stats and their priorities are
    /// derived from what the reference allocations actually stack — so the maximizer chases
    /// proven builds' emphasis instead of a hand-picked checkbox list. When a guide carries
    /// several build versions (e.g. a Selig setup and the standard setup) any number can be
    /// picked and each votes as its own reference; re-importing a variant updates its old
    /// entry. Add several guides and the priorities become their consensus: a stat every
    /// reference stacks ranks high, a single reference's outlier gets diluted.
    /// </summary>
    [RelayCommand]
    private Task AddBuildReference() => RunGuardedAsync("Adding the reference", AddBuildReferenceCoreAsync);

    private async Task AddBuildReferenceCoreAsync()
    {
        using var busy = BeginBusy();
        if (await ImportManyFromClipboardAsync(allowMultiple: true) is not { Count: > 0 } imports)
            return;

        SetStatus("Deriving stat priorities from the reference build(s)…");
        int empty = 0;
        var skillNotes = new List<string>();
        foreach (var import in imports)
        {
            var emphasis = await Task.Run(() =>
            {
                var referenceGraph = ComposedGraph.Build(import.Build.Layout);
                return BuildReference.EmphasisOf(referenceGraph, import.Build.AllocatedCells);
            });
            if (emphasis.Count > 0)
                AddOrReplaceReference($"{import.Source} ({import.Build.AllocatedCells.Count} nodes)", emphasis);
            else
                empty++;

            // The guide's skill setup votes as its own reference — gear, boards, and skills
            // then pull the focus priorities together.
            if (import.Skills is { ActiveSkills.Count: > 0 } skills)
            {
                var skillResult = SkillReference.EmphasisOf(skills);
                if (skillResult.Emphasis.Count > 0)
                {
                    AddOrReplaceReference(
                        $"{import.Source} skills ({skills.ActiveSkills.Count} skills, {skillResult.PrimaryDamageType.ToLowerInvariant()})",
                        skillResult.Emphasis,
                        string.Join(" ", skills.ActiveSkills.Select(s => $"{s.Name} {s.Description}")
                            .Concat(skills.TreeRanks.Keys)));
                    skillNotes.Add($"{string.Join(", ", skillResult.ActiveSkillNames)} " +
                                   $"({skillResult.PrimaryDamageType.ToLowerInvariant()} damage)");
                }
            }
        }

        if (_references.Count == 0)
        {
            SetStatus("The reference build(s) have no allocated nodes or skills to learn from.", error: true);
            return;
        }
        ApplyReferencePriorities();
        string skillSuffix = skillNotes.Count > 0
            ? $" Skills read from the guide: {string.Join(" · ", skillNotes.Distinct())}."
            : "";
        string emptySuffix = empty > 0 ? $" {empty} variant(s) had no allocated nodes." : "";
        SetStatus($"Focus priorities set from {_references.Count} reference(s) — " +
                  $"run Re-analyze to apply.{skillSuffix}{emptySuffix}");
    }

    [RelayCommand]
    private void ClearBuildReferences()
    {
        _references.Clear();
        ReferenceSummary = "";
        RefreshReferenceItems();
        SetStatus("References cleared — focus stats keep their current selection.");
    }

    /// <summary>
    /// The main window's loot filter as (affix name, guide weight) pairs — set by MainWindow so
    /// this window stays free of Core dependencies. Null when opened without a host.
    /// </summary>
    public Func<IReadOnlyList<GearStatPriority>>? GearPriorityProvider { get; set; }

    /// <summary>
    /// Adds the loaded loot filter as a reference: its per-slot affix priorities are translated
    /// to paragon attributes (<see cref="GearReference"/>) and vote in the same consensus as
    /// imported reference builds — the build's GEAR wish list and its paragon allocation then
    /// pull the focus stats together.
    /// </summary>
    [RelayCommand]
    private void AddGearReference()
    {
        var gear = GearPriorityProvider?.Invoke();
        if (gear is null || gear.Count == 0)
        {
            SetStatus("Load or import a filter with affix rules in the main window first — " +
                      "its affix priorities become the reference.", error: true);
            return;
        }

        var result = GearReference.EmphasisOf(gear);
        if (result.Emphasis.Count == 0)
        {
            SetStatus("None of the filter's affixes correspond to stats paragon boards grant.", error: true);
            return;
        }

        AddOrReplaceReference($"Loaded filter ({gear.Count} affix(es))", result.Emphasis);
        ApplyReferencePriorities();
        if (result.UnmappedStats.Count > 0)
        {
            var examples = string.Join(", ", result.UnmappedStats.Take(3));
            SetStatus($"Focus priorities set from {_references.Count} reference(s). " +
                      $"{result.UnmappedStats.Count} filter affix(es) have no paragon counterpart " +
                      $"(e.g. {examples}) — run Re-analyze to apply.");
        }
    }

    /// <summary>
    /// Maps the references' combined emphasis onto this layout's focusable stats. Core stats
    /// structurally dominate every allocation (most nodes grant them), so core and secondary
    /// stats rank against their own tier's best: ≥60% → High, ≥30% → Normal, ≥15% → Low,
    /// below → unselected noise. With <paramref name="assignFocus"/> false only the summary
    /// and details are rebuilt (project load: the saved focus selection stays authoritative).
    /// </summary>
    private void ApplyReferencePriorities(bool assignFocus = true)
    {
        var emphasisLists = _references.Select(r => r.Emphasis).ToList();
        var combined = BuildReference.Combine(emphasisLists, MaximizeFocus.CoreStats);
        var coreSet = MaximizeFocus.CoreStats.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoreByAttribute = combined.ToDictionary(
            e => e.Attribute, e => e.Score, StringComparer.OrdinalIgnoreCase);
        double coreTop = combined.Where(e => coreSet.Contains(e.Attribute))
            .Select(e => e.Score).DefaultIfEmpty(0).Max();
        double secondaryTop = combined.Where(e => !coreSet.Contains(e.Attribute))
            .Select(e => e.Score).DefaultIfEmpty(0).Max();

        string Agreement(string attribute) => _references.Count > 1
            ? $" ({BuildReference.AgreementCount(emphasisLists, attribute, MaximizeFocus.CoreStats)}/{_references.Count})"
            : "";

        var high = new List<string>();
        var normal = new List<string>();
        var low = new List<string>();
        foreach (var focus in FocusStats)
        {
            double score = scoreByAttribute.GetValueOrDefault(focus.Attribute);
            double tierTop = coreSet.Contains(focus.Attribute) ? coreTop : secondaryTop;
            if (tierTop <= 0 || score < tierTop * 0.15)
            {
                if (assignFocus)
                {
                    focus.IsSelected = false;
                    focus.Priority = "Normal";
                }
                continue;
            }
            string priority = score >= tierTop * 0.60 ? "High" : score >= tierTop * 0.30 ? "Normal" : "Low";
            if (assignFocus)
            {
                focus.IsSelected = true;
                focus.Priority = priority;
            }
            string label = focus.DisplayName.Split(" ×")[0] + Agreement(focus.Attribute);
            (priority == "High" ? high : priority == "Normal" ? normal : low).Add(label);
        }

        // Stats the references stack but this layout can't supply are worth knowing about.
        var focusable = FocusStats.Select(f => f.Attribute).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailable = combined
            .Where(e => !focusable.Contains(e.Attribute)
                && e.Score >= (coreSet.Contains(e.Attribute) ? coreTop : secondaryTop) * 0.30)
            .Select(e => ParagonDisplay.FormatAttributeName(e.Attribute))
            .Take(4)
            .ToList();

        RefreshReferenceItems();
        ReferenceSummary = "Derived priorities — " +
            $"High: {(high.Count > 0 ? string.Join(", ", high) : "—")}. " +
            $"Normal: {(normal.Count > 0 ? string.Join(", ", normal) : "—")}. " +
            $"Low: {(low.Count > 0 ? string.Join(", ", low) : "—")}." +
            (unavailable.Count > 0
                ? $" Not on your boards: {string.Join(", ", unavailable)}."
                : "");
        SolveDetails = $"Combined reference emphasis (relative to each build's own tier best" +
            $"{(_references.Count > 1 ? ", averaged across the guides" : "")}):" +
            Environment.NewLine +
            string.Join(Environment.NewLine, combined
                .Where(e => e.Score >= 0.05)
                .Select(e => $"  {e.Score,5:0.00}  {ParagonDisplay.FormatAttributeName(e.Attribute)}{Agreement(e.Attribute)}"));
        if (assignFocus)
            SetStatus($"Focus priorities set from {_references.Count} reference(s) — run Re-analyze to apply them.");
    }
}
