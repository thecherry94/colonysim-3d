namespace ColonySim;

using Godot;

[Tool]
public partial class Main : Node3D
{
    private World _world;

    public override void _Ready()
    {
        GD.Print("=== ColonySim Starting ===");

        SetupWorld();

        if (!Engine.IsEditorHint())
        {
            // Runtime only: reposition camera and spawn test balls
            var camera = GetNode<Camera3D>("Camera3D");
            camera.Position = new Vector3(8, 30, 45);
            camera.LookAt(new Vector3(8, 4, 8), Vector3.Up);

            // Block interaction (left-click to remove)
            var blockInteraction = new BlockInteraction();
            blockInteraction.Name = "BlockInteraction";
            AddChild(blockInteraction);
            blockInteraction.Initialize(camera, _world);

            SpawnTestBalls();
        }
    }

    private void SetupWorld()
    {
        _world = new World();
        _world.Name = "World";
        AddChild(_world);

        // Load a 3x3 grid of chunks centered at origin
        _world.LoadChunkArea(Vector3I.Zero, 1);
        GD.Print("World initialized: 3x3 chunk grid (48x48 blocks)");
    }

    private void SpawnTestBalls()
    {
        // Ball 1: over chunk (0,0,0), should land on terrain
        SpawnBall(new Vector3(8, 20, 8), "Ball_Center");
        // Ball 2: over chunk (-1,0,-1), tests negative coordinates
        SpawnBall(new Vector3(-8, 20, -8), "Ball_Negative");
        // Ball 3: over chunk (1,0,1), tests positive multi-chunk
        SpawnBall(new Vector3(20, 20, 20), "Ball_Positive");
    }

    private void SpawnBall(Vector3 position, string name)
    {
        var ball = new RigidBody3D();
        ball.Name = name;

        var collider = new CollisionShape3D();
        var sphereShape = new SphereShape3D();
        sphereShape.Radius = 0.4f;
        collider.Shape = sphereShape;
        ball.AddChild(collider);

        var meshInst = new MeshInstance3D();
        var sphereMesh = new SphereMesh();
        sphereMesh.Radius = 0.4f;
        sphereMesh.Height = 0.8f;
        meshInst.Mesh = sphereMesh;
        ball.AddChild(meshInst);

        ball.Position = position;
        AddChild(ball);
        GD.Print($"Test ball '{name}' spawned at {position}");
    }
}
