namespace ColonySim;

using Godot;

[Tool]
public partial class Main : Node3D
{
    /// <summary>
    /// Chunk render distance from center. distance=5 → 11×11 grid.
    /// </summary>
    [Export(PropertyHint.Range, "1,30,1")]
    public int ChunkRenderDistance { get; set; } = 5;

    /// <summary>
    /// Number of vertical chunk layers. layers=4 → 64 blocks tall.
    /// </summary>
    [Export(PropertyHint.Range, "1,8,1")]
    public int ChunkYLayers { get; set; } = 4;

    /// <summary>
    /// Seed for terrain generation. Different seeds produce different worlds.
    /// </summary>
    [Export(PropertyHint.Range, "1,99999,1")]
    public int TerrainSeed { get; set; } = 42;

    private World _world;
    private CameraController _cameraController;
    private Colonist _colonist;
    private bool _colonistPhysicsEnabled;
    private Vector2I _spawnChunkXZ;
    private int _spawnSurfaceHeight;

    public override void _Ready()
    {
        GD.Print($"=== ColonySim Starting (distance={ChunkRenderDistance}, yLayers={ChunkYLayers}, seed={TerrainSeed}) ===");

        SetupWorld();

        if (!Engine.IsEditorHint())
        {
            // Disable the editor camera — RTS camera takes over
            var editorCamera = GetNodeOrNull<Camera3D>("Camera3D");
            if (editorCamera != null)
                editorCamera.QueueFree();

            // Find a valid spawn position: dry land near world origin
            var spawnXZ = FindDryLandNear(0, 0);
            _spawnSurfaceHeight = _world.GetSurfaceHeight(spawnXZ.X, spawnXZ.Y);
            _spawnChunkXZ = new Vector2I(
                Mathf.FloorToInt((float)spawnXZ.X / Chunk.SIZE),
                Mathf.FloorToInt((float)spawnXZ.Y / Chunk.SIZE)
            );

            // RTS camera: pivot at spawn area, above terrain
            _cameraController = new CameraController();
            _cameraController.Name = "CameraController";
            _cameraController.Position = new Vector3(spawnXZ.X + 0.5f, _spawnSurfaceHeight + 2, spawnXZ.Y + 0.5f);
            AddChild(_cameraController);
            _cameraController.SetMaxWorldHeight(ChunkYLayers * Chunk.SIZE);
            var camera = _cameraController.Camera;

            // Spawn colonist on the surface (physics frozen until chunks load)
            var spawnPos = new Vector3(spawnXZ.X + 0.5f, _spawnSurfaceHeight + 1.5f, spawnXZ.Y + 0.5f);
            var pathfinder = new VoxelPathfinder(_world);
            _colonist = new Colonist();
            _colonist.Name = "Colonist";
            _colonist.Position = spawnPos;
            AddChild(_colonist);
            _colonist.Initialize(_world, pathfinder, spawnPos);

            GD.Print($"Colonist placed at {spawnPos} (physics frozen until chunks load), surface height={_spawnSurfaceHeight}");

            // Block interaction: left-click remove, right-click command colonist
            var blockInteraction = new BlockInteraction();
            blockInteraction.Name = "BlockInteraction";
            AddChild(blockInteraction);
            blockInteraction.Initialize(camera, _world, _colonist, ChunkRenderDistance);

            // Increase shadow distance for taller terrain
            var light = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
            if (light != null)
                light.DirectionalShadowMaxDistance = 120;
        }
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() || _cameraController == null || _world == null) return;

        // Convert camera world position to chunk XZ coordinate
        var camPos = _cameraController.Position;
        int chunkX = Mathf.FloorToInt(camPos.X / Chunk.SIZE);
        int chunkZ = Mathf.FloorToInt(camPos.Z / Chunk.SIZE);
        _world.UpdateLoadedChunks(new Vector2I(chunkX, chunkZ), ChunkRenderDistance);

