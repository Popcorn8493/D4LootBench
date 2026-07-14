using System.Text.RegularExpressions;

namespace D4LootBench.Core.Compare;

/// <summary>One stat line read off a tooltip screenshot, cleaned for catalog lookup.</summary>
public sealed record TooltipStatLine(
    string StatText, double? Value, bool IsGreater, bool IsTransfigured, string RawLine);

/// <summary>What could be recognized on an item tooltip screenshot.</summary>
public sealed record ParsedTooltip(
    string? ItemName,
    string? SlotText,
    bool IsUnique,
    IReadOnlyList<TooltipStatLine> Stats);

/// <summary>
/// Turns OCR'd item-tooltip text into structured fields the compare window can fill. Pure text
/// heuristics — the OCR engine itself lives in the App (Windows.Media.Ocr needs WinRT). Rules of
/// thumb: the rarity line ("Ancestral Legendary Gloves") splits name from stats; a stat line
/// starts with '+' or is a leading percentage (base lines like "1,224 Armor" or "925 Item Power"
/// carry neither); roll-range brackets and the rolled number are stripped so what remains matches
/// catalog affix names; a leading star glyph (or its common OCR mangling '*') marks a Greater
/// Affix; lines under a header containing "transfigur" are transfigured stats.
/// </summary>
public static partial class ItemTooltipParser
{
    private static readonly string[] RarityWords =
        ["Common", "Magic", "Rare", "Legendary", "Unique", "Mythic"];

    /// <summary>Glyphs OCR plausibly produces for the Greater Affix star icon.</summary>
    private const string GreaterMarkers = "*★☆✦✧✶";

    [GeneratedRegex(@"\[[^\[\]]*\]\s*%?")]
    private static partial Regex RollRange();

    [GeneratedRegex(@"[+-]?\d{1,3}(?:[,.]\d{3})*(?:\.\d+)?\s*%?")]
    private static partial Regex Number();

    public static ParsedTooltip Parse(IReadOnlyList<string> lines)
    {
        var cleaned = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        int typeLineIndex = cleaned.FindIndex(l =>
            RarityWords.Any(r => WordsOf(l).Contains(r, StringComparer.OrdinalIgnoreCase)));

        string? itemName = null;
        string? slotText = null;
        bool isUnique = false;
        if (typeLineIndex >= 0)
        {
            var words = WordsOf(cleaned[typeLineIndex]);
            int rarityAt = Array.FindIndex(words, w =>
                RarityWords.Contains(w, StringComparer.OrdinalIgnoreCase));
            isUnique = words[rarityAt].Equals("Unique", StringComparison.OrdinalIgnoreCase)
                       || words[rarityAt].Equals("Mythic", StringComparison.OrdinalIgnoreCase);
            slotText = string.Join(' ', words.Skip(rarityAt + 1));
            if (slotText.Length == 0)
                slotText = null;
            itemName = string.Join(' ', cleaned.Take(typeLineIndex)).Trim();
            if (itemName.Length == 0)
                itemName = null;
        }
        else if (cleaned.Count > 0)
        {
            itemName = cleaned[0];
        }

        var stats = new List<TooltipStatLine>();
        bool inTransfigured = false;
        foreach (var raw in cleaned.Skip(typeLineIndex + 1))
        {
            if (raw.Contains("transfigur", StringComparison.OrdinalIgnoreCase))
            {
                inTransfigured = true;
                continue;
            }
            if (TryParseStatLine(raw, inTransfigured, out var stat))
                stats.Add(stat);
        }
        return new ParsedTooltip(itemName, slotText, isUnique, stats);
    }

    private static bool TryParseStatLine(string raw, bool transfigured, out TooltipStatLine stat)
    {
        stat = null!;
        string line = raw;

        // Leading bullet/icon garbage; a star glyph among it marks a Greater Affix.
        bool greater = false;
        int start = 0;
        while (start < line.Length && !char.IsLetterOrDigit(line[start]) && line[start] != '+')
        {
            if (GreaterMarkers.Contains(line[start]))
                greater = true;
            start++;
        }
        line = line[start..].Trim();
        if (line.Length == 0)
            return false;

        // Stat lines carry '+' or a leading percentage; base lines ("1,224 Armor",
        // "925 Item Power", "1.10 Attacks per Second") carry neither.
        bool startsPlus = line[0] == '+';
        bool leadingPercent = !startsPlus && char.IsDigit(line[0])
            && Number().Match(line) is { Success: true, Index: 0 } m
            && m.Value.TrimEnd().EndsWith('%');
        if (!startsPlus && !leadingPercent)
            return false;

        string text = RollRange().Replace(line, " ");
        var number = Number().Match(text);
        double? value = null;
        if (number.Success)
        {
            string digits = number.Value.TrimStart('+').TrimEnd('%', ' ').Replace(",", "");
            if (double.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                value = parsed;
            text = text.Remove(number.Index, number.Length);
        }
        text = string.Join(' ', WordsOf(text.Replace("+", " ").Replace("%", " ")));
        if (text.Length == 0)
            return false;

        // Aspect/ability paragraphs can start with a percentage too; affix names are short.
        if (WordsOf(text).Length > 9)
            return false;

        stat = new TooltipStatLine(text, value, greater && !transfigured, transfigured, raw);
        return true;
    }

    private static string[] WordsOf(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
