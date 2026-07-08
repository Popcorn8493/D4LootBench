# D4LootBench — Project Context

## What This Is
A standalone WPF desktop application for editing Diablo IV loot filter share codes. D4's in-game filter UI is clunky; this app lets players import a filter code, visually edit all its rules, then re-export the code to paste back into the game. Distribution via GitHub Releases as a self-contained single-file `.exe` — no installer, no hosting required.

## Technology Stack
- **.NET 10 / WPF** (`net10.0-windows`) — Windows-only desktop app
- **CommunityToolkit.Mvvm 8.4.2** — MVVM source generators (D4LootBench.App)
- **Microsoft.Extensions.DependencyInjection 10.0.0** — DI container for the App
- **AvalonEdit 6.3.0** — JSON editor with syntax highlighting, folding, search
- **Shouldly 4.3.0** — test assertions
- **xUnit** — test runner

## Solution Layout
```
D4LootBench.slnx
├── src/D4LootBench.Core/          # Pure .NET 10 class library — zero WPF dependency
│   ├── Models/               # FilterRuleset, FilterRule, 10 Condition subtypes + UnknownCondition
│   ├── Codec/                # FilterCodec (encode/decode), ProtoWriter, ProtoReader
│   ├── Data/                 # IFilterDataService + per-category catalogs, *Database statics, d4-data.json
│   ├── Validation/           # IFilterValidator, FilterValidator, ValidationResult
│   └── Serialization/        # FilterJsonOptions, HexUInt32Converter, annotated {id,name} converters, FilterDataContext
├── src/D4LootBench.Ai/            # Pure .NET 10 class library — no WPF dependency
│   ├── ILlmProvider.cs       # Core abstraction (GetCompletionAsync)
│   ├── LlmSettings.cs        # Provider enum + config model (BaseUrl, ModelName, ApiKey)
│   ├── LlmCompletion.cs      # Result wrapper (Content, Error, IsSuccess)
│   ├── RuleGenerationResult.cs # Success/failure + Rule + Suggestions + Warnings
│   ├── RuleAssistant.cs      # Orchestrates prompt → provider → parse → resolve → validate
│   ├── SystemPromptBuilder.cs # Builds/caches system prompt from live catalogs
│   ├── NameResolver.cs       # Name → hash ID resolution with fuzzy fallback
│   └── Providers/
│       ├── OllamaProvider.cs # HTTP to localhost OpenAI-compat endpoint
│       └── MockLlmProvider.cs # Hardcoded response for UI dev / test mode
├── src/D4LootBench.App/           # WPF app
│   ├── ViewModels/           # MainWindowVM, VisualEditorVM, FilterRuleVM, RawEditorVM, ColorPickerVM, AiAssistantVM, Conditions/*
│   ├── Views/                # VisualEditorView, RawEditorWindow, ColorPickerDialog, IssuesPanel, AiAssistantView
│   ├── Behaviors/            # ScrollNewItemsIntoView attached behavior
│   ├── Converters/           # BoolToBrushConverter, ValidationSeverityConverter
│   ├── Services/             # ServiceConfiguration, LlmSettingsService, LlmProviderFactory, SettingsAwareLlmProvider
│   └── Utilities/            # ColorUtility (HSV/ABGR conversion, contrast helper)
├── src/D4LootBench.Paragon/       # Pure class library — paragon board tool (Phase 5)
│   ├── Models/               # ParagonData, ParagonBoardDef, ParagonNodeDef, ParagonGlyphDef, thresholds
│   ├── Data/                 # ParagonDatabase lazy singleton, embedded paragon-data.json (79 boards / 561 nodes / 160 glyphs)
│   ├── Solver/               # ParagonLayout, ComposedGraph, SteinerSolver, GlyphRadius, PlanSolver, NodeRules, GlyphOptimizer, PlacementAnalyzer, PointMaximizer, BuildComparer, BuildCombiner, LayoutOptimizer, BuildStats
│   └── Import/               # MaxrollParagonCodec (variant codes), MobalyticsParagonImporter (build-guide pages)
├── tests/D4LootBench.Core.Tests/
│   ├── Codec/                # FilterCodecTests — round-trip, real Raxx filter, idempotency
│   ├── Validation/           # FilterValidatorTests — 19 tests for limits, boundaries, indices
│   ├── SerializationTests/   # AnnotatedJsonTests — id-wins, name-only, legacy form, unknown hash
│   └── TestSetup.cs          # ModuleInitializer that wires FilterDataContext for tests
├── docs/
│   ├── filter-format.md      # Full protobuf spec with field tables and hash IDs
│   ├── d4-data-format.md     # d4-data.json schema reference (for community edits)
│   ├── reference-codes/      # Raw Base64 share codes (Raxx, wudijo, crit-filter, GameRant)
│   └── design/               # Archived design docs and phase history
│       ├── phase-history.md  # Per-phase build narrative (Phases 0–4A)
│       ├── visual-editor.md  # Phase 2 design decisions
│       ├── ai-assistant.md   # Phase 4 design decisions
│       └── data-gaps.md      # Data gap analysis and resolution notes
└── json-filters/             # Reference fixtures (Raxx filter, All Conditions Test)
```

