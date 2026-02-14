# colonysim-3d — Project Bible

> **Read this entire document before writing any code.** It contains hard-won lessons and architectural decisions that will save you from known dead ends.

---

## 1. Game Vision

**Concept:** Minecraft-style voxel world + Rimworld/Dwarf Fortress colony management.

**Core loop:**
1. Player views the world from an RTS/management camera (top-down or isometric)
2. Player designates tasks: mine here, build there, haul this
3. Colonists (AI-controlled pawns) autonomously execute tasks via pathfinding
4. Manage survival, resources, and colony growth

**Key constraints:**
- The player does NOT directly control a character. This is NOT a first-person game.
- Colonists are autonomous agents. They receive task assignments and figure out pathing/execution.
- The world is fully destructible/constructible — any block can be mined or placed.

---

## 2. Technical Stack

| Component | Choice | Notes |
|-----------|--------|-------|
| Engine | Godot 4.6 (Mono/.NET) | C# scripting via .NET |
| Language | C# | All game logic in C# |
| Physics | Jolt Physics | Set in project.godot |
| Renderer | Forward Plus | Default Godot 4 renderer |
| Graphics API | D3D12 | Windows target |
| Assembly | `colonysim_3D` | .csproj assembly name |

---

## 3. Architecture Decisions

These decisions were reached through research and prototyping. They are **final** — do not revisit unless you have a concrete reason with evidence.

### 3.1 Voxel/Chunk System: Procedural ArrayMesh (NOT GridMap)

**Use procedural ArrayMesh per chunk.** Do NOT use Godot's GridMap.

Why GridMap fails:
- GridMap is designed for static level design (hand-placed tiles in editor)
- It regenerates internal octants on every cell change — extremely expensive for dynamic worlds
- No control over face culling, mesh optimization, or collision generation

Why ArrayMesh works:
- Full control over vertex/normal/index buffers
- Can cull internal faces (only render block faces adjacent to air)
- Chunk isolation: modifying one block only regenerates that chunk's mesh
- Greedy meshing merges adjacent same-type faces into larger rectangles (implemented)
- Can extend to texture atlases, LOD later

**Chunk size: 16x16x16 blocks.** This is a standard choice that balances mesh rebuild cost vs. chunk count.

### 3.2 Collision: Distance-Based ConcavePolygonShape3D

Chunks use `StaticBody3D` with `ConcavePolygonShape3D` for collision, but **only within a short radius of the camera** (`CollisionRadius = 4` chunks = 64 blocks). Distant chunks need rendering but NOT collision — the colonist and raycasts are always near the camera.

**How it works:**
- `Chunk.Initialize(coord, withCollision)` accepts a flag. When `withCollision=false`, no `StaticBody3D` or `CollisionShape3D` nodes are created (saves 2 scene nodes per chunk).
- `Chunk.EnableCollision()` / `DisableCollision()` add/remove collision nodes at runtime as chunks move in/out of collision range.
- Collision mesh faces are always cached in `_lastCollisionFaces` when mesh data is applied, so `EnableCollision()` can apply them instantly without re-meshing.
- `World.UpdateLoadedChunks()` includes a collision update pass that iterates loaded chunks and enables/disables collision based on XZ distance from the camera chunk.

**Impact at render distance 20:** 13,448 total chunks, but only ~648 have collision (81 horizontal × 8 Y layers within radius 4). This removes ~25,600 scene nodes and reduces physics broadphase processing by ~95%.

**Future consideration:** When colonists operate outside camera range (autonomous jobs), they may need collision in their area. Options include abstract simulation (no physics) or local collision zones around each colonist. This is deferred to the multi-colonist phase.

The collision mesh is built from the same vertex data as the render mesh, expanded from indexed triangles to a flat vertex list (ConcavePolygonShape3D requires sequential vertex triples, not indexed buffers). Regenerate collision whenever the render mesh changes.

### 3.3 Navigation: Voxel Grid A* (NOT NavigationServer3D)

**Implement A* pathfinding directly on the block grid.** Do NOT use Godot's NavigationServer3D, NavigationMesh, NavigationRegion3D, NavigationLink3D, or NavigationAgent3D.

Why NavigationServer3D fails for voxel worlds:
- **Wall clipping:** NavMesh generates paths along polygon edges. In a block world, this means paths run flush against walls. A colonist following such a path clips into the wall because there's almost no lateral force to push it away. This is a fundamental geometric mismatch, not a tuning problem.
- **Height transitions:** NavMesh cannot natively handle 1-block step-ups. NavigationLink3D exists but requires signal-based jump timing that is unreliable — the `LinkReached` signal fires at unpredictable moments during `MoveAndSlide()`.
- **Sync issues:** After modifying a NavigationRegion3D's mesh, the NavigationServer doesn't update until the next physics frame. If you query a path in the same frame you updated the mesh, you get stale results. This creates race conditions on every block change.
- **Cross-chunk links:** NavigationLink3D works within a single chunk, but linking across chunk boundaries requires World-level orchestration that adds enormous complexity.
- **Overall:** NavigationServer3D was designed for smooth, pre-baked terrain — not dynamic voxel grids.

Why voxel grid A* works:
- Your world IS a grid. Every walkable surface is at a known integer coordinate. A* on this grid is the natural fit.
- Cross-chunk pathfinding is trivial: the A* algorithm queries `World.GetBlock()` which handles chunk lookups transparently.
- Paths route through **block centers** (X+0.5, Z+0.5), guaranteeing wall clearance.
- Height transitions are just neighbor connections with different costs — no special link nodes needed.
- No sync issues — pathfinding operates on block data directly, no intermediate representation.
- Implementation is ~200 lines of straightforward C#.

