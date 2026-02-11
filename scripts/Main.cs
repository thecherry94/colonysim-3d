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
}
