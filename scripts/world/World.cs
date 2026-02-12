namespace ColonySim;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Manages chunks in a Dictionary keyed by chunk coordinate.
/// Provides world-space block access and coordinate conversion.
/// Supports vertical chunk stacking (multiple Y layers).
/// </summary>
[Tool]
public partial class World : Node3D
{
    private readonly Dictionary<Vector3I, Chunk> _chunks = new();
    private TerrainGenerator _terrainGenerator;
    private int _yChunkLayers = 4;

    // Chunk streaming state
    private Vector2I? _lastCameraChunkXZ;
    private readonly Queue<Vector3I> _loadQueue = new();

    // Background chunk generation: terrain + blocks computed on thread pool,
    // Godot objects created on main thread from completed results.
    private readonly ConcurrentQueue<ChunkGenResult> _genResults = new();
    private readonly HashSet<Vector3I> _generating = new();  // coords currently being generated
    private readonly HashSet<Vector3I> _unloadedWhileGenerating = new();  // coords unloaded before gen completed
    private const int MaxConcurrentGens = 8;

    private struct ChunkGenResult
    {
        public Vector3I Coord;
        public BlockType[,,] Blocks;
        public bool IsEmpty;
    }

    // Queue of chunks that need mesh (re)generation.
    // Phase 2 dispatches these to background threads for GenerateMeshData().
    // Phase 2b applies completed mesh results on the main thread.
    private readonly Queue<Vector3I> _meshQueue = new();
    private readonly HashSet<Vector3I> _meshQueueSet = new();  // dedup: avoid queueing same chunk twice

    // Background mesh generation: greedy meshing runs on thread pool,
    // Godot objects (ArrayMesh, ConcavePolygonShape3D) created on main thread.
    private readonly ConcurrentQueue<MeshGenResult> _meshResults = new();
    private readonly HashSet<Vector3I> _meshing = new();  // coords currently being meshed in background
    private const int MaxConcurrentMeshes = 16;  // max simultaneous background mesh jobs
    private const int MaxMeshApplyPerFrame = 24;  // max mesh results applied per frame

    private struct MeshGenResult
    {
        public Vector3I Coord;
        public ChunkMeshGenerator.ChunkMeshData MeshData;
    }

    // Budget for Phase 1 (terrain result application)
    private const int MaxApplyPerFrame = 24;

    // Collision optimization: only create collision shapes for chunks near the camera.
    // Distant chunks need rendering but NOT collision (no player/colonist interaction).
    // At render distance 20, this reduces collision shapes from ~13k to ~650.
    private const int CollisionRadius = 4;  // chunks from camera XZ that get collision
    private Vector2I _lastCollisionCenter;  // last camera chunk used for collision updates

    /// <summary>
    /// Check if a chunk coordinate is within collision radius of the current camera position.
    /// </summary>
    private bool IsWithinCollisionRadius(Vector3I coord)
    {
        if (!_lastCameraChunkXZ.HasValue) return true; // no camera yet, default to collision
        int dx = Mathf.Abs(coord.X - _lastCameraChunkXZ.Value.X);
        int dz = Mathf.Abs(coord.Z - _lastCameraChunkXZ.Value.Y);
        return dx <= CollisionRadius && dz <= CollisionRadius;
    }

    // Terrain prefetch cache: stores pre-generated block data for chunks beyond render
    // distance but within prefetch range. When a chunk enters render distance, its terrain
    // is already available — skipping the background gen latency entirely.
    private readonly ConcurrentDictionary<Vector3I, BlockType[,,]> _terrainCache = new();
    private readonly HashSet<Vector3I> _prefetching = new();  // coords currently being prefetched
    private const int PrefetchRingWidth = 3;  // prefetch extends this many chunks beyond render radius

    // Empty chunks: coords that were generated and found to contain only Air.
    // No Godot node is created — GetBlock() returns Air via the _chunks miss path.
    // Tracked so we don't re-generate them or re-queue them.
    private readonly HashSet<Vector3I> _emptyChunks = new();

