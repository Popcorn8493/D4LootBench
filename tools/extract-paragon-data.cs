// Extracts paragon-data.json from a DiabloTools/d4data checkout.
//
// Usage:
//   dotnet run tools/extract-paragon-data.cs -- --d4data <path-to-d4data> [--out paragon-data.json]
//
// The d4data checkout only needs these paths (a blob-filtered sparse checkout is enough):
//   json/base/meta/ParagonBoard/          json/base/meta/ParagonNode/
//   json/base/meta/ParagonGlyph/          json/base/meta/ParagonGlyphAffix/
//   json/base/meta/ParagonThreshold/      json/base/meta/GameBalance/AttributeFormulas.gam.json
//   json/enUS_Text/meta/StringList/Paragon*  json/enUS_Text/meta/StringList/Power_Paragon*
//   json/base/meta/Power/Paragon_*          json/base/meta/Power/ParagonGlyph_*   (legendary/glyph power values)

#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string? d4dataPath = null;
string outPath = "paragon-data.json";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--d4data" && i + 1 < args.Length) d4dataPath = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
}
if (d4dataPath is null || !Directory.Exists(d4dataPath))
{
    Console.Error.WriteLine("Missing or invalid --d4data <path to DiabloTools/d4data checkout>");
    return 1;
}

string meta = Path.Combine(d4dataPath, "json", "base", "meta");
string stringLists = Path.Combine(d4dataPath, "json", "enUS_Text", "meta", "StringList");

// The six ParagonPowerBudgetMultiplierNode* functions are engine built-ins, absent from the
// data files. Constants below were calibrated empirically against displayed in-game values
// (Season 14 / patch 3.1.x): a magic crit-damage node is "3 * MagicOffensive()" and shows 7.5%,
// a rare crit node is "3 * RareMajorOffensive()" and shows 15%, magic max-life ("1 * MagicDefensive()")
// shows 2.0%, rare max-life shows 4.0%. Values are fractions (0.05 = 5%); flat-stat formulas
// carry their own "* 100"-style scaling.
var multipliers = new Dictionary<string, double>
{
    ["ParagonPowerBudgetMultiplierNodeMagicOffensive"] = 0.025,
    ["ParagonPowerBudgetMultiplierNodeMagicDefensive"] = 0.02,
    ["ParagonPowerBudgetMultiplierNodeRareMinorOffensive"] = 0.05,
    ["ParagonPowerBudgetMultiplierNodeRareMinorDefensive"] = 0.04,
    ["ParagonPowerBudgetMultiplierNodeRareMajorOffensive"] = 0.05,
    ["ParagonPowerBudgetMultiplierNodeRareMajorDefensive"] = 0.04,
    // Power tuning (legendary node effects, glyph Additional Bonuses), calibrated at patch
    // 3.2.1 against published values. GlyphThresholdBonusRare: Superiority "1 * f()" = +10% DR
    // and Talon "2.5 * f()" = 25% (both 3.2.1 patch notes), Exploit "3 * 10 * f()" = 3 s
    // Vulnerable. NodeLegendary: designers write these pre-divided by it — Lust for Carnage
    // "(2 / 0.15) * f()" Spirit, Enchantment Master "(.3 / .15) * f()" potency — so f() = 0.15.
    ["ParagonPowerBudgetMultiplierGlyphThresholdBonusRare"] = 0.1,
    ["ParagonPowerBudgetMultiplierNodeLegendary"] = 0.15,
};

// Class order for fUsableByClass/arUsableByClass flag arrays, derived from single-class glyph
// names (index 0 owns Elementalist/Enchanter = Sorcerer, index 2 owns Might/Ire = Barbarian, ...).
string[] classOrder = ["Sorcerer", "Druid", "Barbarian", "Rogue", "Necromancer", "Spiritborn", "Paladin", "Warlock"];

// Board file names use short class tokens.
var classTokens = new Dictionary<string, string>
{
    ["Barb"] = "Barbarian", ["Druid"] = "Druid", ["Necro"] = "Necromancer", ["Rogue"] = "Rogue",
    ["Sorc"] = "Sorcerer", ["Spirit"] = "Spiritborn", ["Spiritborn"] = "Spiritborn",
    ["Paladin"] = "Paladin", ["Warlock"] = "Warlock",
};

JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(path))!;
string Hex(long sno) => $"0x{sno:x8}";
bool IsJunk(string fileName) => fileName.StartsWith("Axe Bad Data", StringComparison.OrdinalIgnoreCase);

// ── String lists: <prefix>_<internalName>.stl.json, label "Name" ────────────────
string? StringListText(string baseName, string label = "Name")
{
    string path = Path.Combine(stringLists, baseName + ".stl.json");
    if (!File.Exists(path)) return null;
    foreach (var entry in Load(path)["arStrings"]!.AsArray())
        if (entry!["szLabel"]!.GetValue<string>() == label)
            return entry["szText"]!.GetValue<string>().Trim();
    return null;
}

// ── Attribute formulas ───────────────────────────────────────────────────────────
// AttributeFormulas.gam.json: GBID name → formula text (first range).
var formulaText = new Dictionary<string, string>();
{
    var root = Load(Path.Combine(meta, "GameBalance", "AttributeFormulas.gam.json"));
    void Walk(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if ((string?)obj["__type__"] == "AttributeFormulaEntry")
            {
                string name = obj["tHeader"]!["szName"]!.GetValue<string>();
                var ranges = obj["arRanges"]!.AsArray();
                if (ranges.Count > 0)
                    formulaText[name] = ranges[0]!["tFormula"]!["value"]!.GetValue<string>();
            }
            foreach (var kv in obj) Walk(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var item in arr) Walk(item);
    }
    Walk(root);
}

// Evaluates a formula after substituting known built-in functions and variables.
// Returns null when the formula references anything unknown.
double? Evaluate(string formula, Dictionary<string, double>? variables = null)
{
    string expr = Regex.Replace(formula, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)",
        m => multipliers.TryGetValue(m.Groups[1].Value, out double v)
            ? v.ToString("R") : m.Value);
    if (variables is not null)
        foreach (var kv in variables)
            expr = Regex.Replace(expr, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value.ToString("R"));
    if (Regex.IsMatch(expr, "[A-Za-z]")) return null;
    try
    {
        object result = new DataTable().Compute(expr, null);
        return Convert.ToDouble(result);
    }
    catch { return null; }
}

// ── Powers: legendary node effects and glyph Additional Bonuses ───────────────────
// Power/<name>.pow.json holds the tuning values as ScriptFormulas SF_0..SF_n (constants, or
// expressions over other SFs / attributes / engine built-ins); the "desc" string list entry
// references them as [expr|format|] tokens — format flags: 'x' multiplicative ([x]), '%'
// percent, '+' explicit sign, a digit = decimal places. Values that depend on attributes or
// engine built-ins absent from the dump evaluate to null and render as "?".
object? PowerInfo(JsonObject? powerRef)
{
    if (powerRef is null) return null;
    string? name = powerRef["name"]?.GetValue<string>();
    string snoId = Hex(powerRef["__raw__"]!.GetValue<long>());
    if (name is null) return new { snoId, name };

    var formulas = new List<string>();
    string powPath = Path.Combine(meta, "Power", name + ".pow.json");
    if (File.Exists(powPath) && Load(powPath)["ptScriptFormulas"] is JsonArray sfs)
        formulas.AddRange(sfs.Select(f => f?["tFormula"]?["value"]?.GetValue<string>() ?? ""));

