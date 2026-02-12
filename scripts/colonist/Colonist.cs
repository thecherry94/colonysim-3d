namespace ColonySim;

using System.Collections.Generic;
using Godot;

/// <summary>
/// Autonomous colonist with state machine movement on CharacterBody3D.
/// States: Idle, Walking, JumpingUp, Falling.
/// </summary>
public partial class Colonist : CharacterBody3D
{
    private enum State { Idle, Walking, JumpingUp, Falling }

    private const float MoveSpeed = 4.0f;
    private const float JumpVelocity = 6.0f;
    private const float Gravity = 20.0f;
    private const float WaypointReachDist = 0.3f;
    private const float StuckTimeout = 2.0f;
    private const float JumpGraceTime = 0.15f;

    private State _state = State.Idle;
    private List<VoxelNode> _waypoints;
    private int _waypointIndex;
    private VoxelPathfinder _pathfinder;
    private World _world;

    private float _stuckTimer;
    private Vector3 _lastProgressPos;
    private float _jumpGraceTimer;
    private Vector3 _spawnPosition = new(8, 15, 8);
    private bool _physicsReady;

    // Path visualization
    private MeshInstance3D _pathMeshInstance;
    private ImmediateMesh _pathMesh;
    private bool _showPath = true;

    public void Initialize(World world, VoxelPathfinder pathfinder, Vector3 spawnPosition)
    {
        _world = world;
        _pathfinder = pathfinder;
        _spawnPosition = spawnPosition;
    }

    /// <summary>
    /// Update the void-safety teleport position after correcting for caves.
    /// </summary>
    public void SetSpawnPosition(Vector3 spawnPosition)
    {
        _spawnPosition = spawnPosition;
    }

    /// <summary>
    /// Enable physics processing. Called by Main after chunks around the spawn area
    /// have loaded, ensuring collision shapes exist before gravity is applied.
    /// </summary>
    public void EnablePhysics()
    {
        _physicsReady = true;
        GD.Print($"Colonist: physics enabled at {Position}");
    }

    public override void _Ready()
    {
        // Build visual capsule mesh
        var meshInst = new MeshInstance3D();
        var capsuleMesh = new CapsuleMesh();
        capsuleMesh.Radius = 0.3f;
        capsuleMesh.Height = 1.6f;
        meshInst.Mesh = capsuleMesh;
        meshInst.Position = new Vector3(0, 0.8f, 0); // Center capsule mesh on body
        var material = new StandardMaterial3D();
        material.AlbedoColor = new Color(0.2f, 0.4f, 0.9f); // Blue colonist
        capsuleMesh.Material = material;
        AddChild(meshInst);

        // Build collision capsule
        var collider = new CollisionShape3D();
        var capsuleShape = new CapsuleShape3D();
        capsuleShape.Radius = 0.3f;
        capsuleShape.Height = 1.6f;
        collider.Shape = capsuleShape;
        collider.Position = new Vector3(0, 0.8f, 0);
        AddChild(collider);

        // Path visualization (added to scene root so it draws in world space)
        _pathMesh = new ImmediateMesh();
        _pathMeshInstance = new MeshInstance3D();
        _pathMeshInstance.Mesh = _pathMesh;
        _pathMeshInstance.Name = "PathVisualization";
        // Use a bright material that's always visible
        var pathMat = new StandardMaterial3D();
        pathMat.AlbedoColor = new Color(1.0f, 0.2f, 0.2f); // Red path
        pathMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        pathMat.NoDepthTest = true; // Draw on top of everything
        _pathMeshInstance.MaterialOverride = pathMat;

        // Add path visualization to scene root (world space, not colonist-local)
        GetTree().Root.CallDeferred(Node.MethodName.AddChild, _pathMeshInstance);

        // Capsule mesh + shape created in _Ready
    }

    public override void _ExitTree()
    {
        if (_pathMeshInstance != null && _pathMeshInstance.IsInsideTree())
        {
            _pathMeshInstance.GetParent().RemoveChild(_pathMeshInstance);
            _pathMeshInstance.QueueFree();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            if (keyEvent.Keycode == Key.F1)
            {
                _showPath = !_showPath;
                GD.Print($"Path visualization: {(_showPath ? "ON" : "OFF")}");
                if (!_showPath)
                    ClearPathVisualization();
                else
                    RebuildPathVisualization();
            }
            else if (keyEvent.Keycode == Key.F2 && _pathfinder != null)
            {
                _pathfinder.AllowDiagonals = !_pathfinder.AllowDiagonals;
                GD.Print($"Diagonal movement: {(_pathfinder.AllowDiagonals ? "ON" : "OFF")}");
            }
        }
    }

