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
    /// Number of vertical chunk layers. layers=12 → 192 blocks tall.
    /// </summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int ChunkYLayers { get; set; } = 12;

    /// <summary>
    /// Seed for terrain generation. Different seeds produce different worlds.
    /// </summary>
    [Export(PropertyHint.Range, "1,99999,1")]
    public int TerrainSeed { get; set; } = 42;

    private World _world;
    private CameraController _cameraController;
    private FreeFlyCamera _freeFlyCamera;
    private bool _freeFlyActive;
    private Colonist _colonist;
    private bool _colonistPhysicsEnabled;
    private Vector2I _spawnChunkXZ;
    private int _spawnSurfaceHeight;
    private bool _f3WasPressed;
    private bool _f4WasPressed;
    private bool _analyzerRunning;

    // Dynamic lighting
    private DirectionalLight3D _sunLight;
    private ProceduralSkyMaterial _skyMaterial;
    private Environment _environment;
    private DayNightCycle _dayNightCycle;

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

            // Free-fly camera for cave exploration (toggled via F4)
            _freeFlyCamera = new FreeFlyCamera();
            _freeFlyCamera.Name = "FreeFlyCamera";
            _freeFlyCamera.Position = _cameraController.Position;
            AddChild(_freeFlyCamera);
            _freeFlyCamera.SetMaxWorldHeight(ChunkYLayers * Chunk.SIZE);

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

            // Set up dynamic sun light and sky
            SetupDynamicLighting();
        }
    }

    /// <summary>
    /// Create the sun DirectionalLight3D, procedural sky, and environment settings.
    /// Then hand off to DayNightCycle which animates everything over time.
    /// </summary>
    private void SetupDynamicLighting()
    {
        // Increase soft shadow filter quality (PCF samples) for smoother shadow edges.
        // Default SoftLow produces visible stair-stepping on voxel geometry.
        RenderingServer.DirectionalSoftShadowFilterSetQuality(
            RenderingServer.ShadowQuality.SoftMedium);

        // --- Sun light ---
        _sunLight = new DirectionalLight3D();
        _sunLight.Name = "SunLight";
        AddChild(_sunLight);

        // Sun color and energy will be set by DayNightCycle each frame.
        // Configure shadow properties here (these don't change with time of day).
        _sunLight.ShadowEnabled = true;
        _sunLight.ShadowBias = 0.05f;
        _sunLight.ShadowNormalBias = 2.0f;
        _sunLight.ShadowBlur = 1.5f;  // softer shadow edges via PCF blur
        _sunLight.ShadowOpacity = 1.0f;
        _sunLight.DirectionalShadowMaxDistance = 100f;  // was 200 — halved for 2x shadow resolution
        _sunLight.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        _sunLight.DirectionalShadowBlendSplits = true;  // smooth cascade transitions
        _sunLight.DirectionalShadowSplit1 = 0.05f;  // front-load resolution near camera
        _sunLight.DirectionalShadowSplit2 = 0.15f;
        _sunLight.DirectionalShadowSplit3 = 0.40f;
        // PCSS (LightAngularDistance) disabled — known Godot bug #86536 causes shadow
        // degradation far from world origin. Use ShadowBlur + PCF filter quality instead.

        // --- Sky and Environment ---
        var worldEnv = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        if (worldEnv != null)
        {
            _environment = worldEnv.Environment;

            // Create a procedural sky — colors will be animated by DayNightCycle
            _skyMaterial = new ProceduralSkyMaterial();
            _skyMaterial.SunAngleMax = 30f;   // sun/moon disc visible size
            _skyMaterial.SunCurve = 0.15f;    // sun glow falloff

            var sky = new Sky();
            sky.SkyMaterial = _skyMaterial;
            sky.RadianceSize = Sky.RadianceSizeEnum.Size256;

            _environment.Sky = sky;
            _environment.BackgroundMode = Environment.BGMode.Sky;

            // Ambient light source: custom color (not sky-derived, avoids blue tint).
            // Color and energy will be animated by DayNightCycle.
            _environment.AmbientLightSource = Environment.AmbientSource.Color;

            // Reflected light from sky (only affects reflective surfaces like water)
            _environment.ReflectedLightSource = Environment.ReflectionSource.Sky;

            // Tone mapping for natural outdoor look
            _environment.TonemapMode = Environment.ToneMapper.Filmic;
            _environment.TonemapWhite = 6.0f;
        }

        // --- Day/Night Cycle ---
        _dayNightCycle = new DayNightCycle();
        _dayNightCycle.Name = "DayNightCycle";
        AddChild(_dayNightCycle);
        _dayNightCycle.Initialize(_sunLight, _skyMaterial, _environment);

        GD.Print("Dynamic lighting: sun, moon, and day/night cycle initialized");
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() || _cameraController == null || _world == null) return;

        // F4: Toggle free-fly camera (debounced)
        bool f4Now = Input.IsKeyPressed(Key.F4);
        if (f4Now && !_f4WasPressed)
        {
            ToggleFreeFly();
        }
        _f4WasPressed = f4Now;

        // Use the active camera's position for chunk streaming
        var camPos = _freeFlyActive ? _freeFlyCamera.Position : _cameraController.Position;
        int chunkX = Mathf.FloorToInt(camPos.X / Chunk.SIZE);
        int chunkZ = Mathf.FloorToInt(camPos.Z / Chunk.SIZE);
        _world.UpdateLoadedChunks(new Vector2I(chunkX, chunkZ), ChunkRenderDistance);

        // Wait for spawn-area chunks to load before enabling colonist physics
        if (!_colonistPhysicsEnabled && _colonist != null)
        {
            CheckSpawnChunksReady();
        }

        // F3: Run world gen analyzer (debounced, runs on background thread)
        bool f3Now = Input.IsKeyPressed(Key.F3);
        if (f3Now && !_f3WasPressed && !_analyzerRunning)
        {
            _analyzerRunning = true;
            int seed = TerrainSeed;
            int yLayers = ChunkYLayers;
            int centerX = Mathf.FloorToInt(camPos.X);
            int centerZ = Mathf.FloorToInt(camPos.Z);
            GD.Print($"F3: Launching WorldGenAnalyzer at ({centerX}, {centerZ})...");

            // Run on background thread to avoid freezing the game
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    WorldGenAnalyzer.Analyze(centerX, centerZ, seed, yLayers);
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"WorldGenAnalyzer error: {ex.Message}");
                }
                finally
                {
                    _analyzerRunning = false;
                }
            });
        }
        _f3WasPressed = f3Now;
    }

    /// <summary>
    /// Toggle between RTS camera and free-fly noclip camera.
    /// Free-fly starts at the RTS camera's current position.
    /// </summary>
    private void ToggleFreeFly()
    {
        if (_freeFlyCamera == null) return;

        _freeFlyActive = !_freeFlyActive;

        if (_freeFlyActive)
        {
            // Switch to free-fly: position at RTS camera's look target
            _freeFlyCamera.Position = _cameraController.Position;
            _freeFlyCamera.Activate();
            GD.Print("Camera: switched to FREE-FLY (F4 to return, WASD+mouse, Shift=fast, Space/Ctrl=up/down, scroll=speed)");
        }
        else
        {
            // Switch back to RTS
            _freeFlyCamera.Deactivate();
            _cameraController.Camera.MakeCurrent();
            GD.Print("Camera: switched to RTS");
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
    /// that is NOT in a river channel. Prefers elevated positions (WaterLevel + 5)
    /// but falls back to any dry non-river position. Returns the (X, Z) world block coordinate.
    /// </summary>
    private Vector2I FindDryLandNear(int centerX, int centerZ)
    {
        const int minElevation = TerrainGenerator.WaterLevel + 5;
        Vector2I? fallback = null;

        // Check center first
        int h = _world.GetSurfaceHeight(centerX, centerZ);
        if (h >= minElevation && !_world.IsRiverAt(centerX, centerZ))
            return new Vector2I(centerX, centerZ);
        if (h > TerrainGenerator.WaterLevel && !_world.IsRiverAt(centerX, centerZ))
            fallback = new Vector2I(centerX, centerZ);

        // Expand in rings up to 64 blocks out
        for (int radius = 1; radius <= 64; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int[] dzOptions = (dx == -radius || dx == radius)
                    ? GenerateRange(-radius, radius)
                    : new[] { -radius, radius };

                foreach (int dz in dzOptions)
                {
                    int x = centerX + dx;
                    int z = centerZ + dz;
                    h = _world.GetSurfaceHeight(x, z);

                    if (h <= TerrainGenerator.WaterLevel) continue;
                    if (_world.IsRiverAt(x, z)) continue;

                    if (h >= minElevation)
                        return new Vector2I(x, z);

                    fallback ??= new Vector2I(x, z);
                }
            }
        }

        if (fallback.HasValue)
        {
            GD.Print($"Spawn: using fallback dry land at {fallback.Value} (no elevated non-river position within 64 blocks)");
            return fallback.Value;
        }

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