    var memo = new Dictionary<int, double?>();
    double? Sf(int index, int depth = 0)
    {
        if (index < 0 || index >= formulas.Count || depth > 16) return null;
        if (memo.TryGetValue(index, out var cached)) return cached;
        memo[index] = null; // cycle guard
        return memo[index] = EvaluateWithSfs(formulas[index], depth + 1);
    }
    double? EvaluateWithSfs(string expression, int depth)
    {
        string expr = expression.Replace("{", "").Replace("}", "").TrimEnd(']').Trim();
        bool unresolved = false;
        expr = Regex.Replace(expr, @"\bSF_(\d+)\b", m =>
        {
            var v = Sf(int.Parse(m.Groups[1].Value), depth);
            if (v is null) unresolved = true;
            return (v ?? 0).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        });
        if (unresolved || expr.Length == 0) return null;
        // "cond ? a : b" with identical branches (class-resource switches that ended up equal).
        var ternary = Regex.Match(expr, @"^[^?]+\?\s*([-\d.]+)\s*:\s*([-\d.]+)\s*$");
        if (ternary.Success && ternary.Groups[1].Value == ternary.Groups[2].Value)
            expr = ternary.Groups[1].Value;
        // Min/Max(a, b) over constant arguments — engine built-ins substituted first, since
        // their own "()" would otherwise hide the argument list.
        expr = Regex.Replace(expr, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)", m =>
            multipliers.TryGetValue(m.Groups[1].Value, out double builtIn)
                ? builtIn.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : m.Value);
        expr = Regex.Replace(expr, @"(?i)\b(min|max)\s*\(([^(),]+),([^(),]+)\)", m =>
            Evaluate(m.Groups[2].Value) is double a && Evaluate(m.Groups[3].Value) is double b
                ? (m.Groups[1].Value.Equals("min", StringComparison.OrdinalIgnoreCase) ? Math.Min(a, b) : Math.Max(a, b))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : m.Value);
        return Evaluate(expr);
    }

    string? template = StringListText("Power_" + name, "desc");
    var multiplierPercents = new List<double>();
    // A token may carry several |format| segments ("[x * 100|%x||%|]") — their flags union.
    string? description = template is null ? null : Regex.Replace(template, @"\[([^\[\]|]+)((?:\|[^\]|]*)*)\]", m =>
    {
        string format = m.Groups[2].Value.Replace("|", "");
        double? value = EvaluateWithSfs(m.Groups[1].Value, 0);
        if (value is null) return "?";
        int decimals = format.FirstOrDefault(char.IsDigit) is char d and not '\0' ? d - '0' : 0;
        string number = Math.Round(value.Value, decimals)
            .ToString(decimals > 0 ? "0." + new string('0', decimals) : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (format.Contains('x')) multiplierPercents.Add(value.Value);
        return (format.Contains('+') && value >= 0 ? "+" : "") + number
            + (format.Contains('%') ? "%" : "") + (format.Contains('x') ? "[x]" : "");
    });
    if (description is not null)
    {
        // Bare {SF_n} outside a bracket token prints the raw value (durations, counts).
        description = Regex.Replace(description, @"\{SF_(\d+)\}", m =>
            Sf(int.Parse(m.Groups[1].Value)) is double v
                ? Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : "?");
        description = Regex.Replace(description, @"\{/?(c(_[a-z_]+)?|u)\}", "");       // color/underline markup
        description = Regex.Replace(description, @"\{[^{}]*\}", "?");                  // payload/dot/buff refs
        description = Regex.Replace(description, @"\s+", " ").Trim();
    }
    return new
    {
        snoId,
        name,
        description,
        values = Enumerable.Range(0, formulas.Count).Select(i => Sf(i)).ToArray(),
        multiplierPercents = multiplierPercents.ToArray(),
    };
}

// ── Thresholds ────────────────────────────────────────────────────────────────────
var thresholds = new List<object>();
var thresholdBySno = new Dictionary<long, string>(); // sno → internalName
foreach (string path in Directory.GetFiles(Path.Combine(meta, "ParagonThreshold"), "*.pth.json").Order())
{
    if (IsJunk(Path.GetFileName(path))) continue;
    var t = Load(path);
    long sno = t["__snoID__"]!.GetValue<long>();
    string internalName = Path.GetFileName(path).Replace(".pth.json", "");
    thresholdBySno[sno] = internalName;

    var entries = new List<object>();
    foreach (var entry in t["ptThresholds"]!.AsArray())
    {
        string formula = entry!["tThresholdValueFormula"]!["value"]!.GetValue<string>();
        // Requirement scales with the 0-based order the board was attached in.
        var byIndex = new List<double?>();
        for (int i = 0; i <= 5; i++)
            byIndex.Add(Evaluate(formula, new() { ["ParagonBoardEquipIndex"] = i }));
        entries.Add(new
        {
            attribute = entry["tThresholdAttribute"]!["__eAttribute_name__"]!.GetValue<string>(),
            formula,
            valuesByBoardIndex = byIndex,
        });
    }
    var classFlags = t["arUsableByClass"]!.AsArray();
    thresholds.Add(new
    {
        snoId = Hex(sno),
        internalName,
        classes = classOrder.Where((_, i) => classFlags[i]!.GetValue<int>() != 0).ToArray(),
        requirements = entries,
    });
}

