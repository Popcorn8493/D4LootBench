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
/// carry neither, and OCR-dropped '+' signs are tolerated by excluding known base-line words);
/// roll-range brackets and the rolled number are stripped so what remains matches
/// catalog affix names; a leading star glyph (or its common OCR mangling '*') marks a Greater
/// Affix; lines under a header containing "transfigur" are transfigured stats.
/// </summary>
public static partial class ItemTooltipParser
{
    private static readonly string[] RarityWords =
        ["Common", "Magic", "Rare", "Legendary", "Unique", "Mythic"];

    /// <summary>What a base line reads as once its leading number is stripped — these keep a
    /// plus-less "925 Item Power" from being taken for a stat whose '+' the OCR dropped.</summary>
    private static readonly string[] BaseLineMarkers =
        ["item power", "armor", "damage per second", "damage per hit", "attacks per second",
         "requires level", "durability", "empty socket", "gold"];

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
        // "925 Item Power", "1.10 Attacks per Second") carry neither. OCR frequently loses the
        // '+', so a bare leading number is accepted too — provided what follows isn't a known
        // base line and holds no further digits (a damage range would).
        bool startsPlus = line[0] == '+';
        bool leadingNumber = !startsPlus && char.IsDigit(line[0])
            && Number().Match(line) is { Success: true, Index: 0 };
        bool leadingPercent = leadingNumber && Number().Match(line).Value.TrimEnd().EndsWith('%');
        if (!startsPlus && !leadingNumber)
            return false;

        string text = RollRange().Replace(line, " ");
        // OCR can wrap a roll range onto the next line, leaving an unterminated "[6.5 -" here.
        if (text.IndexOf('[') is int bracket and >= 0 && !text.Contains(']'))
            text = text[..bracket];
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

        // The plus-less path is heuristic — hold it to a stricter standard than an explicit '+'.
        if (!startsPlus && !leadingPercent && (text.Any(char.IsDigit) || IsBaseLine(text)))
            return false;

        stat = new TooltipStatLine(text, value, greater && !transfigured, transfigured, raw);
        return true;
    }

    private static bool IsBaseLine(string text)
    {
        string norm = text.ToLowerInvariant();
        return BaseLineMarkers.Any(m => norm == m || norm.StartsWith(m + " ") || norm.EndsWith(" " + m));
    }

    private static string[] WordsOf(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