## Current State
All phases complete (0–4B); Phase 5 (paragon) data layer in progress. **121 tests**, 0 warnings. See `docs/design/phase-history.md` for the full build narrative.

Editor ergonomics round (July 2026): duplicate rule (toolbar + Ctrl+D), Delete key removes the selected rule, drag-to-reorder in the rule list, drag entries between picker lists (Available→Selected adds; cross-picker Selected→Selected moves; drops into the greater-affix picker copy instead of move via `PickerViewModel.OwnsEntries`), Required⇄Optional affix condition conversion (keeps affixes/count/greater flags; blocked while the rule already has the target type), and redundancy detection: `RedundancyAnalyzer` in Core finds duplicate rules and shadowing catch-alls, `FilterValidator` surfaces them as warnings (plus "min count exceeds selected affixes" never-match warnings), and a Clean Up toolbar button removes duplicates after confirmation.

Phase 4B added build guide import: paste a gear section from Mobalytics, Maxroll, or Icy Veins to auto-generate a BiS filter. Parsers live in `D4LootBench.Core/Import/`; `BuildGuideFilterGenerator` in `D4LootBench.Ai/Import/` does deterministic name resolution and rule construction (no LLM). Dialog + ViewModel in `D4LootBench.App`. Output: per-slot rules (ItemType + up to 4 affixes, require 2), Target Uniques rule, All Charms rule, Hide All fallback.

Phase 5 (paragon board tool, July 2026): P1 data layer, P3 path solver, P2 multi-board planner window, and P4 Maxroll interop are done. `tools/extract-paragon-data.cs` generates `paragon-data.json` from a DiabloTools/d4data sparse checkout (boards, nodes, glyphs, glyph affixes, thresholds; schema in `docs/paragon-data-format.md`). Node magnitudes resolve through six empirically calibrated engine multiplier constants — extraction validated cell-for-cell and stat-for-stat against Wowhead's paragon calculator (227/227 values across two boards). `D4LootBench.Paragon` is a pure class library: models + `ParagonDatabase` loader (same embedded/local-override convention as d4-data.json); `Solver/` — `ParagonLayout` (attachment tree, 90° rotations, gates at edge midpoints, overlap/duplicate validation, physical board positions), `ComposedGraph` (gates of ALL physically adjacent boards connect, matching the game, not just parent-child), `SteinerSolver` (exact Dreyfus–Wagner node-weighted Steiner DP up to 10 targets, nearest-terminal heuristic + prune beyond; verified against BFS), and `GlyphRadius` (Manhattan diamond — renders square in-game because boards draw rotated 45°; radius 3, 4 at glyph level 25, 5 at 50; purchased nodes only, same board); `Import/MaxrollParagonCodec` — Maxroll planner "variant codes" are plain JSON (`[{id, nodes, rotation, position, glyph?, glyphLevel?}]`, node keys = unrotated `row*21+col`), decoded/encoded with absolute-position → attachment-tree conversion (starter connects only via its single gate — walk gate connectivity, not raw adjacency). The App's "Paragon" toolbar button opens `ParagonPlannerWindow`: build a layout (attach boards at any gate with rotation, up to 5), click-to-mark targets, Solve highlights the cheapest path with per-board point counts, glyph-socket radius stat report, and a 342-point cap warning; Import Code / Export Code round-trip real Maxroll builds (verified semantically lossless on a live 5-board Barrage Rogue build).

Phase 5 planning layer (July 2026): glyph activation, node rules, and placement analysis. `SolverConstraints` (per-cell weights + blocked cells) threads through `SteinerSolver`; `NodeRules`/`NodeGrouping` groups a layout's nodes by stat (normal/magic) or name (rare/legendary) and applies Allow/Avoid/Exclude/Limit-N per group — Avoid costs weight 5, Exclude is hard-blocked, and limits are enforced by escalating weight re-solves (5→20→80) with a note if the minimum usage still exceeds the limit; `GlyphOptimizer` extends a solved tree to activate socketed glyphs, greedily buying same-board nodes inside the glyph radius by stat-per-point until the requirement is met (the activation threshold is engine-side, not in paragon-data.json — default 40, user-editable per socket); `GlyphInfo` infers a glyph's primary stat (attributeMaps sourceAttribute → core-stat in internalName → affix tags); `PlanSolver` orchestrates all of it and reports per-goal outcomes; `PlacementAnalyzer` re-solves under every alternate rotation of each attached board and brute-forces glyph→socket assignments, suggesting moves that save points or activate more glyphs. In the planner window: right panel with per-socket glyph pickers (level → radius, required stat, "buy stat in radius" checkbox) and node-rule rows, right-click a node to cycle avoid (orange dashed ring) / exclude (red X) / clear, Analyze Placement button, and solve details reporting per-socket radius stat totals and activation status.

