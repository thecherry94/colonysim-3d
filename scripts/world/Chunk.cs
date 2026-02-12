namespace ColonySim;

using System;
using Godot;

/// <summary>
/// A 16x16x16 block chunk. Manages block storage, mesh rendering.
/// Collision is optional — only created for chunks near the camera (performance optimization).
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
    private bool _hasCollision;

    // Cached collision faces for deferred EnableCollision() calls.
    // Stored when mesh is generated without collision, applied when collision is enabled later.
    private Vector3[] _lastCollisionFaces;

    public Vector3I ChunkCoord { get; private set; }
    public bool IsDirty { get; private set; }
    public bool HasCollision => _hasCollision;
    public bool HasMesh { get; private set; }

    /// <summary>
    /// Initialize the chunk. withCollision=false skips creating StaticBody3D + CollisionShape3D
    /// entirely, saving 2 scene nodes per chunk. Distant chunks don't need collision.
    /// </summary>
    public void Initialize(Vector3I chunkCoord, bool withCollision = true)
    {
        ChunkCoord = chunkCoord;
        Position = new Vector3(chunkCoord.X * SIZE, chunkCoord.Y * SIZE, chunkCoord.Z * SIZE);
        Name = $"Chunk_{chunkCoord.X}_{chunkCoord.Y}_{chunkCoord.Z}";

        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "MeshInstance";
        AddChild(_meshInstance);

        _hasCollision = withCollision;
        if (withCollision)
        {
            CreateCollisionNodes();
        }

        // IMPORTANT: Do NOT set Owner = EditedSceneRoot on these children.
        // Setting Owner causes Godot to serialize ALL chunk nodes into main.tscn,
        // bloating the scene file and breaking CharacterBody3D physics (lesson 5.3).
    }

    /// <summary>
    /// Enable collision on this chunk (creates StaticBody3D + CollisionShape3D).
    /// If mesh data has been applied, the cached collision faces are used immediately.
    /// </summary>
    public void EnableCollision()
    {
        if (_hasCollision) return;
        _hasCollision = true;

        CreateCollisionNodes();

        // Apply cached collision faces if we have them
        if (_lastCollisionFaces != null && _lastCollisionFaces.Length > 0)
        {
            var shape = new ConcavePolygonShape3D();
            shape.SetFaces(_lastCollisionFaces);
            _collisionShape.Shape = shape;
        }
    }

    /// <summary>
    /// Disable collision on this chunk (removes and frees StaticBody3D + CollisionShape3D).
    /// Saves 2 scene nodes and removes the chunk from the physics broadphase.
    /// </summary>
    public void DisableCollision()
    {
        if (!_hasCollision) return;
        _hasCollision = false;

        if (_staticBody != null)
        {
            RemoveChild(_staticBody);
            _staticBody.QueueFree();
            _staticBody = null;
            _collisionShape = null;
        }
    }

    private void CreateCollisionNodes()
    {
        _staticBody = new StaticBody3D();
        _staticBody.Name = "StaticBody";
        AddChild(_staticBody);
        _collisionShape = new CollisionShape3D();
        _collisionShape.Name = "CollisionShape";
        _staticBody.AddChild(_collisionShape);
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
        IsDirty = true;
    }

    /// <summary>
    /// Returns a copy of the block data array. Used to cache dirty chunks on unload
    /// (copy needed because blocks may be modified later).
    /// </summary>
    public BlockType[,,] GetBlockData()
    {
        var copy = new BlockType[SIZE, SIZE, SIZE];
        Array.Copy(_blocks, copy, _blocks.Length);
        return copy;
    }

    /// <summary>
    /// Returns a direct reference to the block data array (no copy).
    /// Used to cache clean (non-dirty) chunks on unload — avoids allocation.
    /// Safe because clean chunks won't be modified after unload.
    /// </summary>
    public BlockType[,,] GetBlockDataRef()
    {
        return _blocks;
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
    /// Uses single GenerateMeshData() call for both render + collision (no double work).
    /// </summary>
    public void GenerateMesh(Func<int, int, int, BlockType> getNeighborBlock)
    {
        // Skip empty chunks — no mesh, no collision needed
        if (_isEmpty)
        {
            _meshInstance.Mesh = null;
            if (_hasCollision && _collisionShape != null)
                _collisionShape.Shape = null;
            _lastCollisionFaces = null;
            return;
        }

        // Single call generates both render surfaces and collision faces
        var meshData = ChunkMeshGenerator.GenerateMeshData(_blocks, getNeighborBlock);
        ApplyMeshData(meshData);
    }

    /// <summary>
    /// Apply pre-computed mesh data to the Godot scene objects. MUST be called on the main thread.
    /// Used both by GenerateMesh() (synchronous) and the background pipeline (deferred apply).
    /// Collision faces are cached so EnableCollision() can apply them later.
    /// </summary>
    public void ApplyMeshData(ChunkMeshGenerator.ChunkMeshData meshData)
    {
        if (meshData.IsEmpty || meshData.Surfaces.Length == 0)
        {
            _meshInstance.Mesh = null;
            if (_hasCollision && _collisionShape != null)
                _collisionShape.Shape = null;
            _lastCollisionFaces = null;
            HasMesh = true;
            return;
        }

        _meshInstance.Mesh = ChunkMeshGenerator.BuildArrayMesh(meshData.Surfaces);

        // Always cache collision faces (cheap — just a reference)
        _lastCollisionFaces = meshData.CollisionFaces;

        // Only create collision shape if collision is enabled for this chunk
        if (_hasCollision && _collisionShape != null)
        {
            if (meshData.CollisionFaces.Length > 0)
            {
                var shape = new ConcavePolygonShape3D();
                shape.SetFaces(meshData.CollisionFaces);
                _collisionShape.Shape = shape;
            }
            else
            {
                _collisionShape.Shape = null;
            }
        }

        HasMesh = true;
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
