namespace ColonySim;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Manages chunks in a Dictionary keyed by chunk coordinate.
/// Provides world-space block access and coordinate conversion.
/// </summary>
[Tool]
public partial class World : Node3D
{
    private readonly Dictionary<Vector3I, Chunk> _chunks = new();
    private TerrainGenerator _terrainGenerator;

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
        GD.Print($"SetBlock: world ({worldBlock}) -> chunk ({chunkCoord}) local ({local}) = {type}");
    }

    /// <summary>
    /// Load a grid of chunks centered at the given chunk coordinate.
    /// Only loads Y=0 layer for now (single vertical layer).
    /// </summary>
    public void LoadChunkArea(Vector3I center, int radius, int seed = 42)
    {
        _terrainGenerator ??= new TerrainGenerator(seed);

        for (int x = center.X - radius; x <= center.X + radius; x++)
        for (int z = center.Z - radius; z <= center.Z + radius; z++)
        {
            var coord = new Vector3I(x, 0, z);
            if (_chunks.ContainsKey(coord)) continue;
            LoadChunk(coord);
        }

        GD.Print($"LoadChunkArea: center=({center}), radius={radius}, total chunks={_chunks.Count}");

        // After all chunks loaded, regenerate all meshes for cross-chunk face culling
        RegenerateAllMeshes();
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
        chunk.Initialize(chunkCoord);
        _chunks[chunkCoord] = chunk;

        FillChunkTerrain(chunk, chunkCoord);

        GD.Print($"Loaded chunk at ({chunkCoord.X}, {chunkCoord.Y}, {chunkCoord.Z}): {chunk.CountSolidBlocks()} solid blocks");
    }

    private void FillChunkTerrain(Chunk chunk, Vector3I chunkCoord)
    {
        var blocks = new BlockType[Chunk.SIZE, Chunk.SIZE, Chunk.SIZE];
        _terrainGenerator.GenerateChunkBlocks(blocks, chunkCoord);
        chunk.SetBlockData(blocks);
    }

    private void RegenerateAllMeshes()
    {
        foreach (var (coord, chunk) in _chunks)
            chunk.GenerateMesh(MakeNeighborCallback(coord));

        GD.Print($"Regenerated meshes for {_chunks.Count} chunks");
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
