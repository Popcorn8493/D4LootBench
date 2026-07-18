using D4LootBench.Core.Models;

namespace D4LootBench.Core.Validation;

/// <summary>
/// Detects redundant and conflicting structure in a ruleset: rules that duplicate an
/// earlier rule, catch-all rules (no conditions) that shadow every rule below them,
/// rules fully covered by a broader rule (dead when the broader rule sits above them —
/// a conflict when the two disagree on show/hide), individual list entries already
/// handled elsewhere, groups of rules that could merge into one, and rules whose own
/// conditions contradict each other. Rules are matched first-match-wins, top to bottom.
/// Comparison is structural — names never matter, color matters only for Recolor rules,
/// and ID lists compare as sets so ordering differences don't defeat detection.
/// All non-exact reasoning is conservative: a relation is only claimed when provable
/// from the condition structure, so findings never have false positives.
/// </summary>
public static class RedundancyAnalyzer
{
    /// <summary>Rule at <see cref="Index"/> duplicates the earlier rule at <see cref="DuplicateOfIndex"/>.</summary>
    public sealed record DuplicateRule(int Index, int DuplicateOfIndex);

    /// <summary>
    /// Finds rules that duplicate an earlier rule and therefore never take effect.
    /// A later rule is a duplicate unless the earlier rule is disabled while the later
    /// one is enabled (the later rule is then the one that actually applies).
    /// </summary>
    public static IReadOnlyList<DuplicateRule> FindDuplicateRules(FilterRuleset ruleset)
    {
        var duplicates = new List<DuplicateRule>();
        var duplicateIndices = new HashSet<int>();

        for (var j = 1; j < ruleset.Rules.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                // Compare against originals only, so chains of copies all point at the first.
                if (duplicateIndices.Contains(i)) continue;
                if (!AreRulesEquivalent(ruleset.Rules[i], ruleset.Rules[j])) continue;
                if (!ruleset.Rules[i].IsEnabled && ruleset.Rules[j].IsEnabled) continue;

                duplicates.Add(new DuplicateRule(j, i));
                duplicateIndices.Add(j);
                break;
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Index of the first enabled rule with no conditions (matches every item) that still has
    /// enabled rules below it, or null when no rule is shadowed that way.
    /// </summary>
    public static int? FindShadowingCatchAll(FilterRuleset ruleset)
    {
        for (var i = 0; i < ruleset.Rules.Count; i++)
        {
            var rule = ruleset.Rules[i];
            if (!rule.IsEnabled || rule.Conditions.Count > 0) continue;
            return ruleset.Rules.Skip(i + 1).Any(r => r.IsEnabled) ? i : null;
        }
        return null;
    }

    /// <summary>Number of enabled rules below the given index.</summary>
    public static int CountEnabledRulesBelow(FilterRuleset ruleset, int index) =>
        ruleset.Rules.Skip(index + 1).Count(r => r.IsEnabled);

    public static bool AreRulesEquivalent(FilterRule a, FilterRule b) =>
        EffectsEqual(a, b) && ConditionSetsEquivalent(a.Conditions, b.Conditions);

    private static bool ConditionSetsEquivalent(IReadOnlyList<Condition> a, IReadOnlyList<Condition> b)
    {
        if (a.Count != b.Count) return false;

        var unmatched = b.ToList();
        foreach (var cond in a)
        {
            var match = unmatched.FindIndex(other => AreConditionsEquivalent(cond, other));
            if (match < 0) return false;
            unmatched.RemoveAt(match);
        }
        return true;
    }

    public static bool AreConditionsEquivalent(Condition a, Condition b) => (a, b) switch
    {
        (ItemTypeCondition x, ItemTypeCondition y) => SetEqual(x.TypeIds, y.TypeIds),

        (AffixCondition x, AffixCondition y) =>
            x.MinimumCount == y.MinimumCount &&
            SetEqual(x.AffixIds, y.AffixIds) &&
            SetEqual(x.GreaterEntries.Select(g => g.AffixId), y.GreaterEntries.Select(g => g.AffixId)),

        (OptionalAffixCondition x, OptionalAffixCondition y) =>
            x.MinimumCount == y.MinimumCount &&
            SetEqual(x.AffixIds, y.AffixIds) &&
            SetEqual(x.GreaterEntries.Select(g => g.AffixId), y.GreaterEntries.Select(g => g.AffixId)),

        (SpecificUniqueCondition x, SpecificUniqueCondition y) => SetEqual(x.UniqueIds, y.UniqueIds),

        (TalismanSetCondition x, TalismanSetCondition y) =>
            SetEqual(x.SetIds, y.SetIds) && SetEqual(x.SetEntries, y.SetEntries),

        (UnknownCondition x, UnknownCondition y) =>
            x.ConditionType == y.ConditionType && x.RawBytes.AsSpan().SequenceEqual(y.RawBytes),

        // Remaining condition types carry only scalar fields — record equality is structural.
        _ => a.Equals(b)
    };

    private static bool SetEqual<T>(IEnumerable<T> a, IEnumerable<T> b) =>
        a.ToHashSet().SetEquals(b.ToHashSet());

    // ── Full redundancy / conflict analysis ───────────────────────────────

    /// <summary>Kind of list a per-entry or merge finding refers to.</summary>
    public enum EntryKind { ItemType, Unique, Affix }

    /// <summary>Rule at <see cref="Index"/> is fully covered by the broader earlier rule at
    /// <see cref="ByIndex"/> and never takes effect. <see cref="SameEffect"/> distinguishes a
    /// plain redundancy from a conflict where the two rules disagree on what to do.</summary>
    public sealed record ShadowedRule(int Index, int ByIndex, bool SameEffect);

    /// <summary>Rule at <see cref="Index"/> is redundant: the later rule at <see cref="ByIndex"/>
    /// matches everything it matches with the same effect, and removing it cannot change any
    /// item's outcome (every enabled rule between them either agrees or provably never overlaps).</summary>
    public sealed record ObsoleteRule(int Index, int ByIndex);

    /// <summary>Entries (<see cref="Ids"/>) in a list condition of rule <see cref="Index"/> whose
    /// items are already fully handled by the earlier rule at <see cref="ByIndex"/>.</summary>
    public sealed record CoveredEntries(int Index, int ByIndex, EntryKind Kind, IReadOnlyList<uint> Ids, bool SameEffect);

    /// <summary>Enabled rules (<see cref="Indices"/>, ascending) that are identical except for one
    /// list of <see cref="Kind"/> — they can merge into a single rule without changing behavior.
    /// <see cref="MergedIds"/> is what that rule's combined list would contain, in first-appearance order.</summary>
    public sealed record MergeCandidate(IReadOnlyList<int> Indices, EntryKind Kind, IReadOnlyList<uint> MergedIds)
    {
        /// <summary>The rule the group collapses into: the first member with the combined list,
        /// ready to replace it (the later members are then removed).</summary>
        public required FilterRule MergedRule { get; init; }
    }

    /// <summary>Rule at <see cref="Index"/> contains two conditions no item can satisfy together.</summary>
    public sealed record Contradiction(int Index, Condition First, Condition Second);

    public sealed record RedundancyReport(
        IReadOnlyList<DuplicateRule> Duplicates,
        int? ShadowingCatchAll,
        IReadOnlyList<ShadowedRule> ShadowedRules,
        IReadOnlyList<ObsoleteRule> ObsoleteRules,
        IReadOnlyList<CoveredEntries> CoveredEntries,
        IReadOnlyList<MergeCandidate> MergeCandidates,
        IReadOnlyList<Contradiction> Contradictions);

    /// <summary>
    /// Runs every redundancy/conflict check, suppressing lower-level findings on rules already
    /// reported by a stronger one (a duplicate isn't also reported as shadowed, a shadowed rule's
    /// list entries aren't itemized, rules below a shadowing catch-all are left to that report).
    /// </summary>
    public static RedundancyReport Analyze(FilterRuleset ruleset)
    {
        var rules      = ruleset.Rules;
        var duplicates = FindDuplicateRules(ruleset);
        var dead       = duplicates.Select(d => d.Index).ToHashSet();
        var catchAll   = FindShadowingCatchAll(ruleset);
        var horizon    = catchAll ?? int.MaxValue; // rules below a shadowing catch-all are already reported dead

        var shadowed = FindShadowed(rules, dead, horizon);
        dead.UnionWith(shadowed.Select(s => s.Index));

        var obsolete = FindObsolete(rules, dead, catchAll, horizon);
        // A rule already reported obsolete is expected to be removed — don't base further
        // advice on it (as coverer or coveree), and don't offer to merge it.
        dead.UnionWith(obsolete.Select(o => o.Index));
        var covered = FindCoveredEntries(rules, dead, horizon);
        var merges  = FindMergeCandidates(rules, dead, horizon);

        return new RedundancyReport(
            duplicates, catchAll, shadowed, obsolete, covered, merges, FindContradictions(rules));
    }

    private static List<ShadowedRule> FindShadowed(List<FilterRule> rules, HashSet<int> duplicateIndices, int horizon)
    {
        var shadowed = new List<ShadowedRule>();
        for (var j = 1; j < rules.Count && j <= horizon; j++)
        {
            if (duplicateIndices.Contains(j)) continue;
            for (var i = 0; i < j; i++)
            {
                var earlier = rules[i];
                if (!earlier.IsEnabled || earlier.Conditions.Count == 0 || duplicateIndices.Contains(i)) continue;
                if (!RuleSubsumes(earlier, rules[j])) continue;
                shadowed.Add(new ShadowedRule(j, i, EffectsEqual(earlier, rules[j])));
                break;
            }
        }
        return shadowed;
    }

    private static List<ObsoleteRule> FindObsolete(List<FilterRule> rules, HashSet<int> dead, int? catchAll, int horizon)
    {
        var obsolete = new List<ObsoleteRule>();
        for (var j = 0; j < rules.Count - 1 && j <= horizon; j++)
        {
            var rule = rules[j];
            if (!rule.IsEnabled || dead.Contains(j) || j == catchAll) continue;
            for (var i = j + 1; i < rules.Count; i++)
            {
                var later = rules[i];
                if (!later.IsEnabled || dead.Contains(i)) continue;
                if (!EffectsEqual(rule, later) || !RuleSubsumes(later, rule)) continue;
                // Removing rule j drops its items onto the first matching rule below — safe only
                // when every enabled rule in between either agrees or provably never overlaps.
                if (!Enumerable.Range(j + 1, i - j - 1).All(k =>
                        !rules[k].IsEnabled || EffectsEqual(rules[k], rule) || RulesDisjoint(rules[k], rule)))
                    continue;
                obsolete.Add(new ObsoleteRule(j, i));
                break;
            }
        }
        return obsolete;
    }

    private static List<CoveredEntries> FindCoveredEntries(List<FilterRule> rules, HashSet<int> dead, int horizon)
    {
        var covered = new List<CoveredEntries>();
        for (var j = 1; j < rules.Count && j <= horizon; j++)
        {
            var rule = rules[j];
            if (!rule.IsEnabled || dead.Contains(j)) continue;
            foreach (var cond in rule.Conditions)
            {
                var (kind, ids) = ListEntriesOf(cond);
                // A single-entry list fully covered would make the whole rule shadowed — handled above.
                if (ids is null || ids.Count < 2) continue;

                var groups = new Dictionary<(int By, bool Same), List<uint>>();
                foreach (var id in ids)
                {
                    for (var i = 0; i < j; i++)
                    {
                        var earlier = rules[i];
                        if (!earlier.IsEnabled || earlier.Conditions.Count == 0 || dead.Contains(i)) continue;
                        if (!RuleSubsumes(earlier, RestrictListTo(rule, cond, id))) continue;
                        var key = (i, EffectsEqual(earlier, rule));
                        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                        list.Add(id);
                        break;
                    }
                }
                foreach (var g in groups)
                    covered.Add(new CoveredEntries(j, g.Key.By, kind, g.Value, g.Key.Same));
            }
        }
        return covered;
    }

    private static (EntryKind Kind, IReadOnlyList<uint>? Ids) ListEntriesOf(Condition cond) => cond switch
    {
        ItemTypeCondition it                          => (EntryKind.ItemType, it.TypeIds),
        SpecificUniqueCondition su                    => (EntryKind.Unique, su.UniqueIds),
        OptionalAffixCondition { MinimumCount: 1 } oa => (EntryKind.Affix, oa.AffixIds),
        _                                             => (default, null)
    };

    /// <summary>The rule with <paramref name="cond"/>'s ID list narrowed to a single entry —
    /// the slice of the rule's matches that entry is responsible for.</summary>
    private static FilterRule RestrictListTo(FilterRule rule, Condition cond, uint id)
    {
        Condition narrowed = cond switch
        {
            ItemTypeCondition          => new ItemTypeCondition([id]),
            SpecificUniqueCondition    => new SpecificUniqueCondition([id]),
            OptionalAffixCondition oa  => new OptionalAffixCondition([id], 1)
                { GreaterEntries = oa.GreaterEntries.Where(g => g.AffixId == id).ToList() },
            _ => throw new ArgumentException($"Not a list condition: {cond.GetType().Name}"),
        };
        return new FilterRule(rule.Name, rule.Visibility, rule.Color,
            rule.Conditions.Select(c => ReferenceEquals(c, cond) ? narrowed : c), rule.IsEnabled);
    }

    private static List<MergeCandidate> FindMergeCandidates(List<FilterRule> rules, HashSet<int> flagged, int horizon)
    {
        var merges = new List<MergeCandidate>();
        var used   = new HashSet<int>();
        for (var i = 0; i < rules.Count && i <= horizon; i++)
        {
            if (!rules[i].IsEnabled || flagged.Contains(i) || used.Contains(i)) continue;

            var group = new List<int> { i };
            (Condition Anchor, EntryKind Kind)? axis = null;
            var unionIds = new List<uint>();
            var seen     = new HashSet<uint>();
            void Union(IEnumerable<uint> ids) => unionIds.AddRange(ids.Where(seen.Add));

            for (var j = i + 1; j < rules.Count && j <= horizon; j++)
            {
                if (!rules[j].IsEnabled || flagged.Contains(j) || used.Contains(j)) continue;
                if (!EffectsEqual(rules[i], rules[j])) continue;
                if (FindMergeAxis(rules[i], rules[j]) is not (var mine, var theirs, var kind)) continue;
                if (axis is null)
                {
                    axis = (mine, kind);
                    Union(ListEntriesOf(mine).Ids!);
                }
                else if (!ReferenceEquals(axis.Value.Anchor, mine))
                {
                    continue; // pairs with rule i along a different condition — separate merge
                }
                Union(ListEntriesOf(theirs).Ids!);
                group.Add(j);
            }

            if (group.Count < 2 || axis is null) continue;
            var cap = axis.Value.Kind switch
            {
                EntryKind.Unique => SpecificUniqueCondition.MaxSelectionCount,
                EntryKind.Affix  => OptionalAffixCondition.MaxSelectionCount,
                _                => int.MaxValue,
            };
            if (unionIds.Count > cap) continue;

            // Merging pulls later members' matches up to the first member's position — safe only
            // when every enabled rule inside the span agrees or provably never overlaps the union.
            var mergedList = WithIds(axis.Value.Anchor, unionIds);
            var merged = new FilterRule(rules[i].Name, rules[i].Visibility, rules[i].Color,
                rules[i].Conditions.Select(c => ReferenceEquals(c, axis.Value.Anchor) ? mergedList : c));
            if (!Enumerable.Range(i + 1, group[^1] - i - 1).All(k =>
                    group.Contains(k) || !rules[k].IsEnabled ||
                    EffectsEqual(rules[k], rules[i]) || RulesDisjoint(rules[k], merged)))
                continue;

            merges.Add(new MergeCandidate(group, axis.Value.Kind, unionIds) { MergedRule = merged });
            used.UnionWith(group);
        }
        return merges;
    }

    private static Condition WithIds(Condition cond, IReadOnlyList<uint> ids) => cond switch
    {
        ItemTypeCondition         => new ItemTypeCondition(ids),
        SpecificUniqueCondition   => new SpecificUniqueCondition(ids),
        OptionalAffixCondition oa => new OptionalAffixCondition(ids, oa.MinimumCount),
        _ => throw new ArgumentException($"Not a list condition: {cond.GetType().Name}"),
    };

    /// <summary>
    /// The single condition pair the two rules differ by, when they have the same shape otherwise
    /// and that pair is a mergeable OR-list (item types, uniques, or any-of affixes with no
    /// greater-affix requirements). Null when the rules differ more, or not at all.
    /// </summary>
    private static (Condition Mine, Condition Theirs, EntryKind Kind)? FindMergeAxis(FilterRule a, FilterRule b)
    {
        if (a.Conditions.Count != b.Conditions.Count) return null;

        var unmatched = b.Conditions.ToList();
        Condition? aLeft = null;
        foreach (var ca in a.Conditions)
        {
            var m = unmatched.FindIndex(cb => AreConditionsEquivalent(ca, cb));
            if (m >= 0) unmatched.RemoveAt(m);
            else if (aLeft is null) aLeft = ca;
            else return null;
        }
        if (aLeft is null || unmatched.Count != 1) return null;

        return (aLeft, unmatched[0]) switch
        {
            (ItemTypeCondition x, ItemTypeCondition y)             => (x, y, EntryKind.ItemType),
            (SpecificUniqueCondition x, SpecificUniqueCondition y) => (x, y, EntryKind.Unique),
            (OptionalAffixCondition { MinimumCount: 1, GreaterEntries.Count: 0 } x,
             OptionalAffixCondition { MinimumCount: 1, GreaterEntries.Count: 0 } y) => (x, y, EntryKind.Affix),
            _ => null,
        };
    }

    private static List<Contradiction> FindContradictions(List<FilterRule> rules)
    {
        var contradictions = new List<Contradiction>();
        for (var r = 0; r < rules.Count; r++)
        {
            var conds = rules[r].Conditions;
            var pair = (
                from x in Enumerable.Range(0, conds.Count)
                from y in Enumerable.Range(x + 1, conds.Count - x - 1)
                where ConditionsDisjoint(conds[x], conds[y])
                select new Contradiction(r, conds[x], conds[y])).FirstOrDefault();
            if (pair is not null) contradictions.Add(pair);
        }
        return contradictions;
    }

    // ── Structural relations ──────────────────────────────────────────────

    /// <summary>Whether two rules do the same thing to a matched item. Color only matters for Recolor.</summary>
    public static bool EffectsEqual(FilterRule a, FilterRule b) =>
        a.Visibility == b.Visibility &&
        (a.Visibility != Visibility.Recolor || a.Color == b.Color);

    /// <summary>
    /// True when <paramref name="broader"/> provably matches every item <paramref name="narrower"/>
    /// matches (conditions only — effects are ignored). Conditions AND together, so it suffices
    /// that each of the broader rule's conditions is implied by one of the narrower rule's.
    /// A rule with no conditions subsumes everything.
    /// </summary>
    public static bool RuleSubsumes(FilterRule broader, FilterRule narrower) =>
        broader.Conditions.All(bc => narrower.Conditions.Any(nc => ConditionImplies(nc, bc)));

    /// <summary>
    /// True when every item matching <paramref name="stricter"/> provably also matches
    /// <paramref name="broader"/>. Conservative: unknown or unclear cases compare as
    /// equivalence, so false negatives are possible but false positives are not.
    /// </summary>
    public static bool ConditionImplies(Condition stricter, Condition broader) => (stricter, broader) switch
    {
        (ItemPowerCondition x, ItemPowerCondition y) =>
            x.Minimum <= x.Maximum && x.Minimum >= y.Minimum && x.Maximum <= y.Maximum,

        (RarityCondition x, RarityCondition y) => MaskImplies((uint)x.Mask, (uint)y.Mask),

        (ItemPropertiesCondition x, ItemPropertiesCondition y) =>
            MaskImplies((uint)x.PropertyMask, (uint)y.PropertyMask),

        (GreaterAffixCondition x, GreaterAffixCondition y) => x.MinimumCount >= y.MinimumCount,

        (ItemTypeCondition x, ItemTypeCondition y) => SetImplies(x.TypeIds, y.TypeIds),

        (SpecificUniqueCondition x, SpecificUniqueCondition y) => SetImplies(x.UniqueIds, y.UniqueIds),

        (AffixCondition x, AffixCondition y) =>
            AffixImplies(x.AffixIds, x.MinimumCount, x.GreaterEntries, y.AffixIds, y.MinimumCount, y.GreaterEntries),

        (OptionalAffixCondition x, OptionalAffixCondition y) =>
            AffixImplies(x.AffixIds, x.MinimumCount, x.GreaterEntries, y.AffixIds, y.MinimumCount, y.GreaterEntries),

        // Codex (stateless), talisman sets (pair semantics unclear), unknown types, and
        // cross-type pairs: only exact structural equivalence is safe to claim.
        _ => stricter.GetType() == broader.GetType() && AreConditionsEquivalent(stricter, broader),
    };

    /// <summary>Needing ≥k matches from a smaller affix set implies needing fewer from a superset,
    /// provided the broader condition's greater-affix requirements are all also imposed by the stricter one.</summary>
    private static bool AffixImplies(
        IReadOnlyList<uint> sIds, int sMin, IReadOnlyList<GreaterAffixEntry> sGreater,
        IReadOnlyList<uint> bIds, int bMin, IReadOnlyList<GreaterAffixEntry> bGreater) =>
        sMin >= bMin &&
        SetImplies(sIds, bIds) &&
        bGreater.Select(g => g.AffixId).ToHashSet().IsSubsetOf(sGreater.Select(g => g.AffixId));

    // An empty list/mask could mean "matches nothing" or "no restriction" depending on the game's
    // reading — implication is only claimed between two non-empty ones (or two empty ones), and
    // disjointness only between non-empty ones, so both readings stay safe.
    private static bool SetImplies(IReadOnlyList<uint> stricter, IReadOnlyList<uint> broader) =>
        stricter.Count > 0
            ? broader.Count > 0 && stricter.ToHashSet().IsSubsetOf(broader)
            : broader.Count == 0;

    private static bool MaskImplies(uint stricter, uint broader) =>
        stricter != 0 ? broader != 0 && (stricter & ~broader) == 0 : broader == 0;

    /// <summary>True when some pair of their conditions provably can't match the same item.</summary>
    public static bool RulesDisjoint(FilterRule a, FilterRule b) =>
        a.Conditions.Any(ca => b.Conditions.Any(cb => ConditionsDisjoint(ca, cb)));

    /// <summary>True when no item can satisfy both conditions. Conservative: false when unsure.</summary>
    public static bool ConditionsDisjoint(Condition a, Condition b) => (a, b) switch
    {
        (ItemPowerCondition x, ItemPowerCondition y) =>
            x.Minimum <= x.Maximum && y.Minimum <= y.Maximum &&
            (x.Maximum < y.Minimum || y.Maximum < x.Minimum),

        (RarityCondition x, RarityCondition y) =>
            x.Mask != 0 && y.Mask != 0 && (x.Mask & y.Mask) == 0,

        (ItemPropertiesCondition x, ItemPropertiesCondition y) =>
            x.PropertyMask != 0 && y.PropertyMask != 0 && (x.PropertyMask & y.PropertyMask) == 0,

        (ItemTypeCondition x, ItemTypeCondition y) =>
            x.TypeIds.Count > 0 && y.TypeIds.Count > 0 && !x.TypeIds.Intersect(y.TypeIds).Any(),

        (SpecificUniqueCondition x, SpecificUniqueCondition y) =>
            x.UniqueIds.Count > 0 && y.UniqueIds.Count > 0 && !x.UniqueIds.Intersect(y.UniqueIds).Any(),

        _ => false,
    };
}
