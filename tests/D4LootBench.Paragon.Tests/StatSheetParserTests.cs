using D4LootBench.Paragon.Solver;
using Shouldly;

namespace D4LootBench.Paragon.Tests;

/// <summary>
/// Parsing the in-game stats details panel (Offense section) from OCR lines into the planner's
/// two gear-damage fields. Windows OCR reads the panel's two columns SEPARATELY — every label,
/// then every value — so rows are rebuilt from vertical positions. The parser splits always-on
/// from situational, excludes built-in bases (crit 50 / vulnerable 20 / overpower 50), and picks
/// the largest "Damage with &lt;type&gt;" row as the build's main type.
/// </summary>
public class StatSheetParserTests
{
    private const double RowPitch = 20;
    private const double LineHeight = 8;

    private static SheetOcrLine L(string text, double row, double offset = 0) =>
        new(text, row * RowPitch + offset, LineHeight);

    /// <summary>The Barbarian sheet the feature was designed against, laid out the way the OCR
    /// engine actually returned it: the label column first (wrapped labels as two lines), then
    /// the value column, with the row arrow misread as junk and "Elites" misread as "Flites".</summary>
    private static readonly SheetOcrLine[] BarbarianSheet =
    [
        L("Base Bludgeoning", 0, -5), L("Weapon Damage", 0, +5),
        L("Weapon Speed", 1),
        L("Attack Speed Bonus", 2),
        L("Critical Strike Chance", 3),
        L("Critical Strike Damage", 4),
        L("Vulnerable Damage", 5),
        L("All Damage", 6),
        L("Damage with Physical", 7),
        L("Damage with Fire", 8),
        L("Damage with Lightning", 9),
        L("Damage vs Flites", 10),
        L("Damage vs Bleeding", 11),
        L("Damage while Fortified", 12),
        L("Damage while", 13, -5), L("Berserking", 13, +5),
        L("Thorns", 14),
        L(") 5,157", 0),
        L("1.10", 1),
        L("12.5%", 2),
        L("34.7%", 3),
        L("2,658.6%", 4),
        L("250.1%", 5),
        L("327.6%", 6),
        L("224.0%", 7),
        L("72.5%", 8),
        L("42.5%", 9),
        L("95.8%", 10),
        L("25.0%", 11),
        L("20.0%", 12),
        L("38.0%", 13),
        // Thorns' value ("0") went unrecognized — its label must not steal a neighbor's value.
    ];

    [Fact]
    public void Sheet_rows_land_in_their_buckets()
    {
        var rows = StatSheetParser.Parse(BarbarianSheet);

        Bucket(rows, "All Damage").ShouldBe(SheetBucket.AlwaysOn);
        Bucket(rows, "Critical Strike Damage").ShouldBe(SheetBucket.Situational);
        Bucket(rows, "Vulnerable Damage").ShouldBe(SheetBucket.Situational);
        Bucket(rows, "Damage vs Bleeding").ShouldBe(SheetBucket.Situational);
        Bucket(rows, "Damage while Fortified").ShouldBe(SheetBucket.Situational);
        Bucket(rows, "Damage while Berserking").ShouldBe(SheetBucket.Situational);

        Bucket(rows, "Weapon Speed").ShouldBe(SheetBucket.Ignored);
        Bucket(rows, "Attack Speed Bonus").ShouldBe(SheetBucket.Ignored);
        Bucket(rows, "Critical Strike Chance").ShouldBe(SheetBucket.Ignored);
    }

    [Fact]
    public void Misread_condition_words_still_classify_by_their_prefix()
    {
        // OCR read "Elites" as "Flites" — "Damage vs …" is conditional whatever follows.
        Bucket(StatSheetParser.Parse(BarbarianSheet), "Damage vs Flites")
            .ShouldBe(SheetBucket.Situational);
    }

    [Fact]
    public void Built_in_bases_are_excluded_from_contributions()
    {
        var rows = StatSheetParser.Parse(BarbarianSheet);

        Row(rows, "Critical Strike Damage").Contribution.ShouldBe(2608.6, 0.01);
        Row(rows, "Vulnerable Damage").Contribution.ShouldBe(230.1, 0.01);
        Row(rows, "All Damage").Contribution.ShouldBe(327.6, 0.01);
    }

    [Fact]
    public void Largest_damage_type_is_assumed_main_and_always_on()
    {
        var rows = StatSheetParser.Parse(BarbarianSheet);

        Bucket(rows, "Damage with Physical").ShouldBe(SheetBucket.AlwaysOn);
        Bucket(rows, "Damage with Fire").ShouldBe(SheetBucket.Ignored);
        Bucket(rows, "Damage with Lightning").ShouldBe(SheetBucket.Ignored);
        Row(rows, "Damage with Fire").Note.ShouldNotBeNull();
    }

    [Fact]
    public void Sums_split_the_two_fields()
    {
        var (alwaysOn, situational) = StatSheetParser.Sum(StatSheetParser.Parse(BarbarianSheet));

        alwaysOn.ShouldBe(327.6 + 224.0, 0.01);
        situational.ShouldBe(2608.6 + 230.1 + 95.8 + 25.0 + 20.0 + 38.0, 0.01);
    }

