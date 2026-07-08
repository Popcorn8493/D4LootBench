using System.Globalization;
using D4LootBench.Paragon.Models;

namespace D4LootBench.Paragon;

/// <summary>Human-readable rendering of node data (values are fractions for percent attributes).</summary>
public static class ParagonDisplay
{
    /// <summary>"Life_Percent_Bonus" → "Life Bonus", "Strength_Core" → "Strength".</summary>
    public static string FormatAttributeName(string attribute) => attribute
        .Replace("_Core", "")
        .Replace("_Percent", "")
        .Replace('_', ' ');

    public static string FormatAttribute(NodeAttribute attribute)
    {
        string name = FormatAttributeName(attribute.Attribute);

        if (attribute.Value is not double value)
            return name;

        string number = Math.Abs(value) < 1 && value != 0
            ? $"+{Trim(value * 100)}%"
            : $"+{Trim(value)}";
        return $"{number} {name}";

        static string Trim(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Multi-line description: name, kind, stats (threshold bonuses marked), legendary power.</summary>
    public static string DescribeNode(ParagonNodeDef node)
    {
        var lines = new List<string>
        {
            node.Name ?? node.InternalName.Replace('_', ' '),
            node.Kind switch
            {
                ParagonNodeKind.GlyphSocket => "Glyph Socket",
                ParagonNodeKind.Gate => "Board Attachment Gate",
                ParagonNodeKind.Start => "Starting Node",
                _ => node.Kind.ToString(),
            },
        };

        foreach (var attribute in node.Attributes.Where(a => !a.IsThresholdBonus))
            lines.Add(FormatAttribute(attribute));
        foreach (var attribute in node.Attributes.Where(a => a.IsThresholdBonus))
            lines.Add($"{FormatAttribute(attribute)} (threshold bonus)");

        if (node.Power?.Description is string power)
            lines.Add(power);

        return string.Join(Environment.NewLine, lines);
    }
}
