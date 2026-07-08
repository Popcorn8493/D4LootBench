using System.Text.Json;
using D4LootBench.Core.Codec;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;
using D4LootBench.Core.Serialization;
using Shouldly;

namespace D4LootBench.Core.Tests.Codec;

public sealed class FilterCodecTests
{
    // ── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ThenDecode_PreservesFilterName()
    {
        var ruleset = SimpleRuleset("My Test Filter");
        FilterCodec.Decode(FilterCodec.Encode(ruleset)).Name.ShouldBe("My Test Filter");
    }

    [Fact]
    public void Encode_ThenDecode_PreservesRuleCount()
    {
        FilterCodec.Decode(FilterCodec.Encode(SimpleRuleset("Test"))).Rules.Count.ShouldBe(2);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesRuleFields()
    {
        var rule = new FilterRule(
            "Hide Junk",
            Visibility.HideAll,
            FilterColors.Blue,
            [new RarityCondition(RarityFlags.Common | RarityFlags.Magic | RarityFlags.Rare)]
        );
        var decoded = FilterCodec.Decode(FilterCodec.Encode(new FilterRuleset("Test", [rule]))).Rules[0];

        decoded.Name.ShouldBe("Hide Junk");
        decoded.Visibility.ShouldBe(Visibility.HideAll);
        decoded.Color.ShouldBe(FilterColors.Blue);
        decoded.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Encode_ThenDecode_PreservesRarityCondition()
    {
        const RarityFlags mask = RarityFlags.Rare | RarityFlags.Legendary | RarityFlags.Unique;
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Show, FilterColors.Blue,
            [new RarityCondition(mask)]));

        decoded.Conditions.Count.ShouldBe(1);
        decoded.Conditions[0].ShouldBeOfType<RarityCondition>().Mask.ShouldBe(mask);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesGreaterAffixCondition()
    {
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Recolor, FilterColors.Cyan,
            [new GreaterAffixCondition(2)]));