    // Unified chunk data cache: stores block data for ALL chunks on unload (when enabled).
    // Dirty chunks get a copy (may be modified later), clean chunks share the reference (no copy).
    // On reload, cached data is used instead of regenerating from noise — instant loading.
    private readonly Dictionary<Vector3I, BlockType[,,]> _chunkDataCache = new();
    private bool _cacheAllChunks = true;  // toggle: cache everything vs only dirty chunks

    /// <summary>
    /// Set the terrain generator externally (from Main, which owns the seed).
    /// Must be called before LoadChunkArea().
    /// </summary>
    public void SetTerrainGenerator(TerrainGenerator gen)
    {
        _terrainGenerator = gen;
    }

    /// <summary>
    /// Set the number of vertical chunk layers (default 4 = 64 blocks tall).
    /// Must be called before LoadChunkArea().
    /// </summary>
    public void SetYChunkLayers(int layers)
    {
        _yChunkLayers = layers;
    }

    /// <summary>
    /// Returns the surface height at a world X/Z coordinate.
    /// Used for positioning camera, colonist spawn, etc.
    /// </summary>
    public int GetSurfaceHeight(int worldX, int worldZ)
    {
        if (_terrainGenerator == null) return 30;
        return _terrainGenerator.GetHeight(worldX, worldZ);
    }

    /// <summary>
    /// Returns true if a chunk at the given coordinate is loaded and has had its mesh applied.
    /// Used to check if collision/rendering is ready before enabling colonist physics.
    /// </summary>
    public bool IsChunkReady(Vector3I coord)
    {
        return _chunks.TryGetValue(coord, out var chunk) && chunk.HasMesh;
    }

    /// <summary>
    /// Returns the biome at a world X/Z coordinate.
    /// </summary>
    public BiomeType GetBiome(int worldX, int worldZ)
    {
        if (_terrainGenerator == null) return BiomeType.Grassland;
        return _terrainGenerator.GetBiome(worldX, worldZ);
    }

    /// <summary>
    /// Convert world block coordinate to chunk coordinate.
    /// Uses floor division (not truncation) so negative coords work correctly.
    /// </summary>
    public static Vector3I WorldToChunkCoord(Vector3I worldBlock)
    {
        return new Vector3I(
            FloorDiv(worldBlock.X, Chunk.SIZE),
            FloorDiv(worldBlock.Y, Chunk.SIZE),
            FloorDiv(worldBlock.Z, Chunk.SIZE)
        );
    }

    /// <summary>
    /// Convert world block coordinate to local chunk coordinate (0..15).
    /// Handles negative values with double-modulo pattern.
    /// </summary>
    public static Vector3I WorldToLocalCoord(Vector3I worldBlock)
    {
        return new Vector3I(
            ((worldBlock.X % Chunk.SIZE) + Chunk.SIZE) % Chunk.SIZE,
            ((worldBlock.Y % Chunk.SIZE) + Chunk.SIZE) % Chunk.SIZE,
            ((worldBlock.Z % Chunk.SIZE) + Chunk.SIZE) % Chunk.SIZE
        );
    }

    /// <summary>
    /// Get block type at a world block coordinate. Returns Air for unloaded chunks.
    /// </summary>
    public BlockType GetBlock(Vector3I worldBlock)
    {
        var chunkCoord = WorldToChunkCoord(worldBlock);
        if (!_chunks.TryGetValue(chunkCoord, out var chunk))
            return BlockType.Air;
        var local = WorldToLocalCoord(worldBlock);
        return chunk.GetBlock(local.X, local.Y, local.Z);
    }

    /// <summary>
    /// Set block type at a world block coordinate. No-op for unloaded chunks.
    /// Does NOT auto-regenerate mesh — caller must call RegenerateChunkMesh().
    /// </summary>
    public void SetBlock(Vector3I worldBlock, BlockType type)
    {
        var chunkCoord = WorldToChunkCoord(worldBlock);
        if (!_chunks.TryGetValue(chunkCoord, out var chunk))
            return;
        var local = WorldToLocalCoord(worldBlock);
        chunk.SetBlock(local.X, local.Y, local.Z, type);
    }

