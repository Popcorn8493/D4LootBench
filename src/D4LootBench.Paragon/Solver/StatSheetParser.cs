using System.Globalization;
using System.Text.RegularExpressions;

namespace D4LootBench.Paragon.Solver;

/// <summary>Which of the planner's two gear-damage fields a stat-sheet row feeds.</summary>
public enum SheetBucket
{
    /// <summary>Unconditional "+% damage" — the Additive dmg % field.</summary>
    AlwaysOn,

    /// <summary>Conditional "+% damage" (vulnerable, crit, vs/while/to …) — the Situational field.</summary>
    Situational,

    /// <summary>Not part of the additive bucket (weapon damage, speeds, chances, unknown rows).</summary>
    Ignored,
}

/// <summary>One OCR line with enough geometry to rebuild the panel's rows (any consistent units).</summary>
public sealed record SheetOcrLine(string Text, double CenterY, double Height);

/// <summary>One row read off the in-game stats details panel.</summary>
public sealed record SheetStatLine(string Label, double Value, double BaseValue, SheetBucket Bucket, string? Note)
{
    /// <summary>What the row adds to its bucket: the sheet total minus the game's built-in base.</summary>
    public double Contribution => Math.Max(0, Value - BaseValue);
}

/// <summary>
/// Turns OCR lines from a screenshot of the in-game stats details panel (Offense section) into
/// classified additive-bucket rows. The panel is two columns and the OCR engine reads them
/// SEPARATELY — every label first, then every value — so rows are rebuilt geometrically: each
/// label line joins the value whose row band it sits in (which also folds wrapped two-line
/// labels back together, and drops a label whose value the engine missed rather than pairing it
/// with a neighbor's). The sheet aggregates every source per CATEGORY (All Damage, Vulnerable
/// Damage, Damage vs Elites …) but never splits always-on from conditional or gear from paragon,
/// so each row is classified here and the game's built-in bases (crit 50%, vulnerable 20%,
/// overpower 50%) are excluded — they live elsewhere in the damage model. "Damage with
/// &lt;type&gt;" only applies to skills of that type: the largest such row is assumed to be the
/// build's main damage type (always-on for it), the rest are ignored; every default is meant to
/// be reviewed, so uncertain classifications carry a <see cref="SheetStatLine.Note"/>.
/// </summary>
public static class StatSheetParser
{
    /// <summary>The value column: a trailing number, thousands commas, optional %.</summary>
    private static readonly Regex TrailingNumber =
        new(@"(?<value>\d[\d,]*(?:\.\d+)?)\s*%?\s*$", RegexOptions.Compiled);

