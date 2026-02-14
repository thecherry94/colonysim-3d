# World Generation Deep Dive: Research & Analysis

**Status:** Research & Analysis Only — No Code Changes
**Scope:** Geological layers, ore veins, underground biomes, expanded surface biomes, terrain shape variety, structures

---

## Context

The colonysim-3d project has a solid voxel engine (threaded chunk streaming, greedy meshing, A* pathfinding, cave system, biomes, rivers, trees) but the world generation is fundamentally lacking for a Minecraft/Dwarf Fortress-style colony sim. The underground is 95% uniform Stone — there's no reason to mine, no resource progression, no discovery. The surface has only 6 biomes and 1 tree type. There are no structures, no underground biomes, and no terrain shape variety beyond smooth noise hills.

This document analyzes what the best games do (Minecraft 1.18+, Dwarf Fortress, Vintage Story, TerraFirmaCraft) and provides concrete, actionable recommendations for transforming the world generation — organized into independent implementation phases ordered by impact-to-effort ratio.

---

## 1. Gap Analysis

### The Critical Gaps

| Area | Best Games | This Project | Severity |
|------|-----------|--------------|----------|
| **Underground rock variety** | MC: Deepslate + granite/diorite/andesite. DF: 4 geological layer types, 20+ rock types. VS: 22 rock types via geologic provinces | Uniform Stone everywhere below Y-3 | **CRITICAL** |
| **Ore deposits** | MC: 10+ ores, triangular Y-distribution, large veins. DF: Ores hosted by specific rock types, 4 deposit shapes. VS: disc-shaped deposits | No ores at all | **CRITICAL** |
| **Underground biomes** | MC: Lush Caves, Dripstone Caves, Deep Dark. DF: 3 cavern layers with unique flora/fauna | No underground biomes | **HIGH** |
| **Surface biome count** | MC: 60+ biomes. DF: 40+ biomes | 6 biomes | **HIGH** |
| **Terrain shapes** | MC 1.18: Overhangs, cliffs via 3D density functions. VS: Cliffs via post-processing | Smooth noise hills only | **MEDIUM** |
| **Vegetation variety** | MC: 6+ tree types, flowers, mushrooms. DF: Multiple tree/shrub types per biome | 1 tree type | **HIGH** |
| **Structures** | MC: Villages, temples, mineshafts. DF: Ruins, abandoned settlements | None | **MEDIUM** |
| **Depth progression** | MC: Surface→deepslate→ancient cities. DF: Soil→stone→caverns→magma sea→underworld | Flat: uniform Stone everywhere | **CRITICAL** |
| **Lava/magma** | MC: Lava lakes, aquifers. DF: Magma sea, magma pipes | None | **MEDIUM** |

### What Does NOT Need Changing

The chunk pipeline, greedy meshing, A* pathfinding, collision system, cave carving approach, and tree placement framework all work well. New features slot into `GenerateChunkBlocks()` as additional phases. The BlockType byte enum has 243 unused values. All existing systems automatically handle new solid block types.

---

## 2. Geological Layer System

### What the Best Games Do

**Dwarf Fortress** uses a fixed hierarchy: Sedimentary OR Igneous Extrusive (mutually exclusive) → Metamorphic → Igneous Intrusive (deepest, above magma sea). Each layer hosts specific ores — sedimentary = iron + coal + flux (steel production), igneous intrusive = gold + diamonds. This means regional geology determines what resources are available, making starting location a strategic decision.

**Vintage Story** uses "geologic provinces" — a 2D noise map selects which rock groups dominate a horizontal region. Within a province, rock types distributed by secondary noise. This creates horizontal + vertical variation.

**Minecraft 1.18+** is simpler: Stone above Y=0, Deepslate below, with noise blobs of granite/diorite/andesite scattered throughout.

### Recommended Approach: Depth Bands + Province Noise

Combine DF's depth bands with VS's province noise. The current `BlockType.Stone` fill in `TerrainGenerator.cs` line 376 becomes a call to `GeologyGenerator.GetRockType()`.

#### New Rock BlockTypes (10 types)

