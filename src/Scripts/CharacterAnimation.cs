using System;
using System.Numerics;
using Tallow;

namespace Tallowcraft;

/// <summary>
/// Visual-only animation of a replicated character. Network interpolation,
/// tiny corrective moves and inconsistent render frame times must not make
/// the rig alternate between running and jumping every frame.
/// </summary>
public class CharacterAnimation : Script
{
    public float WalkThreshold = 0.65f;
    public float RunThreshold = 6.75f;
    public float AirThreshold = 2.6f;

    // Imported sample motion has a short 0.6-second run cycle.
    // Slowing it down also avoids violent rapidly changing skinned shadows.
    public float IdlePlayback = 0.65f;
    public float WalkPlayback = 0.55f;
    public float RunPlayback = 0.60f;
    public float JumpPlayback = 0.60f;

    private Animator _animator;
    private Vector3 _lastPosition;
    private bool _ready;
    private float _smoothGroundSpeed;
    private float _smoothVerticalSpeed;
    private float _airTime;
    private string _lastClip = "Idle";

    public override void Start()
    {
        _animator = GameObject.GetComponentInChildren<Animator>();
        _lastPosition = Transform.Position;
        _ready = false;
        _smoothGroundSpeed = 0f;
        _smoothVerticalSpeed = 0f;
        _airTime = 0f;
        _lastClip = "Idle";
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_animator.IsValid)
            _animator = GameObject.GetComponentInChildren<Animator>();
        if (!_animator.IsValid || deltaTime <= 0f)
            return;

        Vector3 current = Transform.Position;
        Vector3 displacement = current - _lastPosition;
        _lastPosition = current;

        // Ignore the first movement sample and network teleport corrections.
        if (!_ready || displacement.LengthSquared() > 4f || deltaTime > 0.25f)
        {
            _ready = true;
            _smoothGroundSpeed = 0f;
            _smoothVerticalSpeed = 0f;
            _airTime = 0f;
            _lastClip = "Idle";
            _animator.Play("Idle", 0.12f);
            _animator.Speed = IdlePlayback;
            return;
        }

        float dt = Math.Clamp(deltaTime, 0.001f, 0.1f);
        float ground = MathF.Sqrt(displacement.X * displacement.X +
            displacement.Z * displacement.Z) / dt;
        float vertical = MathF.Abs(displacement.Y) / dt;
        ground = Math.Clamp(ground, 0f, 15f);
        vertical = Math.Clamp(vertical, 0f, 15f);

        // Exponential smoothing dampens the position-delta spikes that
        // otherwise kick the run and jump animations on every frame.
        float blend = 1f - MathF.Exp(-8f * dt);
        _smoothGroundSpeed += (ground - _smoothGroundSpeed) * blend;
        _smoothVerticalSpeed += (vertical - _smoothVerticalSpeed) * blend;

        if (_smoothVerticalSpeed > AirThreshold)
            _airTime = MathF.Min(0.3f, _airTime + dt);
        else
            _airTime = MathF.Max(0f, _airTime - dt * 2.5f);

        bool running = _lastClip == "Run"
            ? _smoothGroundSpeed > RunThreshold - 0.8f
            : _smoothGroundSpeed > RunThreshold;
        bool walking = _lastClip == "Walk"
            ? _smoothGroundSpeed > WalkThreshold * 0.6f
            : _smoothGroundSpeed > WalkThreshold;

        string clip = _airTime > 0.14f ? "Jump"
            : running ? "Run"
            : walking ? "Walk" : "Idle";
        if (clip != _lastClip || _animator.CurrentClip != clip)
        {
            _animator.Play(clip, clip == "Jump" ? 0.15f : 0.25f);
            _lastClip = clip;
        }

        // Fixed, conservative clip rates: no dynamic amplification based on
        // temporarily spiking remote-player displacement or tick correction.
        _animator.Speed = clip switch
        {
            "Walk" => WalkPlayback,
            "Run" => RunPlayback,
            "Jump" => JumpPlayback,
            _ => IdlePlayback
        };
    }
}