    [Fact]
    public void Wrapped_labels_rejoin_around_their_value()
    {
        var rows = StatSheetParser.Parse([
            L("Base Bludgeoning", 0, -5), L("Weapon Damage", 0, +5), L("5,157", 0),
        ]);

        var row = rows.ShouldHaveSingleItem();
        row.Label.ShouldBe("Base Bludgeoning Weapon Damage");
        row.Value.ShouldBe(5157);
        row.Bucket.ShouldBe(SheetBucket.Ignored);
    }

    [Fact]
    public void A_label_whose_value_was_missed_is_dropped_not_mispaired()
    {
        var rows = StatSheetParser.Parse([
            L("Damage while Berserking", 0), L("Thorns", 1),
            L("38.0%", 0),
            L("Damage vs Elites", 2), L("95.8%", 2),
        ]);

        Row(rows, "Damage while Berserking").Value.ShouldBe(38.0, 0.01);
        rows.ShouldNotContain(r => r.Label.Contains("Thorns"));
    }

    [Fact]
    public void Label_and_value_merged_into_one_ocr_line_still_parse()
    {
        var row = StatSheetParser.Parse([L("Overpower Damage 180.0%", 0)]).ShouldHaveSingleItem();

        row.Label.ShouldBe("Overpower Damage");
        row.Bucket.ShouldBe(SheetBucket.Situational);
        row.Contribution.ShouldBe(130.0, 0.01);
    }

    [Fact]
    public void Ocr_dropped_percent_sign_still_parses()
    {
        var rows = StatSheetParser.Parse([L("Damage vs Elites", 0), L("95.8", 0)]);

        Row(rows, "Damage vs Elites").Contribution.ShouldBe(95.8, 0.01);
    }

    [Fact]
    public void Skill_category_damage_defaults_situational_with_a_note()
    {
        var row = StatSheetParser.Parse([L("Core Skill Damage", 0), L("120.0%", 0)])
            .ShouldHaveSingleItem();

        row.Bucket.ShouldBe(SheetBucket.Situational);
        row.Note.ShouldNotBeNull();
    }

    [Fact]
    public void Unknown_rows_are_ignored_not_guessed()
    {
        var row = StatSheetParser.Parse([L("Overpower Chance", 0), L("12.0%", 0)])
            .ShouldHaveSingleItem();

        row.Bucket.ShouldBe(SheetBucket.Ignored);
    }

    [Fact]
    public void A_value_with_no_label_at_all_is_skipped()
    {
        StatSheetParser.Parse([L("250.1%", 0)]).ShouldBeEmpty();
    }

    /// <summary>A stat's HOVER tooltip carries the game's own decomposition — "You have +X% of
    /// this stat from items and Paragon" — exact, with the inherent base and multiplicative ×%
    /// sources already excluded. It must win over the misleading 2,991.5% headline.</summary>
    [Fact]
    public void Stat_hover_tooltip_yields_the_exact_items_and_paragon_value()
    {
        var row = StatSheetParser.Parse([
            L("Critical Strike Damage Bonus: 2,991.5%", 0),
            L("Extra damage granted to Skills when", 1),
            L("they Critically Strike.", 2),
            L("Includes the inherent x50.0%", 3),
            L("increased damage dealt by Critical", 4),
            L("Strikes.", 5),
            L("You have +289.1% of this stat from", 6),
            L("items and Paragon.", 7),
        ]).ShouldHaveSingleItem();

        row.Label.ShouldBe("Critical Strike Damage");
        row.Bucket.ShouldBe(SheetBucket.Situational);
        row.Value.ShouldBe(289.1, 0.01);
        row.BaseValue.ShouldBe(0); // no headline base to subtract — the game already did
        row.Contribution.ShouldBe(289.1, 0.01);
    }

    [Fact]
    public void Hover_tooltip_header_survives_a_misread_colon()
    {
        var row = StatSheetParser.Parse([
            L("Vulnerable Damage Bonus 250.1%", 0),
            L("You have +180.3% of this stat from items and Paragon.", 1),
        ]).ShouldHaveSingleItem();

        row.Label.ShouldBe("Vulnerable Damage");
        row.Value.ShouldBe(180.3, 0.01);
        row.Bucket.ShouldBe(SheetBucket.Situational);
    }

    [Fact]
    public void Hover_tooltip_for_an_always_on_stat_keeps_its_bucket()
    {
        var row = StatSheetParser.Parse([
            L("All Damage: 327.6%", 0),
            L("You have +327.6% of this stat from items and Paragon.", 1),
        ]).ShouldHaveSingleItem();

        row.Bucket.ShouldBe(SheetBucket.AlwaysOn);
        row.Contribution.ShouldBe(327.6, 0.01);
    }

    private static SheetStatLine Row(IReadOnlyList<SheetStatLine> rows, string label) =>
        rows.Where(l => l.Label == label).ToList().ShouldHaveSingleItem(label);

    private static SheetBucket Bucket(IReadOnlyList<SheetStatLine> rows, string label) =>
        Row(rows, label).Bucket;
}