        decoded.Conditions[0].ShouldBeOfType<GreaterAffixCondition>().MinimumCount.ShouldBe(2);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesCodexCondition()
    {
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Recolor, FilterColors.Green,
            [new CodexCondition()]));

        decoded.Conditions[0].ShouldBeOfType<CodexCondition>();
    }

    [Fact]
    public void Encode_ThenDecode_PreservesAffixConditionIdsAndMinimum()
    {
        uint[] affixIds = [AffixDatabase.ByName["+Critical Strike Chance"].Hash, AffixDatabase.ByName["+Attack Speed"].Hash];
        var decoded = RoundTripRule(new FilterRule("BiS Rare", Visibility.Recolor, FilterColors.Gold,
            [new AffixCondition(affixIds, 2)]));

        var result = decoded.Conditions[0].ShouldBeOfType<AffixCondition>();
        result.MinimumCount.ShouldBe(2);
        result.AffixIds.ShouldBe(affixIds);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesItemTypeCondition()
    {
        uint[] typeIds = [0x00237e80u, 0x0022ed05u];
        var decoded = RoundTripRule(new FilterRule("Talismans", Visibility.Show, FilterColors.Blue,
            [new ItemTypeCondition(typeIds)]));

        decoded.Conditions[0].ShouldBeOfType<ItemTypeCondition>().TypeIds.ShouldBe(typeIds);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesMultipleConditionsPerRule()
    {
        var conditions = new Condition[]
        {
            new RarityCondition(RarityFlags.Rare),
            new AffixCondition([AffixDatabase.ByName["All Damage Multiplier"].Hash], 1)
        };
        RoundTripRule(new FilterRule("Orange Rare", Visibility.Recolor, FilterColors.Orange,
            conditions)).Conditions.Count.ShouldBe(2);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesItemPowerCondition()
    {
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Show, FilterColors.Blue,
            [new ItemPowerCondition(700, 925)]));

        var result = decoded.Conditions[0].ShouldBeOfType<ItemPowerCondition>();
        result.Minimum.ShouldBe(700);
        result.Maximum.ShouldBe(925);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesItemPropertiesCondition()
    {
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Show, FilterColors.Blue,
            [new ItemPropertiesCondition(4)]));

        decoded.Conditions[0].ShouldBeOfType<ItemPropertiesCondition>().PropertyMask.ShouldBe(4);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesItemPropertiesCombinedMask()
    {
        // Ancestral (4) | Mythic (32) — as exported by the in-game editor with both checked
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Show, FilterColors.Blue,
            [new ItemPropertiesCondition(36)]));

        decoded.Conditions[0].ShouldBeOfType<ItemPropertiesCondition>().PropertyMask.ShouldBe(36);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesOptionalAffixCondition()
    {
        uint[] affixIds = [AffixDatabase.ByName["+Critical Strike Chance"].Hash, AffixDatabase.ByName["Maximum Life"].Hash];
        var decoded = RoundTripRule(new FilterRule("Test", Visibility.Show, FilterColors.Blue,
            [new OptionalAffixCondition(affixIds, 2)]));

        var result = decoded.Conditions[0].ShouldBeOfType<OptionalAffixCondition>();
        result.AffixIds.ShouldBe(affixIds);
        result.MinimumCount.ShouldBe(2);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesSpecificUniqueCondition()
    {
        uint[] uniqueIds =
        [
            UniqueItemDatabase.All[0].SnoId,
            UniqueItemDatabase.All[1].SnoId,
            UniqueItemDatabase.All[2].SnoId
        ];
        var decoded = RoundTripRule(new FilterRule("Uniques", Visibility.Show, FilterColors.Blue,
            [new SpecificUniqueCondition(uniqueIds)]));

        var result = decoded.Conditions[0].ShouldBeOfType<SpecificUniqueCondition>();
        result.UniqueIds.ShouldBe(uniqueIds);
    }

    [Fact]
    public void Decode_TalismanSetCondition_RealSample()
    {
        // Single-rule filter: one type-9 condition with Berserker's Crucible set (0x0022fb41)
        // and one set item Beru of the Crucible (0x002506e2).
        const string code = "Ch4QAB0AAP//IhMICRVB+yIAGgoNQfsiABXiBiUAKAESEVRhaWxzbWFuIFNldCBsaXN0GAcgAQ==";
        var ruleset = FilterCodec.Decode(code);

        var cond = ruleset.Rules[0].Conditions
            .OfType<TalismanSetCondition>()
            .ShouldHaveSingleItem();

        cond.SetIds.ShouldHaveSingleItem();
        cond.SetIds[0].ShouldBe(0x0022fb41u);

        cond.SetEntries.ShouldHaveSingleItem();
        cond.SetEntries[0].SetId.ShouldBe(0x0022fb41u);
        cond.SetEntries[0].ItemId.ShouldBe(0x002506e2u);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesTalismanSetCondition_Empty()
    {
        // CAk= is the Raxx "Set Charms (SELECT)" placeholder — type 9, no IDs
        var decoded = RoundTripRule(new FilterRule("Set", Visibility.Show, FilterColors.Blue,
            [new TalismanSetCondition()]));

        var result = decoded.Conditions[0].ShouldBeOfType<TalismanSetCondition>();
        result.SetIds.ShouldBeEmpty();
        result.SetEntries.ShouldBeEmpty();
    }

    [Fact]
    public void Encode_ThenDecode_PreservesTalismanSetCondition_WithIds()
    {
        var cond = new TalismanSetCondition
        {
            SetIds     = [0x00112233, 0x00445566],
            SetEntries = [new TalismanSetEntry(0x00112233, 0x00aabbcc)]
        };
        var decoded = RoundTripRule(new FilterRule("Set", Visibility.Show, FilterColors.Blue, [cond]));

        var result = decoded.Conditions[0].ShouldBeOfType<TalismanSetCondition>();
        result.SetIds.ShouldBe(cond.SetIds);
        result.SetEntries.Count.ShouldBe(1);
        result.SetEntries[0].SetId.ShouldBe(0x00112233u);
        result.SetEntries[0].ItemId.ShouldBe(0x00aabbccu);
    }

    [Fact]
    public void Encode_ThenDecode_PreservesTalismanSetCondition_MultipleItemsSameSet()
    {
        // Multiple items in the same set should be packed into one sub-message and round-trip intact.
        var cond = new TalismanSetCondition
        {
            SetIds     = [0x00230acc],
            SetEntries =
            [
                new TalismanSetEntry(0x00230acc, 0x00250ee3),
                new TalismanSetEntry(0x00230acc, 0x00250edb)
            ]
        };
        var decoded = RoundTripRule(new FilterRule("Set", Visibility.Show, FilterColors.Blue, [cond]));

        var result = decoded.Conditions[0].ShouldBeOfType<TalismanSetCondition>();
        result.SetIds.ShouldBe(cond.SetIds);
        result.SetEntries.Count.ShouldBe(2);
        result.SetEntries[0].SetId.ShouldBe(0x00230accu);
        result.SetEntries[0].ItemId.ShouldBe(0x00250ee3u);
        result.SetEntries[1].SetId.ShouldBe(0x00230accu);
        result.SetEntries[1].ItemId.ShouldBe(0x00250edbu);
    }

    [Fact]
    public void Decode_TalismanSetCondition_GameFormat_MultipleItemsSameSet()
    {
        // Game-encoded: single sub-message with SetId + 2x ItemId
        const string code = "Ci0KCFRhbGlzbWFuEAAdAAAAACIYCAkVzAojABoPDcwKIwAV4w4lABXbDiUAKAESCk5ldyBGaWx0ZXIYCCAB";
        var ruleset = FilterCodec.Decode(code);

        var cond = ruleset.Rules[0].Conditions
            .OfType<TalismanSetCondition>()
            .ShouldHaveSingleItem();

        cond.SetIds.ShouldHaveSingleItem();
        cond.SetIds[0].ShouldBe(0x00230accu);

        cond.SetEntries.Count.ShouldBe(2);
        cond.SetEntries[0].SetId.ShouldBe(0x00230accu);
        cond.SetEntries[0].ItemId.ShouldBe(0x00250ee3u);
        cond.SetEntries[1].SetId.ShouldBe(0x00230accu);
        cond.SetEntries[1].ItemId.ShouldBe(0x00250edbu);
    }

    // ── Encode produces valid Base64 ──────────────────────────────────────

    [Fact]
    public void Encode_ProducesValidBase64()
    {
        var code = FilterCodec.Encode(SimpleRuleset("Test"));
        Should.NotThrow(() => Convert.FromBase64String(code));
    }

    // ── Decode is tolerant of whitespace ─────────────────────────────────

    [Fact]
    public void Decode_StripsWhitespaceFromShareCode()
    {
        var code = FilterCodec.Encode(SimpleRuleset("Test"));
        Should.NotThrow(() => FilterCodec.Decode("  " + code + "\n"));
    }

    // ── Decodes real-world share code ──────────────────────────

    [Fact]
    public void Decode_RaxxFilter_RecoversAll16Rules()
    {
        var code = @"CiQKE0V2ZXJ5IE15dGhpYyBVbmlxdWUQAB3oIqj/IgQIASAgKAEKIwoSQWxsIENvZGV4IFVwZ3JhZGVzEAAdAAD//yIECAMwASgBCjIKGExlZy9VbmlxdWUvTXl0aGljIENoYXJtcxAAHQAA//8iBwgFFQXtIgAiBAgBIDgoAQoxChNTZXQgQ2hhcm1zIChTRUxFQ1QpEAAdAAD//yICCAkiBwgFFQXtIgAiBAgBIEAoAQooCg5BbGwgU2V0IENoYXJtcxAAHQAA//8iBwgFFQXtIgAiBAgBIEAoAQoxChdMZWcvVW5pcXVlL015dGhpYyBTZWFscxAAHQAA//8iBwgFFYB+IwAiBAgBIHgoAQo1ChZTYWx2YWdlIFNlYWxzICYgQ2hhcm1zEAAdAAD//yIMCAUVgH4jABUF7SIAIgQIASAEKAEKHwoQVW5pcXVlcyAoU0VMRUNUKRACHa7oIv8iAggIKAEKHAoLQWxsIFVuaXF1ZXMQAB0AAP//IgQIASAQKAEKNwoVU3BlY2lmaWMgR0FzIChTRUxFQ1QpEAAdAAD//yIVCAYV9wonABoKDfcKJwAV9wonACABKAEKYgoNTWFpbiBTdGF0IEdBcxAAHQAA//8iSAgGFcLqGwAVxuobABW66hsAFb7qGwAaCg3C6hsAFcLqGwAaCg3G6hsAFcbqGwAaCg266hsAFbrqGwAaCg2+6hsAFb7qGwAgASgBCo0CCg1Vbml2ZXJzYWwgR0FzEAAdAAD//yLyAQgGFTFuHQAVzuobABU4/RsAFbjqGwAV3uobABWy6hsAFQo8JwAVtOobABWA/BsAFZP8JwAVY24dABXY6hsAFdTqGwAV0uobABoKDTFuHQAVMW4dABoKDTj9GwAVOP0bABoKDc7qGwAVzuobABoKDbjqGwAVuOobABoKDbLqGwAVsuobABoKDd7qGwAV3uobABoKDbTqGwAVtOobABoKDQo8JwAVCjwnABoKDWNuHQAVY24dABoKDZP8JwAVk/wnABoKDYD8GwAVgPwbABoKDdjqGwAV2OobABoKDdTqGwAV1OobABoKDdLqGwAV0uobACABKAEKJgoTQWxsIEdyZWF0ZXIgQWZmaXhlcxAAHQAA//8iBggEIAEwASgBCh8KDkFsbCBBbmNlc3RyYWxzEAAdAAD//yIECAIgBCgBCjsKF1doaXRlcyBUbyBDdWJlIChTRUxFQ1QpEAAdAAD//yIICAAg0gYohAciBwgFFVnRBgAiBAgBIAEoAQoMCgEgEAMdAAD//ygBEhhSYXh4J3MgVG9ybWVudCA2KyBGaWx0ZXIYASAC";
        var ruleset = FilterCodec.Decode(code);

        ruleset.Rules.Count.ShouldBe(16);
        ruleset.Name.ShouldBe("Raxx's Torment 6+ Filter");
        ruleset.OriginalCode.ShouldBe(code);

        // All Greater Affixes rule should decode correctly
        var ga = ruleset.Rules[12];
        ga.Name.ShouldBe("All Greater Affixes");
        ga.Visibility.ShouldBe(Visibility.Show);
        ga.IsEnabled.ShouldBeTrue();
        ga.Conditions.Count.ShouldBe(1);
        ga.Conditions[0].ShouldBeOfType<GreaterAffixCondition>();
        ((GreaterAffixCondition)ga.Conditions[0]).MinimumCount.ShouldBe(1);

        // Universal GAs rule — game-exported version has all 14 greater entries
        var uni = ruleset.Rules.FirstOrDefault(r => r.Name == "Universal GAs");
        uni.ShouldNotBeNull();
        var ac = uni.Conditions.OfType<AffixCondition>().FirstOrDefault();
        ac.ShouldNotBeNull();
        ac.MinimumCount.ShouldBe(1);
        ac.GreaterEntries.Count.ShouldBe(14);
        ac.Field5.ShouldBe(0);

        // "Whites To Cube" visibility should match game
        var whites = ruleset.Rules.FirstOrDefault(r => r.Name.StartsWith("Whites"));
        whites.ShouldNotBeNull();
        whites.Visibility.ShouldBe(Visibility.Show);

        var catchAll = ruleset.Rules.FirstOrDefault(r => r.Name == " ");
        catchAll.ShouldNotBeNull();
        catchAll.Visibility.ShouldBe(Visibility.HideAll);
    }

    // ── Encode → decode → encode produces identical bytes ────────────────

    [Fact]
    public void EncodeIsIdempotent_AfterRoundTrip()
    {
        var original = BuildGenericCritRuleset();
        var code1 = FilterCodec.Encode(original);
        FilterCodec.Encode(FilterCodec.Decode(code1)).ShouldBe(code1);
    }

    // ── TalismanSetDatabase name resolution ──────────────────────────────

    [Fact]
    public void TalismanSetDatabase_ResolvesSetName_BerserkersCrucible()
    {
        TalismanSetDatabase.GetSetName(0x0022fb41u).ShouldBe("Berserker's Crucible");
    }

    [Fact]
    public void TalismanSetDatabase_ResolvesItemName_BeruOfTheCrucible()
    {
        TalismanSetDatabase.GetItemName(0x002506e2u).ShouldBe("Berú of the Crucible");
    }

    [Fact]
    public void TalismanSetDatabase_All_Contains45ResolvedSets()
    {
        // 45 class/generic sets have hashes; 5 X1_QST Hatred sets do not
        TalismanSetDatabase.All.Count.ShouldBe(45);
    }

    [Fact]
    public void TalismanSetDatabase_BerserkersCrucible_HasFiveItems()
    {
        var set = TalismanSetDatabase.ByHash[0x0022fb41u];
        set.Items.Count.ShouldBe(5);
    }

    // ── JSON round-trip ──────────────────────────────────────────────────

    [Fact]
    public void JsonRoundTrip_PreservesOptionalAffixCondition()
    {
        var original = new OptionalAffixCondition(
            [AffixDatabase.ByName["+Critical Strike Chance"].Hash], 2)
        {
            GreaterEntries = [new GreaterAffixEntry(0x001beace, 1)],
            Field5 = 3
        };
        var rule = new FilterRule("Test", Visibility.Show, FilterColors.Blue, [original]);
        var json = System.Text.Json.JsonSerializer.Serialize(rule, Serialization.FilterJsonOptions.Default);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FilterRule>(json, Serialization.FilterJsonOptions.Default);
        restored.ShouldNotBeNull();
        var cond = restored.Conditions[0].ShouldBeOfType<OptionalAffixCondition>();
        cond.AffixIds.ShouldBe(original.AffixIds);
        cond.MinimumCount.ShouldBe(original.MinimumCount);
        cond.GreaterEntries.Count.ShouldBe(original.GreaterEntries.Count);
        cond.Field5.ShouldBe(original.Field5);
    }

    [Fact]
    public void JsonDeserialize_OldOptionalAffix_WithoutMinCount()
    {
        var json = """{"$type":"optionalAffix","AffixIds":["0x001beace"]}""";
        var cond = System.Text.Json.JsonSerializer.Deserialize<Condition>(json, Serialization.FilterJsonOptions.Default);
        cond.ShouldBeOfType<OptionalAffixCondition>();
        cond.ShouldNotBeNull();
        var oa = (OptionalAffixCondition)cond;
        oa.MinimumCount.ShouldBe(0);
        oa.AffixIds.Count.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static FilterRule RoundTripRule(FilterRule rule)
        => FilterCodec.Decode(FilterCodec.Encode(new FilterRuleset("T", [rule]))).Rules[0];

    private static FilterRuleset SimpleRuleset(string name) => new(name,
    [
        new FilterRule("Show All",  Visibility.Show,    FilterColors.Blue, [new RarityCondition(RarityFlags.All)]),
        new FilterRule("Hide Junk", Visibility.HideAll, FilterColors.Blue, [new RarityCondition(RarityFlags.Common | RarityFlags.Magic)])
    ]);

    private static FilterRuleset BuildGenericCritRuleset()
    {
        uint[] coreIds =
        [
            AffixDatabase.ByName["+Critical Strike Chance"].Hash,
            AffixDatabase.ByName["Critical Strike Damage"].Hash,
            AffixDatabase.ByName["+Attack Speed"].Hash,
            AffixDatabase.ByName["All Damage Multiplier"].Hash,
            AffixDatabase.ByName["Vulnerable Damage Multiplier"].Hash,
        ];
        uint[] secIds =
        [
            AffixDatabase.ByName["Maximum Life"].Hash,
            AffixDatabase.ByName["+Armor"].Hash,
        ];
        var allIds = coreIds.Concat(secIds).ToArray();

        return new FilterRuleset("Generic Crit",
        [
            new FilterRule("Legendary Talismans",      Visibility.Show,    FilterColors.Blue,
                [new RarityCondition(RarityFlags.LegendaryPlus), new ItemTypeCondition([0x00237e80, 0x0022ed05])]),
            new FilterRule("Hide Junk",                Visibility.HideAll, FilterColors.Blue,
                [new RarityCondition(RarityFlags.Common | RarityFlags.Magic | RarityFlags.Rare)]),
            new FilterRule("Check Rare - Build Affix", Visibility.Recolor, FilterColors.Orange,
                [new RarityCondition(RarityFlags.Rare), new AffixCondition(allIds, 1)]),
            new FilterRule("BiS Rare - 2+ Core Stats", Visibility.Recolor, FilterColors.Gold,
                [new RarityCondition(RarityFlags.Rare), new AffixCondition(coreIds, 2)]),
            new FilterRule("Codex Upgrade",            Visibility.Recolor, FilterColors.Green,
                [new CodexCondition()]),
            new FilterRule("Legendaries - Keep All",   Visibility.Recolor, FilterColors.Green,
                [new RarityCondition(RarityFlags.LegendaryPlus)]),
            new FilterRule("Greater Affix - Loot",     Visibility.Recolor, FilterColors.Cyan,
                [new GreaterAffixCondition(1)]),
            new FilterRule("Show All - Catch All",     Visibility.Show,    FilterColors.Blue,
                [new RarityCondition(RarityFlags.All)]),
        ]);
    }

    [Fact]
    public void Decode_ExampleUniqueFilter_IdentifiesHashIds()
    {
        // Real filter code containing unique item references
        const string code = "Cj8QAB0AAP//IjQICBU3aAMAFYjnEwAVq6sdABXVuicAFbh7BQAVJW4fABWTvSYAFVlfAwAV0qsdABW0ECQAKAESB1VuaXF1ZXMYByAB";
        var ruleset = FilterCodec.Decode(code);

        ruleset.Name.ShouldBe("Uniques");
        ruleset.Rules.Count.ShouldBe(1);
        
        var rule = ruleset.Rules[0];
        rule.Conditions.Count.ShouldBe(1);
        rule.Conditions[0].ShouldBeOfType<SpecificUniqueCondition>();
        
        var unique = (SpecificUniqueCondition)rule.Conditions[0];
        // These should be hash IDs from the wire format
        unique.UniqueIds.Count.ShouldBeGreaterThan(0);
        
        // Log the IDs for inspection
        var idStrs = unique.UniqueIds.Select(id => $"0x{id:x8}").ToList();
        System.Diagnostics.Debug.WriteLine($"Unique IDs: {string.Join(", ", idStrs)}");
    }

    [Fact]
    public void LoadAllConditionsJson_EncodeDecode_RoundTrips()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\json-filters\All Conditions Test.json");
        var json = File.ReadAllText(Path.GetFullPath(jsonPath));
        var ruleset = JsonSerializer.Deserialize<FilterRuleset>(json, FilterJsonOptions.Default)!;

        FilterCodec.Decode(FilterCodec.Encode(ruleset)).Rules.Count.ShouldBe(ruleset.Rules.Count);

        var code = FilterCodec.Encode(ruleset);
        var decoded = FilterCodec.Decode(code);

        for (int i = 0; i < ruleset.Rules.Count; i++)
        {
            var o = ruleset.Rules[i];
            var d = decoded.Rules[i];
            d.Name.ShouldBe(o.Name);
            d.Visibility.ShouldBe(o.Visibility);
            d.Color.ShouldBe(o.Color);
            d.IsEnabled.ShouldBe(o.IsEnabled);
            d.Conditions.Count.ShouldBe(o.Conditions.Count);
        }
    }
}

