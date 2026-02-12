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

            // Find a valid spawn position: dry land near world center
            float worldCenterX = ChunkRenderDistance * Chunk.SIZE + Chunk.SIZE / 2.0f;
            float worldCenterZ = ChunkRenderDistance * Chunk.SIZE + Chunk.SIZE / 2.0f;
            var spawnXZ = FindDryLandNear((int)worldCenterX, (int)worldCenterZ);
            int surfaceHeight = _world.GetSurfaceHeight(spawnXZ.X, spawnXZ.Y);

            // RTS camera: pivot at spawn area, above terrain
            var cameraController = new CameraController();
            cameraController.Name = "CameraController";
            cameraController.Position = new Vector3(spawnXZ.X + 0.5f, surfaceHeight + 2, spawnXZ.Y + 0.5f);
            AddChild(cameraController);
            var camera = cameraController.Camera;

            // Spawn colonist on the surface (surfaceHeight + 1 = standing on top of surface block)
            var spawnPos = new Vector3(spawnXZ.X + 0.5f, surfaceHeight + 1.5f, spawnXZ.Y + 0.5f);
            var pathfinder = new VoxelPathfinder(_world);
            var colonist = new Colonist();
            colonist.Name = "Colonist";
            colonist.Position = spawnPos;
            AddChild(colonist);
            colonist.Initialize(_world, pathfinder, spawnPos);

            GD.Print($"Colonist spawned at {spawnPos}, surface height={surfaceHeight}");

            // Block interaction: left-click remove, right-click command colonist
            var blockInteraction = new BlockInteraction();
            blockInteraction.Name = "BlockInteraction";
            AddChild(blockInteraction);
            blockInteraction.Initialize(camera, _world, colonist);

            // Increase shadow distance for taller terrain
            var light = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
            if (light != null)
                light.DirectionalShadowMaxDistance = 250;
        }
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

        if (Engine.IsEditorHint())
            _world.Owner = GetTree().EditedSceneRoot;

        // Create terrain generator with the configured seed and pass to world
        var terrainGen = new TerrainGenerator(TerrainSeed);
        _world.SetTerrainGenerator(terrainGen);
        _world.SetYChunkLayers(ChunkYLayers);

        // Load chunk grid: center chunk = (distance, 0, distance) so world starts at (0,0,0)
        var center = new Vector3I(ChunkRenderDistance, 0, ChunkRenderDistance);
        _world.LoadChunkArea(center, ChunkRenderDistance);

        int gridSize = 2 * ChunkRenderDistance + 1;
        int blockSpan = gridSize * Chunk.SIZE;
        int worldHeight = ChunkYLayers * Chunk.SIZE;
        GD.Print($"World ready: {gridSize}x{gridSize} chunks, {ChunkYLayers} Y layers ({blockSpan}x{worldHeight}x{blockSpan} blocks)");
    }
}
