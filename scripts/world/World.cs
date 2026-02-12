namespace ColonySim;

using System;
using System.Collections.Generic;
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
    private const int ChunksPerFrame = 16;

    // Modified chunk cache: stores block data for dirty chunks that were unloaded.
    // On reload, cached data is used instead of regenerating from noise.
    private readonly Dictionary<Vector3I, BlockType[,,]> _modifiedChunkCache = new();

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

        // Queue chunks that need loading
        for (int x = cameraChunkXZ.X - radius; x <= cameraChunkXZ.X + radius; x++)
        for (int z = cameraChunkXZ.Y - radius; z <= cameraChunkXZ.Y + radius; z++)
        for (int y = 0; y < _yChunkLayers; y++)
        {
            var coord = new Vector3I(x, y, z);
            if (!_chunks.ContainsKey(coord))
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

        if (toUnload.Count > 0)
            GD.Print($"Unloaded {toUnload.Count} chunks");
    }

    private void ProcessLoadQueue()
    {
        if (_loadQueue.Count == 0) return;

        int loaded = 0;
        var newlyLoaded = new List<Vector3I>();

        while (_loadQueue.Count > 0 && loaded < ChunksPerFrame)
        {
            var coord = _loadQueue.Dequeue();
            if (_chunks.ContainsKey(coord)) continue; // Already loaded (e.g. by initial burst)
            LoadChunk(coord);
            newlyLoaded.Add(coord);
            loaded++;
        }

        // Regenerate meshes for newly loaded chunks + their neighbors
        var toRegenerate = new HashSet<Vector3I>();
        foreach (var coord in newlyLoaded)
        {
            toRegenerate.Add(coord);
            // Add all 6 face-adjacent neighbors
            toRegenerate.Add(coord + new Vector3I(1, 0, 0));
            toRegenerate.Add(coord + new Vector3I(-1, 0, 0));
            toRegenerate.Add(coord + new Vector3I(0, 1, 0));
            toRegenerate.Add(coord + new Vector3I(0, -1, 0));
            toRegenerate.Add(coord + new Vector3I(0, 0, 1));
            toRegenerate.Add(coord + new Vector3I(0, 0, -1));
        }

        foreach (var coord in toRegenerate)
            RegenerateChunkMesh(coord);

        if (loaded > 0)
            GD.Print($"Loaded {loaded} chunks ({_loadQueue.Count} remaining)");
    }

    private void UnloadChunk(Vector3I coord)
    {
        if (!_chunks.TryGetValue(coord, out var chunk)) return;

        if (chunk.IsDirty)
        {
            _modifiedChunkCache[coord] = chunk.GetBlockData();
            GD.Print($"Cached modified chunk {coord} ({_modifiedChunkCache.Count} cached total)");
        }

        _chunks.Remove(coord);
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
        chunk.Initialize(chunkCoord);
        _chunks[chunkCoord] = chunk;

        // Restore from cache if this chunk was previously modified, otherwise generate from noise
        if (_modifiedChunkCache.TryGetValue(chunkCoord, out var cachedBlocks))
        {
            chunk.SetBlockData(cachedBlocks);
            _modifiedChunkCache.Remove(chunkCoord);
            GD.Print($"Restored modified chunk {chunkCoord} from cache");
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
    /// Floor division that rounds toward negative infinity (not toward zero).
    /// </summary>
    private static int FloorDiv(int a, int b)
    {
        return a >= 0 ? a / b : (a - b + 1) / b;
    }
}