    /// <summary>
    /// Request the colonist to walk to a world position.
    /// Runs pathfinding and starts following waypoints.
    /// </summary>
    public void SetDestination(Vector3 targetWorldPos)
    {
        if (_pathfinder == null || _world == null) return;

        var startNode = _pathfinder.WorldPosToVoxelNode(Position);
        var goalNode = _pathfinder.WorldPosToVoxelNode(targetWorldPos);

        if (startNode == null)
        {
            GD.Print($"Colonist: cannot find ground under current position {Position}");
            return;
        }
        if (goalNode == null)
        {
            GD.Print($"Colonist: cannot find ground under target {targetWorldPos}");
            return;
        }

        var result = _pathfinder.FindPath(startNode.Value, goalNode.Value);
        if (!result.Success)
        {
            GD.Print("Colonist: path not found, staying idle");
            return;
        }

        _waypoints = result.Waypoints;
        _waypointIndex = 1; // Skip first waypoint (it's the start position)
        _stuckTimer = 0;
        _lastProgressPos = Position;

        if (_waypointIndex < _waypoints.Count)
        {
            _state = State.Walking;
            GD.Print($"Colonist: path set, {_waypoints.Count} waypoints");
            RebuildPathVisualization();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // Wait for chunks to load before applying physics — prevents falling through void
        if (!_physicsReady) return;

        float dt = (float)delta;

        // Void safety: if fallen below the world, teleport back to spawn
        if (Position.Y < -20)
        {
            GD.Print($"Colonist: fell into void at {Position}, resetting to spawn at {_spawnPosition}");
            Position = _spawnPosition;
            Velocity = Vector3.Zero;
            _waypoints = null;
            _state = State.Idle;
            return;
        }

        // Y-level slice visibility: hide colonist when above the slice level
        Visible = !SliceState.Enabled || Position.Y <= SliceState.YLevel;

        switch (_state)
        {
            case State.Idle:
                // Zero horizontal velocity when idle so we don't drift off edges
                var idleVel = Velocity;
                idleVel.X = 0;
                idleVel.Z = 0;
                if (!IsOnFloor()) idleVel.Y -= Gravity * dt;
                Velocity = idleVel;
                MoveAndSlide();
                break;

            case State.Walking:
                ProcessWalking(dt);
                break;

            case State.JumpingUp:
                ProcessJumping(dt);
                break;

            case State.Falling:
                ProcessFalling(dt);
                break;
        }
    }

    private void ProcessWalking(float dt)
    {
        if (_waypointIndex >= _waypoints.Count)
        {
            TransitionTo(State.Idle);
            GD.Print($"Colonist: reached destination {Position}");
            return;
        }

        var target = _waypoints[_waypointIndex].StandPosition;
        var targetXZ = new Vector3(target.X, Position.Y, target.Z);
        var dirXZ = (targetXZ - Position).Normalized();
        float distXZ = new Vector2(target.X - Position.X, target.Z - Position.Z).Length();

        // Check if we need to jump up to reach next waypoint
        if (target.Y > Position.Y + 0.5f)
        {
            // Need to jump
            var vel = Velocity;
            vel.Y = JumpVelocity;
            vel.X = dirXZ.X * MoveSpeed;
            vel.Z = dirXZ.Z * MoveSpeed;
            Velocity = vel;
            _jumpGraceTimer = JumpGraceTime;
            TransitionTo(State.JumpingUp);
            return;
        }

        // Move horizontally toward waypoint
        var velocity = Velocity;
        velocity.X = dirXZ.X * MoveSpeed;
        velocity.Z = dirXZ.Z * MoveSpeed;
        ApplyGravity(ref velocity, dt);
        Velocity = velocity;
        MoveAndSlide();

        // Check if we've reached this waypoint
        if (distXZ < WaypointReachDist)
        {
            _waypointIndex++;
            _stuckTimer = 0;
            _lastProgressPos = Position;
        }

        // Not on floor? Start falling
        if (!IsOnFloor())
        {
            TransitionTo(State.Falling);
            return;
        }

        // Stuck detection
        _stuckTimer += dt;
        if (_stuckTimer > StuckTimeout)
        {
            float progressDist = (Position - _lastProgressPos).Length();
            if (progressDist < 0.5f)
            {
                GD.Print($"Colonist: stuck for {_stuckTimer:F1}s at {Position}, clearing path");
                _waypoints = null;
                TransitionTo(State.Idle);
                return;
            }
            _lastProgressPos = Position;
            _stuckTimer = 0;
        }
    }

    private void ProcessJumping(float dt)
    {
        _jumpGraceTimer -= dt;

        // Continue horizontal movement toward waypoint
        if (_waypointIndex < _waypoints.Count)
        {
            var target = _waypoints[_waypointIndex].StandPosition;
            var dirXZ = new Vector3(target.X - Position.X, 0, target.Z - Position.Z).Normalized();
            var velocity = Velocity;
            velocity.X = dirXZ.X * MoveSpeed;
            velocity.Z = dirXZ.Z * MoveSpeed;
            ApplyGravity(ref velocity, dt);
            Velocity = velocity;
        }
        else
        {
            var velocity = Velocity;
            ApplyGravity(ref velocity, dt);
            Velocity = velocity;
        }

        MoveAndSlide();

        // Only check landing after grace timer expires
        if (_jumpGraceTimer <= 0 && IsOnFloor())
        {
            TransitionTo(State.Walking);
        }

        // Failsafe: if falling back down, switch to falling state
        if (_jumpGraceTimer <= 0 && Velocity.Y < 0)
        {
            TransitionTo(State.Falling);
        }
    }

    private void ProcessFalling(float dt)
    {
        // Continue horizontal movement if we have waypoints
        if (_waypointIndex < _waypoints?.Count)
        {
            var target = _waypoints[_waypointIndex].StandPosition;
            var dirXZ = new Vector3(target.X - Position.X, 0, target.Z - Position.Z).Normalized();
            var velocity = Velocity;
            velocity.X = dirXZ.X * MoveSpeed;
            velocity.Z = dirXZ.Z * MoveSpeed;
            ApplyGravity(ref velocity, dt);
            Velocity = velocity;
        }
        else
        {
            var velocity = Velocity;
            velocity.X = 0;
            velocity.Z = 0;
            ApplyGravity(ref velocity, dt);
            Velocity = velocity;
        }

        MoveAndSlide();

        if (IsOnFloor())
        {
            TransitionTo(_waypoints != null && _waypointIndex < _waypoints.Count ? State.Walking : State.Idle);
        }
    }

    private void ApplyGravity(float dt)
    {
        var velocity = Velocity;
        ApplyGravity(ref velocity, dt);
        Velocity = velocity;
    }

    private void ApplyGravity(ref Vector3 velocity, float dt)
    {
        if (!IsOnFloor())
            velocity.Y -= Gravity * dt;
    }

    private void TransitionTo(State newState)
    {
        if (_state != newState)
        {
            _state = newState;

            if (newState == State.Idle)
                ClearPathVisualization();
        }
    }

    private void RebuildPathVisualization()
    {
        if (_pathMesh == null || !_showPath) return;
        if (_waypoints == null || _waypoints.Count < 2)
        {
            ClearPathVisualization();
            return;
        }

        _pathMesh.ClearSurfaces();
        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        // Draw lines between waypoints
        for (int i = 0; i < _waypoints.Count - 1; i++)
        {
            var from = _waypoints[i].StandPosition + new Vector3(0, 0.1f, 0); // Slight Y offset to avoid z-fight
            var to = _waypoints[i + 1].StandPosition + new Vector3(0, 0.1f, 0);
            _pathMesh.SurfaceAddVertex(from);
            _pathMesh.SurfaceAddVertex(to);
        }

        _pathMesh.SurfaceEnd();

        // Also draw small markers at each waypoint using a second surface
        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        float markerSize = 0.15f;
        for (int i = 0; i < _waypoints.Count; i++)
        {
            var center = _waypoints[i].StandPosition + new Vector3(0, 0.1f, 0);
            // Small cross at each waypoint
            _pathMesh.SurfaceAddVertex(center + new Vector3(-markerSize, 0, 0));
            _pathMesh.SurfaceAddVertex(center + new Vector3(markerSize, 0, 0));
            _pathMesh.SurfaceAddVertex(center + new Vector3(0, 0, -markerSize));
            _pathMesh.SurfaceAddVertex(center + new Vector3(0, 0, markerSize));
            _pathMesh.SurfaceAddVertex(center + new Vector3(0, -markerSize, 0));
            _pathMesh.SurfaceAddVertex(center + new Vector3(0, markerSize, 0));
        }
        _pathMesh.SurfaceEnd();
    }

    private void ClearPathVisualization()
    {
        _pathMesh?.ClearSurfaces();
    }
}
