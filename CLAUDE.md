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

### 3.2 Collision: ConcavePolygonShape3D Per Chunk

Each chunk gets its own `StaticBody3D` with a `ConcavePolygonShape3D`. The collision mesh is built from the same vertex data as the render mesh, expanded from indexed triangles to a flat vertex list (ConcavePolygonShape3D requires sequential vertex triples, not indexed buffers).

Regenerate collision whenever the render mesh changes.

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

**Capsule dimensions:** radius=0.3, height=1.6. This gives 0.2 units of wall clearance when the colonist is at a block center (0.5 - 0.3 = 0.2).

**Path visualization:** Red line + cross markers drawn via ImmediateMesh, toggled with F1. Added to scene root (world space, not colonist-local) with NoDepthTest for visibility.

### 3.5 Chunk Streaming & Threaded Generation

Chunks load/unload dynamically based on camera position with a **three-phase pipeline** inside `ProcessLoadQueue()`:

**Phase 1 — Apply terrain results (budgeted):** Background threads post completed `ChunkGenResult` structs to a `ConcurrentQueue`. Phase 1 dequeues these, creates Godot scene nodes (`Chunk` + `MeshInstance3D` + `StaticBody3D` + `CollisionShape3D`), and queues the chunk + its 6 neighbors for mesh generation. Budgeted to `MaxApplyPerFrame` (16) to prevent frame spikes when many results arrive at once. Overflow results are re-enqueued for the next frame.

**Phase 2 — Time-budgeted mesh generation:** Greedy meshing (`GenerateMeshData()`) runs on the main thread with correct neighbor data via `MakeNeighborCallback()`. Uses a **wall-clock time budget** (`MeshTimeBudgetMs = 4.0`) via `Stopwatch.GetTimestamp()` instead of a fixed chunk count. This adapts to hardware speed — fast PCs mesh more chunks per frame, slow PCs mesh fewer, both maintaining smooth frame rates.

**Phase 3 — Dispatch terrain generation:** New chunks are dequeued from the load queue. Modified chunk cache and terrain prefetch cache are checked first (instant restore). Otherwise, `Task.Run()` dispatches `TerrainGenerator.GenerateChunkBlocks()` to the .NET thread pool. Up to `MaxConcurrentGens` (8, or 32 during large queue bursts) chunks generate concurrently.

**Terrain prefetch ring:** `DispatchPrefetch()` pre-generates terrain for chunks within `renderRadius + PrefetchRingWidth` (3) but beyond render distance. Results are stored in a `ConcurrentDictionary<Vector3I, BlockType[,,]>` terrain cache. When the camera pans and these chunks enter render distance, their terrain data is instantly available — no thread pool wait. Up to 8 prefetches dispatched per frame. Stale cache entries and in-flight prefetches are evicted when the camera moves away.

**Empty chunk optimization:** Chunks that are 100% air (typically upper Y layers above terrain) are tracked in a lightweight `HashSet<Vector3I>` instead of creating Godot scene nodes. This eliminates ~50% of node creation overhead since half the Y layers are usually empty sky. Empty status is detected both from background terrain gen results and prefetch cache checks.

**Other details:**
- `Main._Process()` converts camera position to chunk XZ coordinate each frame, calls `World.UpdateLoadedChunks()`
- Chunks unload when their XZ distance from camera exceeds `radius + 2` (hysteresis prevents thrashing at boundaries)
- In-flight background generation is cancelled for chunks that move out of range (`_unloadedWhileGenerating` set)
- `QueueNeighborMeshes()` helper deduplicates the pattern of queuing 6 face-adjacent neighbors for mesh regeneration
- Editor mode still uses synchronous `LoadChunkArea()` for immediate preview

**Why mesh generation stays on the main thread:** Greedy meshing needs correct neighbor data for cross-chunk face culling. When chunks load in waves, neighbors don't exist yet during the initial loading burst. Attempting background mesh gen with placeholder Air neighbors causes grid-pattern artifacts at chunk boundaries (every boundary face renders). Snapshotting neighbor boundary slices also fails because neighbors haven't been generated yet. The time-budgeted main-thread approach guarantees correct neighbor data while adapting to hardware speed.

### 3.6 Dirty Chunk Caching

Modified chunks are preserved across unload/reload cycles:
- `Chunk.IsDirty` flag is set whenever `SetBlock()` modifies a block
- On unload, dirty chunks save a copy of their block data to `World._modifiedChunkCache` (`Dictionary<Vector3I, BlockType[,,]>`)
- On reload, the cache is checked first — cached data is restored instead of regenerating from noise
- Unmodified chunks are never cached; they regenerate deterministically from the seed

This is an **in-memory cache only** — modifications are lost when the game restarts. Disk persistence is a future feature.