**Current A* design:**
- 8-connected neighbors (diagonals toggleable at runtime via F2, default ON)
- Cardinal neighbor types: flat walk (same Y), step up (Y+1), step down (Y-1)
- Diagonal neighbor types: flat and step-down only (no diagonal step-up — jump physics don't support it)
- Diagonal corner-cutting prevention: both adjacent cardinal neighbors must be passable
- 2-high clearance checks at every destination (colonists are ~2 blocks tall)
- Move costs: flat cardinal=1.0, step down=1.2, step up=2.0, flat diagonal=1.414, diagonal step-down=1.7
- Heuristic: octile distance (when diagonals enabled) or Manhattan (when disabled), Y weighted ~1.5x
- Hard limit on nodes explored (~10,000) to prevent runaway searches on large worlds
- .NET's `PriorityQueue<TElement, TPriority>` is available and works fine

### 3.4 Colonist Movement: State Machine on CharacterBody3D

Use `CharacterBody3D` with `MoveAndSlide()` and a simple state machine:

**States:** `Idle → Walking → JumpingUp → Falling`
- **Walking:** Follow waypoint list. Move horizontally toward current waypoint's block center. Advance when close enough. If next waypoint is higher → transition to JumpingUp.
- **JumpingUp:** Apply upward velocity + continue horizontal movement. Transition back to Walking on landing. Use a grace timer (~0.15s) after jumping to avoid false `IsOnFloor()` detection.
- **Falling:** Gravity descent (step-down, or pushed off edge). Resume Walking on landing.

Add **stuck detection**: if the colonist makes no progress for ~2 seconds, clear the path and go idle (or repath).

**Deferred physics:** `_physicsReady` flag (default `false`) gates all `_PhysicsProcess` logic. `Main` calls `EnablePhysics()` after spawn-area chunks have loaded. `SetSpawnPosition()` updates the void-safety teleport target after cave-safe height correction. See section 3.12.

**Capsule dimensions:** radius=0.3, height=1.6. This gives 0.2 units of wall clearance when the colonist is at a block center (0.5 - 0.3 = 0.2).

**Path visualization:** Red line + cross markers drawn via ImmediateMesh, toggled with F1. Added to scene root (world space, not colonist-local) with NoDepthTest for visibility.

### 3.5 Chunk Streaming & Threaded Generation

Chunks load/unload dynamically based on camera position with a **four-phase pipeline** inside `ProcessLoadQueue()`:

**Phase 1 — Apply terrain results (budgeted):** Background threads post completed `ChunkGenResult` structs to a `ConcurrentQueue`. Phase 1 dequeues these, creates Godot scene nodes (`Chunk` + `MeshInstance3D`, and conditionally `StaticBody3D` + `CollisionShape3D` only if within `CollisionRadius`), and queues the chunk + its 6 neighbors for mesh generation. Budgeted to `MaxApplyPerFrame` (24) to prevent frame spikes when many results arrive at once. Overflow results are re-enqueued for the next frame.

**Phase 2 — Dispatch background mesh generation:** Chunks from the mesh queue are dispatched to the .NET thread pool for greedy meshing. Before dispatch, the main thread snapshots neighbor boundary data via `MakeSnapshotNeighborCallback()` — 6 flat arrays of 16×16 blocks, one per face-adjacent neighbor. The returned callback reads from these snapshots, making it safe to call from any thread. Up to `MaxConcurrentMeshes` (16) mesh jobs run concurrently. `GenerateMeshData()` (~13ms per chunk) runs entirely off the main thread.

**Phase 2b — Apply mesh results:** Completed `MeshGenResult` structs are dequeued from `_meshResults` (`ConcurrentQueue`). `Chunk.ApplyMeshData()` creates Godot objects (`BuildArrayMesh()` + optionally `ConcavePolygonShape3D` if collision enabled) on the main thread — this is fast (~1ms per chunk). Up to `MaxMeshApplyPerFrame` (24) results applied per frame.

**Phase 3 — Dispatch terrain generation:** New chunks are dequeued from the load queue. Chunk data cache (all previously-loaded chunks) and terrain prefetch cache are checked first (instant restore). Otherwise, `Task.Run()` dispatches `TerrainGenerator.GenerateChunkBlocks()` to the .NET thread pool. Up to `MaxConcurrentGens` (8, or 48 during large queue bursts) chunks generate concurrently.

**Terrain prefetch ring:** `DispatchPrefetch()` pre-generates terrain for chunks within `renderRadius + PrefetchRingWidth` (3) but beyond render distance. Results are stored in a `ConcurrentDictionary<Vector3I, BlockType[,,]>` terrain cache. When the camera pans and these chunks enter render distance, their terrain data is instantly available — no thread pool wait. Up to 8 prefetches dispatched per frame. Stale cache entries and in-flight prefetches are evicted when the camera moves away.

**Empty chunk optimization:** Chunks that are 100% air (typically upper Y layers above terrain) are tracked in a lightweight `HashSet<Vector3I>` instead of creating Godot scene nodes. This eliminates ~50% of node creation overhead since half the Y layers are usually empty sky. Empty status is detected both from background terrain gen results and prefetch cache checks.

**Other details:**
- `Main._Process()` converts camera position to chunk XZ coordinate each frame, calls `World.UpdateLoadedChunks()`
- Chunks unload when their XZ distance from camera exceeds `radius + 2` (hysteresis prevents thrashing at boundaries)
- In-flight background generation is cancelled for chunks that move out of range (`_unloadedWhileGenerating` set)
- `QueueNeighborMeshes()` helper deduplicates the pattern of queuing 6 face-adjacent neighbors for mesh regeneration
- `QueueMeshGeneration()` skips chunks already in-flight (`_meshing` set) to prevent duplicate work
- Editor mode still uses synchronous `LoadChunkArea()` for immediate preview
- Block modification (`RegenerateChunkMesh()`) still uses synchronous `MakeNeighborCallback()` for immediate visual feedback

**Background mesh generation with neighbor snapshots:** The earlier approach of main-thread meshing (see lesson 5.4) was a bottleneck — each chunk took ~13ms, and with 800+ in the mesh queue, initial loading took ~13 seconds at 1 chunk/frame. The solution was `MakeSnapshotNeighborCallback()`: before dispatching, the main thread copies the 16×16 boundary face of each neighbor into 6 flat arrays. This works because by the time the mesh queue processes chunks, their neighbors are already loaded (terrain generation completes first). The snapshot approach avoids the original artifact problem documented in lesson 5.4 because it only dispatches when neighbor data actually exists.

### 3.6 Chunk Data Caching

ALL generated chunks are cached in memory on unload (toggleable via `_cacheAllChunks`, default true). This makes revisiting previously-explored areas instant — no re-generation from noise.

**How it works:**
- `World._chunkDataCache` (`Dictionary<Vector3I, BlockType[,,]>`) stores block data for every unloaded chunk
- Dirty chunks (player-modified) store a **copy** via `GetBlockData()` — protects against mutation
- Clean chunks store a **direct reference** via `GetBlockDataRef()` — zero allocation, safe because the chunk node is freed
- On reload, the cache is checked first (Priority 1 in Phase 3) — cached data restored instantly
- Cache eviction at **3x render radius** prevents unbounded memory growth
- Empty chunk records (`_emptyChunks`) also evict at 3x render radius

**Fallback:** When `_cacheAllChunks` is false, only dirty chunks are cached (original behavior). Clean chunks regenerate deterministically from the seed.

This is an **in-memory cache only** — all data is lost when the game restarts. Disk persistence is a future feature.

### 3.7 Terrain Generation

Multi-layer noise terrain using 7 `FastNoiseLite` layers:
- **Continentalness** (freq 0.003): broad terrain category — lowlands, midlands, highlands
- **Elevation** (freq 0.01): primary height variation, amplitude scaled by continentalness
- **Detail** (freq 0.06): fine surface roughness, suppressed in flat areas
- **River** (freq 0.005): rivers form where `abs(noise) ≈ 0`, only in non-mountainous terrain above water level. River depth is capped at `RiverDepth=12` blocks below surrounding terrain
- **Temperature** (freq 0.0008) and **Moisture** (freq 0.001): drive biome classification
- **River Width** (freq 0.018): modulates river width per-column from 0.5× to 2.0× base width, creating narrow creek sections and wider river/pool sections

Height range: 0-90 across multiple Y chunk layers (default 8 layers = 128 blocks tall). Water level: 45. Tall terrain (base 50-70, amplitude 5-20) creates 45-65 blocks of underground stone for deep cave networks. Snow caps on mountains at height >= 82.

**Variable-width rivers:** River width is modulated per-column by a separate noise layer. `RiverWidth` (0.04) and `RiverBankWidth` (0.16) are multiplied by `RiverWidthMod` (0.5–2.0), creating narrow creeks (half-width), normal rivers, and wider pools/rivers (double-width) along the same channel. The width noise (freq 0.018) changes roughly every ~55 blocks, so 2-3 width transitions are visible per stretch.

**Minecraft-style water level:** All water (ocean and rivers) sits at the global `WaterLevel` (45). Rivers that carve below sea level naturally fill with water. Rivers above sea level become dry valleys — this is the same approach Minecraft uses and avoids water wall artifacts at boundaries. No per-section local water levels.

**Biome system:** 6 biomes (Grassland, Forest, Desert, Tundra, Swamp, Mountains) selected by temperature, moisture, and continentalness. Each biome has distinct surface/subsurface blocks, height offsets, amplitude scales, and detail scales defined in `BiomeTable`. Biome boundaries use weighted blending of the 4 nearest biome heights (by Euclidean distance in temp/moisture space) to avoid hard terrain seams. See `Biome.cs` for definitions and `TerrainGenerator.cs` for blending logic.

Block types by position:
- Surface: determined by biome (`BiomeData.SurfaceBlock` — Grass, RedSand, Snow, Stone), with sand at water edges
- Subsurface (3 layers): determined by biome (`BiomeData.SubSurfaceBlock`), sand at water edges
- Underwater: determined by biome (`BiomeData.UnderwaterSurface`)
- Deep: Geological rock layers (see section 3.14)
- Ore deposits embedded in appropriate host rocks (see section 3.15)
- Water fills from terrain up to global water level

### 3.8 Greedy Meshing

The chunk mesh generator uses **greedy meshing** (Mikola Lysenko algorithm) to merge adjacent same-type block faces into larger rectangles, dramatically reducing triangle count.

**How it works:** For each of 6 face directions, process 16 2D slices perpendicular to that direction. For each slice, build a 16×16 mask of which cells need a face (same culling rules as before). Then greedily merge: scan row-by-row, expand each unvisited cell rightward (same type), then downward (all cells match), emit one quad per merged rectangle.

**Key implementation details:**
- `FaceConfig` struct maps each face direction to its 2D slice coordinate system (depth/U/V axes)
- `[ThreadStatic]` scratch buffers (`_sliceMask`, `_sliceVisited`) for thread-safe parallel generation
- `GenerateMeshData()` returns thread-safe `ChunkMeshData` struct (raw arrays), `BuildArrayMesh()` creates Godot objects on main thread
- Collision merges ALL solid types together (better merging than render path since no visual distinction)
- CW winding order preserved: verified for all 6 faces with w=1, h=1 matching original vertex positions

**Single-surface optimization:** All opaque/solid block types are merged into a **single mesh surface** per chunk. Only water gets a separate surface (different shader for alpha blending). This produces max 2 surfaces per chunk instead of one per block type (~6-8 surfaces). At render distance 20, this reduces draw calls from ~80,000 to ~13,000. The greedy merge respects block type boundaries (won't merge Grass into Stone), so vertex colors remain correct per block type. Two shared `ShaderMaterial` instances (opaque + water) are reused across all chunks — no per-chunk material allocations.

**Performance:** Flat 16×16 surfaces reduce from 256 quads (512 triangles) to 1 quad (2 triangles) — up to 256× reduction. Typical terrain sees 5-10× overall triangle reduction. The single-surface approach also speeds up mesh generation: 2 mask passes (opaque + water + collision) instead of 12+ (one per block type + collision).

### 3.9 Tree Generation

Deterministic grid-based tree placement integrated into `TerrainGenerator.GenerateChunkBlocks()`:

**Grid spacing:** The world is divided into 4×4 block cells (`TreeGridSize`). Each cell can spawn at most one tree at a deterministic position within it, guaranteeing ~4-block minimum spacing. `PositionHash(cellX, cellZ, seed)` provides a fast integer hash for both probability checks and position offsets.

**Per-biome density:** Each `BiomeData` has a `TreeDensity` threshold (0.0–1.0). Forest=0.30 (dense), Swamp=0.15, Grassland=0.05 (scattered), Mountains=0.02, Tundra=0.01, Desert=0.00 (none). Trees below water level are skipped.

**Tree shape (~34 leaves + ~5 trunk):**
- Trunk: 4-6 blocks of Wood above surface (height varies deterministically per tree)
- Lower canopy: 2 layers of Leaves at radius 2 (diamond shape) around trunk top
- Upper canopy: 2 layers of Leaves at radius 1 (cross shape) above trunk top

**Cross-chunk correctness:** Trees near chunk boundaries have canopy extending into neighboring chunks. `PlaceTreesInChunk()` checks grid cells within `chunkBounds ± TreeInfluenceRadius` (2 blocks), so both chunks independently place the same tree's overlapping blocks within their own bounds. No inter-chunk coordination needed — pure determinism.

**Block types:** `Wood = 11`, `Leaves = 12`. Both are solid (`IsSolid()` returns true). Colors: Wood = brown `(0.55, 0.35, 0.18)`, Leaves = green `(0.20, 0.55, 0.15)`. Existing meshing, collision, and pathfinding handle them automatically.

### 3.10 Cave Generation

Three-layer Minecraft-inspired cave system combined with OR logic (any system can independently carve):

**Spaghetti tunnels:** Two independent 1-octave (`FractalType.None`) 3D simplex noise fields with 1:6 frequency ratio (0.01 and 0.06). A block is carved where `abs(noise1) < 0.10 AND abs(noise2) < 0.08`. Critical: 1-octave noise produces smooth continuous isosurfaces — FBM fragments them into disconnected pockets. Asymmetric thresholds. Y-squash (0.5) makes tunnels prefer horizontal. Carved volume ≈ 3.2%.

**Cheese caverns:** Ridged-noise field (freq 0.018, 1 ridged octave, seed+900) creates larger open chambers very deep underground (25+ blocks below surface). Depth-scaled threshold: Lerp(0.85, 0.62) from 25→50 blocks depth — deeper = bigger caverns. Heavy Y-squash (0.35) makes caverns wide and flat.

**Noodle tunnels:** Very thin connecting passages (freq 0.03/0.09, thresholds 0.05/0.05) only 15+ blocks below surface. Connect the spaghetti and cavern networks.

**Cave entrances:** 2D noise (freq 0.008, threshold 0.52) punches clearly visible holes through surface protection, creating openings players can spot from above. Only above WaterLevel+3.

**Safety rules:**
- **Surface protection:** No caves within `CaveMinDepth=15` blocks of surface — colonists must mine down to reach caves. Caves fade in over `CaveFadeRange=10` blocks below that.
- **Floor protection:** No carving at `worldY <= 2` (bedrock).
- **No water protection:** Caves extend below water level into deep stone for impressive deep networks.

**Integration:** `CaveGenerator.CarveCaves()` is called in `TerrainGenerator.GenerateChunkBlocks()` after terrain fill but before tree placement. Surface heights are cached per-column in a `[ThreadStatic]` array and passed to the cave generator. Caves are fully deterministic — same seed = same caves. No cross-chunk coordination needed.

**Impact:** Pathfinding, meshing, collision, and tree generation all work automatically with caves — no changes needed to those systems.

### 3.11 Y-Level Camera Slicing

Dwarf Fortress-style Y-level slicing lets the player see underground by hiding everything above a configurable Y level.

**Shader-based approach:** Custom `.gdshader` files replace `StandardMaterial3D` on all chunk meshes. The fragment shader reads global uniforms `slice_y_level` and `slice_enabled` and discards fragments above the slice. This requires **zero re-meshing** — slice level changes are instant.

**Visualization:** Two complementary techniques make caves visible in sliced view:
1. **Dark background:** When slicing is active, `CameraController` changes the `WorldEnvironment` background from sky blue to dark gray `(0.15, 0.15, 0.18)`. Cave voids appear as dark holes contrasting against lit stone.
2. **Cross-section tint:** The opaque shader darkens upward-facing surfaces (NORMAL.y > 0.5) within 1 block below the slice level (multiplied by 0.55). This creates a "floor plan" look on the cut surface without affecting side walls. The previous approach of tinting ALL faces near the slice (including walls) created ugly dark blobs and was reverted.

**Two shaders:** `chunk_opaque.gdshader` for solid blocks (with cross-section tint), `chunk_water.gdshader` for water (with alpha blending, no tint). Global uniforms are declared in `project.godot` under `[shader_globals]`.

**SliceState:** Static class (`scripts/camera/SliceState.cs`) with `Enabled` and `YLevel` properties. Updated by `CameraController`, read by `Colonist` (visibility toggle) and `BlockInteraction` (raycast pierce-through).

**Raycast pierce-through:** When slicing is active, raycasts that hit blocks above the slice level fire continuation rays past the hit point until a valid (below-slice) hit is found. Loop limit of 10 prevents infinite loops.

**Controls:** Page Down = lower slice (1 block per press, first press starts at Y=40), Page Up = raise slice (1 block per press), Home = disable slicing. Slice keys use `Input.IsKeyPressed` polling with debounce in `_Process` (not `_UnhandledInput`) to avoid event routing issues.

### 3.12 Colonist Spawn Safety

The colonist must not receive physics (gravity) until the chunks around it have loaded and have collision shapes. Without this, the colonist falls through void in frame 0-1 before any collision exists.

**Deferred physics pattern:**
- `Colonist._physicsReady` starts `false`. `_PhysicsProcess()` returns early until enabled.
- `Main.CheckSpawnChunksReady()` runs each frame in `_Process()`, checking `World.IsChunkReady(spawnChunk)` — which returns true when the chunk exists in `_chunks` and has had `ApplyMeshData()` called (`Chunk.HasMesh`).
- Once the spawn chunk and the chunk below it are ready, `Main` corrects the colonist's height and calls `Colonist.EnablePhysics()`.
- `Colonist.SetSpawnPosition()` updates the void-safety teleport target after height correction.

**Cave-safe height correction:** `World.GetSurfaceHeight()` uses `TerrainGenerator.GetHeight()` which evaluates noise — it doesn't account for caves carved below the surface. After chunks load, `Main.FindActualSurface()` scans downward through real `World.GetBlock()` data to find the highest solid block. This is the colonist's actual spawn Y.

**Spawn origin:** The colonist spawns near world origin `(0, 0)` and `FindDryLandNear()` searches outward for dry land. The old formula `ChunkRenderDistance * 16 + 8` was an arbitrary offset from the pre-streaming era that could land in ocean depending on seed. `FindDryLandNear()` also avoids river channels (via `World.IsRiverAt()`) and prefers elevated positions (`WaterLevel + 5`) to avoid spawning in valleys.

### 3.14 Geological Layer System

Underground rock types vary by **depth below surface** and **horizontal geological province**, replacing the previous uniform Stone fill. Inspired by Dwarf Fortress's geological layers and Vintage Story's geologic provinces.

**Depth Bands** (measured from surface, boundaries offset ±4 blocks by 2D noise):
| Band | Depth | Rock Types | Character |
|------|-------|-----------|-----------|
| Soil | 0-3 | Dirt/Sand/Clay (existing biome subsurface) | Already implemented |
| Upper Stone | 4-20 | Sedimentary: Limestone, Sandstone, Mudstone | Common ores (coal, iron) |
| Mid Stone | 20-45 | Igneous/Metamorphic: Granite, Basalt, Andesite, Marble, Slate | Valuable ores |
| Deep Stone | 45+ | Deepstone + Quartzite pockets | Rare ores, danger |

**Province Noise** (2D, freq 0.002): Selects which rock type dominates within each band. Province [0, 0.33): Limestone/Granite dominant. Province [0.33, 0.66): Sandstone/Basalt dominant. Province [0.66, 1.0]: Mudstone/Andesite dominant. Different regions have different geology — ores are hosted by specific rocks, so geology determines available resources.

**Rock Blob Noise** (3D, freq 0.05): Creates pockets of secondary rock within the dominant matrix (~25-30% volume). Breaks up visual monotony within bands.

**Band Boundary Noise** (2D, freq 0.015): Offsets depth band transitions by ±4 blocks to create undulating boundaries instead of flat artificial lines.

**Implementation:** `GeologyGenerator.cs` owns 3 noise instances. Single method `GetRockType(worldX, worldY, worldZ, surfaceY, province)` returns the appropriate `BlockType`. Called per-block from `GenerateChunkBlocks()` for blocks deeper than the soil layer. Province value is sampled once per column in `SampleColumn()` and stored in `ColumnSample.Province`.

### 3.15 Ore Generation (Tier 1)

Four Tier 1 ores placed as noise-based cluster deposits in the Upper Stone band. Each ore has its own 3D noise field, depth range, and **host rock restrictions** — ores only replace specific geological rock types.

| Ore | BlockType ID | Depth | Host Rocks | Noise Freq | Threshold | Cluster Size |
|-----|-------------|-------|-----------|-----------|-----------|-------------|
| Coal | 23 | 5-30 | Sedimentary | 0.08 | 0.60 | 50-150 blocks |
| Iron | 24 | 10-35 | Sedimentary | 0.10 | 0.65 | 30-80 blocks |
| Copper | 25 | 5-25 | Any rock | 0.09 | 0.68 | 20-60 blocks |
| Tin | 26 | 10-30 | Sed. + Meta. | 0.11 | 0.72 | 10-30 blocks |

**Host Rock Restriction** creates natural regional variation: areas with Granite underground (igneous) won't have iron or coal (which require sedimentary rock), but will have copper. This means different starting locations have different resource profiles.

**Generation Order:** Ore placement runs AFTER geology fill but BEFORE cave carving:
1. Terrain fill (surface/subsurface blocks)
2. Geology fill (replaces Stone with rock types)
3. Ore placement (replaces some rock with ore)
4. Cave carving (may remove ore blocks, naturally exposing ore in cave walls)
5. Tree placement

**Implementation:** `OreGenerator.cs` owns 4 noise instances. `TryPlaceOre(worldX, worldY, worldZ, depthBelowSurface, currentRock)` checks each ore's depth/host-rock/threshold conditions and returns the ore `BlockType` if placed, or the original rock if not. Rarer ores checked first to avoid being overwritten.

### 3.16 Why Build From Scratch (Not Use Existing Voxel Libraries)

Evaluated options:
- **Zylann's godot_voxel** (C++): Powerful but C# bindings are broken, and it's overkill for colony sim needs
- **VoxelFactory** (C#): Abandoned, no Godot 4 support
- **Godot Voxel Game Demo** (GDScript): Good reference for threading/chunking patterns, but GDScript only
- **Chunkee** (Rust): Wrong language

Building from scratch gives full control over the C# implementation and keeps the codebase understandable. The core chunk rendering is only ~200-300 lines.

---

## 4. Coordinate Systems

Three coordinate systems are used throughout. Mixing them up causes bugs.

| System | Type | Example | Used For |
|--------|------|---------|----------|
| **Local block** | `int (0-15)` | `(3, 7, 12)` | Indexing into `Chunk._blocks[x,y,z]` |
| **World block** | `Vector3I` | `(19, 7, 28)` | Identifying any block globally |
| **World position** | `Vector3` | `(19.5, 8.0, 28.5)` | Colonist position, camera, raycasts |

**Conversions:**
- World block → Chunk coord: `chunkCoord = FloorDiv(worldBlock, 16)` (per axis, rounds toward -inf)
- World block → Local block: `local = ((worldBlock % 16) + 16) % 16` (handles negatives!)
- Local block → World block: `worldBlock = chunkCoord * 16 + local`
- Block center world position: `(worldBlockX + 0.5, worldBlockY + 1.0, worldBlockZ + 0.5)` — the +1 on Y is because the colonist stands ON TOP of the block

**Critical:** When converting to local coords, C# `%` operator returns negative values for negative inputs. Use the `((x % 16) + 16) % 16` pattern or you'll get array-out-of-bounds on negative world coordinates.

---

## 5. Lessons Learned (Critical — Read Before Coding)

These are concrete mistakes made during prototyping. Each one wasted significant time.

### 5.1 Mesh Winding Order

**Problem:** Chunk rendered hollow — only back faces were visible.

**Root cause:** Triangle indices were counter-clockwise. Godot expects clockwise winding (when viewed from outside) to determine the front face.

**Fix:** Reverse the triangle index order for quads:
- Wrong: `{ 0, 1, 2, 0, 2, 3 }` (CCW)
- Right: `{ 0, 2, 1, 0, 3, 2 }` (CW)

**Do NOT** use `CullMode.Front` as a workaround. It makes faces appear solid but normals point inward, breaking all lighting.

**Debug technique:** Set `CullMode.Disabled` temporarily. If the chunk looks solid with culling disabled, the winding is wrong. If it still has holes, the vertices are wrong.

### 5.2 NavigationServer3D Is Wrong for Voxel Worlds

See section 3.3. Do not attempt to use Godot's built-in navigation system. It was tried extensively and failed due to structural mismatches with voxel geometry. Use grid-based A* instead.

### 5.3 [Tool] Attribute + Scene File Bloat = Broken Physics

**Problem:** Colonist spawned in the air and `MoveAndSlide()` was a complete no-op — position never changed despite velocity accumulating correctly. Both `MoveAndSlide()` and `MoveAndCollide()` produced zero movement.

**Root cause:** The `[Tool]` attribute on `Main.cs` causes Godot's editor to execute `_Ready()`, which creates the entire World with all chunks, meshes, and collision shapes. The editor then **serializes all of these runtime-generated nodes into the `.tscn` scene file**. The file grew to 14.3MB with thousands of baked Chunk nodes.

When the game runs, it loads the pre-baked collision shapes from the scene AND creates new ones from scratch. The `CharacterBody3D` becomes trapped between two overlapping collision worlds, making `MoveAndSlide()` a complete no-op.

**Fix:** Cleaned `main.tscn` to contain only essential nodes (root, camera, light, environment). File went from 14.3MB to ~0.8KB.

**Root cause of recurrence:** The initial fix only cleaned the scene file without addressing the code. The `Owner = GetTree().EditedSceneRoot` assignments in `Main.cs`, `Chunk.cs`, and `World.cs` told Godot's editor to serialize every runtime-created node. Each time the editor saved, the scene bloated again (grew to 48.5MB on second occurrence). The permanent fix was removing ALL `Owner = EditedSceneRoot` assignments and disabling the editor preview `LoadChunkArea()` call.

**Prevention rules:**
- **NEVER set `Owner = EditedSceneRoot` on runtime-generated nodes.** This is the mechanism that causes serialization. Without Owner set, Godot won't persist runtime nodes to the scene file.
- **NEVER let the scene file get bloated with baked runtime data.** If `main.tscn` grows beyond a few KB, something is wrong.
- The `[Tool]` attribute is kept only for `[Export]` property editing in the inspector. Editor terrain preview is permanently disabled because it causes scene bloat.
- If `MoveAndSlide()` produces zero movement despite valid velocity, check for overlapping collision shapes first.
- After cleaning the scene file, **close and reopen the Godot editor** — it may cache the old scene in memory.

### 5.4 Background Mesh Generation: Timing Matters for Neighbor Snapshots

**Problem (initial attempt):** Moving greedy meshing to background threads caused visible grid-pattern artifacts at chunk boundaries — every boundary face was rendered, creating a wireframe grid effect.

**Root cause:** Chunks load in waves. During the initial loading burst, neighbors haven't been generated yet. Snapshotting neighbor boundary slices at dispatch time fails because the snapshots are mostly null/Air.

**Initial workaround:** Keep mesh generation on the main thread with a time budget. This was correct as a first pass but created a severe bottleneck — each chunk took ~13ms to mesh, meaning 800+ queued chunks drained at 1/frame, taking ~13 seconds.

**Correct solution (implemented):** Background mesh generation with `MakeSnapshotNeighborCallback()` — but dispatch only after terrain generation completes. The key insight from diagnostic logging: the mesh queue only starts processing after `loadQueue=0` and `generating=0`. By that point, all neighbors exist and boundary snapshots are correct. The 4-phase pipeline naturally ensures this ordering.

**Lesson:** Thread-safe data access is not enough — the data must actually *exist* when you need it. The solution is not "never background it" but rather "background it at the right time." Pipeline ordering (terrain gen → node creation → mesh dispatch) guarantees neighbors exist when snapshots are taken.

### 5.5 General Debugging Principles

1. **Understand the system before changing code.** Random trial-and-error on 6 cube faces with 4 vertices each produces chaos.
2. **Don't accept workarounds that "look right."** Verify the fix is actually correct (check lighting, normals, physics behavior — not just visual appearance).
3. **Change one thing at a time.** When debugging, isolate variables. Test with the simplest case (one block, one face).
4. **Search the web first.** Most Godot/voxel issues have known solutions.
5. **Limit to 3 attempts before reassessing.** If the same approach fails 3 times, stop coding. Re-read the relevant Godot docs or search the web. The bug is likely in a different place than you think (data vs code, scene files vs scripts, timing vs logic).
6. **Check data before code.** Many "code bugs" are actually data problems: bloated `.tscn` files, wrong shader uniforms in `project.godot`, missing collision shapes due to timing. Verify the data pipeline before debugging the logic.
7. **Don't add complexity to work around symptoms.** If a colonist falls through the floor, don't add bounce-back logic — find why there's no floor. Grace timers, extra raycasts, and retry loops usually indicate the root cause hasn't been found.

---

## 6. Implementation Status

### Completed Phases

| Phase | Feature | Status |
|-------|---------|--------|
| 1 | Project setup (Godot 4.6 Mono, Jolt, D3D12) | Done |
| 2 | Block definitions (Air, Stone, Dirt, Grass, Sand, Water, Gravel) | Done |
| 3 | Single chunk rendering (ArrayMesh, face culling, CW winding) | Done |
| 4 | Chunk collision (ConcavePolygonShape3D per chunk) | Done |
| 5 | Multi-chunk world (Dictionary<Vector3I, Chunk>, coordinate conversion) | Done |
| 6 | Terrain generation (4-layer noise, rivers, beaches, mountains) | Done |
| 7 | Block modification (left-click remove, cross-chunk mesh regen) | Done |
| 8 | A* pathfinding (8-connected, toggleable diagonals, clearance checks) | Done |
| 9 | Colonist (CharacterBody3D state machine, path visualization) | Done |
| 10 | RTS camera (WASD pan, scroll zoom, middle-mouse rotate) | Done |
| 11 | Vertical chunks (configurable Y layers, 64-block default height) | Done |
| 12 | Chunk streaming (camera-based load/unload, 16/frame budget) | Done |
| 13 | Chunk data caching (in-memory cache of ALL chunks, eviction at 3x radius) | Done |
| 14 | Biome system (6 biomes, temperature/moisture noise, height blending) | Done |
| 15 | Greedy meshing (Mikola Lysenko algorithm, up to 256× triangle reduction) | Done |
| 16 | Threaded chunk generation (background terrain gen, budgeted main-thread mesh) | Done |
| 17 | Streaming optimizations (terrain prefetch, empty chunk skip, time-budgeted mesh) | Done |
| 18 | Tree generation (deterministic grid-based, per-biome density, Wood + Leaves blocks) | Done |
| 19 | Background mesh generation (threaded greedy meshing, neighbor snapshots, softer lighting) | Done |
| 20 | Cave generation (dual-threshold 3D noise spaghetti caves, depth-scaled) | Done |
| 21 | Y-level camera slicing (shader-based Y-clip, Page Up/Down controls, raycast pierce) | Done |
| 22 | Performance: distance-based collision, increased pipeline throughput, reduced shadows | Done |
| 23 | Performance: single-surface chunks (merge all opaque blocks, ~6x fewer draw calls) | Done |
| 24 | Colonist spawn safety (deferred physics, cave-safe height, origin spawn) | Done |
| 25 | Variable-width rivers (width modulation noise, Minecraft-style global water level) | Done |
| 26 | Geological layers (10 rock types, depth bands, province noise, boundary noise) | Done |
| 27 | Tier 1 ore generation (Coal, Iron, Copper, Tin — noise clusters, host-rock restrictions) | Done |

### Future Phases (not yet planned in detail):
- Cave decoration (moss, glowing mushrooms, stalactites by depth — see WORLDGEN_PLAN.md Phase B)
- Expanded biomes (14 biomes, 6 tree styles, 4D selection — see WORLDGEN_PLAN.md Phase C)
- Terrain drama (cliff faces, mesa terracing, plateau flat-tops — see WORLDGEN_PLAN.md Phase D)
- Deep underground (lava, crystal/magma caves, Tier 2-3 ores — see WORLDGEN_PLAN.md Phase E)
- Structures & POIs (ruins, abandoned mines, stone circles — see WORLDGEN_PLAN.md Phase F)
- Multiple colonists
- Task/job system (mine, build, haul designations)
- Inventory and resource system (mined blocks become items)
- Colonist needs (hunger, rest, mood)
- Selection system (click to select colonists, area designation)
- Save/load system (disk persistence for modified chunks)
- UI (menus, status panels, notifications)

---

## 7. File Structure

```
colonysim-3d/
├── project.godot
├── CLAUDE.md
├── WORLDGEN_PLAN.md                      # World generation research & phased implementation plan
├── main.tscn                             # Entry scene (KEEP MINIMAL — see lesson 5.3)
├── shaders/
│   ├── chunk_opaque.gdshader             # Vertex-color lit shader + Y-level slice (solid blocks)
│   └── chunk_water.gdshader              # Same + alpha blending (water blocks)
├── scripts/
│   ├── Main.cs                           # Entry point, world setup, camera/colonist spawn
│   ├── world/
│   │   ├── World.cs                      # Chunk manager, streaming, chunk cache, collision radius
│   │   ├── Chunk.cs                      # 16x16x16 block storage, mesh, distance-based collision
│   │   ├── ChunkMeshGenerator.cs         # Greedy meshing, single-surface opaque + water, collision
│   │   ├── TerrainGenerator.cs           # 7-layer FastNoiseLite terrain + biomes + rivers + geology
│   │   ├── GeologyGenerator.cs           # Depth-banded rock layers + province noise (Phase A)
│   │   ├── OreGenerator.cs               # Tier 1 noise-cluster ore deposits (Phase A)
│   │   ├── TreeGenerator.cs              # Deterministic grid-based tree placement
│   │   ├── CaveGenerator.cs              # Dual-threshold 3D noise cave carving
│   │   ├── Biome.cs                      # BiomeType enum, BiomeData struct, BiomeTable
│   │   └── Block.cs                      # BlockType enum (27 types) + BlockData utilities
│   ├── navigation/
│   │   ├── VoxelPathfinder.cs            # A* on voxel grid (8-connected, toggleable diagonals)
│   │   └── PathRequest.cs                # VoxelNode, PathResult data structures
│   ├── colonist/
│   │   └── Colonist.cs                   # CharacterBody3D + state machine + path visualization
│   ├── interaction/
│   │   └── BlockInteraction.cs           # Mouse raycast, block removal, colonist commands
│   └── camera/
│       ├── CameraController.cs           # RTS camera (pan, zoom, rotate, Y-slice controls)
│       └── SliceState.cs                 # Global Y-level slice state (static class)
└── godot-docs-master/                    # Local Godot 4.6 docs (reference)
```

---

## 8. Gotchas & Warnings

1. **Do NOT use NavigationServer3D** for pathfinding. Use grid-based A*. See section 3.3 for why.

2. **Do NOT use GridMap** for the voxel world. Use procedural ArrayMesh. See section 3.1 for why.

3. **Mesh winding order must be clockwise** (viewed from outside). If blocks render inside-out, reverse the index order. See section 5.1.

4. **ConcavePolygonShape3D wants a flat vertex list**, not indexed triangles. Expand indices to sequential vertex triples before passing to `SetFaces()`.

5. **Negative coordinate modulo in C#** returns negative values. Use `((x % 16) + 16) % 16` for chunk-local conversion.

6. **`Vector3.Floor()` rounds toward negative infinity**, which is correct for block position math. But casting to `int` truncates toward zero — use `Mathf.FloorToInt()` instead.

7. **Chunk.GetBlock() should return Air for out-of-bounds coordinates**, not throw. This simplifies face culling (boundary blocks treat out-of-chunk neighbors as air, rendering their exposed faces).

8. **Colonist capsule radius** (0.3) + **block center routing** (X+0.5) = **0.2 units wall clearance**. This is tight but sufficient. If you increase capsule radius, you need to account for clearance in pathfinding neighbor checks.

9. **Blocks are 1x1x1 units.** Colonists are ~2 blocks tall. All clearance checks must verify 2 air blocks above a walkable surface.

10. **After `MoveAndSlide()`, `IsOnFloor()` may return true for 1-2 frames after a jump** due to the character still touching the launch surface. Use a grace timer (~0.15s) before checking `IsOnFloor()` in jump state.

11. **Keep `main.tscn` minimal.** The `[Tool]` attribute causes the editor to serialize runtime nodes into the scene file. If the file grows beyond a few KB, it will break CharacterBody3D physics. The fix is to NEVER set `Owner = EditedSceneRoot` on runtime-created nodes and to NOT use `LoadChunkArea()` in editor mode. See section 5.3.

12. **Water is non-solid.** `BlockData.IsSolid()` returns false for Water (and Air). This means face culling treats water surfaces as exposed faces on adjacent solid blocks, and pathfinding won't route through water.

13. **Do NOT create Godot objects on background threads.** `new Chunk()`, `AddChild()`, `ArrayMesh`, `StandardMaterial3D`, etc. must all be created on the main thread. Background threads should only compute raw data (block arrays, vertex arrays) and enqueue results for the main thread to apply.

14. **`[ThreadStatic]` buffers need lazy initialization.** `[ThreadStatic]` fields are per-thread but NOT initialized on new threads — they default to null/zero. Always check and initialize before use (e.g., `_sliceMask ??= new BlockType[256]`).

15. **Move expensive computation off the main thread.** Greedy meshing (~13ms/chunk) should run on background threads, not the main thread with a time budget. The time budget approach only processes 1 chunk/frame when each chunk exceeds the budget, creating massive backlogs. Use `MakeSnapshotNeighborCallback()` to capture neighbor boundary data before dispatch so background threads have correct data without accessing shared state.

16. **Skip empty chunks entirely.** Upper Y layers are almost always 100% air. Tracking them in a `HashSet<Vector3I>` instead of creating Godot node hierarchies eliminates ~50% of scene tree overhead during streaming. Non-empty chunks outside `CollisionRadius` only create 2 nodes (`Chunk` → `MeshInstance3D`) instead of 4, further reducing overhead.

17. **Tree generation is deterministic — no special caching needed.** Trees are placed via `PositionHash(worldX, worldZ, seed)` during terrain generation. When a chunk unloads and reloads, identical trees regenerate from the same seed. With `_cacheAllChunks` enabled, chunks are cached on unload for instant reload regardless.

18. **Chunk materials use ShaderMaterial, not StandardMaterial3D.** All chunk meshes use custom `.gdshader` files for Y-level slice support. The shaders replicate the same vertex-color lit appearance as `StandardMaterial3D` but add global uniform-based fragment discard for slicing. Lazy-loaded via static properties in `ChunkMeshGenerator`.

19. **Cave generation is deterministic — same as trees.** Caves are carved via 3D noise evaluation at world coordinates. When a chunk unloads and reloads, identical caves regenerate. With `_cacheAllChunks` enabled, chunks are cached on unload for instant reload regardless.

20. **Global shader uniforms must be declared in project.godot.** The `[shader_globals]` section defines `slice_y_level` and `slice_enabled`. Without this declaration, `RenderingServer.GlobalShaderParameterSet()` calls are silently ignored.

21. **Godot shader `fragment()` does not allow `return` statements.** Use `if/else` branching with `discard` instead. Also, compound boolean expressions mixing `bool` and float comparisons (e.g., `slice_enabled && world_position.y > level`) may fail to compile — use nested `if` blocks instead.

22. **Y-level slice cross-section tint must use NORMAL to avoid side walls.** The first attempt tinted ALL fragments within 1 block of the slice level, creating ugly dark blobs on side walls and cliffs. The correct approach: only tint upward-facing surfaces (NORMAL.y > 0.5) so only the "floor plan" cut surface gets the darkening effect.

23. **ConcavePolygonShape3D is extremely expensive for the physics broadphase.** At render distance 20 (13,448 chunks), having collision on every chunk tanks FPS because Jolt processes all shapes every physics frame. Use distance-based collision (`CollisionRadius = 4`) — only chunks near the camera need collision. Distant chunks only need rendering (Godot auto-frustum-culls `MeshInstance3D` nodes). When adding colonist simulation outside camera range, consider abstract pathfinding without physics collision.

24. **Chunk collision faces are cached separately from the mesh.** `_lastCollisionFaces` stores the raw `Vector3[]` from mesh generation. This allows `EnableCollision()` to apply collision data without re-running greedy meshing. The cache is a reference (not copy) — cheap to store.

25. **Never create one mesh surface per block type.** Each mesh surface = 1 draw call. With 12 block types and 13k chunks, that's ~80k draw calls — way too many. Instead, merge all opaque blocks into a single surface with per-vertex colors. Only water needs a separate surface (different shader). Max 2 surfaces per chunk = max ~26k draw calls (after frustum culling: ~6-8k).

26. **Share ShaderMaterial instances across chunks.** Since chunk shaders have no per-material uniforms (all differentiation via vertex colors and global uniforms), a single `ShaderMaterial` instance can be shared by all chunks. This avoids allocating 80k+ material objects.

27. **Colonist physics must be frozen until chunks load.** `CharacterBody3D` gravity applies every `_PhysicsProcess` frame. At startup, zero chunks exist — the colonist falls through void in 1-2 frames. Use `_physicsReady` flag and enable only after `World.IsChunkReady()` confirms the spawn chunk has a mesh. Never assume collision exists at `_Ready()` time. See section 3.12.

28. **`GetSurfaceHeight()` returns noise height, not actual surface.** Caves carved below terrain create air pockets the noise doesn't know about. After chunks load, use `World.GetBlock()` to scan downward and find the real highest solid block. Don't rely on noise height for colonist placement after cave generation was added.

29. **River water follows Minecraft-style global water level.** All water sits at `WaterLevel` (45) — no per-section local water levels. Rivers that carve below sea level fill with water naturally; rivers above sea level are dry valleys. This avoids water wall artifacts at wet/dry boundaries. Per-section local water levels were tried and produced visible water walls where noise thresholds changed.

30. **River width modulation is a separate noise from river path.** The width noise (freq 0.018) modulates the channel shape via `RiverWidthMod` (0.5–2.0×), stored in `ColumnSample` and used consistently in `ComputeHeight()` and `IsRiverChannel()`.

31. **Geology and ore generation run BETWEEN terrain fill and cave carving.** Pipeline order: terrain height → biome surface/subsurface → geology rock types → ore placement → cave carving → trees. This means ores naturally appear in cave walls when caves carve through ore deposits. Changing this order will break the ore-in-cave-walls feature.

32. **Province noise should be sampled once per column, not per block.** `GeologyGenerator.SampleProvince()` is 2D (XZ only). The value is stored in `ColumnSample.Province` and reused for all blocks in the column. Sampling it per-block would waste ~15 noise evaluations per column with identical results.

33. **Ore host-rock restrictions create natural regional variation.** Iron and Coal only spawn in sedimentary rock (Limestone, Sandstone, Mudstone). If a region's upper stone band is Granite-dominant, it won't have iron — but it will have copper (which spawns in any upper stone rock). This is by design, not a bug.

34. **All new rock and ore types are solid.** `BlockData.IsSolid()` returns true for all geological rocks and ores. Existing meshing, collision, pathfinding, and face culling handle them automatically with zero changes needed.

---

## 9. Runtime Controls

| Key | Action |
|-----|--------|
| WASD / Arrow keys | Pan camera |
| Scroll wheel | Zoom in/out |
| Middle mouse + drag | Rotate camera |
| Left click | Remove block |
| Right click | Command colonist to walk to position |
| F1 | Toggle path visualization (red line + markers) |
| F2 | Toggle diagonal pathfinding movement |
| Page Down | Lower Y-level slice (reveal underground) |
| Page Up | Raise Y-level slice |
| Home | Disable Y-level slice (show full world) |

---

## 10. Reference Materials

Godot 4.6 documentation is available locally at:
```
E:\hobbies\programming\godot\colonysim-3D\godot-docs-master\
```

Key docs:
- ArrayMesh procedural geometry: `tutorials/3d/procedural_geometry/arraymesh.rst`
- CharacterBody3D: `classes/class_characterbody3d.rst`
- FastNoiseLite: `classes/class_fastnoiselite.rst`
- SurfaceTool: `classes/class_surfacetool.rst`

---

## 11. Testing & Verification

You cannot run the Godot project yourself. The **user** runs the game and reports back. This means you must make it easy for the user to verify that things work.

### Debug Logging

**Use `GD.Print()` liberally.** Every significant action should log to the Godot console so the user can confirm behavior without reading code. Examples:

- Chunk loaded: `"Loaded 16 chunks (48 remaining)"`
- Chunk unloaded: `"Unloaded 32 chunks, cancelled 0 generating (128 cached)"`
- Chunk restored: `"Cache hits: 16 chunks loaded instantly (128 data cached, 0 prefetched)"`
- Chunk unloaded: `"Unloaded 32 chunks"`
- Block modified: `"Removed block at (19, 7, 28) — was Grass"`
- Path found: `"Colonist: path set, 14 waypoints"`
- Path failed: `"Colonist: path not found, staying idle"`
- Colonist state: `"Colonist: reached destination (25.5, 6.0, 30.5)"`
- Colonist stuck: `"Colonist: stuck for 2.1s at (22.4, 8.0, 29.5), clearing path"`

### Asking the User to Verify

After completing each feature, **ask the user to run the game and report back.** Be specific about what to look for. If something doesn't work, **ask the user to paste the console output.** The debug logs will tell you what happened without needing to guess.

---

## 12. Quality Standards

- Every change must **compile with 0 errors and 0 warnings** before committing.
- Test each feature in the running game, not just in theory.
- Prefer simple, readable code over clever abstractions. This project will grow — understandability matters more than cleverness.
- Do not over-engineer for future requirements. Build what's needed now.
- If stuck on a bug for more than 2-3 attempts, stop and analyze the root cause before trying more fixes. Random permutations waste time.