    /// <summary>
    /// Load a grid of chunks centered at the given chunk coordinate.
    /// Loads multiple Y layers (0 to _yChunkLayers-1) for vertical terrain.
    /// Terrain generator must be set via SetTerrainGenerator() before calling this.
    /// </summary>
    public void LoadChunkArea(Vector3I center, int radius)
    {
        _terrainGenerator ??= new TerrainGenerator();

        int horizontalCount = (2 * radius + 1) * (2 * radius + 1);
        int totalChunks = horizontalCount * _yChunkLayers;
        int loaded = 0;

        GD.Print($"LoadChunkArea: loading {totalChunks} chunks ({2 * radius + 1}x{2 * radius + 1} x {_yChunkLayers} Y layers)...");

        for (int x = center.X - radius; x <= center.X + radius; x++)
        for (int z = center.Z - radius; z <= center.Z + radius; z++)
        for (int y = 0; y < _yChunkLayers; y++)
        {
            var coord = new Vector3I(x, y, z);
            if (_chunks.ContainsKey(coord)) continue;
            LoadChunk(coord);
            loaded++;
            if (loaded % 100 == 0)
                GD.Print($"  Loading chunks: {loaded}/{totalChunks}...");
        }

        GD.Print($"LoadChunkArea: {loaded} chunks loaded");

        // After all chunks loaded, regenerate all meshes for cross-chunk face culling
        RegenerateAllMeshes();
    }