### 3.7 Terrain Generation

Multi-layer noise terrain using 5 `FastNoiseLite` layers:
- **Continentalness** (freq 0.003): broad terrain category — lowlands, midlands, highlands
- **Elevation** (freq 0.01): primary height variation, amplitude scaled by continentalness
- **Detail** (freq 0.06): fine surface roughness, suppressed in flat areas
- **River** (freq 0.005): rivers form where `abs(noise) ≈ 0`, only in non-mountainous terrain above water level
- **Temperature** (freq 0.004) and **Moisture** (freq 0.005): drive biome classification

Height range: 0-62 across multiple Y chunk layers (default 4 layers = 64 blocks tall). Water level: 25.

**Biome system:** 6 biomes (Grassland, Forest, Desert, Tundra, Swamp, Mountains) selected by temperature, moisture, and continentalness. Each biome has distinct surface/subsurface blocks, height offsets, amplitude scales, and detail scales defined in `BiomeTable`. Biome boundaries use weighted blending of the 4 nearest biome heights (by Euclidean distance in temp/moisture space) to avoid hard terrain seams. See `Biome.cs` for definitions and `TerrainGenerator.cs` for blending logic.

Block types by position:
- Surface: determined by biome (`BiomeData.SurfaceBlock` — Grass, RedSand, Snow, Stone)
- Subsurface (3 layers): determined by biome (`BiomeData.SubSurfaceBlock`)
- Underwater: determined by biome (`BiomeData.UnderwaterSurface`)
- Deep: Stone everywhere
- Water fills from height down to water level

### 3.8 Greedy Meshing

The chunk mesh generator uses **greedy meshing** (Mikola Lysenko algorithm) to merge adjacent same-type block faces into larger rectangles, dramatically reducing triangle count.

**How it works:** For each of 6 face directions, process 16 2D slices perpendicular to that direction. For each slice, build a 16×16 mask of which cells need a face (same culling rules as before). Then greedily merge: scan row-by-row, expand each unvisited cell rightward (same type), then downward (all cells match), emit one quad per merged rectangle.

**Key implementation details:**
- `FaceConfig` struct maps each face direction to its 2D slice coordinate system (depth/U/V axes)
- `[ThreadStatic]` scratch buffers (`_sliceMask`, `_sliceVisited`) for thread-safe parallel generation
- `GenerateMeshData()` returns thread-safe `ChunkMeshData` struct (raw arrays), `BuildArrayMesh()` creates Godot objects on main thread
- Collision merges ALL solid types together (better merging than render path since no visual distinction)
- One surface per block type in the render mesh (same material setup as before)
- CW winding order preserved: verified for all 6 faces with w=1, h=1 matching original vertex positions

**Performance:** Flat 16×16 surfaces reduce from 256 quads (512 triangles) to 1 quad (2 triangles) — up to 256× reduction. Typical terrain sees 5-10× overall triangle reduction.

### 3.9 Why Build From Scratch (Not Use Existing Voxel Libraries)

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

**Prevention rules:**
- **NEVER let the scene file get bloated with baked runtime data.** If `main.tscn` grows beyond a few KB, something is wrong.
- The `[Tool]` attribute is useful for editor previews but creates this risk. All runtime node creation in `_Ready()` must be guarded with `if (!Engine.IsEditorHint())` where appropriate, OR the scene file must be kept clean.
- If `MoveAndSlide()` produces zero movement despite valid velocity, check for overlapping collision shapes first.

### 5.4 Background Mesh Generation Causes Chunk Boundary Artifacts

**Problem:** After moving terrain generation to background threads, attempting to also move greedy meshing to background threads caused visible grid-pattern artifacts at chunk boundaries — especially on water surfaces. Every chunk boundary face was rendered, creating a wireframe grid effect.

**Root cause:** Chunks load in waves. When a chunk is dispatched to a background thread, most of its 6 face-adjacent neighbors haven't been generated yet. Using `BlockType.Air` as a fallback for missing neighbors causes every boundary face to be treated as "exposed to air" and rendered.

**Attempted fix:** Snapshotting neighbor boundary slices (copying the 16×16 boundary face of each loaded neighbor before dispatch). This also failed because during the initial loading burst, neighbors genuinely don't exist yet — the snapshots are mostly null/Air.

**Correct solution:** Keep mesh generation on the main thread where all loaded chunks are accessible via `MakeNeighborCallback()`. Budget mesh generation to a few chunks per frame (4) to avoid stutter. Only terrain block generation (noise sampling) runs on background threads.

**Lesson:** Thread-safe data access is not enough — the data must actually *exist* when you need it. In a chunk streaming system, neighbors load asynchronously and may not be available when a chunk first appears. Mesh generation that depends on neighbor state must wait until neighbors are loaded.

