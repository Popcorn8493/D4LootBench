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
    // ── Pinned build summary (recomputed whenever the purchase set changes) ──

    [ObservableProperty]
    private string _pointsSummary = "";

    /// <summary>Spent fraction of the point pool, clamped to 1 for the progress bar.</summary>
    [ObservableProperty]
    private double _pointsFraction;

    [ObservableProperty]
    private bool _pointsOverBudget;

    [ObservableProperty]
    private string _nodeMixSummary = "";

    /// <summary>Points per board — compare against the game's per-board totals to find where a
    /// hand-copied build diverges.</summary>
    [ObservableProperty]
    private string _perBoardSummary = "";

    /// <summary>Shown when the path enters boards in a different order than the slot numbers —
    /// the game prices threshold tiers by that entry (attachment) order.</summary>
    [ObservableProperty]
    private string _attachOrderSummary = "";

    [ObservableProperty]
    private string _glyphSummary = "";

    [ObservableProperty]
    private string _thresholdSummary = "";

    /// <summary>Effective stat totals of the purchase set, core stats (with sheet offsets) first.</summary>
    public ObservableCollection<StatTotalLine> StatTotals { get; } = [];

    /// <summary>
    /// Threshold bonuses that CANNOT be met by buying more nodes — the boards can't supply
    /// enough of the stat, so the rest must come from level/gear (Character Stats).
    /// </summary>
    public ObservableCollection<string> ThresholdWarnings { get; } = [];

    /// <summary>Unmet thresholds from the last summary refresh — read by the item compare tool
    /// (a pending debounced refresh is applied first, so the read is never stale).</summary>
    public IReadOnlyList<ParagonStatNeed> UnmetThresholdNeeds
    {
        get
        {
            FlushBuildSummary();
            return _unmetThresholdNeeds;
        }
        private set => _unmetThresholdNeeds = value;
    }

    private IReadOnlyList<ParagonStatNeed> _unmetThresholdNeeds = [];

    /// <summary>Per purchased glyph socket: in-radius stat totals plus activation status, then solver notes.</summary>
    private string BuildSolveDetails(PlanResult result) =>
        BuildPurchaseReport(result.PurchasedCells.ToHashSet(), result.GlyphOutcomes, result.Notes);

    private string BuildPurchaseReport(
        HashSet<CellRef> purchased, IReadOnlyList<GlyphGoalOutcome> outcomes, IReadOnlyCollection<string> notes)
    {
        if (_graph is null)
            return "";

        var lines = new List<string>();
        foreach (var socket in GlyphSockets)
        {
            if (!purchased.Contains(socket.Socket))
                continue;

            int radius = socket.SelectedGlyph is null ? GlyphRadius.RadiusForLevel(50) : socket.Radius;
            var totals = GlyphRadius.AttributeTotalsInRange(
                _graph, socket.Socket, purchased, radius, GlyphRadius.GameMetric);
            string stats = string.Join(", ", totals
                .Where(kv => kv.Key.EndsWith("_Core", StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value:0} {ParagonDisplay.FormatAttributeName(kv.Key)}"));

            string glyph = socket.SelectedGlyph is null
                ? "no glyph assigned, assumes level 50+"
                : $"{socket.SelectedGlyph.Name} lvl {socket.Level}";
            var outcome = outcomes.FirstOrDefault(o => o.Goal.Socket == socket.Socket);
            string activation = "";
            if (outcome is not null)
            {
                activation = $" — {(outcome.Met ? "activated" : "NOT activated")} " +
                             $"({outcome.AchievedTotal:0}/{outcome.Goal.RequiredTotal:0} {ParagonDisplay.FormatAttributeName(outcome.Goal.SourceAttribute)})";
            }
            else if (socket.SourceAttribute is string sourceAttribute)
            {
                double have = totals.GetValueOrDefault(sourceAttribute);
                activation = $" — {(have >= socket.RequiredStat ? "activated" : "NOT activated")} " +
                             $"({have:0}/{socket.RequiredStat:0} {ParagonDisplay.FormatAttributeName(sourceAttribute)})";
            }

            // What the glyph turns the radius into. Scores are relative (game units aren't in the data).
            string delivery = "";
            if (socket.SelectedGlyph is ParagonGlyphDef def
                && GlyphInfo.BonusScalarAt(def, socket.Level) is double scalar)
            {
                delivery = GlyphInfo.IsAttributeMapped(def) && socket.SourceAttribute is string source
                    ? $", delivers {GlyphInfo.DeliveryTarget(def)} from {totals.GetValueOrDefault(source):0} " +
                      $"{ParagonDisplay.FormatAttributeName(source)} (benefit score {GlyphInfo.DeliveredBonus(def, socket.Level, totals.GetValueOrDefault(source)):0.#})"
                    : $", boosts {GlyphInfo.DeliveryTarget(def)} (scalar {scalar:0.#} at lvl {socket.Level})";
            }
            if (socket.SelectedGlyph is not null)
            {
                delivery += string.Join("", GlyphNodeBuffs.BuffsAt(socket.SelectedGlyph, socket.Level)
                    .Select(b => $", +{b.Percent:0.#}% to {b.Kind} nodes in radius (counted in the stat totals)"));
                // Placement-invariant extras: they depend only on glyph choice + activation, so
                // they inform the reader rather than the solve.
                if (GlyphInfo.LegendaryAffix(socket.SelectedGlyph) is GlyphAffixDef legendary)
                {
                    delivery += GlyphInfo.LegendaryMultiplierPercentAt(socket.SelectedGlyph, socket.Level) is double percent
                        ? $", legendary rank: ×{percent:0.#}% ({GlyphInfo.TagsLabel(legendary)})"
                        : $", legendary rank at lvl {GlyphInfo.LegendaryUpgradeLevel}+ adds a multiplicative bonus ({GlyphInfo.TagsLabel(legendary)})";
                }
                if (GlyphInfo.AdditionalBonusAffix(socket.SelectedGlyph) is GlyphAffixDef extra)
                {
                    if (extra.BonusPower?.Description is { Length: > 0 } bonus)
                        delivery += $", additional bonus while active: {bonus}";
                    else if (GlyphInfo.TagsLabel(extra) is { Length: > 0 } tags)
                        delivery += $", additional bonus while active: {tags}";
                }
            }

            lines.Add($"Socket on {socket.BoardName} ({glyph}): " +
                      $"{(stats.Length > 0 ? stats : "no stats")} in radius {radius}{activation}{delivery}");
        }

        // A Limit rule caps its GROUP, not the stat: rare nodes granting the same attribute are
        // separate per-name groups, which reads as "the limit broke" — say so explicitly.
        var cellsByGroup = NodeGrouping.CellsByGroup(_graph);
        foreach (var rule in NodeRules.Where(r => r.Mode is NodeRuleMode.Limit or NodeRuleMode.Minimal))
        {
            // Magic/Normal group keys are "<Kind>:<Attribute>:<Param>".
            var parts = rule.Group.Key.Split(':');
            if (parts.Length < 2 || parts[0] is not ("Magic" or "Normal"))
                continue;
            string attribute = parts[1];
            var groupSet = (cellsByGroup.GetValueOrDefault(rule.Group.Key) ?? []).ToHashSet();
            var outside = _graph.Vertices
                .Where(v => purchased.Contains(v.Cell) && !groupSet.Contains(v.Cell)
                    && v.Node.Attributes.Any(a => a.Value is not null
                        && string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase)))
                .Select(v => v.Node.Name ?? v.Node.InternalName)
                .Distinct()
                .ToList();
            if (outside.Count > 0)
            {
                string statName = ParagonDisplay.FormatAttributeName(attribute);
                lines.Add($"Limit note: '{rule.Group.DisplayName}' held at {groupSet.Count(purchased.Contains)} " +
                          $"of {rule.Limit}, but {outside.Count} other purchased node(s) also grant " +
                          $"{statName}: {string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? ", …" : "")} — " +
                          $"to cap the stat across every node kind, limit the 'Any: {statName}' row instead.");
            }
        }

        // Rare-node threshold bonuses: requirements scale with the board's attachment slot.
        var report = BuildStats.Compute(_graph, purchased, ParagonDatabase.Data, SheetStatOffsets(), SelectedClass,
            CellMultipliers(purchased));
        if (report.Thresholds.Count > 0)
        {
            lines.Add($"Threshold bonuses: {report.ThresholdsMet} of {report.Thresholds.Count} active " +
                      "(counting the character sheet stats from level/gear).");
            foreach (var status in report.Thresholds.Where(t => !t.Met).Take(6))
            {
                lines.Add($"  Not active: {status.NodeName} (slot {status.Cell.BoardSlot}) needs " +
                          $"{status.Requirement:0} {ParagonDisplay.FormatAttributeName(status.Attribute)} — have {status.Have:0}.");
            }
        }

        lines.AddRange(notes);
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeGlyph(GlyphSocketViewModel socket) =>
        $"{socket.SelectedGlyph!.Name} (lvl {socket.Level}, slot {socket.Socket.BoardSlot})";

    /// <summary>Recomputes the pinned build summary and the effective stat totals panel.</summary>
    /// <summary>
    /// The build's damage-formula breakdown (see <see cref="DamageModel"/>): one shared additive
    /// "+%" bucket with diminishing returns, the class main-stat multiplier, crit chance, and the
    /// legendary-rank glyph "×%" multipliers. The tooltip carries marginal guidance so "+10%
    /// damage" can be weighed against main stat or a full multiplier.
    /// </summary>
    private void RefreshDamageBalance(BuildStatsReport report, ISet<CellRef> purchased)
    {
        var socketed = GlyphSockets
            .Where(s => s.SelectedGlyph is not null && purchased.Contains(s.Socket))
            .Select(s => (s.SelectedGlyph!, s.Level))
            .ToList();
        string? mainStat = DamageModel.MainStatAttribute(SelectedClass);
        var profile = DamageModel.Profile(report.Totals, SelectedClass,
            mainStat is not null ? SheetStatOffsets().For(mainStat) : 0, socketed,
            additiveOffset: SheetAdditiveDamage / 100.0,
            situationalAdditiveOffset: SheetSituationalDamage / 100.0);
        _damageProfile = profile;
        _paragonAdditiveSlices = DamageModel.AdditiveSlices(report.Totals);

        string mults = profile.GlyphMultipliers.Count == 0
            ? "no ×% glyph mults"
            : $"{profile.GlyphMultipliers.Count} legendary glyph mult(s)";
        DamageSummary =
            $"Damage: additive +{profile.AdditiveFraction * 100:0}% · " +
            $"main stat ×{1 + profile.MainStatTotal / profile.MainStatCoefficient:0.00} · {mults} · " +
            $"expected ×{profile.ExpectedMultiplier:0.00} (×{profile.ExpectedMultiplierUnconditional:0.00} always-on)";

        string gearShare = profile.AdditiveOffsetFraction > 0
            ? $" incl. +{profile.AdditiveOffsetFraction * 100:0}% from gear;"
            : "";
        var detail = new List<string>
        {
            $"Additive bucket +{profile.AdditiveFraction * 100:0}%{gearShare} " +
            $"({profile.SituationalAdditiveFraction * 100:0}% of it situational) — " +
            $"another +10% additive ≈ +{profile.MarginalAdditive(0.10) * 100:0.#}% real damage.",
            $"Expected ×{profile.ExpectedMultiplier:0.00} assumes every condition holds " +
            $"(vulnerable/close/crit damage …); ×{profile.ExpectedMultiplierUnconditional:0.00} is the " +
            "always-on floor with the situational slice dropped.",
            $"Main stat {profile.MainStatTotal:0} {ParagonDisplay.FormatAttributeName(profile.MainStatAttribute)} " +
            $"(×{1 + profile.MainStatTotal / profile.MainStatCoefficient:0.00}) — " +
            $"+50 more ≈ +{profile.MarginalMainStat(50) * 100:0.#}% real damage.",
        };
        if (profile.CritChanceBonusFraction > 0)
            detail.Add($"Crit chance +{profile.CritChanceBonusFraction * 100:0.#}% from boards.");
        detail.AddRange(profile.GlyphMultipliers.Select(m =>
            $"{m.GlyphName} legendary rank ×{m.Percent:0.#}% ({m.Label}) — multiplies in full."));
        // Legendary nodes' ×% are real multipliers but conditional (skill/status/situation) —
        // listed beside the expected range, never folded into it.
        var legendaryNodes = Cells
            .Where(c => c.IsPurchased && c.Node.Kind == ParagonNodeKind.Legendary)
            .Select(c => c.Node)
            .ToList();
        var legendaryContext = CurrentLegendaryContext();
        foreach (var node in legendaryNodes)
        {
            if (LegendaryNodeInfo.HeadlineMultiplierPercent(node) is not double percent)
                continue;
            string applies = LegendaryRelevance.Of(node, legendaryContext) switch
            {
                >= 1 => "your build meets its condition — placement judging counts it",
                > 0 => "no specific condition — placement judging counts it at half",
                _ => "condition not detected in your focus stats/skills — placement judging ignores it",
            };
            detail.Add($"{node.Name ?? node.InternalName} (legendary node) {percent:0.#}%[x] — {applies}.");
        }
        if (legendaryNodes.Count > 1 && LegendaryNodeInfo.HeadlineProduct(legendaryNodes) is var product and > 1)
            detail.Add($"Legendary node ×% combined ×{product:0.00} if every condition holds at once.");
        if (profile.AdditiveOffsetFraction <= 0)
            detail.Add("Gear's additive damage isn't entered (Character Stats → Additive dmg % / Situational dmg %): " +
                       "the bucket is a lower bound, the additive marginal an upper bound.");
        DamageSummaryDetail = string.Join(Environment.NewLine, detail);
    }

    /// <summary>Coalesces high-frequency triggers (sheet-stat keystrokes, glyph level edits,
    /// per-socket restores) into one refresh after <see cref="SummaryDebounce"/> of quiet.</summary>
    private DispatcherTimer? _summaryTimer;

    private static readonly TimeSpan SummaryDebounce = TimeSpan.FromMilliseconds(150);

    private void ScheduleBuildSummary()
    {
        if (_summaryTimer is null)
        {
            _summaryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SummaryDebounce };
            _summaryTimer.Tick += (_, _) => RefreshBuildSummary();
        }
        _summaryTimer.Stop(); // restart the quiet period
        _summaryTimer.Start();
    }

    /// <summary>Runs a pending debounced refresh now — for readers that need current totals.</summary>
    private void FlushBuildSummary()
    {
        if (_summaryTimer?.IsEnabled == true)
            RefreshBuildSummary();
    }

    /// <summary>Recomputes the summary synchronously (and cancels any pending debounced one) —
    /// for callers that read the result right away.</summary>
    private void RefreshBuildSummary()
    {
        _summaryTimer?.Stop();
        UpdateGettingStarted();
        StatTotals.Clear();
        ThresholdWarnings.Clear();
        var purchasedCells = Cells.Where(c => c.IsPurchased).ToList();
        int spent = _graph is null
            ? purchasedCells.Count
            : GateCrossings.PointCost(_graph, purchasedCells.Select(c => c.Cell).ToList());
        int freeGates = purchasedCells.Count - spent;
        PointsSummary = $"{spent} of {TotalPoints} points spent" +
            (freeGates > 0 ? $" ({freeGates} crossing gate{(freeGates == 1 ? "" : "s")} free)" : "");
        PointsFraction = TotalPoints > 0 ? Math.Min(1.0, (double)spent / TotalPoints) : 0;
        PointsOverBudget = spent > TotalPoints;

        if (_graph is null || spent == 0)
        {
            NodeMixSummary = "No nodes allocated yet — solve a path or import a build.";
            PerBoardSummary = "";
            AttachOrderSummary = "";
            GlyphSummary = "";
            ThresholdSummary = "";
            DamageSummary = "";
            DamageSummaryDetail = "";
            _damageProfile = null;
            _paragonAdditiveSlices = default;
            UnmetThresholdNeeds = [];
            PathEdges.Clear();
            foreach (var rule in NodeRules)
                rule.UsedCount = 0;
            UpdateStatHighlights();
            UpdateBuffTooltips(new Dictionary<CellRef, double>());
            if (_graph is not null)
                UpdateThresholdTooltips(new Dictionary<string, double>(),
                    BuildStats.EffectiveAttachTiers(_graph, []));
            return;
        }

        var kindCounts = purchasedCells.CountBy(c => c.Node.Kind).ToDictionary();
        string Mix(ParagonNodeKind kind, string label) =>
            kindCounts.TryGetValue(kind, out int count) ? $"{count} {label}" : "";
        NodeMixSummary = string.Join("  ·  ", new[]
        {
            Mix(ParagonNodeKind.Normal, "normal"),
            Mix(ParagonNodeKind.Magic, "magic"),
            Mix(ParagonNodeKind.Rare, "rare"),
            Mix(ParagonNodeKind.Legendary, "legendary"),
            Mix(ParagonNodeKind.GlyphSocket, "socket"),
            Mix(ParagonNodeKind.Gate, "gate"),
        }.Where(part => part.Length > 0));

        PerBoardSummary = "Per board: " + string.Join("  ·  ", purchasedCells
            .GroupBy(c => c.Cell.BoardSlot)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {BoardDisplayName(_placedBoards[g.Key].Board)}: {g.Count()}"));

        // The game assigns threshold tiers by the order boards are ATTACHED, which follows the
        // path's gate purchases — say so whenever that differs from the slot numbering.
        var attachTiers = BuildStats.EffectiveAttachTiers(
            _graph, purchasedCells.Select(c => c.Cell).ToHashSet());
        AttachOrderSummary = Enumerable.Range(0, attachTiers.Count).Any(s => attachTiers[s] != s)
            ? "Attach order on the path: " + string.Join(" → ", Enumerable.Range(1, attachTiers.Count - 1)
                  .OrderBy(s => attachTiers[s])
                  .Select(s => $"{attachTiers[s]}. {BoardDisplayName(_placedBoards[s].Board)}")) +
              " — threshold requirements follow this order, not the slot numbers."
            : "";

        var purchased = purchasedCells.Select(c => c.Cell).ToHashSet();
        UpdatePathEdges(purchased);

        // Per-rule usage, so a Limit row shows the rule held (or by how much it couldn't).
        var cellsByGroup = NodeGrouping.CellsByGroup(_graph);
        foreach (var rule in NodeRules)
        {
            rule.UsedCount = cellsByGroup.TryGetValue(rule.Group.Key, out var groupCells)
                ? groupCells.Count(purchased.Contains)
                : 0;
        }

        int socketsBought = 0, glyphsAssigned = 0, glyphsActive = 0;
        foreach (var socket in GlyphSockets)
        {
            if (!purchased.Contains(socket.Socket))
                continue;
            socketsBought++;
            if (socket.SelectedGlyph is null)
                continue;
            glyphsAssigned++;
            if (socket.SourceAttribute is string source)
            {
                double have = GlyphRadius.AttributeTotalsInRange(
                        _graph, socket.Socket, purchased, socket.Radius, GlyphRadius.GameMetric)
                    .GetValueOrDefault(source);
                if (have >= socket.RequiredStat)
                    glyphsActive++;
            }
        }
        GlyphSummary = socketsBought == 0
            ? "No glyph sockets on the path."
            : $"{socketsBought} socket(s) on the path, {glyphsAssigned} glyph(s) socketed, {glyphsActive} activated";

        var multipliers = CellMultipliers(purchased);
        UpdateBuffTooltips(multipliers);
        var report = BuildStats.Compute(
            _graph, purchased, ParagonDatabase.Data, SheetStatOffsets(), SelectedClass, multipliers);
        ThresholdSummary = report.Thresholds.Count == 0
            ? ""
            : $"{report.ThresholdsMet} of {report.Thresholds.Count} rare-node threshold bonus(es) active";

        RefreshDamageBalance(report, purchased);

        UnmetThresholdNeeds = report.Thresholds
            .Where(t => !t.Met)
            .Select(t => new ParagonStatNeed(
                t.Attribute,
                ParagonDisplay.FormatAttributeName(t.Attribute),
                t.Requirement - t.Have,
                t.NodeName))
            .ToList();

        // Warn when a threshold can't be met even by buying every remaining stat node — the
        // missing amount has to come from level/gear (the Character Stats inputs).
        ThresholdWarnings.Clear();
        foreach (var status in report.Thresholds.Where(t => !t.Met).OrderBy(t => t.Requirement - t.Have))
        {
            string paragonKey = status.Attribute.EndsWith("_Total", StringComparison.Ordinal)
                ? status.Attribute[..^"_Total".Length] + "_Core"
                : status.Attribute;
            double available = _graph.Vertices
                .Where(v => !purchased.Contains(v.Cell))
                .Sum(v => v.Node.Attributes
                    .Where(a => !a.IsThresholdBonus && a.Value is not null
                        && string.Equals(a.Attribute, paragonKey, StringComparison.OrdinalIgnoreCase))
                    .Sum(a => a.Value!.Value)
                    * multipliers.GetValueOrDefault(v.Cell, 1.0));
            double deficit = status.Requirement - status.Have;
            if (available >= deficit)
                continue;
            if (ThresholdWarnings.Count == 4)
            {
                ThresholdWarnings.Add("…and more — see the solve details.");
                break;
            }
            ThresholdWarnings.Add(
                $"⚠ {status.NodeName} needs {deficit:0} more {ParagonDisplay.FormatAttributeName(status.Attribute)} " +
                $"and the boards can only add {available:0} — enter at least {deficit - available:0} more " +
                "from level/gear in Character Stats.");
        }

        // What every remaining (unpurchased) node could still add, per attribute — the "/ max"
        // suffix shows the ceiling these boards can reach for each stat.
        var availableByAttribute = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var vertex in _graph.Vertices)
        {
            if (purchased.Contains(vertex.Cell))
                continue;
            double multiplier = multipliers.GetValueOrDefault(vertex.Cell, 1.0);
            foreach (var attribute in vertex.Node.Attributes)
            {
                if (attribute.IsThresholdBonus || attribute.Value is not double value)
                    continue;
                availableByAttribute[attribute.Attribute] =
                    availableByAttribute.GetValueOrDefault(attribute.Attribute) + value * multiplier;
            }
        }

        // Core stats first, folding the sheet offset into a character total; then the rest.
        var sheet = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Strength_Core"] = SheetStrength,
            ["Intelligence_Core"] = SheetIntelligence,
            ["Willpower_Core"] = SheetWillpower,
            ["Dexterity_Core"] = SheetDexterity,
        };
        foreach (var core in MaximizeFocus.CoreStats)
        {
            double paragon = report.Totals.GetValueOrDefault(core);
            double offset = sheet.GetValueOrDefault(core);
            if (paragon == 0 && offset == 0)
                continue;
            double available = availableByAttribute.GetValueOrDefault(core);
            StatTotals.Add(new StatTotalLine(
                ParagonDisplay.FormatAttributeName(core),
                $"{paragon + offset:0.##}",
                isCore: true,
                attribute: core,
                detail: $"{paragon:0.##} from paragon + {offset:0.##} from level/gear" +
                        (available > 0 ? $"; the boards can still add {available:0.##}" : ""),
                possible: available > 0 ? $"/ {paragon + offset + available:0.##}" : ""));
        }
        foreach (var (attribute, value) in report.Totals
                     .Where(kv => kv.Value != 0
                         && !MaximizeFocus.CoreStats.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(kv => ParagonDisplay.FormatAttributeName(kv.Key), StringComparer.OrdinalIgnoreCase))
        {
            double available = availableByAttribute.GetValueOrDefault(attribute);
            StatTotals.Add(new StatTotalLine(
                ParagonDisplay.FormatAttributeName(attribute), FormatGain(value), isCore: false, attribute,
                detail: available > 0 ? $"The boards can still add {FormatGain(available)}" : null,
                possible: available > 0 ? $"/ {FormatGain(value + available)}" : ""));
        }

        UpdateStatHighlights();
        UpdateThresholdTooltips(report.Totals, attachTiers);
    }

    /// <summary>
    /// Rebuilds the line segments joining adjacent purchased cells (the start node counts as
    /// purchased), so the allocation reads as a connected route instead of scattered rings.
    /// </summary>
    private void UpdatePathEdges(IReadOnlySet<CellRef> purchased)
    {
        PathEdges.Clear();
        if (_graph is null || purchased.Count == 0)
            return;

        var cellByRef = Cells.ToDictionary(c => c.Cell);
        bool InPath(int vertex) =>
            vertex == _graph.StartVertex || purchased.Contains(_graph.Vertices[vertex].Cell);
        const double half = CellSize / 2;
        for (int i = 0; i < _graph.Vertices.Count; i++)
        {
            if (!InPath(i))
                continue;
            foreach (int j in _graph.Adjacency[i])
            {
                if (j <= i || !InPath(j))
                    continue;
                if (!cellByRef.TryGetValue(_graph.Vertices[i].Cell, out var a)
                    || !cellByRef.TryGetValue(_graph.Vertices[j].Cell, out var b))
                    continue;
                PathEdges.Add(new PathEdge(
                    a.CanvasLeft + half, a.CanvasTop + half,
                    b.CanvasLeft + half, b.CanvasTop + half));
            }
        }
    }

    /// <summary>
    /// Shows the effective node values under "+X% to [rarity] nodes in radius" glyph buffs
    /// (e.g. Marshal) on each affected cell's tooltip: "7 → 28.4 Willpower (×4.05 glyph buff)".
    /// Unpurchased cells in a buffed radius get it too — that's what buying one would grant.
    /// </summary>
    private void UpdateBuffTooltips(IReadOnlyDictionary<CellRef, double> multipliers)
    {
        foreach (var cell in Cells)
        {
            double multiplier = multipliers.GetValueOrDefault(cell.Cell, 1.0);
            if (Math.Abs(multiplier - 1.0) < 1e-9)
            {
                cell.BuffInfo = null;
                continue;
            }
            var effective = cell.Node.Attributes
                .Where(a => !a.IsThresholdBonus && a.Value is not null)
                .Select(a => $"{FormatGain(a.Value!.Value)} → {FormatGain(a.Value.Value * multiplier)} " +
                             ParagonDisplay.FormatAttributeName(a.Attribute))
                .ToList();
            cell.BuffInfo = effective.Count == 0
                ? null
                : $"Glyph node buff ×{multiplier:0.##} ({multiplier - 1:+0%;-0%}): {string.Join(", ", effective)}";
        }
    }

    // ── Click a Stat Totals row to light up its contributing nodes ───────

    private string? _highlightedStat;

    [RelayCommand]
    private void ToggleStatHighlight(StatTotalLine line)
    {
        _highlightedStat = string.Equals(_highlightedStat, line.Attribute, StringComparison.OrdinalIgnoreCase)
            ? null
            : line.Attribute;
        UpdateStatHighlights();
        SetStatus(_highlightedStat is null
            ? "Stat highlight cleared."
            : $"Highlighting allocated nodes granting {line.Name} — click the row again to clear.");
    }

    private void UpdateStatHighlights()
    {
        foreach (var line in StatTotals)
            line.IsHighlighted = string.Equals(_highlightedStat, line.Attribute, StringComparison.OrdinalIgnoreCase);
        foreach (var cell in Cells)
        {
            cell.IsStatHighlighted = _highlightedStat is not null && cell.IsPurchased
                && cell.Node.Attributes.Any(a => a.Value is not null
                    && string.Equals(a.Attribute, _highlightedStat, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Live tooltips: threshold math on rares, socketed glyph on sockets ─

    private static readonly Dictionary<string, ParagonThresholdDef> ThresholdsBySno =
        ParagonDatabase.Data.Thresholds.ToDictionary(t => t.SnoId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts the exact requirement-vs-have math on every threshold rare's tooltip.</summary>
    private void UpdateThresholdTooltips(IReadOnlyDictionary<string, double> totals, IReadOnlyList<int> attachTiers)
    {
        var sheet = SheetStatOffsets();
        foreach (var cell in Cells)
        {
            if (cell.Node.Thresholds.Count == 0)
                continue;
            var defs = cell.Node.Thresholds
                .Select(sno => ThresholdsBySno.GetValueOrDefault(sno))
                .Where(def => def is not null)
                .ToList();
            var def = defs.FirstOrDefault(d =>
                    d!.Classes.Count == 0 || d.Classes.Contains(SelectedClass, StringComparer.OrdinalIgnoreCase))
                ?? defs.FirstOrDefault();
            if (def?.Requirements.FirstOrDefault() is not ThresholdRequirement requirement)
                continue;

            double required = BuildStats.RequirementAt(requirement, attachTiers[cell.Cell.BoardSlot]);
            string attribute = requirement.Attribute;
            string paragonKey = attribute.EndsWith("_Total", StringComparison.Ordinal)
                ? attribute[..^"_Total".Length] + "_Core"
                : attribute;
            double paragonPart = totals.GetValueOrDefault(paragonKey) + totals.GetValueOrDefault(attribute);
            double sheetPart = attribute.EndsWith("_Total", StringComparison.Ordinal) ? sheet.For(attribute) : 0;
            double have = paragonPart + sheetPart;
            string status = have >= required
                ? cell.IsPurchased ? "ACTIVE" : "would activate if purchased"
                : $"{required - have:0} short";
            cell.DynamicInfo =
                $"Threshold at this board's attach tier ({attachTiers[cell.Cell.BoardSlot]}): " +
                $"needs {required:0} {ParagonDisplay.FormatAttributeName(attribute)}" +
                Environment.NewLine +
                $"Character total: {have:0} ({paragonPart:0} paragon + {sheetPart:0} level/gear) — {status}";
        }
    }

    /// <summary>Board-label anchor per slot, kept so glyph changes can retitle without a relayout.</summary>
    private (double Left, double Top)[] _labelPositions = [];

    /// <summary>
    /// Retitles each board label as "slot · Board / Glyph", fills the socket cell solid, and puts
    /// the glyph details on the socket's hover tooltip.
    /// </summary>
    private void UpdateGlyphLabels()
    {
        var glyphBySlot = GlyphSockets
            .Where(s => s.SelectedGlyph is not null)
            .ToDictionary(s => s.Socket.BoardSlot, s => s);

        BoardLabels.Clear();
        for (int slot = 0; slot < _placedBoards.Count && slot < _labelPositions.Length; slot++)
        {
            string text = $"{slot} · {BoardDisplayName(_placedBoards[slot].Board)}";
            if (glyphBySlot.TryGetValue(slot, out var socketed))
                text += $" / {socketed.SelectedGlyph!.Name}";
            BoardLabels.Add(new BoardLabel(text, _labelPositions[slot].Left, _labelPositions[slot].Top));
        }

        var cellByRef = Cells.ToDictionary(c => c.Cell);
        foreach (var socket in GlyphSockets)
        {
            if (!cellByRef.TryGetValue(socket.Socket, out var cell))
                continue;
            cell.HasSocketedGlyph = socket.SelectedGlyph is not null;
            cell.DynamicInfo = socket.SelectedGlyph is null
                ? null
                : $"Socketed: {socket.SelectedGlyph.Name} (lvl {socket.Level}, radius {socket.Radius})";
        }
    }
}