| Type | ID | Color | Category |
|------|-----|-------|----------|
| Limestone | 13 | (0.78, 0.76, 0.68) pale tan | Sedimentary |
| Sandstone | 14 | (0.76, 0.60, 0.42) warm brown | Sedimentary |
| Mudstone | 15 | (0.50, 0.45, 0.38) dark brown-grey | Sedimentary |
| Granite | 16 | (0.68, 0.63, 0.62) pink-grey | Igneous |
| Basalt | 17 | (0.30, 0.30, 0.32) very dark grey | Igneous |
| Andesite | 18 | (0.52, 0.52, 0.50) medium grey | Igneous |
| Marble | 19 | (0.88, 0.87, 0.85) near-white | Metamorphic |
| Slate | 20 | (0.38, 0.40, 0.45) blue-grey | Metamorphic |
| Quartzite | 21 | (0.82, 0.80, 0.78) light grey-pink | Metamorphic |
| Deepstone | 22 | (0.25, 0.25, 0.28) very dark | Deep |

#### Depth Band Structure

| Band | Depth Below Surface | Primary Rocks | Character |
|------|--------------------|---------------|-----------|
| **Soil** | 0-3 blocks | Dirt/Sand/Clay (existing) | Already implemented |
| **Upper Stone** | 4-20 blocks | Sedimentary: Limestone, Sandstone, Mudstone | Easy mining, Tier 1 ores |
| **Mid Stone** | 20-45 blocks | Igneous/Metamorphic: Granite, Basalt, Marble, Slate | Better ores (gold, gems) |
| **Deep Stone** | 45+ blocks | Deepstone + Quartzite pockets | Rare ores, danger |

Band boundaries are offset by ±4 blocks using a 2D noise layer (freq 0.015) to prevent flat artificial lines.

#### Province Noise for Horizontal Variation

A 2D noise (freq 0.002) produces a value [0,1] per column that selects which rock types dominate within each band. Upper Stone might be Limestone-dominant in one region, Sandstone-dominant 200 blocks away. A 3D noise (freq 0.05) creates blobs of secondary rock within the dominant matrix (70/30 split).

#### Integration

- **New file:** `GeologyGenerator.cs` (~150-200 lines)
- **Modified:** `TerrainGenerator.cs` (replace Stone fill with `GetRockType()` call)
- **Modified:** `Block.cs` (add 10 BlockTypes + colors)
- **New noise layers:** 3 (province 2D, boundary offset 2D, rock blobs 3D)
- **Performance cost:** ~0.125ms per chunk (one extra 3D noise per underground block)
- **Impact on meshing/pathfinding/collision:** Zero. All new rocks are solid.

---

## 3. Ore & Resource System

### Design Principles for Colony Sims

1. **Clustered deposits** (not uniform scatter) — colonists haul efficiently from concentrated veins
2. **Tiered by depth** — natural progression as colony develops mining infrastructure
3. **Host-rock restrictions** — ores only in specific rock types, creating natural regional variation
4. **Surface indicators** — ores exposed in cave walls (free from cave carving running after ore placement)
5. **Sufficient early-game nearby** — iron/coal within 30 blocks of surface in most starts

### Ore Types (9 total, 3 tiers)

#### Tier 1 — Essential (Upper Stone, 5-35 blocks deep)

| Ore | ID | Color | Deposit Shape | Size | Host Rocks |
|-----|-----|-------|--------------|------|------------|
| Coal | 23 | (0.20, 0.20, 0.22) very dark | Cluster (3D noise) | 50-150 blocks | Sedimentary |
| Iron | 24 | (0.60, 0.42, 0.35) rusty | Cluster (3D noise) | 30-80 blocks | Sedimentary |
| Copper | 25 | (0.55, 0.72, 0.52) green-tinted | Cluster (3D noise) | 20-60 blocks | Any Upper Stone |
| Tin | 26 | (0.62, 0.60, 0.55) dull grey | Cluster (3D noise) | 10-30 blocks | Sed. + Meta. |

#### Tier 2 — Valuable (Mid Stone, 20-50 blocks deep)

| Ore | ID | Color | Deposit Shape | Size | Host Rocks |
|-----|-----|-------|--------------|------|------------|
| Gold | 48 | (0.85, 0.75, 0.25) bright yellow | Vein (dual-threshold) | 10-25 blocks | Igneous |
| Silver | 49 | (0.78, 0.78, 0.82) silver-white | Vein (dual-threshold) | 15-35 blocks | Metamorphic |
| Gemstone | 50 | (0.45, 0.70, 0.85) blue crystal | Scatter (hash) | 3-8 blocks | Metamorphic |

