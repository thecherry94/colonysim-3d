namespace ColonySim;

using System;
using Godot;

/// <summary>
/// A 16x16x16 block chunk. Manages block storage, mesh rendering.
/// </summary>
[Tool]
public partial class Chunk : Node3D
{
    public const int SIZE = 16;

    private BlockType[,,] _blocks = new BlockType[SIZE, SIZE, SIZE];
    private MeshInstance3D _meshInstance;
    private StaticBody3D _staticBody;
    private CollisionShape3D _collisionShape;
    private bool _isEmpty = true;

    public Vector3I ChunkCoord { get; private set; }

    public void Initialize(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Position = new Vector3(chunkCoord.X * SIZE, chunkCoord.Y * SIZE, chunkCoord.Z * SIZE);
        Name = $"Chunk_{chunkCoord.X}_{chunkCoord.Y}_{chunkCoord.Z}";

        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "MeshInstance";
        AddChild(_meshInstance);

        _staticBody = new StaticBody3D();
        _staticBody.Name = "StaticBody";
        AddChild(_staticBody);
        _collisionShape = new CollisionShape3D();
        _collisionShape.Name = "CollisionShape";
        _staticBody.AddChild(_collisionShape);

        // Make children visible in editor scene tree
        if (Engine.IsEditorHint())
        {
            var root = GetTree().EditedSceneRoot;
            _meshInstance.Owner = root;
            _staticBody.Owner = root;
            _collisionShape.Owner = root;
        }
    }

    /// <summary>
    /// Returns true if this chunk contains only Air blocks (no geometry needed).
    /// </summary>
    public bool IsEmpty() => _isEmpty;

    /// <summary>
    /// Returns the block type at local coordinates. Returns Air for out-of-bounds.
    /// </summary>
    public BlockType GetBlock(int localX, int localY, int localZ)
    {
        if (localX < 0 || localX >= SIZE || localY < 0 || localY >= SIZE || localZ < 0 || localZ >= SIZE)
            return BlockType.Air;
        return _blocks[localX, localY, localZ];
    }

    public void SetBlock(int localX, int localY, int localZ, BlockType type)
    {
        if (localX < 0 || localX >= SIZE || localY < 0 || localY >= SIZE || localZ < 0 || localZ >= SIZE)
            return;
        _blocks[localX, localY, localZ] = type;
    }

    /// <summary>
    /// Bulk-set the entire block array (used during terrain generation).
    /// Also updates the _isEmpty cache.
    /// </summary>
    public void SetBlockData(BlockType[,,] blocks)
    {
        _blocks = blocks;
        _isEmpty = !HasAnyNonAirBlocks();
    }

    /// <summary>
    /// Rebuild the mesh. getNeighborBlock handles coords outside 0..15.
    /// Skips mesh generation entirely for empty chunks (all air).
    /// </summary>
    public void GenerateMesh(Func<int, int, int, BlockType> getNeighborBlock)
    {
        // Skip empty chunks — no mesh, no collision needed
        if (_isEmpty)
        {
            _meshInstance.Mesh = null;
            _collisionShape.Shape = null;
            return;
        }

        var mesh = ChunkMeshGenerator.GenerateMesh(_blocks, getNeighborBlock);
        _meshInstance.Mesh = mesh;

        // Build collision
        var collisionFaces = ChunkMeshGenerator.GenerateCollisionFaces(_blocks, getNeighborBlock);
        if (collisionFaces.Length > 0)
        {
            var shape = new ConcavePolygonShape3D();
            shape.SetFaces(collisionFaces);
            _collisionShape.Shape = shape;
        }
        else
        {
            _collisionShape.Shape = null;
        }
    }

    /// <summary>
    /// Fill with test terrain: flat at y=4, grass/dirt/stone layers, 3x3 pit.
    /// </summary>
    public void FillTestData()
    {
        for (int x = 0; x < SIZE; x++)
        for (int z = 0; z < SIZE; z++)
        {
            for (int y = 0; y <= 4 && y < SIZE; y++)
            {
                if (y == 4)
                    _blocks[x, y, z] = BlockType.Grass;
                else if (y >= 2)
                    _blocks[x, y, z] = BlockType.Dirt;
                else
                    _blocks[x, y, z] = BlockType.Stone;
            }

            // Cut a 3x3 pit from y=2 to y=4 near center
            if (x >= 7 && x <= 9 && z >= 7 && z <= 9)
            {
                _blocks[x, 2, z] = BlockType.Air;
                _blocks[x, 3, z] = BlockType.Air;
                _blocks[x, 4, z] = BlockType.Air;
            }
        }
        _isEmpty = false;
    }

    public int CountSolidBlocks()
    {
        int count = 0;
        for (int x = 0; x < SIZE; x++)
        for (int y = 0; y < SIZE; y++)
        for (int z = 0; z < SIZE; z++)
        {
            if (BlockData.IsSolid(_blocks[x, y, z]))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Returns true if any block in this chunk is not Air.
    /// Used to determine if mesh generation can be skipped.
    /// </summary>
    private bool HasAnyNonAirBlocks()
    {
        for (int x = 0; x < SIZE; x++)
        for (int y = 0; y < SIZE; y++)
        for (int z = 0; z < SIZE; z++)
        {
            if (_blocks[x, y, z] != BlockType.Air) return true;
        }
        return false;
    }
}