    /// <summary>The detail tooltip's "You have +289.1% of this stat from items and Paragon".</summary>
    private static readonly Regex YouHave =
        new(@"you have\s*\+?\s*(?<value>\d[\d,]*(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The detail tooltip's header, "Critical Strike Damage Bonus: 2,991.5%".</summary>
    private static readonly Regex TooltipHeader =
        new(@"^(?<label>[A-Za-z][A-Za-z '\-]*?)\s*[:;]\s*\+?\d[\d,]*(?:\.\d+)?\s*%", RegexOptions.Compiled);

    private static readonly Regex LabelJunk = new(@"^[^A-Za-z]+|[^A-Za-z]+$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<SheetStatLine> Parse(IReadOnlyList<SheetOcrLine> ocrLines)
    {
        if (TryParseStatTooltip(ocrLines, out var tooltipRow))
        {
            var single = new List<SheetStatLine> { tooltipRow };
            ResolveDamageTypes(single);
            return single;
        }

        var rows = new List<(double Y, string Label, double Value)>();
        var values = new List<(double Y, double Value)>();
        var labels = new List<(double Y, string Text)>();
        foreach (var line in ocrLines)
        {
            var match = TrailingNumber.Match(line.Text);
            if (match.Success && double.TryParse(match.Groups["value"].Value.Replace(",", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                string inline = CleanLabel(line.Text[..match.Index]);
                if (inline.Length > 0)
                    rows.Add((line.CenterY, inline, value)); // label and value on one OCR line
                else
                    values.Add((line.CenterY, value));
            }
            else if (CleanLabel(line.Text) is { Length: > 0 } label)
            {
                labels.Add((line.CenterY, label));
            }
        }

        // A label belongs to the value vertically nearest to it, but only within a row band —
        // beyond that it's a row whose value the engine missed, and inventing a pairing would
        // silently attach its text to a neighbor.
        double band = RowBand(ocrLines, values);
        var joined = values.Select(v => (v.Y, v.Value, Parts: new List<(double Y, string Text)>())).ToList();
        foreach (var label in labels)
        {
            int nearest = -1;
            for (int i = 0; i < joined.Count; i++)
            {
                if (nearest < 0 || Math.Abs(joined[i].Y - label.Y) < Math.Abs(joined[nearest].Y - label.Y))
                    nearest = i;
            }
            if (nearest >= 0 && Math.Abs(joined[nearest].Y - label.Y) <= band)
                joined[nearest].Parts.Add(label);
        }
        rows.AddRange(joined
            .Where(v => v.Parts.Count > 0)
            .Select(v => (v.Y,
                Label: string.Join(" ", v.Parts.OrderBy(p => p.Y).Select(p => p.Text)),
                v.Value)));

        var result = rows.OrderBy(r => r.Y).Select(r => Classify(r.Label, r.Value)).ToList();
        ResolveDamageTypes(result);
        return result;
    }

    /// <summary>Bucket sums in sheet units (percent): what the two planner fields should hold.</summary>
    public static (double AlwaysOn, double Situational) Sum(IEnumerable<SheetStatLine> lines)
    {
        double alwaysOn = 0, situational = 0;
        foreach (var line in lines)
        {
            if (line.Bucket == SheetBucket.AlwaysOn)
                alwaysOn += line.Contribution;
            else if (line.Bucket == SheetBucket.Situational)
                situational += line.Contribution;
        }
        return (alwaysOn, situational);
    }

    /// <summary>
    /// How far (vertically) a label may sit from its value. Wrapped label halves are about 0.7
    /// line heights off the row center, while the nearest WRONG value is a full row pitch (≥ 2
    /// line heights of text plus padding) away — 1.6 line heights separates the two, and stays
    /// honest when a missed value leaves a hole in the value column (value-gap statistics don't:
    /// the hole doubles the observed pitch and would let the orphaned label steal a neighbor).
    /// </summary>
    private static double RowBand(IReadOnlyList<SheetOcrLine> ocrLines, List<(double Y, double Value)> values)
    {
        double lineHeight = Median(ocrLines.Select(l => l.Height).Where(h => h > 0).ToList());
        if (lineHeight > 0)
            return 1.6 * lineHeight;
        var pitches = values.Select(v => v.Y).OrderBy(y => y).ToList();
        var gaps = pitches.Zip(pitches.Skip(1), (a, b) => b - a).Where(g => g > 0).ToList();
        return gaps.Count > 0 ? 0.4 * Median(gaps) : double.MaxValue;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static string CleanLabel(string text) =>
        LabelJunk.Replace(Whitespace.Replace(text, " ").Trim(), "");

    /// <summary>
    /// A single stat's HOVER tooltip instead of the panel: its "You have +X% of this stat from
    /// items and Paragon" line is the game's own additive decomposition — exact, with the
    /// inherent base and multiplicative ×% sources already excluded (the panel headline folds
    /// both in, so this is the accurate way to enter stats like Critical Strike Damage).
    /// </summary>
    private static bool TryParseStatTooltip(IReadOnlyList<SheetOcrLine> lines, out SheetStatLine row)
    {
        row = null!;
        if (!lines.Any(l => l.Text.Contains("of this stat", StringComparison.OrdinalIgnoreCase)
                            || l.Text.Contains("items and paragon", StringComparison.OrdinalIgnoreCase)))
            return false;

        double? value = null;
        foreach (var line in lines)
        {
            var match = YouHave.Match(line.Text);
            if (match.Success && double.TryParse(match.Groups["value"].Value.Replace(",", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                break;
            }
        }
        if (value is not double statValue)
            return false;

        string? label = lines
            .Select(l => TooltipHeader.Match(l.Text.Trim()))
            .Where(m => m.Success)
            .Select(m => CleanLabel(m.Groups["label"].Value))
            .FirstOrDefault(l => l.Length > 0);
        // The colon is an easy OCR casualty — fall back to the first line ending in a number.
        label ??= lines
            .Select(l => TrailingNumber.Match(l.Text) is { Success: true } m
                ? CleanLabel(l.Text[..m.Index]) : "")
            .FirstOrDefault(l => l.Length > 0);
        if (label is null)
            return false;
        if (label.EndsWith(" Bonus", StringComparison.OrdinalIgnoreCase))
            label = label[..^" Bonus".Length];

        row = Classify(label, statValue) with
        {
            BaseValue = 0,
            Note = "Exact from the stat's hover tooltip: the 'you have +X% from items and " +
                   "Paragon' line already excludes the inherent base and multiplicative ×% sources.",
        };
        return true;
    }

    private static SheetStatLine Classify(string label, double value)
    {
        string norm = label.ToLowerInvariant();

        SheetStatLine Line(SheetBucket bucket, double baseValue = 0, string? note = null) =>
            new(label, value, baseValue, bucket, note);

        if (norm == "all damage")
            return Line(SheetBucket.AlwaysOn, note: "Generic +% damage — applies to every hit.");
        if (norm == "critical strike damage")
            return Line(SheetBucket.Situational, baseValue: 50,
                "Base 50% excluded (the base ×1.5 crit multiplier is modeled separately) — but the " +
                "panel headline ALSO folds in multiplicative ×% crit sources, so this is an " +
                "overestimate. For the exact share, hover the stat in game, snip its tooltip (the " +
                "'You have +X% from items and Paragon' line), and Scan clipboard again — or edit " +
                "the number here.");
        if (norm == "vulnerable damage")
            return Line(SheetBucket.Situational, baseValue: 20,
                "Base 20% excluded — the ×1.2 vulnerable base multiplier isn't part of the bucket.");
        if (norm == "overpower damage")
            return Line(SheetBucket.Situational, baseValue: 50, "Base 50% excluded.");
        if (norm.StartsWith("damage with "))
            return Line(SheetBucket.Ignored); // resolved against its siblings in ResolveDamageTypes

        if (norm.StartsWith("damage vs ") || norm.StartsWith("damage against ")
            || norm.StartsWith("damage to ") || norm.StartsWith("damage while ")
            || norm == "damage over time")
            return Line(SheetBucket.Situational);
        if (norm.EndsWith("skill damage"))
            return Line(SheetBucket.Situational,
                note: "Effectively always-on if this is your main skill's category — flip it.");

        // Recognized non-bucket rows get a reason; anything else is ignored without one.
        if (norm.Contains("weapon damage"))
            return Line(SheetBucket.Ignored, note: "Base weapon damage, not a +% bucket stat.");
        if (norm.Contains("speed"))
            return Line(SheetBucket.Ignored, note: "Speed isn't part of the additive bucket.");
        if (norm.Contains("critical strike chance"))
            return Line(SheetBucket.Ignored,
                note: "Crit chance is modeled separately (5% base + paragon crit nodes).");
        if (norm.Contains("chance") || norm.Contains("lucky hit") || norm.Contains("thorns"))
            return Line(SheetBucket.Ignored, note: "Not part of the additive damage bucket.");
        return Line(SheetBucket.Ignored, note: "Not recognized as an additive-damage stat.");
    }

    /// <summary>
    /// A type bonus only applies to skills dealing that type, so exactly one "Damage with X" row
    /// can be always-on. Builds stack their own type, so the largest is assumed to be it.
    /// </summary>
    private static void ResolveDamageTypes(List<SheetStatLine> rows)
    {
        int best = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Label.StartsWith("Damage with ", StringComparison.OrdinalIgnoreCase))
                continue;
            if (best < 0 || rows[i].Value > rows[best].Value)
                best = i;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Label.StartsWith("Damage with ", StringComparison.OrdinalIgnoreCase))
                continue;
            string type = rows[i].Label["Damage with ".Length..];
            rows[i] = i == best
                ? rows[i] with
                {
                    Bucket = SheetBucket.AlwaysOn,
                    Note = $"Assumed {type} is your main skill's damage type (largest type bonus) — " +
                           "always-on for matching skills. Ignore it if that's wrong.",
                }
                : rows[i] with
                {
                    Bucket = SheetBucket.Ignored,
                    Note = $"Only applies to {type} skills — flip to always-on if that's your main type.",
                };
        }
    }
}
