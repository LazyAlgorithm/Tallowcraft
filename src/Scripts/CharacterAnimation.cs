using System;
using System.Numerics;
using Tallow;

namespace Tallowcraft;

/// <summary>
/// Plays the real Quaternius CC0 humanoid animations through Tallow's native
/// SkinnedMeshRenderer and its matched Universal Animation Library GLB.
/// Only visual pose selection is local; the Jolt CharacterController determines
/// actual movement and remains server-authoritative for gameplay.
/// </summary>
public class CharacterAnimation : Script
{
    public float WalkThreshold = 0.65f;
    public float RunThreshold = 6.6f;
    public float AirThreshold = 3.0f;

    // The imported clips are native-speed locomotion, not the 0.55x
    // placeholder .tanim loops. Keep rates close to the source animation.
    public float IdlePlayback = 1.0f;
    public float WalkPlayback = 0.95f;
    public float RunPlayback = 0.90f;
    public float JumpPlayback = 0.95f;

    private SkinnedMeshRenderer _rig;
    private Vector3 _lastPosition;
    private bool _ready;
    private float _smoothGroundSpeed;
    private float _smoothVerticalSpeed;
    private float _airTime;
    private string _lastClip = "";

    public override void Start()
    {
        _rig = GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
        _lastPosition = Transform.Position;
        _ready = false;
        _smoothGroundSpeed = _smoothVerticalSpeed = _airTime = 0f;
        _lastClip = "";
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_rig.IsValid)
            _rig = GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
        if (!_rig.IsValid || deltaTime <= 0f)
            return;

        Vector3 now = Transform.Position;
        Vector3 movement = now - _lastPosition;
        _lastPosition = now;

        // Ignore first-frame deltas, big network corrections and frame stalls.
        if (!_ready || movement.LengthSquared() > 4f || deltaTime > 0.25f)
        {
            _ready = true;
            _smoothGroundSpeed = _smoothVerticalSpeed = _airTime = 0f;
            SetClip(QuaterniusClips.Idle, IdlePlayback);
            return;
        }

        float dt = Math.Clamp(deltaTime, 0.001f, 0.1f);
        float ground = MathF.Sqrt(movement.X * movement.X +
            movement.Z * movement.Z) / dt;
        float vertical = MathF.Abs(movement.Y) / dt;
        float blend = 1f - MathF.Exp(-8f * dt);
        _smoothGroundSpeed += (Math.Clamp(ground, 0f, 15f) - _smoothGroundSpeed) * blend;
        _smoothVerticalSpeed += (Math.Clamp(vertical, 0f, 15f) - _smoothVerticalSpeed) * blend;

        if (_smoothVerticalSpeed > AirThreshold)
            _airTime = MathF.Min(0.3f, _airTime + dt);
        else
            _airTime = MathF.Max(0f, _airTime - dt * 2.5f);

        bool running = _lastClip == QuaterniusClips.Run
            ? _smoothGroundSpeed > RunThreshold - 0.8f
            : _smoothGroundSpeed > RunThreshold;
        bool walking = _lastClip == QuaterniusClips.Walk
            ? _smoothGroundSpeed > WalkThreshold * 0.6f
            : _smoothGroundSpeed > WalkThreshold;

        string clip = _airTime > 0.14f ? QuaterniusClips.Jump
            : running ? QuaterniusClips.Run
            : walking ? QuaterniusClips.Walk : QuaterniusClips.Idle;
        float speed = clip == QuaterniusClips.Run ? RunPlayback
            : clip == QuaterniusClips.Walk ? WalkPlayback
            : clip == QuaterniusClips.Jump ? JumpPlayback : IdlePlayback;
        SetClip(clip, speed);
    }

    private void SetClip(string clip, float speed)
    {
        // The engine cross-fades imported .glb clips by bone name when the
        // SkinnedMeshRenderer.Clip property changes. Do not restart every frame.
        if (_lastClip != clip)
        {
            _rig.Clip = clip;
            _lastClip = clip;
        }
        if (MathF.Abs(_rig.Speed - speed) > 0.001f)
            _rig.Speed = speed;
    }
}