### 5.5 General Debugging Principles

1. **Understand the system before changing code.** Random trial-and-error on 6 cube faces with 4 vertices each produces chaos.
2. **Don't accept workarounds that "look right."** Verify the fix is actually correct (check lighting, normals, physics behavior — not just visual appearance).
3. **Change one thing at a time.** When debugging, isolate variables. Test with the simplest case (one block, one face).
4. **Search the web first.** Most Godot/voxel issues have known solutions.

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
| 13 | Dirty chunk caching (in-memory preservation of modified chunks) | Done |
| 14 | Biome system (6 biomes, temperature/moisture noise, height blending) | Done |
| 15 | Greedy meshing (Mikola Lysenko algorithm, up to 256× triangle reduction) | Done |
| 16 | Threaded chunk generation (background terrain gen, budgeted main-thread mesh) | Done |
| 17 | Streaming optimizations (terrain prefetch, empty chunk skip, time-budgeted mesh) | Done |

### Future Phases (not yet planned in detail):
- Multiple colonists
- Task/job system (mine, build, haul designations)
- Inventory and resource system (mined blocks become items)
- Colonist needs (hunger, rest, mood)
- More block types (wood, ore)
- Better terrain (caves, overhangs, ore veins)
- Selection system (click to select colonists, area designation)
- Save/load system (disk persistence for modified chunks)
- UI (menus, status panels, notifications)

---

## 7. File Structure

```
colonysim-3d/
├── project.godot
├── CLAUDE.md
├── main.tscn                             # Entry scene (KEEP MINIMAL — see lesson 5.3)
├── scripts/
│   ├── Main.cs                           # Entry point, world setup, camera/colonist spawn
│   ├── world/
│   │   ├── World.cs                      # Chunk manager, streaming, dirty cache, block access
│   │   ├── Chunk.cs                      # 16x16x16 block storage, mesh, collision, dirty flag
│   │   ├── ChunkMeshGenerator.cs         # Greedy meshing ArrayMesh + collision generation
│   │   ├── TerrainGenerator.cs           # 5-layer FastNoiseLite terrain + biomes + rivers
│   │   ├── Biome.cs                      # BiomeType enum, BiomeData struct, BiomeTable
│   │   └── Block.cs                      # BlockType enum + BlockData utilities
│   ├── navigation/
│   │   ├── VoxelPathfinder.cs            # A* on voxel grid (8-connected, toggleable diagonals)
│   │   └── PathRequest.cs                # VoxelNode, PathResult data structures
│   ├── colonist/
│   │   └── Colonist.cs                   # CharacterBody3D + state machine + path visualization
│   ├── interaction/
│   │   └── BlockInteraction.cs           # Mouse raycast, block removal, colonist commands
│   └── camera/
│       └── CameraController.cs           # RTS camera (pan, zoom, rotate)
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

11. **Keep `main.tscn` minimal.** The `[Tool]` attribute causes the editor to serialize runtime nodes into the scene file. If the file grows beyond a few KB, it will break CharacterBody3D physics. See section 5.3.

12. **Water is non-solid.** `BlockData.IsSolid()` returns false for Water (and Air). This means face culling treats water surfaces as exposed faces on adjacent solid blocks, and pathfinding won't route through water.

13. **Do NOT create Godot objects on background threads.** `new Chunk()`, `AddChild()`, `ArrayMesh`, `StandardMaterial3D`, etc. must all be created on the main thread. Background threads should only compute raw data (block arrays, vertex arrays) and enqueue results for the main thread to apply.

14. **`[ThreadStatic]` buffers need lazy initialization.** `[ThreadStatic]` fields are per-thread but NOT initialized on new threads — they default to null/zero. Always check and initialize before use (e.g., `_sliceMask ??= new BlockType[256]`).

15. **Use time budgets, not fixed counts, for per-frame work.** A fixed "N chunks per frame" is either too few on fast hardware or too many on slow hardware. Use `Stopwatch.GetTimestamp()` with a wall-clock millisecond budget (e.g., 4ms) so the system adapts automatically. This applies to any expensive per-frame loop (mesh gen, node creation, etc.).

16. **Skip empty chunks entirely.** Upper Y layers are almost always 100% air. Tracking them in a `HashSet<Vector3I>` instead of creating full Godot node hierarchies (`Chunk` → `MeshInstance3D` → `StaticBody3D` → `CollisionShape3D`) eliminates ~50% of scene tree overhead during streaming.

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
- Chunk cached: `"Cached modified chunk (3, 0, 5) (2 cached total)"`
- Chunk restored: `"Restored modified chunk (3, 0, 5) from cache"`
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