#### Tier 3 — Rare (Deep Stone, 45+ blocks deep)

| Ore | ID | Color | Deposit Shape | Size | Host Rocks |
|-----|-----|-------|--------------|------|------------|
| Crystal | 51 | (0.80, 0.55, 0.90) purple | Scatter (hash) | 2-6 blocks | Deepstone |
| Mithril | 52 | (0.50, 0.85, 0.90) cyan-blue | Scatter (hash) | 1-2 blocks | Deepstone |

### Deposit Shape Techniques

**Clusters** (Coal, Iron, Copper, Tin): 3D noise field per ore (freq 0.08-0.12). Where noise exceeds threshold AND host rock matches AND depth in range → place ore. Same noise technique as existing caves.

**Veins** (Gold, Silver): Dual-threshold technique (same as spaghetti caves but higher freq, tighter thresholds). Two 3D noises at freq 0.15/0.45, thresholds abs < 0.03. Creates thin sinuous lines 1-2 blocks wide, 10-35 blocks long. Y-squash 0.3 makes veins horizontal.

**Scatter** (Gemstone, Crystal, Mithril): Position hash probability check per block. Rarity: Gem 0.08%, Crystal 0.04%, Mithril 0.01%.

### Generation Order

Ores run BETWEEN geology fill and cave carving. This means:
1. Geology fills rock types
2. Ore replaces some rock with ore blocks
3. Cave carving removes some blocks (including ore) → **naturally exposes ore in cave walls**

**Performance cost:** ~0.7ms per chunk (4 cluster noises + 4 vein noises for Tier 2, all evaluated per underground block).

---

## 4. Underground Biomes & Cavern Layers

### What the Best Games Do

**Dwarf Fortress:** 3 cavern layers (mushroom forests → exotic flora → extreme creatures), each deeper and more dangerous. Below cavern 3 lies the magma sea. This "depth as narrative" creates a compelling progression.

**Minecraft 1.18+:** Underground biomes via the depth parameter in 6D biome selection. Lush Caves (moss, glow berries under humid biomes), Dripstone Caves (stalactites), Deep Dark (sculk, ancient cities under mountains).

### Recommended: 4 Depth-Based Cave Biomes

Decorate existing caves based on depth. No changes to cave generation itself — just add blocks to cave surfaces.

| Biome | Depth | Floor | Walls | Ceiling | Character |
|-------|-------|-------|-------|---------|-----------|
| **Shallow Caves** | 5-20 | Moss, Gravel patches | Moss patches | Roots hanging down | Natural extension of surface |
| **Fungal Caverns** | 20-35 | MyceliumBlock | GlowMushroom clusters | Hanging lichen | Bioluminescent, eerie |
| **Crystal Caverns** | 25-40 | Smooth stone | CrystalCluster on walls | Crystal stalactites | Valuable, rewarding |
| **Magma Depths** | 45+ | MagmaRock | Obsidian formations | Basalt drip | Dangerous, industrial |

#### New BlockTypes (9 types)

| Type | ID | Color | Notes |
|------|-----|-------|-------|
| Moss | 27 | (0.25, 0.50, 0.20) dark green | Replaces solid block on cave floor/wall |
| Roots | 28 | (0.45, 0.30, 0.15) brown | Placed in air below solid ceiling |
| MyceliumBlock | 29 | (0.50, 0.42, 0.55) purple-grey | Underground ground cover |
| GlowMushroom | 30 | (0.30, 0.80, 0.65) cyan-green | Bright color suggests luminescence |
| Stalactite | 31 | (0.60, 0.58, 0.55) stone-colored | Hangs from ceiling, solid (blocks pathfinding) |
| CrystalCluster | 44 | (0.75, 0.60, 0.90) pale purple | Wall decoration |
| MagmaRock | 45 | (0.35, 0.15, 0.10) dark red-brown | Deep cave floor |
| Obsidian | 46 | (0.12, 0.10, 0.15) near-black | Deep cave wall feature |
| Lava | 47 | (0.95, 0.45, 0.10, 0.9) bright orange | Liquid like Water, pools on deep cave floors |

#### The "Depth as Narrative" Progression