        // Wait for spawn-area chunks to load before enabling colonist physics
        if (!_colonistPhysicsEnabled && _colonist != null)
        {
            CheckSpawnChunksReady();
        }
    }

    /// <summary>
    /// Check if the chunks around the spawn position have loaded and have meshes/collision.
    /// When ready, enable colonist physics and correct spawn height using actual block data.
    /// </summary>
    private void CheckSpawnChunksReady()
    {
        // Check the spawn chunk and its immediate Y column neighbors
        int chunkX = _spawnChunkXZ.X;
        int chunkZ = _spawnChunkXZ.Y;
        int spawnChunkY = Mathf.FloorToInt((float)_spawnSurfaceHeight / Chunk.SIZE);

        // Need at least the chunk containing the spawn Y level to be ready
        var spawnChunk = new Vector3I(chunkX, spawnChunkY, chunkZ);
        if (!_world.IsChunkReady(spawnChunk)) return;

        // Also check the chunk below (colonist might be near a chunk Y boundary)
        if (spawnChunkY > 0)
        {
            var belowChunk = new Vector3I(chunkX, spawnChunkY - 1, chunkZ);
            if (!_world.IsChunkReady(belowChunk)) return;
        }

        // Chunks are ready — find actual surface using real block data (accounts for caves)
        var spawnXZ = new Vector2I(
            Mathf.FloorToInt(_colonist.Position.X),
            Mathf.FloorToInt(_colonist.Position.Z)
        );
        int actualSurface = FindActualSurface(spawnXZ.X, spawnXZ.Y, _spawnSurfaceHeight + 10);

        // Correct colonist position to actual surface
        var correctedPos = new Vector3(spawnXZ.X + 0.5f, actualSurface + 1.5f, spawnXZ.Y + 0.5f);
        _colonist.Position = correctedPos;
        _colonist.SetSpawnPosition(correctedPos); // Update void-safety teleport target

        // Enable physics and mark as done
        _colonist.EnablePhysics();
        _colonistPhysicsEnabled = true;

        GD.Print($"Colonist spawn finalized at {correctedPos} (actual surface={actualSurface}, noise height={_spawnSurfaceHeight})");
    }

    /// <summary>
    /// Scan downward through actual block data to find the highest solid block at (x, z).
    /// Handles caves that may have carved away the noise-predicted surface.
    /// </summary>
    private int FindActualSurface(int worldX, int worldZ, int startY)
    {
        for (int y = startY; y >= 0; y--)
        {
            var block = _world.GetBlock(new Vector3I(worldX, y, worldZ));
            if (BlockData.IsSolid(block))
                return y;
        }
        // Fallback: use noise height if no solid block found (shouldn't happen)
        return _spawnSurfaceHeight;
    }

    /// <summary>
    /// Search in expanding squares from the center to find a position on dry land
    /// (surface height above water level). Returns the (X, Z) world block coordinate.
    /// </summary>
    private Vector2I FindDryLandNear(int centerX, int centerZ)
    {
        // Check center first
        int h = _world.GetSurfaceHeight(centerX, centerZ);
        if (h > TerrainGenerator.WaterLevel)
            return new Vector2I(centerX, centerZ);

        // Expand in rings up to 64 blocks out
        for (int radius = 1; radius <= 64; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                // Check the 4 edges of the square ring
                int[] dzOptions = (dx == -radius || dx == radius)
                    ? GenerateRange(-radius, radius)
                    : new[] { -radius, radius };

                foreach (int dz in dzOptions)
                {
                    h = _world.GetSurfaceHeight(centerX + dx, centerZ + dz);
                    if (h > TerrainGenerator.WaterLevel)
                        return new Vector2I(centerX + dx, centerZ + dz);
                }
            }
        }

        // Fallback: just use center
        GD.Print("WARNING: No dry land found near spawn, using center");
        return new Vector2I(centerX, centerZ);
    }

    private static int[] GenerateRange(int from, int to)
    {
        var result = new int[to - from + 1];
        for (int i = 0; i < result.Length; i++)
            result[i] = from + i;
        return result;
    }

    private void SetupWorld()
    {
        // In editor, remove any previously-created World to avoid duplicates on re-run
        if (Engine.IsEditorHint())
        {
            var existing = GetNodeOrNull<World>("World");
            if (existing != null)
            {
                RemoveChild(existing);
                existing.QueueFree();
            }
        }

        _world = new World();
        _world.Name = "World";
        AddChild(_world);

        // IMPORTANT: Do NOT set _world.Owner = EditedSceneRoot here.
        // Setting Owner causes Godot to serialize ALL runtime-generated chunks into main.tscn,
        // bloating it to 50+ MB and creating overlapping collision shapes that make
        // CharacterBody3D.MoveAndSlide() a complete no-op (lesson 5.3).

        // Create terrain generator with the configured seed and pass to world
        var terrainGen = new TerrainGenerator(TerrainSeed);
        _world.SetTerrainGenerator(terrainGen);
        _world.SetYChunkLayers(ChunkYLayers);

        // NOTE: Editor preview (LoadChunkArea) is intentionally disabled.
        // It caused main.tscn to bloat to 50+ MB, breaking CharacterBody3D physics.
        // The [Tool] attribute is kept only for [Export] property editing in the inspector.
        // To preview terrain, run the game instead.

        int gridSize = 2 * ChunkRenderDistance + 1;
        int blockSpan = gridSize * Chunk.SIZE;
        int worldHeight = ChunkYLayers * Chunk.SIZE;
        GD.Print($"World ready: streaming {gridSize}x{gridSize} chunks, {ChunkYLayers} Y layers ({blockSpan}x{worldHeight}x{blockSpan} blocks)");
    }
}
