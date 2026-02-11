namespace ColonySim;

using Godot;

public partial class Main : Node3D
{
    public override void _Ready()
    {
        GD.Print("=== ColonySim Starting ===");

        // Point camera at the test chunk center
        var camera = GetNode<Camera3D>("Camera3D");
        camera.LookAt(new Vector3(8, 2, 8), Vector3.Up);

        SetupTestChunk();
        SpawnTestBalls();
    }

    private void SetupTestChunk()
    {
        var chunk = new Chunk();
        AddChild(chunk);
        chunk.Initialize(Vector3I.Zero);
        chunk.FillTestData();
        chunk.GenerateMesh((lx, ly, lz) => BlockType.Air);
        GD.Print("Test chunk created and meshed.");
    }

    private void SpawnTestBalls()
    {
        // Ball 1: over solid terrain, should land on surface
        SpawnBall(new Vector3(4, 12, 4), "Ball_Terrain");
        // Ball 2: over the 3x3 pit, should fall into the pit
        SpawnBall(new Vector3(8, 12, 8), "Ball_Pit");
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
