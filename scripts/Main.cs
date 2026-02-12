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

            // Compute world center position from chunk grid
            float worldCenterX = ChunkRenderDistance * Chunk.SIZE + Chunk.SIZE / 2.0f;
            float worldCenterZ = ChunkRenderDistance * Chunk.SIZE + Chunk.SIZE / 2.0f;
            int surfaceHeight = _world.GetSurfaceHeight(
                (int)worldCenterX, (int)worldCenterZ);

            // RTS camera: pivot centered on world, above terrain
            var cameraController = new CameraController();
            cameraController.Name = "CameraController";
            cameraController.Position = new Vector3(worldCenterX, surfaceHeight + 2, worldCenterZ);
            AddChild(cameraController);
            var camera = cameraController.Camera;

            // Spawn colonist at world center, above terrain
            var spawnPos = new Vector3(worldCenterX, surfaceHeight + 3, worldCenterZ);
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