    /// <summary>
    /// Stream chunks around the camera position. Call every frame from Main._Process().
    /// Queues new chunks for loading and unloads distant ones.
    /// </summary>
    public void UpdateLoadedChunks(Vector2I cameraChunkXZ, int radius)
    {
        // Process any pending loads from the queue (budgeted per frame)
        ProcessLoadQueue();

        // Only recalculate desired chunks when camera moves to a new chunk
        if (_lastCameraChunkXZ.HasValue && _lastCameraChunkXZ.Value == cameraChunkXZ)
            return;

        _lastCameraChunkXZ = cameraChunkXZ;

        // Update collision: enable for chunks near camera, disable for distant ones.
        // Only checks chunks that changed collision state (camera moved to a new chunk).
        if (_lastCollisionCenter != cameraChunkXZ)
        {
            _lastCollisionCenter = cameraChunkXZ;
            foreach (var (coord, chunk) in _chunks)
            {
                int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
                int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
                bool needsCollision = dx <= CollisionRadius && dz <= CollisionRadius;

                if (needsCollision && !chunk.HasCollision)
                    chunk.EnableCollision();
                else if (!needsCollision && chunk.HasCollision)
                    chunk.DisableCollision();
            }
        }

        // Queue chunks that need loading (skip already loaded and known-empty)
        for (int x = cameraChunkXZ.X - radius; x <= cameraChunkXZ.X + radius; x++)
        for (int z = cameraChunkXZ.Y - radius; z <= cameraChunkXZ.Y + radius; z++)
        for (int y = 0; y < _yChunkLayers; y++)
        {
            var coord = new Vector3I(x, y, z);
            if (!_chunks.ContainsKey(coord) && !_emptyChunks.Contains(coord))
                _loadQueue.Enqueue(coord);
        }

        // Unload chunks outside radius + 2 (hysteresis to prevent thrashing)
        int unloadDist = radius + 2;
        var toUnload = new List<Vector3I>();
        foreach (var coord in _chunks.Keys)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > unloadDist || dz > unloadDist)
                toUnload.Add(coord);
        }

        foreach (var coord in toUnload)
            UnloadChunk(coord);

        // Evict empty chunk records and cached chunk data beyond 3x render radius.
        // This prevents unbounded memory growth while keeping nearby chunks for instant reload.
        int cacheEvictDist = radius * 3;
        var emptyToRemove = new List<Vector3I>();
        foreach (var coord in _emptyChunks)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > cacheEvictDist || dz > cacheEvictDist)
                emptyToRemove.Add(coord);
        }
        foreach (var coord in emptyToRemove)
            _emptyChunks.Remove(coord);

        // Evict cached chunk data beyond 3x render radius
        var cacheToEvict = new List<Vector3I>();
        foreach (var coord in _chunkDataCache.Keys)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > cacheEvictDist || dz > cacheEvictDist)
                cacheToEvict.Add(coord);
        }
        foreach (var coord in cacheToEvict)
            _chunkDataCache.Remove(coord);

        if (cacheToEvict.Count > 0)
            GD.Print($"Evicted {cacheToEvict.Count} cached chunks ({_chunkDataCache.Count} remain)");

        // Cancel in-flight background generation for chunks that are now out of range
        var toCancel = new List<Vector3I>();
        foreach (var coord in _generating)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > unloadDist || dz > unloadDist)
                toCancel.Add(coord);
        }
        foreach (var coord in toCancel)
        {
            _generating.Remove(coord);
            _unloadedWhileGenerating.Add(coord);
        }

        int totalUnloaded = toUnload.Count + toCancel.Count;
        if (totalUnloaded > 0)
            GD.Print($"Unloaded {toUnload.Count} chunks, cancelled {toCancel.Count} generating ({_chunkDataCache.Count} cached)");

        // Evict terrain cache entries that are far beyond prefetch range
        int prefetchDist = radius + PrefetchRingWidth;
        int evictDist = prefetchDist + 2;
        var toEvict = new List<Vector3I>();
        foreach (var coord in _terrainCache.Keys)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > evictDist || dz > evictDist)
                toEvict.Add(coord);
        }
        foreach (var coord in toEvict)
            _terrainCache.TryRemove(coord, out _);

        // Cancel out-of-range prefetches
        var toCancelPrefetch = new List<Vector3I>();
        foreach (var coord in _prefetching)
        {
            int dx = Mathf.Abs(coord.X - cameraChunkXZ.X);
            int dz = Mathf.Abs(coord.Z - cameraChunkXZ.Y);
            if (dx > evictDist || dz > evictDist)
                toCancelPrefetch.Add(coord);
        }
        foreach (var coord in toCancelPrefetch)
            _prefetching.Remove(coord);

        // Dispatch prefetch jobs for chunks in the prefetch ring (beyond render, within prefetch range)
        DispatchPrefetch(cameraChunkXZ, radius);
    }

    private void ProcessLoadQueue()
    {
        // Phase 1: Apply completed background terrain generation results (budgeted).
        // Creates Chunk nodes and sets block data — no mesh generation (deferred to Phase 2).
        // Empty chunks (all air) skip node creation entirely to save resources.
        int applied = 0;
        int skippedEmpty = 0;
        while (_genResults.TryDequeue(out var result))
        {
            _generating.Remove(result.Coord);

            // Skip if chunk was unloaded while generating (camera moved away)
            if (_unloadedWhileGenerating.Remove(result.Coord)) continue;
            if (_chunks.ContainsKey(result.Coord) || _emptyChunks.Contains(result.Coord)) continue;

            // Empty chunks: no Godot node needed. GetBlock() returns Air via _chunks miss.
            if (result.IsEmpty)
            {
                _emptyChunks.Add(result.Coord);
                skippedEmpty++;
                continue;
            }

            // Budget: don't create too many Chunk nodes in one frame (AddChild is expensive)
            if (applied >= MaxApplyPerFrame)
            {
                // Put back for next frame
                _genResults.Enqueue(result);
                break;
            }

            var chunk = new Chunk();
            AddChild(chunk);
            // Do NOT set chunk.Owner — prevents scene file bloat (lesson 5.3)
            chunk.Initialize(result.Coord, IsWithinCollisionRadius(result.Coord));
            chunk.SetBlockData(result.Blocks);
            _chunks[result.Coord] = chunk;
            applied++;

            // Queue this chunk + its 6 neighbors for mesh generation.
            QueueMeshGeneration(result.Coord);
            QueueNeighborMeshes(result.Coord);
        }

        if (applied > 0 || skippedEmpty > 0)
            GD.Print($"Applied {applied} terrain results, {skippedEmpty} empty skipped ({_loadQueue.Count} queued, {_generating.Count} generating, {_meshQueue.Count} mesh pending)");

        // Phase 2: Dispatch mesh generation to background threads.
        // Snapshots neighbor boundary data on the main thread (safe), then dispatches
        // GenerateMeshData() to the thread pool. This moves the ~13ms greedy meshing
        // off the main thread entirely.
        int dispatched = 0;
        while (_meshQueue.Count > 0 && _meshing.Count < MaxConcurrentMeshes)
        {
            var coord = _meshQueue.Dequeue();
            _meshQueueSet.Remove(coord);
            if (!_chunks.TryGetValue(coord, out var chunk)) continue;
            if (_meshing.Contains(coord)) continue;

            // Skip empty chunks — no mesh needed
            if (chunk.IsEmpty())
            {
                chunk.ApplyMeshData(new ChunkMeshGenerator.ChunkMeshData
                {
                    IsEmpty = true,
                    Surfaces = Array.Empty<ChunkMeshGenerator.SurfaceData>(),
                    CollisionFaces = Array.Empty<Vector3>(),
                });
                continue;
            }

            // Snapshot block data and neighbor boundaries on main thread
            var blocks = chunk.GetBlockData();
            var neighborCallback = MakeSnapshotNeighborCallback(coord);

            _meshing.Add(coord);
            var meshCoord = coord;

            Task.Run(() =>
            {
                var meshData = ChunkMeshGenerator.GenerateMeshData(blocks, neighborCallback);
                _meshResults.Enqueue(new MeshGenResult
                {
                    Coord = meshCoord,
                    MeshData = meshData,
                });
            });
            dispatched++;
        }

        // Phase 2b: Apply completed background mesh results on the main thread.
        // BuildArrayMesh + ConcavePolygonShape3D creation are fast (~1ms) — not a bottleneck.
        int meshApplied = 0;
        while (_meshResults.TryDequeue(out var meshResult) && meshApplied < MaxMeshApplyPerFrame)
        {
            _meshing.Remove(meshResult.Coord);
            if (_chunks.TryGetValue(meshResult.Coord, out var chunk))
            {
                chunk.ApplyMeshData(meshResult.MeshData);
                meshApplied++;
            }
        }

        // Phase 3: Load chunks from queue. Check caches first (instant), then dispatch to thread pool.
        if (_loadQueue.Count == 0) return;

        int budget = _loadQueue.Count > 100 ? MaxConcurrentGens * 6 : MaxConcurrentGens;
        int cacheHits = 0;

        while (_loadQueue.Count > 0 && _generating.Count < budget)
        {
            var coord = _loadQueue.Dequeue();
            if (_chunks.ContainsKey(coord) || _generating.Contains(coord)
                || _emptyChunks.Contains(coord)) continue;

            // Priority 1: Chunk data cache (previously generated/modified chunks)
            if (_chunkDataCache.TryGetValue(coord, out var cachedBlocks))
            {
                _chunkDataCache.Remove(coord);
                var chunk = new Chunk();
                AddChild(chunk);
                if (Engine.IsEditorHint())
                    chunk.Owner = GetTree().EditedSceneRoot;
                chunk.Initialize(coord, IsWithinCollisionRadius(coord));
                chunk.SetBlockData(cachedBlocks);
                _chunks[coord] = chunk;
                QueueMeshGeneration(coord);
                QueueNeighborMeshes(coord);
                cacheHits++;
                continue;
            }

            // Priority 2: Prefetched terrain cache (pre-generated beyond render distance)
            if (_terrainCache.TryRemove(coord, out var prefetchedBlocks))
            {
                _prefetching.Remove(coord);

                // Check if prefetched data is all air
                bool prefetchEmpty = true;
                for (int bx = 0; bx < Chunk.SIZE && prefetchEmpty; bx++)
                for (int by = 0; by < Chunk.SIZE && prefetchEmpty; by++)
                for (int bz = 0; bz < Chunk.SIZE && prefetchEmpty; bz++)
                {
                    if (prefetchedBlocks[bx, by, bz] != BlockType.Air)
                        prefetchEmpty = false;
                }

                if (prefetchEmpty)
                {
                    _emptyChunks.Add(coord);
                    continue;
                }

                var chunk = new Chunk();
                AddChild(chunk);
                if (Engine.IsEditorHint())
                    chunk.Owner = GetTree().EditedSceneRoot;
                chunk.Initialize(coord, IsWithinCollisionRadius(coord));
                chunk.SetBlockData(prefetchedBlocks);
                _chunks[coord] = chunk;
                QueueMeshGeneration(coord);
                QueueNeighborMeshes(coord);
                cacheHits++;
                continue;
            }

            // Priority 3: Dispatch to thread pool for terrain generation
            _generating.Add(coord);

            var genCoord = coord;
            var terrainGen = _terrainGenerator;

            Task.Run(() =>
            {
                var blocks = new BlockType[Chunk.SIZE, Chunk.SIZE, Chunk.SIZE];
                terrainGen.GenerateChunkBlocks(blocks, genCoord);

                bool isEmpty = true;
                for (int x = 0; x < Chunk.SIZE && isEmpty; x++)
                for (int y = 0; y < Chunk.SIZE && isEmpty; y++)
                for (int z = 0; z < Chunk.SIZE && isEmpty; z++)
                {
                    if (blocks[x, y, z] != BlockType.Air)
                        isEmpty = false;
                }

                _genResults.Enqueue(new ChunkGenResult
                {
                    Coord = genCoord,
                    Blocks = blocks,
                    IsEmpty = isEmpty,
                });
            });
        }

        if (cacheHits > 0)
            GD.Print($"Cache hits: {cacheHits} chunks loaded instantly ({_chunkDataCache.Count} data cached, {_terrainCache.Count} prefetched)");
    }

    /// <summary>
    /// Queue a chunk for mesh (re)generation if it exists and isn't already queued or in-flight.
    /// </summary>
    private void QueueMeshGeneration(Vector3I coord)
    {
        if (_chunks.ContainsKey(coord) && !_meshing.Contains(coord) && _meshQueueSet.Add(coord))
            _meshQueue.Enqueue(coord);
    }

    /// <summary>
    /// Queue the 6 face-adjacent neighbors for mesh re-generation (cross-chunk face culling).
    /// </summary>
    private void QueueNeighborMeshes(Vector3I coord)
    {
        QueueMeshGeneration(coord + new Vector3I(1, 0, 0));
        QueueMeshGeneration(coord + new Vector3I(-1, 0, 0));
        QueueMeshGeneration(coord + new Vector3I(0, 1, 0));
        QueueMeshGeneration(coord + new Vector3I(0, -1, 0));
        QueueMeshGeneration(coord + new Vector3I(0, 0, 1));
        QueueMeshGeneration(coord + new Vector3I(0, 0, -1));
    }

    private void UnloadChunk(Vector3I coord)
    {
        if (!_chunks.TryGetValue(coord, out var chunk)) return;

        if (chunk.IsDirty)
        {
            // Dirty: copy the block data (may be modified later by player)
            _chunkDataCache[coord] = chunk.GetBlockData();
        }
        else if (_cacheAllChunks)
        {
            // Clean: share the reference directly (no allocation)
            _chunkDataCache[coord] = chunk.GetBlockDataRef();
        }

        _chunks.Remove(coord);
        _meshQueueSet.Remove(coord);  // Remove stale mesh queue entry
        _meshing.Remove(coord);  // Background mesh result will be discarded in Phase 2b
        RemoveChild(chunk);
        chunk.QueueFree();
    }

    public void RegenerateChunkMesh(Vector3I chunkCoord)
    {
        if (_chunks.TryGetValue(chunkCoord, out var chunk))
            chunk.GenerateMesh(MakeNeighborCallback(chunkCoord));
    }

    private void LoadChunk(Vector3I chunkCoord)
    {
        var chunk = new Chunk();
        AddChild(chunk);
        if (Engine.IsEditorHint())
            chunk.Owner = GetTree().EditedSceneRoot;
        chunk.Initialize(chunkCoord, IsWithinCollisionRadius(chunkCoord));
        _chunks[chunkCoord] = chunk;

        // Restore from cache if this chunk was previously cached, otherwise generate from noise
        if (_chunkDataCache.TryGetValue(chunkCoord, out var cachedBlocks))
        {
            chunk.SetBlockData(cachedBlocks);
            _chunkDataCache.Remove(chunkCoord);
            GD.Print($"Restored chunk {chunkCoord} from cache");
        }
        else
        {
            FillChunkTerrain(chunk, chunkCoord);
        }
    }

    private void FillChunkTerrain(Chunk chunk, Vector3I chunkCoord)
    {
        var blocks = new BlockType[Chunk.SIZE, Chunk.SIZE, Chunk.SIZE];
        _terrainGenerator.GenerateChunkBlocks(blocks, chunkCoord);
        chunk.SetBlockData(blocks);
    }

    private void RegenerateAllMeshes()
    {
        int meshCount = 0;
        int skippedCount = 0;
        foreach (var (coord, chunk) in _chunks)
        {
            chunk.GenerateMesh(MakeNeighborCallback(coord));
            if (chunk.IsEmpty())
                skippedCount++;
            else
                meshCount++;
        }

        GD.Print($"Meshes generated: {meshCount} active, {skippedCount} empty (skipped)");
    }

    /// <summary>
    /// Creates a callback for ChunkMeshGenerator that resolves out-of-bounds
    /// local coordinates by converting to world coords and querying the world.
    /// MUST be called on the main thread (reads _chunks dictionary).
    /// Used for synchronous mesh generation (editor mode, block modification).
    /// </summary>
    private Func<int, int, int, BlockType> MakeNeighborCallback(Vector3I chunkCoord)
    {
        return (int lx, int ly, int lz) =>
        {
            var worldBlock = new Vector3I(
                chunkCoord.X * Chunk.SIZE + lx,
                chunkCoord.Y * Chunk.SIZE + ly,
                chunkCoord.Z * Chunk.SIZE + lz
            );
            return GetBlock(worldBlock);
        };
    }

    /// <summary>
    /// Creates a thread-safe neighbor callback by snapshotting the boundary slices
    /// of all 6 face-adjacent neighbors. The returned callback can be called from
    /// any thread — it reads from pre-captured arrays, not from _chunks.
    ///
    /// Each neighbor contributes one 16×16 slice of blocks (the face touching this chunk).
    /// For missing neighbors, the snapshot contains all Air (correct default).
    ///
    /// The callback only needs to handle out-of-bounds local coords (the greedy mesher
    /// checks in-bounds blocks directly from the block array). Out-of-bounds is always
    /// exactly 1 block past one face, so we only need the immediately adjacent slice.
    /// </summary>
    private Func<int, int, int, BlockType> MakeSnapshotNeighborCallback(Vector3I chunkCoord)
    {
        // 6 neighbor slices: +X, -X, +Y, -Y, +Z, -Z
        // Each is 16×16 blocks, indexed by the two non-normal axes.
        var slicePosX = new BlockType[Chunk.SIZE * Chunk.SIZE];
        var sliceNegX = new BlockType[Chunk.SIZE * Chunk.SIZE];
        var slicePosY = new BlockType[Chunk.SIZE * Chunk.SIZE];
        var sliceNegY = new BlockType[Chunk.SIZE * Chunk.SIZE];
        var slicePosZ = new BlockType[Chunk.SIZE * Chunk.SIZE];
        var sliceNegZ = new BlockType[Chunk.SIZE * Chunk.SIZE];

        // +X neighbor: their local x=0 face → our local x=16
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(1, 0, 0), out var nPosX))
            for (int y = 0; y < Chunk.SIZE; y++)
            for (int z = 0; z < Chunk.SIZE; z++)
                slicePosX[y * Chunk.SIZE + z] = nPosX.GetBlock(0, y, z);

        // -X neighbor: their local x=15 face → our local x=-1
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(-1, 0, 0), out var nNegX))
            for (int y = 0; y < Chunk.SIZE; y++)
            for (int z = 0; z < Chunk.SIZE; z++)
                sliceNegX[y * Chunk.SIZE + z] = nNegX.GetBlock(Chunk.SIZE - 1, y, z);

        // +Y neighbor: their local y=0 face → our local y=16
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(0, 1, 0), out var nPosY))
            for (int x = 0; x < Chunk.SIZE; x++)
            for (int z = 0; z < Chunk.SIZE; z++)
                slicePosY[x * Chunk.SIZE + z] = nPosY.GetBlock(x, 0, z);

        // -Y neighbor: their local y=15 face → our local y=-1
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(0, -1, 0), out var nNegY))
            for (int x = 0; x < Chunk.SIZE; x++)
            for (int z = 0; z < Chunk.SIZE; z++)
                sliceNegY[x * Chunk.SIZE + z] = nNegY.GetBlock(x, Chunk.SIZE - 1, z);

        // +Z neighbor: their local z=0 face → our local z=16
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(0, 0, 1), out var nPosZ))
            for (int x = 0; x < Chunk.SIZE; x++)
            for (int y = 0; y < Chunk.SIZE; y++)
                slicePosZ[x * Chunk.SIZE + y] = nPosZ.GetBlock(x, y, 0);

        // -Z neighbor: their local z=15 face → our local z=-1
        if (_chunks.TryGetValue(chunkCoord + new Vector3I(0, 0, -1), out var nNegZ))
            for (int x = 0; x < Chunk.SIZE; x++)
            for (int y = 0; y < Chunk.SIZE; y++)
                sliceNegZ[x * Chunk.SIZE + y] = nNegZ.GetBlock(x, y, Chunk.SIZE - 1);

        return (int lx, int ly, int lz) =>
        {
            // Determine which face we've crossed and look up from snapshot
            if (lx >= Chunk.SIZE) return slicePosX[ly * Chunk.SIZE + lz];
            if (lx < 0) return sliceNegX[ly * Chunk.SIZE + lz];
            if (ly >= Chunk.SIZE) return slicePosY[lx * Chunk.SIZE + lz];
            if (ly < 0) return sliceNegY[lx * Chunk.SIZE + lz];
            if (lz >= Chunk.SIZE) return slicePosZ[lx * Chunk.SIZE + ly];
            if (lz < 0) return sliceNegZ[lx * Chunk.SIZE + ly];
            return BlockType.Air; // shouldn't happen — in-bounds checked directly
        };
    }

    /// <summary>
    /// Dispatch background terrain generation for chunks in the prefetch ring
    /// (beyond render distance, within prefetch range). These chunks won't be
    /// added to the scene — their block data is cached for instant loading later.
    /// </summary>
    private void DispatchPrefetch(Vector2I cameraChunkXZ, int renderRadius)
    {
        int prefetchDist = renderRadius + PrefetchRingWidth;
        int dispatched = 0;
        int maxPrefetchPerFrame = 8;

        for (int x = cameraChunkXZ.X - prefetchDist; x <= cameraChunkXZ.X + prefetchDist; x++)
        for (int z = cameraChunkXZ.Y - prefetchDist; z <= cameraChunkXZ.Y + prefetchDist; z++)
        {
            // Skip chunks inside render distance (they're handled by the normal pipeline)
            int dx = Mathf.Abs(x - cameraChunkXZ.X);
            int dz = Mathf.Abs(z - cameraChunkXZ.Y);
            if (dx <= renderRadius && dz <= renderRadius) continue;

            for (int y = 0; y < _yChunkLayers; y++)
            {
                if (dispatched >= maxPrefetchPerFrame) return;

                var coord = new Vector3I(x, y, z);

                // Skip if already loaded, cached, generating, prefetching, or known empty
                if (_chunks.ContainsKey(coord)) continue;
                if (_emptyChunks.Contains(coord)) continue;
                if (_terrainCache.ContainsKey(coord)) continue;
                if (_generating.Contains(coord)) continue;
                if (_prefetching.Contains(coord)) continue;
                if (_chunkDataCache.ContainsKey(coord)) continue;

                _prefetching.Add(coord);
                dispatched++;

                var genCoord = coord;
                var terrainGen = _terrainGenerator;

                Task.Run(() =>
                {
                    var blocks = new BlockType[Chunk.SIZE, Chunk.SIZE, Chunk.SIZE];
                    terrainGen.GenerateChunkBlocks(blocks, genCoord);
                    _terrainCache[genCoord] = blocks;
                });
            }
        }
    }

    /// <summary>
    /// Floor division that rounds toward negative infinity (not toward zero).
    /// </summary>
    private static int FloorDiv(int a, int b)
    {
        return a >= 0 ? a / b : (a - b + 1) / b;
    }
}
