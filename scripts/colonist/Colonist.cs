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

    public void Initialize(World world, VoxelPathfinder pathfinder)
    {
        _world = world;
        _pathfinder = pathfinder;
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

        GD.Print($"Colonist spawned at {Position}");
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
            GD.Print($"Colonist: Walking, {_waypoints.Count} waypoints");
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // Void safety: if fallen below the world, teleport back to spawn
        if (Position.Y < -20)
        {
            GD.Print($"Colonist: fell into void at {Position}, resetting to spawn");
            Position = new Vector3(8, 15, 8);
            Velocity = Vector3.Zero;
            _waypoints = null;
            _state = State.Idle;
            return;
        }

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
            GD.Print($"Colonist: {_state} -> {newState} at ({Position.X:F1}, {Position.Y:F1}, {Position.Z:F1})");
            _state = newState;
        }
    }
}
