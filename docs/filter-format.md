# D4 Loot Filter — Share Code Format Specification

## Attribution

This format was reverse-engineered from community research. The following sources must be
credited in any public release of D4LootBench:

| Source | License | Contribution |
|--------|---------|-------------|
| [Upsilon72/d4-filter-generator](https://github.com/Upsilon72/d4-filter-generator) | MIT | Original protobuf wire format reverse engineering; affix hash IDs; condition type encoding (Season 13) |
| [fnuecke/diablo4-loot-filter-viewer](https://github.com/fnuecke/diablo4-loot-filter-viewer) | Unlicense (public domain) | Complete `.proto` field layout; condition type semantics for all 10 types; `names.json` ID lookup table |
| [DiabloTools/d4data](https://github.com/DiabloTools/d4data) | MIT | `CoreTOC_flat.json` — authoritative datamined ID tables for all skills, item types, and affixes; `json/enUS_Text/meta/StringList/Item_*.stl.json` — player-friendly unique item display names (build 3.0.2.71886, May 2026) |
| [ThunderEagle/LootBenchDataExtract](https://github.com/ThunderEagle/LootBenchDataExtract) | Private | CASC extraction pipeline that generates `d4-data.json`; gates unique items on release-state flag; adds `isMythic` from `Item.Meta+0x20` |
| [d4lfteam/d4lf](https://github.com/d4lfteam/d4lf) | MIT | Affix name reference database |
| [Raxx (filter author)](https://github.com/raxxanterax/GAMING/blob/main/Raxxs%20Diablo%204%20T6%2B%20Endgame%20Filter.txt) | N/A | Real-world filter export used to validate and extend the format spec |

---

## Encoding

Share codes are **Base64-encoded Protocol Buffers binary** (no compression).

```
share_code = Base64( protobuf_bytes )
```

The protobuf encoding is hand-rolled (not using a generated .proto file in the official client), but follows standard wire format:

| Wire type | Value | Used for |
|-----------|-------|----------|
| VARINT    | 0     | integers, enums, booleans |
| FIXED32   | 5     | 32-bit hash IDs (little-endian) |
| LEN       | 2     | strings, nested messages |

### Hash IDs

**Critical finding:** Hash IDs in the filter format are **not computed hashes** — they are **the hexadecimal representation of SNO IDs** (game asset identifiers from CoreTOC).

```
hash_id = sno_id_as_uint32
```

**Examples:**
| Asset | SNO ID (decimal) | Hash ID (hex) | Wire format (little-endian) |
|-------|-----------------|---------------|--------------------------|
| Axe (item type) | 446801 | 0x0006D151 | `51 d1 06 00` |
| Fists of Fate (unique) | 223287 | 0x00036837 | `37 68 03 00` |
| Charm (item type) | 2288901 | 0x0022ED05 | `05 ed 22 00` |

This means:
- All item type IDs (affixes, uniques, item types, skills) are stored and transmitted as their SNO IDs
- No hash function is applied; the value is used directly in FIXED32 wire format
- Community databases (fnuecke, DiabloTools/d4data) that map SNO IDs to names can be used directly to decode filter conditions

---

## Top-Level Message: Filter

Fields are NOT in field-number order in the binary. Rules come first, then name/count/version.

| Field | # | Wire | Type | Notes |
|-------|---|------|------|-------|
| rules | 1 | LEN  | Rule[] | Repeated; written first in the binary |
| name  | 2 | LEN  | string | Filter display name (max ~30 chars) |
| count | 3 | VARINT | uint32 | Number of rules (redundant with array length) |
| version | 4 | VARINT | uint32 | Always `1` in observed exports |

**Byte layout of makeFilter output:**
```
[rule1_bytes] [rule2_bytes] ... [ruleN_bytes]
[tag=0x12][len][name_utf8]
[tag=0x18][count_varint]
[tag=0x20][0x01]
```

---

## Rule Message (field 1 of Filter)

| Field | # | Wire | Type | Notes |
|-------|---|------|------|-------|
| name     | 1 | LEN    | string | Rule label shown in game |
| visibility | 2 | VARINT | Visibility | 0=SHOW, 2=RECOLOR, 3=HIDE_ALL |
| color    | 3 | FIXED32 | uint32 | ARGB packed; `0xFFE82222` = red; omitted → game default |
| conditions | 4 | LEN  | Condition[] | Repeated; one per condition |
| enabled  | 5 | VARINT | bool | Always `1` (true) |

### Visibility Enum
```
SHOW     = 0  — item is visible, no color change
RECOLOR  = 2  — item is visible with tint applied
HIDE_ALL = 3  — item is hidden entirely
```

### Color Format (ARGB packed uint32, little-endian)

The in-game color picker displays colors as 6-char RGB hex (e.g. `E82222`).
The binary stores the full ARGB uint32; alpha is always `0xFF`.

```csharp
uint PackColor(byte r, byte g, byte b, byte a = 255)
    => (uint)((a << 24) | (r << 16) | (g << 8) | b);
```

Known color constants:
| Name | Value | RGB |
|------|-------|-----|
| Blue   | `0xFF0000FF` | (0, 0, 255) |
| Cyan   | `0xFF00FFFF` | (0, 255, 255) |
| Green  | `0xFF00C800` | (0, 200, 0) |
| Orange | `0xFFFF8C00` | (255, 140, 0) |
| Gold   | `0xFFFFD700` | (255, 215, 0) |

> **Note:** field 3 is omitted when the rule has no color override (game renders native item color).
> `0` is the sentinel value in the model; the encoder skips field 3 when `Color == 0`.

---

## Condition Message (field 4 of Rule)

The condition type discriminator is field 1. The complete enum (from
[fnuecke/diablo4-loot-filter-viewer](https://github.com/fnuecke/diablo4-loot-filter-viewer)):

| Type | Name | C# class | Status |
|------|------|-----------|--------|
| 0 | Item Power Range | `ItemPowerCondition` | ✅ fully modelled |
| 1 | Item Rarity Match | `RarityCondition` | ✅ fully modelled |
| 2 | Item Properties | `ItemPropertiesCondition` | ✅ fully modelled (bitmask: 1=None, 4=Ancestral, 32=Mythic) |
| 3 | Codex Upgrade Check | `CodexCondition` | ✅ fully modelled |
| 4 | Greater Affix Check | `GreaterAffixCondition` | ✅ fully modelled |
| 5 | Item Type Match | `ItemTypeCondition` | ✅ fully modelled |
| 6 | Has Required Affixes | `AffixCondition` | ✅ fully modelled (GA pairs decoded; Field5 observed always 0) |
| 7 | Has Optional Affixes | `OptionalAffixCondition` | ✅ fully modelled (GA pairs decoded; Field5 observed always 0) |
| 8 | Is Specific Unique | `SpecificUniqueCondition` | ✅ modelled (~900 IDs, all internal names — no player-friendly labels) |
| 9 | Talisman Set Bonus | `TalismanSetCondition` | ✅ modelled (SetIds + SetEntries decoded; no set ID database yet) |

> **Correction — fnuecke type mapping reversed for types 3 and 4.** The original
> [fnuecke/diablo4-loot-filter-viewer](https://github.com/fnuecke/diablo4-loot-filter-viewer)
> proto mapped type 3 to Greater Affix Check and type 4 to Codex Upgrade Check.
> Real-world validation against Raxx's Torment 6+ filter export showed these are
> **swapped in the game's actual encoding**: type 3 is Codex Upgrade Check and
> type 4 is Greater Affix Check. Both are fully modelled but the codec uses the
> game-corrected mapping (`FilterCodec.cs` — `EncodeCondition`/`DecodeCondition`).

**Condition message fields** (same set regardless of type):
```
field 1  (varint)           = type discriminator
field 2  (fixed32, repeated) = params1  — hash IDs (affix IDs, item type IDs, unique IDs, set IDs)
field 3  (LEN, repeated)    = params2  — ConditionParameter pairs { fixed32 value1, fixed32 value2 }
field 4  (varint)           = value1
field 5  (varint)           = value2
field 6  (varint)           = value3
```

---

### Type 0 — Item Power Range
```
field 1 (varint) = 0
field 4 (varint) = minimum_item_power
field 5 (varint) = maximum_item_power
```

---

### Type 1 — Rarity Match
```
field 1 (varint) = 1
field 4 (varint) = rarity_bitmask
```

Rarity bitmask bits:
| Bit | Value | Rarity |
|-----|-------|--------|
| 0   | 0x01  | Common (Normal) |
| 1   | 0x02  | Magic |
| 2   | 0x04  | Rare |
| 3   | 0x08  | Legendary |
| 4   | 0x10  | Unique |
| 5   | 0x20  | Mythic Unique |
| 6   | 0x40  | Talisman/Charm |

---

### Type 2 — Item Properties
```
field 1 (varint) = 2
field 4 (varint) = property_mask
```

Property mask bits (a bitmask — the in-game editor's None/Ancestral/Mythic checkboxes OR together):
| Bit | Value | Meaning |
|-----|-------|---------|
| 0   | 1  | None (no property filter) |
| 2   | 4  | Ancestral |
| 5   | 32 | Mythic |

Examples: "All Ancestrals" raw bytes `CAIgBA==` → type=2, field 4=4 (Ancestral).
A rule with Ancestral + Mythic checked in-game exports mask 36 (= 4 | 32).
Modelled as `ItemPropertiesCondition`; unknown bits are preserved for lossless round-trips.

---

### Type 3 — Codex Upgrade Check
```
field 1 (varint) = 3
field 6 (varint) = 1
```

---

### Type 4 — Greater Affix Check
```
field 1 (varint) = 4
field 4 (varint) = 1   (always)
field 6 (varint) = minimum_count
```

---

### Type 5 — Item Type Match
```
field 1 (varint)           = 5
field 2 (fixed32, repeated) = type_hash_id
```

See `ItemTypeDatabase` for the full list. Selected entries:
| Name | Hash |
|------|------|
| Charm | `0x0022ed05` |
| Horadric Seal | `0x00237e80` |
| Dagger | `0x0006d159` |
| Axe | `0x0006d151` |
| Bow | `0x0006d167` |

All 0x6Dxxx-range IDs sourced from fnuecke/diablo4-loot-filter-viewer `names.json` (datamined).

> **Note:** Upsilon72/d4-filter-generator has Charm (`0x0022ed05`) and Seal (`0x00237e80`) labeled
> in reverse. Our `ItemTypeDatabase` uses the correct names as confirmed by `names.json`.

---

### Type 6 — Has Required Affixes
```
field 1  (varint)           = 6
field 2  (fixed32, repeated) = affix_hash_id
field 3  (LEN, repeated)    = params2  — GA pairs: { fixed32 affix_id1, fixed32 affix_id2 }
field 4  (varint)           = minimum_count
```

`params2` encodes Greater Affix requirements as pairs of affix hash IDs. Codec decodes
field 3 into `GreaterEntries` on `AffixCondition`.

**Greater Affix pair shape.** Each field 3 sub-message holds two fixed32 IDs.
Across every game-exported sample we have collected (Raxx 2024 Universal/Main-Stat/Specific
GA rules; six hand-built samples with mixed configurations of 2/0, 2/1, 3/1, 3/2 and
all-greater shapes), the **second uint always equals the first** — i.e. the affix hash is
echoed. The model preserves it as `GreaterAffixEntry.AffixIdEcho` for lossless round-trips
in case some unobserved game state uses it differently, but encoders should write the same
value to both slots.

**Mixed greater / non-greater shape.** AffixIds (field 2) carries the complete required
list — both regular-required and must-be-greater hashes. GreaterEntries (field 3) carries
only the must-be-greater subset. For a 3-affix rule where 1 is greater, AffixIds.Count = 3
and GreaterEntries.Count = 1; D4 imports this correctly.

**Field 5 observation.** Across every observed game-exported sample (including the mixed
configurations above), field 5 is absent or zero. The model retains `AffixCondition.Field5`
for verbatim round-trips in case patches reintroduce it, but the encoder skips field 5
when the value is 0 — matching the game's own writes.

---

### Type 7 — Has Optional Affixes
```
Same field layout as Type 6.
```

Items match if they have *any* of the listed affixes (OR semantics vs Type 6's AND/count).
Model class: `OptionalAffixCondition` — structurally identical to `AffixCondition`
(AffixIds, MinimumCount, GreaterEntries, Field5) with a different type discriminator.
Same Field 5 / GA-pair / mixed-shape observations as Type 6.

---

### Type 8 — Is Specific Unique
```
field 1  (varint)           = 8
field 2  (fixed32, repeated) = unique_item_sno_id
```

Matches specific named Unique items by SNOName ID. Model class: `SpecificUniqueCondition`.
`UniqueItemDatabase` contains ~900 entries from `CoreTOC_flat.json` but all with internal
non-friendly names (e.g. `Gloves_Unique_Generic_002`). Adding player-friendly display
names requires cross-referencing against community databases or in-game data.

---

### Type 9 — Talisman Set Bonus
```
field 1  (varint)           = 9
field 2  (fixed32, repeated) = set_hash_id       → TalismanSetCondition.SetIds
field 3  (LEN, repeated)    = { fixed32 set_id, fixed32 item_id }  → TalismanSetCondition.SetEntries
```

Model class: `TalismanSetCondition` with `SetIds` (field 2) and `SetEntries` (field 3 pairs).
An empty selection (`CAk=` → type=9, no IDs) matches no items; round-trips as an empty
`TalismanSetCondition`. Set IDs not yet catalogued — display as hex until a set database
is built. fnuecke's `names.json` has matching entries prefixed `Talisman_`.

---

## Affix Hash IDs (63 confirmed, Season 13)

All IDs confirmed via Upsilon72/d4-filter-generator; `0x001beab8` additionally verified
against fnuecke/diablo4-loot-filter-viewer `names.json` (`S04_CooldownReductionCDR`).

### Phantom affixes in CoreTOC (not filter-selectable)

DiabloTools/d4data's `CoreTOC_flat.json` contains a cluster of `% X` primary-stat entries
at hashes `0x001d5ded` (`% Armor`), `0x001d5def` (`% Dexterity`), `0x001d5df1`
(`% Intelligence`), `0x001d5df3` (`% Strength`), `0x001d5df5` (`% Willpower`). These hashes
exist in the game data dump but **D4's in-game filter editor does not expose them** — share
codes that reference them import with the affix silently dropped from the rule. The real
flat-stat affixes are at the `0x001bea**` range (e.g. Dexterity `0x001beaba`, Intelligence
`0x001beabe`).

The `%` prefix alone is not a disqualifier — `% Cooldown Reduction` at `0x001beab8` is a
legitimate filter affix, confirmed by a game-exported sample. Future contributors
regenerating `d4-data.json` from CoreTOC should re-prune the `0x001d5ded..0x001d5df5`
cluster.

### Offensive
| Affix | Hash |
|-------|------|
| Weapon Damage | `0x0027fc93` |
| All Damage Multiplier | `0x001beac6` |
| Attack Speed | `0x001beace` |
| Critical Strike Chance | `0x001bead2` |
| Critical Strike Damage Multiplier | `0x001bead4` |
| Vulnerable Damage Multiplier | `0x001bfc80` |
| Damage Over Time Multiplier | `0x001bead6` |
| Cold Damage Multiplier | `0x00270af5` |
| Fire Damage Multiplier | `0x00270af7` |
| Holy Damage Multiplier | `0x00270aff` |
| Lightning Damage Multiplier | `0x00270afd` |
| Physical Damage Multiplier | `0x00270ad0` |
| Poison Damage Multiplier | `0x00270afb` |
| Shadow Damage Multiplier | `0x00270af9` |

### Primary Stats
| Affix | Hash |
|-------|------|
| Strength | `0x001beac2` |
| Intelligence | `0x001beabe` |
| Willpower | `0x001beab4` |
| % Cooldown Reduction | `0x001beab8` |
| Dexterity | `0x001beaba` |

### Defensive
| Affix | Hash |
|-------|------|
| Maximum Life | `0x001bead8` |
| Life Regeneration | `0x001beada` |
| Life On Hit | `0x001d5e13` |
| Life on Kill | `0x0025da8c` |
| Armor | `0x001beab2` |
| Resistance to All Elements | `0x001bfd38` |
| Fire Resistance | `0x001beaee` |
| Cold Resistance | `0x001beb2e` |
| Lightning Resistance | `0x001beaf2` |
| Poison Resistance | `0x001beaf4` |
| Shadow Resistance | `0x001beaf6` |
| Physical Resistance | `0x002557e4` |
| Damage Reduction | `0x001d6e63` |
| Dodge Chance | `0x001bfc85` |
| Thorns | `0x001beb22` |

### Resource
| Affix | Hash |
|-------|------|
| Maximum Resource | `0x001bfc79` |
| Energy Regeneration | `0x001d5e30` |
| Essence Regeneration | `0x001d5e3a` |
| Fury Regeneration | `0x001d5e38` |
| Mana Regeneration | `0x001d5e36` |
| Spirit Regeneration | `0x001d5e33` |
| Vigor Regeneration | `0x001eb549` |
| Faith Regeneration | `0x002674b9` |
| Wrath Regeneration | `0x0026a37c` |
| Energy On Kill | `0x001d5e25` |
| Essence On Kill | `0x001d5e27` |
| Fury On Kill | `0x001d5e29` |
| Mana On Kill | `0x001d5e2b` |
| Spirit On Kill | `0x001d5e2d` |
| Vigor On Kill | `0x001eb481` |
| Faith On Kill | `0x002674bb` |
| Wrath every 10 Kills | `0x0026a374` |
| Resource Cost Reduction | `0x001d3a0f` |
| Resource Generation | `0x001beb20` |
| Lucky Hit Restore Primary Resource | `0x0024527f` |

### Utility
| Affix | Hash |
|-------|------|
| Potion Capacity | `0x001beae2` |
| Lucky Hit Chance | `0x001beadc` |
| Healing Received | `0x001bfcbf` |
| Fortify Generation | `0x00266b1e` |
| Barrier Generation | `0x00266b22` |

### Mobility
| Affix | Hash |
|-------|------|
| Movement Speed | `0x001beade` |
| Attacks Reduce Evade Cooldown | `0x0026c56c` |
| Maximum Evade Charge | `0x0026c56e` |
| Evade Grants Movement Speed | `0x0026c570` |

---

## Skill Rank Hash IDs

All IDs datamined from DiabloTools/d4data `CoreTOC_flat.json` (build 3.0.2.71886, updated May 2026).
**None have been verified in-game** (InGameVerified=false in `SkillDatabase`).
Sorcerer basic skill display names (Spark, Fire Bolt, Frost Bolt, Arc Lash) are resolved in
`d4-data.json` using the CoreTOC mapping.

> **Warning — Upsilon72 labels were incorrect:** Upsilon72's original Warlock confirmations
> mis-identified most skill names. Do NOT use the Season 13 Upsilon72 labels; use `SkillDatabase`
> (derived from CoreTOC) as the authoritative source. Example: Upsilon72 labelled `0x0026ad42`
> as "Abyss Skills"; CoreTOC identifies it as "Hell Fracture".

### Generic (all classes)
| Skill | Hash |
|-------|------|
| All Skills      | `0x00273c0a` |
| Core Skills     | `0x001d6e31` |
| Basic Skills    | `0x001d6e2f` |
| Defensive Skills | `0x001d6e2b` |

### Barbarian
| Skill | Hash |
|-------|------|
| Bash | `0x001c60bc` |
| Flay | `0x001c60c0` |
| Frenzy | `0x001c60c2` |
| Lunging Strike | `0x001c60c4` |
| Hammer of the Ancients | `0x001c68cb` |
| Rend | `0x001c68f6` |
| Double Swing | `0x001c6908` |
| Upheaval | `0x001c690e` |
| Whirlwind | `0x001c6920` |
| Challenging Shout | `0x001c692a` |
| Charge | `0x001c692c` |
| Death Blow | `0x001c692e` |
| Ground Stomp | `0x001c6935` |
| Iron Skin | `0x001c6938` |
| Kick | `0x001c693a` |
| Leap | `0x001c6941` |
| Rallying Cry | `0x001c6943` |
| Rupture | `0x001c6945` |
| Steel Grasp | `0x001c6947` |
| War Cry | `0x001c6949` |
| Call of the Ancients | `0x002782a5` |
| Dust Devils | `0x002782a9` |
| Earthquake | `0x002782ab` |
| Iron Shrapnel | `0x002782af` |
| Brawling Skills | `0x001d6e25` |
| Weapon Mastery Skills | `0x001d6e27` |
| Bludgeoning Skills | `0x00280b83` |
| Dual Wield Skills | `0x00280b85` |
| Slashing Skills | `0x00280b87` |

### Druid
| Skill | Hash |
|-------|------|
| Earthspike | `0x001cc052` |
| Claw | `0x001cc054` |
| Storm Strike | `0x001cc056` |
| Wind Shear | `0x001cc058` |
| Maul | `0x001cc060` |
| Landslide | `0x001cc062` |
| Pulverize | `0x001cc064` |
| Tornado | `0x001cc066` |
| Lightning Storm | `0x001cc068` |
| Shred | `0x001cc06a` |
| Blood Howl | `0x001cc06d` |
| Boulder | `0x001cc06f` |
| Cyclone Armor | `0x001cc071` |
| Debilitating Roar | `0x001cc073` |
| Earthen Bulwark | `0x001cc075` |
| Hurricane | `0x001cc077` |
| Rabies | `0x001cc183` |
| Ravens | `0x001cc185` |
| Trample | `0x001cc188` |
| Vine Creeper | `0x001cc18a` |
| Wolves | `0x001cc18d` |
| Human | `0x002782b7` |
| Versatile | `0x002782b9` |
| Companion Skills | `0x001d6e2d` |
| Wrath Skills | `0x001d6e33` |
| Earth Skills | `0x00280b89` |
| Nature Magic Skills | `0x00280b8b` |
| Shapeshifting Skills | `0x00280b8d` |
| Storm Skills | `0x00280b8f` |
| Werebear Skills | `0x00280b91` |
| Werewolf Skills | `0x00280b93` |

### Necromancer
| Skill | Hash |
|-------|------|
| Bone Splinters | `0x001c7e6e` |
| Decompose | `0x001c7e7d` |
| Hemorrhage | `0x001c7e84` |
| Reap | `0x001c7e88` |
| Bone Spear | `0x001c7e90` |
| Blight | `0x001c7e9a` |
| Sever | `0x001c7ea1` |
| Blood Surge | `0x001c7ea8` |
| Blood Lance | `0x001c7eb0` |
| Blood Mist | `0x001c7eb2` |
| Bone Prison | `0x001c7eb6` |
| Bone Spirit | `0x001c7eb8` |
| Corpse Explosion | `0x001c7eba` |
| Corpse Tendrils | `0x001c7ebc` |
| Decrepify | `0x001c7ebe` |
| Iron Maiden | `0x001c7ec0` |
| Skeleton Warrior | `0x001d6e4f` |
| Skeleton Mage | `0x001d6e51` |
| Golem | `0x001d6e53` |
| Macabre Skills | `0x001d6e47` |
| Curse Skills | `0x001d6e49` |
| Corpse Skills | `0x001d6e4b` |
| Blood Skills | `0x00280b95` |
| Bone Skills | `0x00280b97` |
| Darkness Skills | `0x00280b99` |
| Minion Skills | `0x00280b9b` |

### Rogue
| Skill | Hash |
|-------|------|
| Heartseeker | `0x001c952f` |
| Puncture | `0x001c9531` |
| Invigorating Strike | `0x001c9533` |
| Forceful Arrow | `0x001c9535` |
| Blade Shift | `0x001c9537` |
| Rapid Fire | `0x001c953b` |
| Flurry | `0x001c953d` |
| Barrage | `0x001c953f` |
| Twisting Blades | `0x001c9541` |
| Penetrating Shot | `0x001c9543` |
| Poison Imbuement | `0x001c9545` |
| Caltrops | `0x001c9547` |
| Cold Imbuement | `0x001c9549` |
| Concealment | `0x001c954b` |
| Dark Shroud | `0x001c954d` |
| Dash | `0x001c954f` |
| Poison Trap | `0x001c9551` |
| Shadow Imbuement | `0x001c9553` |
| Shadow Step | `0x001c9555` |
| Smoke Bomb | `0x001c9557` |
| Grenade | `0x002782b1` |
| Shade | `0x002782b3` |
| Arrow Storm | `0x002782b5` |
| Agility Skills | `0x001d6e41` |
| Subterfuge Skills | `0x001d6e43` |
| Imbuement Skills | `0x001d6e45` |
| Cutthroat Skills | `0x00280ba1` |
| Grenade Skills | `0x00280ba3` |
| Marksman Skills | `0x00280ba5` |
| Trap Skills | `0x00280ba7` |

### Sorcerer
| Skill | Hash | Notes |
|-------|------|-------|
| Spark | `0x001d674b` | Formerly Basic_1; confirmed in d4-data.json |
| Fire Bolt | `0x001d674d` | Formerly Basic_2; confirmed in d4-data.json |
| Frost Bolt | `0x001d6750` | Formerly Basic_3; confirmed in d4-data.json |
| Arc Lash | `0x001d6752` | Formerly Basic_4; confirmed in d4-data.json |
| Fireball | `0x001d673f` | |
| Ice Shards | `0x001d6741` | |
| Chain Lightning | `0x001d6743` | |
| Charged Bolts | `0x001d6745` | |
| Incinerate | `0x001d6747` | |
| Frozen Orb | `0x001d6749` | |
| Ball Lightning | `0x001d6754` | |
| Blizzard | `0x001d6756` | |
| Firewall | `0x001d6758` | |
| Flame Shield | `0x001d675a` | |
| Frost Nova | `0x001d675c` | |
| Hydra | `0x001d675e` | |
| Ice Armor | `0x001d6760` | |
| Ice Blades | `0x001d6762` | |
| Lightning Spear | `0x001d6764` | |
| Meteor | `0x001d6766` | |
| Teleport | `0x001d6768` | |
| Familiar | `0x001e79a8` | Added in Vessel of Hatred (X1) |
| Conjuration Skills | `0x001d6e3d` | |
| Mastery Skills | `0x001d6e3f` | |
| Frost Skills | `0x00280ba9` | |
| Pyromancy Skills | `0x00280bab` | |
| Shock Skills | `0x00280bad` | |

### Spiritborn
All individual skills use `X1_` prefix (Vessel of Hatred DLC). Guardian category IDs (`X2_`) were added in Season 7.

| Skill | Hash |
|-------|------|
| Rock Splitter | `0x001eb8a4` |
| Thunderspike | `0x001ebbad` |
| Thrash | `0x001ebbb7` |
| Withering Fist | `0x001ebd1b` |
| Crushing Hand | `0x001ebd36` |
| Quill Volley | `0x001ebd49` |
| Rake | `0x001ebd60` |
| Stinger | `0x001ebdb9` |
| Vortex | `0x001ebee9` |
| Soar | `0x001ec0fe` |
| Ravager | `0x001ec119` |
| Toxic Skin | `0x001ec199` |
| Armored Hide | `0x001ec1b8` |
| Concussive Stomp | `0x001ed05a` |
| Counterattack | `0x001ed0cb` |
| Scourge | `0x001ed1d4` |
| Payback | `0x001ed2e9` |
| Razor Wings | `0x001ed2f1` |
| Rushing Claw | `0x001ed338` |
| Touch of Death | `0x001ed34d` |
| Focus Skills | `0x001ebdfd` |
| Potency Skills | `0x001ed2c8` |
| Centipede Skills | `0x00280baf` |
| Eagle Skills | `0x00280bb1` |
| Gorilla Skills | `0x00280bb3` |
| Jaguar Skills | `0x00280bb5` |

### Paladin
| Skill | Hash |
|-------|------|
| Advance | `0x002539ab` |
| Brandish | `0x00253a6c` |
| Holy Bolt | `0x00261aa5` |
| Clash | `0x00261aa7` |
| Blessed Shield | `0x0024f051` |
| Blessed Hammer | `0x0024f057` |
| Shield Bash | `0x0025122e` |
| Divine Lance | `0x0025b0e2` |
| Spear of the Heavens | `0x0025c005` |
| Zeal | `0x00261ab7` |
| Fanaticism Aura | `0x00261abb` |
| Holy Light Aura | `0x00261abd` |
| Defiance Aura | `0x00261abf` |
| Aegis | `0x00261ace` |
| Shield Charge | `0x00261ad8` |
| Falling Star | `0x00261add` |
| Rally | `0x00261ae2` |
| Consecration | `0x00261ae4` |
| Purify | `0x00261ae6` |
| Condemn | `0x00261ae8` |
| Aura Skills | `0x00261ac2` |
| Valor Skills | `0x00261ac7` |
| Justice Skills | `0x00261acc` |
| Judicator Skills | `0x0024f033` |
| Zealot Skills | `0x0024f06a` |
| Disciple Skills | `0x00280b9d` |
| Juggernaut Skills | `0x00280b9f` |

### Warlock
`Rampage` (`0x0026ad7e`) is documented here but missing from `d4-data.json` (CoreTOC extraction gap).

| Skill | Hash |
|-------|------|
| Command Fallen | `0x0026ad4d` |
| Molten Bomb | `0x0026ad68` |
| Doom | `0x0026ad6a` |
| Hellion Sting | `0x0026ad6c` |
| Hell Fracture | `0x0026ad42` |
| Umbral Chains | `0x0026ad44` |
| Blazing Scream | `0x0026ad46` |
| Dread Claws | `0x0026ad48` |
| Bombardment | `0x0026adcd` |
| Wall of Agony | `0x0026ad71` |
| Tortured Wretch | `0x0026ad78` |
| Dark Prison | `0x0026ad7a` |
| Nether Step | `0x0026ad7c` |
| Rampage | `0x0026ad7e` |
| Infernal Breath | `0x0026ad80` |
| Tyrant's Grasp | `0x0026ad83` |
| Profane Sentinel | `0x0026ad85` |
| Sigil of Subversion | `0x0026ad87` |
| Sigil of Summons | `0x0026ad89` |
| Sigil of Chaos | `0x0026ad8b` |
| Occult Skills | `0x0026adc2` |
| Hellfire Skills | `0x0026adc4` |
| Abyss Skills | `0x0026adc6` |
| Demonology Skills | `0x0026adc8` |

---

## Test Vectors

To validate a codec implementation, generate the "Generic Crit" preset and verify the output matches known-good game imports:

Core stats: Critical Strike Chance, Critical Strike Damage Multiplier, Attack Speed, All Damage Multiplier, Vulnerable Damage Multiplier
Secondary: Maximum Life, Armor
Skills: Core Skills, All Skills
Gold threshold: 2+ core stats

Expected rules (in priority order, highest first):
1. Show All - Catch All (SHOW, all rarities)
2. Greater Affix - Loot (RECOLOR cyan, ≥1 GA)
3. Legendaries - Keep All (RECOLOR green, Legendary|Unique|Mythic|Talisman)
4. Codex Upgrade (RECOLOR green, codex condition)
5. BiS Rare - 2+ Core Stats (RECOLOR gold, Rare, ≥2 of core affix IDs)
6. Check Rare - Build Affix (RECOLOR orange, Rare, ≥1 of all affix IDs)
7. Hide Junk (HIDE_ALL, Common|Magic|Rare)
8. Legendary Talismans (SHOW, Legendary+, item types Charm+Seal)

Note: Rules are written to binary in REVERSE display order (rule 8 bytes first, rule 1 bytes last).

---

## Constraints

- **Maximum 25 rules per filter** — enforced by the game client. The editor should validate and prevent export of filters exceeding this limit.
