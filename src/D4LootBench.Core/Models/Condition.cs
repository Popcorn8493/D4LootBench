using System.Text.Json.Serialization;
using D4LootBench.Core.Serialization;

namespace D4LootBench.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ItemPowerCondition),     "itemPower")]
[JsonDerivedType(typeof(RarityCondition),        "rarity")]
[JsonDerivedType(typeof(ItemPropertiesCondition),"itemProperties")]
[JsonDerivedType(typeof(GreaterAffixCondition),  "greaterAffix")]
[JsonDerivedType(typeof(CodexCondition),         "codex")]
[JsonDerivedType(typeof(ItemTypeCondition),      "itemType")]
[JsonDerivedType(typeof(AffixCondition),         "affix")]
[JsonDerivedType(typeof(OptionalAffixCondition), "optionalAffix")]
[JsonDerivedType(typeof(SpecificUniqueCondition), "specificUnique")]
[JsonDerivedType(typeof(TalismanSetCondition),   "talismanSet")]
[JsonDerivedType(typeof(UnknownCondition),       "unknown")]
public abstract record Condition;

/// <summary>Type 0 — items within an item power range (inclusive).</summary>
public sealed record ItemPowerCondition(int Minimum, int Maximum) : Condition;

public sealed record RarityCondition(RarityFlags Mask) : Condition;

/// <summary>Type 2 — filters by item property. Bitmask: 1=None, 4=Ancestral, 32=Mythic (combinable, e.g. 36).</summary>
public sealed record ItemPropertiesCondition(int PropertyMask) : Condition;

/// <summary>Type 3 — items with at least <see cref="MinimumCount"/> Greater Affixes.</summary>
public sealed record GreaterAffixCondition(int MinimumCount) : Condition;

/// <summary>Type 4 — items usable to upgrade a Codex of Power entry.</summary>
public sealed record CodexCondition : Condition;

/// <summary>Type 5 — item type hash IDs (e.g. Charm, Seal).</summary>
public sealed record ItemTypeCondition(
    [property: JsonConverter(typeof(AnnotatedItemTypeListConverter))]
    IReadOnlyList<uint> TypeIds) : Condition;

/// <summary>
/// Flags a single affix as "must be greater" inside an <see cref="AffixCondition"/> or
/// <see cref="OptionalAffixCondition"/>. Maps to one field-3 sub-message in the wire format,
/// where field 1 is the affix hash and field 2 carries a second uint we have only ever
/// observed equalling the affix hash. The field is preserved verbatim (<see cref="AffixIdEcho"/>)
/// for lossless round-trips in case some game state uses a different value we haven't seen.
/// </summary>
[JsonConverter(typeof(GreaterAffixEntryConverter))]
public sealed record GreaterAffixEntry(uint AffixId, uint AffixIdEcho);

/// <summary>Max 15 affix IDs per condition (game-enforced).</summary>
public sealed record AffixCondition(
    [property: JsonConverter(typeof(AnnotatedAffixListConverter))]
    IReadOnlyList<uint> AffixIds,
    int MinimumCount) : Condition
{
    public const int MaxSelectionCount = 15;

    public IReadOnlyList<GreaterAffixEntry> GreaterEntries { get; init; } = [];
    public int Field5 { get; init; }
}

/// <summary>Type 7 — affix hash IDs with OR semantics: matches if the item has any of the listed affixes.</summary>
public sealed record OptionalAffixCondition(
    [property: JsonConverter(typeof(AnnotatedAffixListConverter))]
    IReadOnlyList<uint> AffixIds,
    int MinimumCount) : Condition
{
    public const int MaxSelectionCount = 15;

    public IReadOnlyList<GreaterAffixEntry> GreaterEntries { get; init; } = [];
    public int Field5 { get; init; }
}

/// <summary>Type 8 — matches specific named Unique items by sno ID.</summary>
/// <remarks>Max 10 unique items per condition (game-enforced).</remarks>
public sealed record SpecificUniqueCondition(
    [property: JsonConverter(typeof(AnnotatedUniqueListConverter))]
    IReadOnlyList<uint> UniqueIds) : Condition
{
    public const int MaxSelectionCount = 10;
}

/// <summary>A set/item pair inside a <see cref="TalismanSetCondition"/> (field 3 sub-message).</summary>
[JsonConverter(typeof(TalismanSetEntryConverter))]
public sealed record TalismanSetEntry(uint SetId, uint ItemId);

/// <summary>Type 9 — Talisman Set Bonus: matches charms/seals belonging to specific sets.
/// Field 2 carries set hash IDs; field 3 carries { set_id, item_id } pair sub-messages.
/// Set IDs are not yet catalogued — they will display as hex until a TalismanSetDatabase is built.
/// Max 5 set IDs per condition (game-enforced).</summary>
public sealed record TalismanSetCondition : Condition
{
    public const int MaxSelectionCount = 5;

    [JsonConverter(typeof(AnnotatedTalismanSetListConverter))]
    public IReadOnlyList<uint> SetIds { get; init; } = [];

    public IReadOnlyList<TalismanSetEntry> SetEntries { get; init; } = [];
}

/// <summary>Preserves raw bytes for condition types not yet mapped, enabling lossless round-trips.</summary>
public sealed record UnknownCondition(int ConditionType, byte[] RawBytes) : Condition;