// ── Nodes ─────────────────────────────────────────────────────────────────────────
var nodes = new List<object>();
foreach (string path in Directory.GetFiles(Path.Combine(meta, "ParagonNode"), "*.pgn.json").Order())
{
    string fileName = Path.GetFileName(path);
    if (IsJunk(fileName)) continue;
    var n = Load(path);
    string internalName = fileName.Replace(".pgn.json", "");
    long sno = n["__snoID__"]!.GetValue<long>();
    bool isGate = n["bIsGate"]!.GetValue<bool>();
    bool hasSocket = n["bHasSocket"]!.GetValue<bool>();
    int rarityValue = n["eRarityOverride"]!.GetValue<int>();

    string kind = isGate ? "Gate"
        : hasSocket ? "GlyphSocket"
        : internalName.StartsWith("StartNode") ? "Start"
        : rarityValue switch { 2 => "Magic", 3 => "Rare", 4 => "Legendary", _ => "Normal" };

    var bonusIndices = n["ptBonusAttributeIndices"]!.AsArray().Select(x => x!.GetValue<int>()).ToHashSet();
    var attributes = new List<object>();
    int attrIndex = 0;
    foreach (var attr in n["ptAttributes"]!.AsArray())
    {
        string? formulaName = attr!["gbidFormula"]?["name"]?.GetValue<string>();
        string? text = formulaName is not null && formulaText.TryGetValue(formulaName, out string? ft) ? ft : null;
        int param = attr["nParam"]!.GetValue<int>();
        attributes.Add(new
        {
            attribute = attr["__eAttribute_name__"]!.GetValue<string>(),
            param = param == -1 ? (int?)null : param,
            formulaName,
            formula = text,
            value = text is null ? null : Evaluate(text),
            isThresholdBonus = bonusIndices.Contains(attrIndex),
        });
        attrIndex++;
    }

    var power = PowerInfo(n["snoPassivePower"] as JsonObject);

    nodes.Add(new
    {
        snoId = Hex(sno),
        internalName,
        name = StringListText("ParagonNode_" + internalName),
        kind,
        attributes,
        thresholds = n["arThresholdSelector"]!.AsArray()
            .Select(t => Hex(t!["__raw__"]!.GetValue<long>())).ToArray(),
        power,
        tags = n["arSkillTags"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>().Replace("Search_", "")).ToArray(),
    });
}

// ── Boards ────────────────────────────────────────────────────────────────────────
var boards = new List<object>();
foreach (string path in Directory.GetFiles(Path.Combine(meta, "ParagonBoard"), "*.pbd.json").Order())
{
    string fileName = Path.GetFileName(path);
    if (IsJunk(fileName)) continue;
    var b = Load(path);
    string internalName = fileName.Replace(".pbd.json", "");
    int width = b["nWidth"]!.GetValue<int>();

    var m = Regex.Match(internalName, @"^Paragon_([A-Za-z]+)_(\d+)$");
    string? className = m.Success && classTokens.TryGetValue(m.Groups[1].Value, out string? c) ? c : null;

    var placements = new List<object>();
    var entries = b["arEntries"]!.AsArray();
    for (int i = 0; i < entries.Count; i++)
    {
        if (entries[i] is not JsonObject entry) continue;
        placements.Add(new
        {
            x = i % width,
            y = i / width,
            node = Hex(entry["__raw__"]!.GetValue<long>()),
        });
    }

    boards.Add(new
    {
        snoId = Hex(b["__snoID__"]!.GetValue<long>()),
        internalName,
        name = StringListText("ParagonBoard_" + internalName),
        className,
        boardIndex = m.Success ? int.Parse(m.Groups[2].Value) : (int?)null,
        width,
        contentLicense = b["dwContentLicenseRequirements"]!.GetValue<long>(),
        nodes = placements,
    });
}