- **Depth 0-5 (Soil):** Safe, familiar. Dirt and roots.
- **Depth 5-20 (Shallow Caves):** First caves via entrances. Moss, roots, coal/iron in walls. Safe mining.
- **Depth 20-35 (Fungal Caverns):** Larger caves (cheese caverns appear). Purple mycelium, glowing mushrooms. Gold/silver veins. Eerie beauty rewards exploration.
- **Depth 35-45 (Crystal Caverns):** Grand cheese caverns. Crystal clusters glitter. Gemstone ore. The "wow" discovery moment.
- **Depth 45+ (Magma Depths):** Dark basalt, obsidian, lava pools. Mithril ore. Most valuable resources in the most dangerous zone. Lava proximity eventually enables fuel-free smelting.

#### Lava as a New Liquid

Lava behaves like Water technically (`IsLiquid=true`, `IsSolid=false`). Requires a third mesh surface in the greedy mesher with its own shader (bright/emissive coloring). Generated on deep cave floors via 2D noise check. Pathfinding excludes it automatically (same as Water).

#### Integration

- **New file:** `CaveDecorator.cs` (~150-250 lines)
- Runs after cave carving: scans Air blocks adjacent to solid blocks, applies depth-based decoration
- Decoration uses hash-based probability (no new noise needed) — e.g., 30% floor moss in Shallow Caves
- **Lava:** Requires `ChunkMeshGenerator.cs` modification (third surface pass, ~40 lines) + `chunk_lava.gdshader`

---

## 5. Expanded Surface Biome System

### Recommended: 14 Biomes (from 6)

Add 1 new noise axis (**Erosion**, freq 0.004) for terrain ruggedness. Replace hard-threshold `ClassifyBiome()` with 4D nearest-point selection.

| # | Biome | Key Parameters | Surface | Trees | Distinct Feature |
|---|-------|---------------|---------|-------|------------------|
| 0 | Grassland | mid T, mid M | Grass | Oak 5% | Gentle rolling hills |
| 1 | Forest | mid T, high M | Grass | Oak 30% | Dense canopy, hilly |
| 2 | Desert | hot, dry | RedSand | None | Flat dunes |
| 3 | Tundra | cold, mid M | Snow | Spruce 1% | Frozen flat expanses |
| 4 | Swamp | hot, wet | Grass | Oak 15% | Low, waterlogged |
| 5 | Mountains | high C, low E | Stone | Spruce 2% | Dramatic peaks |
| 6 | **Taiga** | cold, high M | Snow | Spruce 20% | Snowy dense forest |
| 7 | **Savanna** | hot, low-mid M | DryGrass | Acacia 3% | Golden grass, flat-top trees |
| 8 | **Badlands** | hot, dry, mid C, low E | RedSand | None | Layered mesa terrain |
| 9 | **Birch Forest** | mid-cool, mid-high M | Grass | Birch 25% | Light-colored trees |
| 10 | **Jungle** | hot, wet, low E | Grass | Jungle 40% | Dense tall trees, lush |
| 11 | **Plateau** | mid-high C, high E | Grass | Oak 4% | Flat-topped elevated terrain |
| 12 | **Frozen Peaks** | cold, high C, low E | Snow | None | Jagged ice spires |
| 13 | **Mushroom Fields** | mid T, high M, high E | Mycelium | Mushroom 0% (special) | Giant mushroom trees |

### Tree Variety (6 styles)

| Style | Used By | Shape | Wood/Leaf BlockTypes |
|-------|---------|-------|---------------------|
| Oak | Grassland, Forest, Swamp, Plateau | Current shape (4-6 trunk, diamond+cross canopy) | Wood/Leaves (existing) |
| Spruce | Taiga, Tundra, Mountains | Tall narrow (6-8 trunk, conical canopy) | SpruceWood/SpruceLeaves |
| Birch | Birch Forest | Thin white trunk (5-7, narrow canopy) | BirchWood/BirchLeaves |
| Acacia | Savanna | Short trunk (3-4), wide flat canopy offset to one side | AcaciaWood/AcaciaLeaves |
| Jungle | Jungle | Very tall (8-12 trunk), large canopy | JungleWood/JungleLeaves |
| Mushroom | Mushroom Fields | Fat stem (2-3 wide), flat red cap | MushroomStem/MushroomCap |

New surface BlockTypes: DryGrass, Mycelium + 10 tree types = **12 new types**.

### Selection System Overhaul