Import/compare/combine + point spending (July 2026): `MaxrollBuildImporter` imports paragon builds by URL — build-guide pages embed `d4/planner/<id>` links, and the profile API (`planners.maxroll.gg/profiles/d4/<id>`) returns profiles whose `paragon.steps[].data` is exactly the variant-code array, so guide URL, planner URL, and pasted code all flow through one "Import Build" button (Mobalytics URLs/HTML too). "Compare Import" pits the current build against a clipboard build: points, rare/legendary counts, glyphs, per-attribute stat totals (own units, biggest diffs first), and a per-metric verdict naming which build looks better overall. "Combine Import" merges a clipboard build into the current one (`BuildCombiner`: boards union by position, same board at different positions folds via rotation-independent node keys, board-cap and gate-connectivity conflicts dropped with notes, duplicate glyphs deduped) and re-solves the cheapest tree covering both builds' rare/legendary nodes. Placement suggestions are now actionable: each carries a `PlacementChange` (rotation or glyph re-socketing) with an Apply button that edits the live board — remapping targets/constraints/glyph picks through the rotation — and re-solves so the difference is visible, plus a one-shot Revert (verified in-app: a suggested 180° rotation re-solved 57 → 42 points and reverted cleanly). "Spend Remaining Points" (`PointMaximizer`) grows the solved tree with the leftover of a user-set point pool: greedy value-per-point with per-attribute normalization (flat vs percent compete fairly), focus checkboxes for specific stats (default: the four core stats), an optional rares-first phase ("prefer rare nodes"), honoring excludes/avoids and stopping Limit groups at their cap.

Start-from-goal planning (July 2026): the "Plan Layout" toolbar button opens a dialog where each class board is Must use / Consider (pool) / Ignore, desired glyphs are checked off (with a filter box), and boards-to-use, glyph level, and required stat are set; `LayoutOptimizer` then computes the whole build. Key decomposition: a glyph's attainable stat around a socket is rotation-independent (the radius diamond rotates with the board), so glyph→board assignment (brute-force, maximizing activatable count then total stat) happens before any arrangement search; pool boards fill open slots ranked by best glyph support. Arrangement search enumerates connected position sets grown from the starter's one gate (positions may touch the starter's gateless edges — connectivity flows through other boards), permutes boards across positions, picks each board's rotation by BFS cost from its entry gates to its legendary node (plus socket when glyphed), ranks all combos by a depth+internal-cost heuristic, and full-`PlanSolver`-solves the top 25 (targets = every board's legendary; glyph goals attached), choosing by activated glyphs then points. The winning layout, glyph socket assignments, targets, and solved path apply straight to the planner. Verified in-app: pool-only run picked 2 boards, socketed the glyph on the best-fit board, 69 points reaching both legendaries with the glyph activated.

Thresholds + glyph delivery (July 2026): `BuildStats` computes a build's effective stat totals including rare-node threshold bonuses — the game scales a threshold's requirement with the board's 0-based attachment order (`thresholds[].requirements[].valuesByBoardIndex`, e.g. 190 → 265 → 340 Willpower; the planner's slot order is the attachment order), checks it against character TOTAL stat (a user-editable "Sheet stats (level+gear)" offset in the right panel plus paragon `_Core` totals), and activates bonuses to a fixpoint since one met bonus can grant the stat that unlocks another. Solve details report "Threshold bonuses: x of y active" with per-node shortfalls ("Tenacity (slot 0) needs 630 Strength Total — have 45"). Glyph delivery: attribute-mapped glyphs (e.g. Enchanter, Int → Damage) deliver scalar(level) × in-radius source stat; node-rarity-bonus glyphs (e.g. Ambidextrous, Unleash) deliver their scalar flat, with the in-radius stat only gating activation — scalars come from `startingBonusScalar + addedBonusScalarPerLevel`, and since the game's display units aren't in the data they're surfaced as relative benefit scores, not sheet numbers. Factored in everywhere: solve details show each socket's delivery; `PointMaximizer` counts source stat inside an active glyph's radius twice (raw value + glyph conversion); `LayoutOptimizer` ranks arrangements by activated glyphs → active thresholds (slot order changes requirements) → points → delivery; `BuildComparer` gains "activates more threshold bonuses" and "higher glyph delivery score" metrics and counts met bonuses in stat totals. UIA gotcha discovered: buttons that open modal dialogs block `InvokePattern.Invoke` — issue the Invoke from a throwaway helper process and drive the dialog from the main script; physical clicks are unreliable while the user works (foreground can't be stolen, and topmost windows fight the user).