// ── Glyph affixes ─────────────────────────────────────────────────────────────────
var glyphAffixBySno = new Dictionary<long, object>();
foreach (string path in Directory.GetFiles(Path.Combine(meta, "ParagonGlyphAffix"), "*.gaf.json").Order())
{
    if (IsJunk(Path.GetFileName(path))) continue;
    var a = Load(path);
    long sno = a["__snoID__"]!.GetValue<long>();
    var maps = new List<object>();
    foreach (var map in a["unk_e80c332"]!.AsArray())
    {
        int srcParam = map!["tSourceAttribute"]!["nParam"]!.GetValue<int>();
        int dstParam = map["tDestinationAttribute"]!["nParam"]!.GetValue<int>();
        maps.Add(new
        {
            destinationAttribute = map["tDestinationAttribute"]!["__eAttribute_name__"]!.GetValue<string>(),
            destinationParam = dstParam == -1 ? (int?)null : dstParam,
            sourceAttribute = map["tSourceAttribute"]!["__eAttribute_name__"]!.GetValue<string>(),
            sourceParam = srcParam == -1 ? (int?)null : srcParam,
        });
    }
    glyphAffixBySno[sno] = new
    {
        snoId = Hex(sno),
        internalName = Path.GetFileName(path).Replace(".gaf.json", ""),
        affectedNodeRarity = a["eAffectedNodeRarity"]!.GetValue<int>(),
        requiredRarity = a["eRequiredRarity"]!.GetValue<int>(),
        bonusOperation = a["eBonusOperation"]!.GetValue<int>(),
        attributeMaps = maps,
        startingBonusScalar = a["flStartingBonusScalar"]!.GetValue<double>(),
        addedBonusScalarPerLevel = a["flAddedBonusScalarPerLevel"]!.GetValue<double>(),
        budgetFormulaName = a["gbidPowerBudgetFormula"]?["name"]?.GetValue<string>(),
        bonusPower = PowerInfo(a["snoBonusPassivePower"] as JsonObject),
        tags = a["arAffixSkillTags"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>().Replace("Search_", "")).ToArray(),
    };
}

// ── Glyphs ────────────────────────────────────────────────────────────────────────
var glyphs = new List<object>();
foreach (string path in Directory.GetFiles(Path.Combine(meta, "ParagonGlyph"), "*.gph.json").Order())
{
    string fileName = Path.GetFileName(path);
    if (IsJunk(fileName)) continue;
    var g = Load(path);
    string internalName = fileName.Replace(".gph.json", "");
    var classFlags = g["fUsableByClass"]!.AsArray();
    glyphs.Add(new
    {
        snoId = Hex(g["__snoID__"]!.GetValue<long>()),
        internalName,
        name = StringListText("ParagonGlyph_" + internalName),
        rarity = g["eRarity"]!.GetValue<int>(),
        classes = classOrder.Where((_, i) => classFlags[i]!.GetValue<int>() != 0).ToArray(),
        affixes = g["arAffixes"]!.AsArray()
            .Where(a => a is JsonObject)
            .Select(a => glyphAffixBySno.TryGetValue(a!["__raw__"]!.GetValue<long>(), out object? affix)
                ? affix
                : new { snoId = Hex(a["__raw__"]!.GetValue<long>()) } as object)
            .ToArray(),
    });
}

// ── Emit ──────────────────────────────────────────────────────────────────────────
var output = new
{
    formatVersion = 1,
    source = $"DiabloTools/d4data ({DescribeCheckout(d4dataPath)})",
    notes = "Multiplier constants are empirical calibrations of engine built-in functions; " +
            "values are fractions (0.05 = 5%) for percent attributes, flat for stat attributes. " +
            "Class order for flag arrays derived from single-class glyph names.",
    multipliers,
    boards,
    nodes,
    glyphs,
    thresholds,
};

File.WriteAllText(outPath, JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
}));

int unresolved = 0;
foreach (var name in formulaText.Keys)
    if (name.StartsWith("ParagonNode") && Evaluate(formulaText[name]) is null) unresolved++;
Console.WriteLine($"Wrote {outPath}: {boards.Count} boards, {nodes.Count} nodes, {glyphs.Count} glyphs, " +
                  $"{glyphAffixBySno.Count} glyph affixes, {thresholds.Count} thresholds. " +
                  $"{unresolved} ParagonNode formulas unresolved.");
return 0;

static string DescribeCheckout(string d4dataPath)
{
    string headPath = Path.Combine(d4dataPath, ".git", "HEAD");
    if (!File.Exists(headPath)) return "unknown revision";
    string head = File.ReadAllText(headPath).Trim();
    if (head.StartsWith("ref: "))
    {
        string refPath = Path.Combine(d4dataPath, ".git", head[5..].Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(refPath)) return File.ReadAllText(refPath).Trim()[..12];
    }
    return head.Length >= 12 ? head[..12] : head;
}