Replace `ClassifyBiome()` with 4D nearest-point using the existing Gaussian distance formula extended to (T, M, C, E) space. Per-biome axis weight multipliers handle "any" values (e.g., Mountains: TWeight=0, MWeight=0, selected purely by high C + low E).

Surface block transitions at biome boundaries use probabilistic dithering: if biome A has weight 0.7 and biome B has weight 0.3, use position hash to select A's block 70% of the time — creates natural-looking dithered boundaries.

---

## 6. Terrain Shape Variety

### Recommendation: Stay with Heightmap + Selective 3D Modifiers

Full 3D density functions (Minecraft 1.18 approach) would require rewriting the entire terrain pipeline — 16x more noise evaluations, complex surface detection, and enormous implementation effort. The heightmap approach covers 90% of needs when combined with targeted post-processing.

### Specific Terrain Features

**Cliff Faces (Mountains, Badlands, Plateau):** Compute heightmap gradient between adjacent columns. Where gradient > threshold, force vertical block columns instead of slopes. Per-biome threshold: Mountains=3, Badlands=2, Grassland=8 (never triggers). Cost: 4 extra `ComputeHeight()` per column, only for cliff-capable biomes.

**Badlands Mesa Terracing:** Quantize heightmap to multiples of 4-5 blocks → flat-topped layers separated by cliff walls. Add alternating Clay/RedSand/Sand bands at specific Y-levels (`worldY % 6`) for the iconic striped appearance.

**Plateau Flat Tops:** Clamp elevation noise contribution to ±0.1 at high base height (+6). Edges naturally create cliffs where flat top meets lower terrain.

**Frozen Peaks Spires:** High-frequency vertical noise (freq 0.08) adds ±8 blocks of jagged variation. Creates irregular jagged skyline distinct from smoother Mountains.

**Optional Overhangs:** 3D noise carving (freq 0.08) near cliff faces, only within 3-5 blocks of cliff top. Creates shallow cave-like indentations and overhangs. Nice but not essential.

### Why NOT Full Density Functions

1. Complete pipeline rewrite — every block needs 3D evaluation (16x more noise calls)
2. Surface detection becomes expensive — must scan vertically to find air/solid transition
3. Heightmap + modifiers covers cliffs, mesas, plateaus, overhangs with much less complexity
4. Future migration path exists: heightmap becomes one input to density function if needed later

---

## 7. Structures & Points of Interest

### Design for a Colony Sim

Structures serve two purposes: (1) early-game resources/shortcuts, (2) world history. They don't need Minecraft village complexity — simple ruins and resource caches are sufficient.

### Recommended Structures

**Surface (5 types):** Ruined Cabin (5×4×5, Forest/Grassland), Abandoned Mine Entrance (3×3×8, Mountains/Badlands — tunnel into hillside), Stone Circle (7×2×7, Grassland/Tundra), Desert Well (3×4×3, Desert), Watchtower Ruin (4×8×4, Mountains/Plateau).

**Underground (3 types):** Abandoned Mineshaft (corridor network, depth 10-30), Buried Vault (5×4×5 room, depth 20-40), Ore Motherload (8×6×8 dense ore cluster, depth 30+).

### Placement System

Grid-based (128-block cells, same pattern as trees). Per-cell hash determines: structure spawns? → which type? → exact position. Terrain adaptation: surface structures check height variance (skip if >4 blocks), extend foundation pillars downward. Underground structures simply carve their own space in solid rock.

New BlockTypes: StoneBrick (53), CrackedBrick (54), MossyBrick (55), WoodPlanks (56). Decay pass randomly damages 20-30% of bricks. Moss pass adds MossyBrick adjacent to air.

---

## 8. Implementation Phases (Ordered by Impact/Effort)

### Phase A: Underground Foundation (Geology + Tier 1 Ores)
**~500-600 lines | Depends on: Nothing | HIGHEST PRIORITY**

Build `GeologyGenerator.cs` and `OreGenerator.cs`. Replace uniform Stone with layered geology. Add Coal, Iron, Copper, Tin ores. 14 new BlockTypes.

**Player impact:** Underground transforms from monotone grey to visually rich layered geology with discoverable ore deposits. Y-level slicing shows colorful geological cross-sections. Mining has a purpose.

### Phase B: Cave Life (Underground Decoration)
**~200-300 lines | Depends on: Phase A**