## Filter Code Format (Critical Background)
D4 share codes are **Base64-encoded hand-rolled Protocol Buffers binary**. Full spec in `docs/filter-format.md`. Key points:
- **Filter** → repeated Rule messages (field 1) + name (field 2) + count (field 3) + version=1 (field 4)
- **Rule** → name (1), visibility/enum (2), color/ABGR-uint32 (3), repeated Condition (4), enabled (5)
- **Condition** types (all 10 known + `UnknownCondition` defensive fallback): Item Power (0), Rarity (1), Item Properties (2), Greater Affix (3), Codex (4), Item Type (5), Required Affixes (6), Optional Affixes (7), Specific Unique (8), Talisman Set (9)
- All 10 condition types are fully modelled with codec support and per-type editor ViewModels
- Color format: packed ABGR `uint32` little-endian
- Rules are written in **reverse display order** (lowest-priority rule first in binary)
- **Maximum 25 rules per filter** — game-enforced limit; pre-emptive validation disables Copy Code when violated
- 251 affix hash IDs, ~200 skills (9 classes), 27 item types, ~900 unique items (~848 display names resolved)

Sources: Upsilon72/d4-filter-generator, fnuecke/diablo4-loot-filter-viewer, DiabloTools/d4data, d4lfteam/d4lf

## JSON Output Format (Annotated)
Filter JSON emits hash IDs as `{ "id": "0x…", "name": "…" }` objects across affixes, item types, uniques, talisman sets, plus name siblings on `GreaterAffixEntry` and `TalismanSetEntry`. Hash IDs remain authoritative — names are informational. On read: `id` wins when present; `id` missing falls back to name lookup; mismatched `id`+`name` prefers `id` (validator surfaces a warning). Legacy string-hash form (`"AffixIds": ["0x…"]`) still deserializes. Converters resolve names through `FilterDataContext.Current`, set once at app startup.

## Key Decisions
- **WPF over MAUI** — audience is 100% Windows, simpler deployment
- **Custom protobuf codec** over Google.Protobuf — 3 wire types, ~80 lines, handles unknown fields for patch resilience
- **Shouldly** over FluentAssertions — FA v8 went commercial; Shouldly stays MIT
- **UnknownCondition** — preserves raw bytes for future/prototype condition types, ensuring lossless round-trips
- **Per-type ViewModels** — each condition type gets its own editor ViewModel + DataTemplate
- **DI via Microsoft.Extensions.DependencyInjection** — standard; `SettingsAwareLlmProvider`, `SystemPromptBuilder`, `NameResolver`, `RuleAssistant` registered as singletons
- **`SettingsAwareLlmProvider` singleton** — lets `RuleAssistant` be a singleton while provider selection changes at runtime; reads `LlmSettingsService.Current` on each call
- **DPAPI for API key storage** — `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser`; in-box on `net10.0-windows`, no extra NuGet package needed
- **No hardcoded API key** — users bring their own Ollama instance; Ollama is the recommended free path
- **Annotated JSON over wire form** — `{id, name}` makes the format human-editable AND lets an LLM reason about content; old string-hash form still reads for backward compat
- **Static `FilterDataContext` for JSON converters** — STJ reflectively constructs converters with no ctor args, so the data service is reached via a narrow set-once static
- **Phantom `% X` primary stats removed from `d4-data.json`** — hashes `0x001d5ded..0x001d5df5` exist in CoreTOC but D4's filter editor doesn't expose them. See `docs/filter-format.md`.
- **Panel collapse via row heights** — `Visibility=Collapsed` on fixed-height Grid rows doesn't reclaim space; code-behind sets row heights to 0 instead. Last user-dragged height preserved.

## Running / Testing
```powershell
dotnet build          # full solution (0 warnings)
dotnet test           # 109 tests in D4LootBench.Core.Tests
dotnet publish src/D4LootBench.App -r win-x64 -p:PublishSingleFile=true --self-contained true
```

## Ad-Hoc Verification
Use `dotnet run verify.cs` (no extra install — built into .NET 10) for one-off C# scripts that reference the solution. Write a top-level statement file, add project references inline, and run. Do NOT use `dotnet script` — that requires installing a separate global tool. Prefer a temporary xunit test or `dotnet run verify.cs` over standalone scripts.