Build `CaveDecorator.cs`. Moss + Roots in shallow caves, Mycelium + GlowMushroom in deep caves, Stalactites. 5 new BlockTypes.

**Player impact:** Caves at different depths look different. Exploring caves feels rewarding.

### Phase C: Surface Variety (Expanded Biomes + Trees)
**~500-700 lines | Depends on: Nothing (parallel with A/B)**

Expand to 14 biomes, add Erosion noise axis, 4D selection, 6 tree styles. 12 new BlockTypes.

**Player impact:** World has dramatically more visual variety. Each biome is distinct.

### Phase D: Terrain Drama (Cliffs + Terracing)
**~200-300 lines | Depends on: Phase C**

Build `TerrainModifiers.cs`. Cliff faces, mesa terracing, Badlands clay banding, Frozen Peaks spires.

**Player impact:** Mountains have dramatic cliffs. Badlands have iconic striped mesas.

### Phase E: Deep Underground (Lava + Crystal/Magma Caves + Tier 2-3 Ores)
**~300-400 lines | Depends on: Phase A + B**

Lava liquid type + shader, Crystal/Magma cave decorations, Gold/Silver/Gem/Crystal/Mithril ores. 10 new BlockTypes.

**Player impact:** Deep underground has complete depth progression: safe → interesting → valuable → dangerous.

### Phase F: World History (Structures)
**~400-500 lines | Depends on: Phase A + C**

Build `StructureGenerator.cs`. Grid-based placement, templates, decay pass. 4-5 surface structures, 1-2 underground structures. 4 new BlockTypes.

**Player impact:** World feels inhabited, with ruins hinting at history.

### Dependency Graph
```
Phase A (Geology + Ores) ──┬──> Phase B (Cave Life) ──┬──> Phase E (Deep Underground)
                           │                          │
Phase C (Biomes + Trees) ──┼──> Phase D (Terrain) ────┘
                           │
                           └──> Phase F (Structures)
```

### BlockType Budget

57 of 256 values used across all phases (22%). 199 remaining for future expansion.

### New Source Files

| File | Phase | Lines |
|------|-------|-------|
| `scripts/world/GeologyGenerator.cs` | A | 150-200 |
| `scripts/world/OreGenerator.cs` | A, E | 200-300 |
| `scripts/world/CaveDecorator.cs` | B, E | 150-250 |
| `scripts/world/TerrainModifiers.cs` | D | 150-200 |
| `scripts/world/StructureGenerator.cs` | F | 300-400 |
| `shaders/chunk_lava.gdshader` | E | 25 |

### Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Performance from extra noise layers | ~0.7ms added per chunk (total ~2.7ms vs current ~2ms). Well within threaded pipeline capacity. |
| Greedy meshing less effective underground | Only underground chunks affected. Underground faces mostly culled (solid-solid). |
| Biome selection instability at 14 boundaries | Gaussian weighting already smooths. Tune BlendSharpness 3-6. |
| Thread safety in new generators | Follow existing pattern: generators own noise instances, write only to local block arrays, no shared mutable state. |

---

## Appendix: New Noise Layer Registry

| Noise | Freq | Dim | Phase | Purpose |
|-------|------|-----|-------|---------|
| Rock Province | 0.002 | 2D | A | Horizontal geological variation |
| Band Boundary | 0.015 | 2D | A | Undulating depth band transitions |
| Rock Blobs | 0.05 | 3D | A | Secondary rock type patches |
| Coal Cluster | 0.10 | 3D | A | Ore deposit shape |
| Iron Cluster | 0.10 | 3D | A | Ore deposit shape |
| Copper Cluster | 0.09 | 3D | A | Ore deposit shape |
| Tin Cluster | 0.11 | 3D | A | Ore deposit shape |
| Erosion | 0.004 | 2D | C | Terrain ruggedness axis |
| Overhang Carve | 0.08 | 3D | D | Cliff face pockets |
| Gold Vein 1/2 | 0.15/0.45 | 3D | E | Dual-threshold vein shape |
| Silver Vein 1/2 | 0.15/0.45 | 3D | E | Dual-threshold vein shape |
| Lava Pool | 0.03 | 2D | E | Deep cave floor pools |

Total: 14 new noise instances (current: 8, final: 22).

---

**Recommended starting point: Phase A.** Highest impact-to-effort ratio, transforms the biggest weakness, establishes the foundation for Phases B, E, and F.
